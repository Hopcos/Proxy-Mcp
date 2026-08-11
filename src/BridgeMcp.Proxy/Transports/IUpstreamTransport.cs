namespace BridgeMcp.Transports;

using BridgeMcp.Protocol;

/// <summary>
/// A transport that talks to an upstream MCP server. The proxy core is bound
/// to this abstraction so that non-STDIO upstreams (e.g. an already-HTTP
/// server, an in-process server) can be supported by adding a new
/// implementation — no core changes required.
/// </summary>
public interface IUpstreamTransport : IAsyncDisposable
{
    /// <summary>Whether the upstream is currently connected and usable.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Performs the MCP <c>initialize</c> handshake against the upstream and
    /// returns the server's advertised result. Must be idempotent.
    /// </summary>
    Task<JsonRpcMessage> InitializeAsync(CancellationToken ct);

    /// <summary>Sends a notification (no response expected), e.g. <c>notifications/initialized</c>.</summary>
    Task SendNotificationAsync(JsonRpcMessage notification, CancellationToken ct);

    /// <summary>
    /// Sends a request and awaits the single matching response (matched by id).
    /// Forwards upstream JSON-RPC errors as a returned message, not an exception.
    /// </summary>
    Task<JsonRpcMessage> SendRequestAsync(JsonRpcMessage request, CancellationToken ct);
}
