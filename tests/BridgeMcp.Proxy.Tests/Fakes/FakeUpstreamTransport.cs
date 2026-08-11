using System.Collections.Concurrent;
using System.Text.Json;
using BridgeMcp.Protocol;
using BridgeMcp.Transports;

namespace BridgeMcp.Proxy.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IUpstreamTransport"/> that records sent messages and
/// lets tests script responses/notifications. Used to test the proxy engine
/// without a real child process.
/// </summary>
public sealed class FakeUpstreamTransport : IUpstreamTransport
{
    private readonly ConcurrentQueue<JsonRpcMessage> _sent = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new();
    private readonly BlockingCollection<JsonRpcMessage> _serverOutbox = new();

    // Signaled (and reset) whenever a request is enqueued, so tests can await
    // "request seen" before delivering a scripted response. This avoids the
    // race where Parallel.ForAsync hasn't yet run its body.
    private readonly TaskCompletionSource _requestSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WhenRequestSent => _requestSeen.Task;

    public IReadOnlyList<JsonRpcMessage> Sent => _sent.ToArray();
    public bool IsConnected { get; set; } = true;

    public Task<JsonRpcMessage> InitializeAsync(CancellationToken ct)
    {
        var resp = new JsonRpcMessage
        {
            Id = JsonSerializer.SerializeToElement(1),
            Result = JsonSerializer.SerializeToElement(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "fake", version = "1.0.0" },
            }),
        };
        return Task.FromResult(resp);
    }

    public Task SendNotificationAsync(JsonRpcMessage notification, CancellationToken ct)
    {
        _sent.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task<JsonRpcMessage> SendRequestAsync(JsonRpcMessage request, CancellationToken ct)
    {
        if (!IsConnected)
            return Task.FromException<JsonRpcMessage>(
                new InvalidOperationException("upstream not connected"));

        _sent.Enqueue(request);
        var key = IdKey(request.Id!.Value);
        var tcs = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        _requestSeen.TrySetResult();
        return tcs.Task;
    }

    /// <summary>Test helper: deliver the response for a pending request by id.</summary>
    public void DeliverResponse(JsonElement? id, JsonRpcMessage response)
        => Respond(IdKey(id!.Value), response);

    /// <summary>Test helper: deliver a JSON-RPC error for a pending request.</summary>
    public void DeliverError(JsonElement? id, int code, string message)
    {
        var err = new JsonRpcMessage
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message },
        };
        Respond(IdKey(id!.Value), err);
    }

    private void Respond(string key, JsonRpcMessage msg)
    {
        if (_pending.TryRemove(key, out var tcs)) tcs.SetResult(msg);
    }

    public ValueTask DisposeAsync()
    {
        _serverOutbox.Dispose();
        foreach (var (_, tcs) in _pending) tcs.TrySetCanceled();
        return ValueTask.CompletedTask;
    }

    private static string IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number => "n:" + id.GetRawText(),
        _ => id.GetRawText(),
    };
}
