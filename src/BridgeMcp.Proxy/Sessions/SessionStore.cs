using System.Collections.Concurrent;
using BridgeMcp.Core;
using BridgeMcp.Transports;

namespace BridgeMcp.Sessions;

/// <summary>
/// Tracks the HTTP sessions currently connected to a given upstream server.
/// The Streamable HTTP transport requires that after <c>initialize</c> the
/// client echoes back the <c>mcp-session-id</c> header on every request;
/// this store resolves that header to the per-session engine.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    /// <summary>
    /// The upstream transport is shared per server, but each session keeps its
    /// own engine and remembers whether the MCP handshake has completed.
    /// </summary>
    public sealed record SessionEntry(string SessionId, ProxyEngine Engine, IUpstreamTransport Transport)
    {
        public bool Initialized { get; set; }
    }

    public SessionEntry GetOrCreate(string sessionId, Func<string, SessionEntry> factory)
        => _sessions.GetOrAdd(sessionId, factory);

    public SessionEntry? Get(string sessionId)
        => _sessions.TryGetValue(sessionId, out var e) ? e : null;

    public bool Remove(string sessionId, out SessionEntry? removed)
        => _sessions.TryRemove(sessionId, out removed);

    public int Count => _sessions.Count;
}
