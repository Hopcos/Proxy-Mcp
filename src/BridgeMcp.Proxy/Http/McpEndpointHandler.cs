using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BridgeMcp.Protocol;
using BridgeMcp.Sessions;
using BridgeMcp.Transports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BridgeMcp.Http;

/// <summary>
/// Implements the MCP Streamable HTTP transport over ASP.NET Core. Handles a
/// single MCP endpoint (<c>/{server}/mcp</c>): POST for client→server
/// messages, GET for an SSE stream of server→client messages, DELETE to close
/// a session. Stateless functions grouped here keep <see cref="Program"/>
/// minimal.
/// </summary>
internal sealed class McpEndpointHandler
{
    private const string SessionIdHeader = "mcp-session-id";
    private const string ProtocolVersionHeader = "mcp-protocol-version";
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly ServerRegistry _registry;
    private readonly SessionStore _sessions;
    private readonly ILogger<McpEndpointHandler> _logger;

    public McpEndpointHandler(ServerRegistry registry, SessionStore sessions, ILogger<McpEndpointHandler> logger)
    {
        _registry = registry;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>POST: the main request path.</summary>
    public async Task HandlePostAsync(HttpContext ctx, string server)
    {
        if (!_registry.HasServer(server))
        {
            await WriteErrorAsync(ctx, 404, "Unknown server.", id: null).ConfigureAwait(false);
            return;
        }

        var ct = ctx.RequestAborted;
        byte[] body;
        await using (var ms = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            body = ms.ToArray();
        }
        if (body.Length == 0)
        {
            await WriteErrorAsync(ctx, 400, "Empty request body.", id: null).ConfigureAwait(false);
            return;
        }

        var frame = JsonRpcSerializer.ParseFrame(body);

        // An initialize with no session header creates a new session.
        var sessionId = ctx.Request.Headers[SessionIdHeader].ToString();
        SessionStore.SessionEntry? entry;
        if (string.IsNullOrEmpty(sessionId))
        {
            if (!IsInitialize(frame))
            {
                await WriteErrorAsync(ctx, 400, "Bad Request: No valid session ID provided.", id: null).ConfigureAwait(false);
                return;
            }

            var transport = _registry.GetTransport(server);
            try { await transport.InitializeAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upstream initialize failed for {Server}.", server);
                await WriteErrorAsync(ctx, 502, $"Upstream initialize failed: {ex.Message}", id: FirstId(frame))
                    .ConfigureAwait(false);
                return;
            }

            var newId = Guid.NewGuid().ToString("N");
            entry = _sessions.GetOrCreate(newId, _ => new SessionStore.SessionEntry(
                newId, _registry.CreateEngine(server), transport) { Initialized = true });
            ctx.Response.Headers[SessionIdHeader] = newId;
        }
        else
        {
            entry = _sessions.Get(sessionId);
            if (entry is null)
            {
                await WriteErrorAsync(ctx, 400, "Invalid or missing session ID.", id: FirstId(frame))
                    .ConfigureAwait(false);
                return;
            }
        }

        var engine = entry.Engine;
        Frame? reply;
        try
        {
            reply = await engine.ProcessAsync(frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proxy engine failed for {Server}.", server);
            await WriteErrorAsync(ctx, 500, ex.Message, id: FirstId(frame)).ConfigureAwait(false);
            return;
        }

        if (reply is null)
        {
            // All-notification frame: 202 Accepted, no body.
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        // Successful initialize: confirm capabilities/protocolVersion to the client.
        if (IsInitialize(frame) && !reply.IsError)
        {
            ctx.Response.Headers[ProtocolVersionHeader] = DefaultProtocolVersion;
        }

        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.WriteAsync(JsonRpcSerializer.SerializeFrame(reply), ct).ConfigureAwait(false);
    }

    /// <summary>GET: opens a Server-Sent Events stream for server-initiated messages.</summary>
    public async Task HandleGetAsync(HttpContext ctx, string server)
    {
        var sessionId = ctx.Request.Headers[SessionIdHeader].ToString();
        var entry = !string.IsNullOrEmpty(sessionId) ? _sessions.Get(sessionId) : null;
        if (entry is null)
        {
            await WriteErrorAsync(ctx, 400, "Invalid or missing session ID.", id: null).ConfigureAwait(false);
            return;
        }

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);

        // Relay upstream-originated messages onto this SSE stream while the
        // client keeps the GET open. We attach a handler to the transport and
        // remove it when the stream ends.
        var transport = (StdioUpstreamTransport)entry.Transport;
        void OnMessage(JsonRpcMessage m)
        {
            try
            {
                var data = JsonRpcSerializer.Serialize(m);
                // SSE: two fields, terminated by a blank line.
                var sb = new StringBuilder("event: message\ndata: ")
                    .Append(JsonSerializer.Serialize(data)) // JSON-encode the payload
                    .Append("\n\n");
                ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted).GetAwaiter().GetResult();
                ctx.Response.Body.FlushAsync(ctx.RequestAborted).GetAwaiter().GetResult();
            }
            catch { /* client gone */ }
        }
        transport.OnServerMessage += OnMessage;

        try
        {
            // Keep the connection open until the client disconnects.
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                await Task.Delay(1000, ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.WriteAsync(": keepalive\n\n", ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException) { /* normal disconnect */ }
        finally
        {
            transport.OnServerMessage -= OnMessage;
        }
    }

    /// <summary>DELETE: closes a session.</summary>
    public Task HandleDeleteAsync(HttpContext ctx, string server)
    {
        var sessionId = ctx.Request.Headers[SessionIdHeader].ToString();
        if (string.IsNullOrEmpty(sessionId))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }
        _sessions.Remove(sessionId, out _);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private static bool IsInitialize(Frame f)
    {
        if (f.IsError) return false;
        foreach (var m in f.AllMessages)
            if (m.IsRequest() && m.Method == "initialize") return true;
        return false;
    }

    private static JsonElement? FirstId(Frame f)
    {
        foreach (var m in f.AllMessages)
            if (m.Id is not null) return m.Id;
        return null;
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string message, JsonElement? id)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var err = new JsonRpcMessage
        {
            Error = new JsonRpcError { Code = JsonRpcErrors.McpBad, Message = message },
            Id = id,
        };
        await ctx.Response.WriteAsync(JsonRpcSerializer.Serialize(err), ctx.RequestAborted).ConfigureAwait(false);
    }
}
