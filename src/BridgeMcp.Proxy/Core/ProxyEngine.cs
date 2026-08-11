using System.Text.Json;
using BridgeMcp.Protocol;
using BridgeMcp.Transports;
using Microsoft.Extensions.Logging;

namespace BridgeMcp.Core;

/// <summary>
/// The transport-agnostic proxy core. Given a parsed inbound JSON-RPC frame and
/// a connected upstream, it forwards each request/notification to the upstream
/// and assembles the matching response frame. It owns no HTTP state; the HTTP
/// adapter feeds it frames and gets frames back, which keeps this logic unit-
/// testable without spinning up Kestrel.
/// </summary>
public sealed class ProxyEngine
{
    private readonly IUpstreamTransport _upstream;
    private readonly ILogger<ProxyEngine> _logger;

    public ProxyEngine(IUpstreamTransport upstream, ILogger<ProxyEngine> logger)
    {
        _upstream = upstream;
        _logger = logger;
    }

    /// <summary>
    /// Forwards a frame to the upstream and returns the frame the proxy should
    /// send back to the client. Notifications produce no response. A batch is
    /// answered with a batch. A parse-error frame is returned unchanged.
    /// </summary>
    public async Task<Frame?> ProcessAsync(Frame frame, CancellationToken ct)
    {
        // Parse/invalid-request errors are passed straight back to the client.
        if (frame.IsError) return frame;

        // Notifications: forward, expect nothing back.
        var notifications = frame.Notifications.ToList();
        foreach (var n in notifications)
        {
            try { await _upstream.SendNotificationAsync(n, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to forward notification {Method}.", n.Method);
            }
        }

        var requests = frame.Requests.ToList();
        if (requests.Count == 0)
        {
            // All-notification frame: no response to send.
            return null;
        }

        // Fire all requests concurrently, preserve inbound ordering in the reply.
        var responses = new JsonRpcMessage[requests.Count];
        await Parallel.ForAsync(0, requests.Count, ct, async (i, token) =>
        {
            try
            {
                responses[i] = await _upstream.SendRequestAsync(requests[i], token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                responses[i] = ErrorFor(requests[i], ex);
            }
        }).ConfigureAwait(false);

        return frame.IsBatch ? Frame.Batch(responses!) : Frame.Single(responses[0]);
    }

    private static JsonRpcMessage ErrorFor(JsonRpcMessage req, Exception ex)
    {
        var code = ex switch
        {
            TimeoutException => -32001,
            _ => JsonRpcErrors.InternalError,
        };
        return new JsonRpcMessage
        {
            Id = req.Id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = ex.Message,
                Data = JsonSerializer.SerializeToElement(new { source = "bridgemcp" }),
            },
        };
    }
}
