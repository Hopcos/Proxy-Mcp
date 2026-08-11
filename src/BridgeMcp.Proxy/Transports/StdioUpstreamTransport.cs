using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BridgeMcp.Configuration;
using BridgeMcp.Protocol;
using Microsoft.Extensions.Logging;

namespace BridgeMcp.Transports;

/// <summary>
/// Speaks the MCP STDIO transport to a child process: JSON-RPC 2.0 messages
/// delimited by newlines on stdout/stdin. (The spec also permits the LSP-style
/// <c>Content-Length</c> framing; both are accepted on input, newline framing
/// is used on output.)
///
/// Responses are demultiplexed by request id. Server-initiated notifications
/// and requests (e.g. <c>notifications/progress</c>, sampling requests) are
/// surfaced via <see cref="OnServerMessage"/> so the proxy can relay them.
/// </summary>
public sealed class StdioUpstreamTransport : IUpstreamTransport
{
    private readonly StdioServerOptions _options;
    private readonly string _protocolVersion;
    private readonly ILogger<StdioUpstreamTransport> _logger;
    private readonly object _gate = new();

    private Process? _process;
    private StreamReader? _stdout;
    private StreamWriter? _stdin;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readLoop;

    // Monotonic id generator for the proxy's own requests to the upstream.
    private long _nextProxyId = 1;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _process is not null && !_process.HasExited;
            }
        }
    }

    /// <summary>
    /// Server-initiated messages (notifications/requests the upstream sends
    /// without being asked). The HTTP layer relays these onto the open SSE
    /// stream for the relevant session.
    /// </summary>
    public event Action<JsonRpcMessage>? OnServerMessage;

    public StdioUpstreamTransport(StdioServerOptions options, ILogger<StdioUpstreamTransport> logger)
        : this(options, "2025-06-18", logger) { }

    public StdioUpstreamTransport(StdioServerOptions options, string protocolVersion, ILogger<StdioUpstreamTransport> logger)
    {
        _options = options;
        _logger = logger;
        _protocolVersion = protocolVersion;
    }

    /// <summary>Launches the child process and starts the stdout reader. Safe to call once.</summary>
    public StdioUpstreamTransport Start()
    {
        lock (_gate)
        {
            if (_process is not null) return this;

            // UTF-8 WITHOUT BOM: many MCP servers (Python, Node) choke on a
            // leading U+FEFF and report a JSON parse error at column 0.
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var psi = new ProcessStartInfo
            {
                FileName = _options.Command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8NoBom,
                StandardInputEncoding = utf8NoBom,
            };
            foreach (var a in _options.Args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in _options.Env) psi.Environment[k] = v;
            if (_options.WorkingDirectory is not null) psi.WorkingDirectory = _options.WorkingDirectory;

            try
            {
                _process = Process.Start(psi)
                    ?? throw new InvalidOperationException($"Failed to start '{_options.Command}'.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to launch upstream '{_options.Name}' (command '{_options.Command}').", ex);
            }

            _stdout = _process.StandardOutput;
            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("[{Server}] upstream stderr: {Line}", _options.Name, e.Data);
            };
            _process.BeginErrorReadLine();

            _readLoop = Task.Run(ReadLoopAsync);
            _logger.LogInformation("[{Server}] upstream process started (pid {Pid}).",
                _options.Name, _process.Id);
            return this;
        }
    }

    public async Task<JsonRpcMessage> InitializeAsync(CancellationToken ct)
    {
        Start();

        var init = new JsonRpcMessage
        {
            Id = MakeId(),
            Method = "initialize",
            Params = JsonSerializer.SerializeToElement(new
            {
                protocolVersion = _protocolVersion,
                capabilities = new { },
                clientInfo = new { name = "bridgemcp", version = "1.0.0" },
            }),
        };

        var resp = await SendRequestAsync(init, ct).ConfigureAwait(false);
        // Follow up with the initialized notification to complete the handshake.
        await SendNotificationAsync(new JsonRpcMessage { Method = "notifications/initialized" }, ct)
            .ConfigureAwait(false);
        return resp;
    }

    public Task SendNotificationAsync(JsonRpcMessage notification, CancellationToken ct)
        => WriteAsync(notification, ct);

    public async Task<JsonRpcMessage> SendRequestAsync(JsonRpcMessage request, CancellationToken ct)
    {
        if (request.Id is null)
            throw new ArgumentException("Request must carry an id.", nameof(request));

        var key = IdKey(request.Id.Value);
        var tcs = new TaskCompletionSource<JsonRpcMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (!IsConnected)
                throw new InvalidOperationException($"Upstream '{_options.Name}' is not connected.");
            _pending[key] = tcs;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Token.Register(() =>
        {
            lock (_gate) _pending.Remove(key);
            tcs.TrySetException(new TimeoutException(
                $"Upstream '{_options.Name}' did not respond within the timeout for id {key}."));
        });
        cts.CancelAfter(_options.RequestTimeout);

        try
        {
            await WriteAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _pending.Remove(key);
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task WriteAsync(JsonRpcMessage message, CancellationToken ct)
    {
        var json = JsonRpcSerializer.Serialize(message);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var writer = _stdin ?? throw new ObjectDisposedException(nameof(StdioUpstreamTransport));
            await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = _stdout!;
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_readerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Server}] stdout read failed.", _options.Name);
                    break;
                }

                if (line is null) break; // EOF: child exited
                if (line.Length == 0) continue;

                JsonRpcMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<JsonRpcMessage>(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[{Server}] dropped unparseable line from upstream.", _options.Name);
                    continue;
                }

                if (msg is null) continue;
                Dispatch(msg);
            }
        }
        finally
        {
            FailAllPending("upstream closed");
        }
    }

    private void Dispatch(JsonRpcMessage msg)
    {
        // A response/error to a pending proxy request: resolve the waiter.
        if ((msg.IsResponse() || msg.IsError()) && msg.Id is not null)
        {
            var key = IdKey(msg.Id.Value);
            TaskCompletionSource<JsonRpcMessage>? tcs;
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out tcs)) _pending.Remove(key);
            }
            if (tcs is not null) tcs.TrySetResult(msg);
            else _logger.LogDebug("[{Server}] unmatched response for id {Id}.", _options.Name, key);
            return;
        }

        // Server-initiated notification or request: surface to the proxy.
        OnServerMessage?.Invoke(msg);
    }

    private void FailAllPending(string reason)
    {
        List<TaskCompletionSource<JsonRpcMessage>>? waiters;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            waiters = new List<TaskCompletionSource<JsonRpcMessage>>(_pending.Values);
            _pending.Clear();
        }
        foreach (var w in waiters)
            w.TrySetException(new InvalidOperationException(
                $"Upstream '{_options.Name}' closed before responding: {reason}."));
    }

    private JsonElement MakeId()
    {
        var n = Interlocked.Increment(ref _nextProxyId) - 1;
        return JsonSerializer.SerializeToElement(n);
    }

    private static string IdKey(JsonElement id)
    {
        // Stable string key regardless of whether the id is a number or string.
        return id.ValueKind switch
        {
            JsonValueKind.String => "s:" + id.GetString(),
            JsonValueKind.Number => "n:" + id.GetRawText(),
            _ => id.GetRawText(),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _readerCts.Cancel();
        try
        {
            if (_stdin is not null) await _stdin.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* ignore */ }

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(_options.ShutdownGraceSeconds * 1000))
                    _process.Kill(true);
            }
            catch { /* ignore */ }
        }
        _process?.Dispose();

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        FailAllPending("transport disposed");
        _readerCts.Dispose();
        _sendGate.Dispose();
    }
}
