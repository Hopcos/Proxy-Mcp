using System.Collections.Concurrent;
using BridgeMcp.Configuration;
using BridgeMcp.Core;
using BridgeMcp.Transports;
using Microsoft.Extensions.Logging;

namespace BridgeMcp.Sessions;

/// <summary>
/// Owns the upstream transports for every configured server and the per-server
/// HTTP session bookkeeping. Each HTTP <c>mcp-session-id</c> maps to a
/// dedicated upstream connection so concurrent clients don't interleave their
/// JSON-RPC traffic on a shared STDIO pipe.
/// </summary>
public sealed class ServerRegistry : IAsyncDisposable
{
    private readonly BridgeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServerRegistry> _logger;

    // name -> the lazily-started upstream transport (shared across sessions).
    private readonly ConcurrentDictionary<string, StdioUpstreamTransport> _transports = new(StringComparer.OrdinalIgnoreCase);

    public ServerRegistry(BridgeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ServerRegistry>();
    }

    public IEnumerable<string> ServerNames => _options.Servers.Keys;

    public bool HasServer(string name) => _options.Servers.ContainsKey(name);

    /// <summary>Gets (starting if needed) the upstream transport for a server.</summary>
    public StdioUpstreamTransport GetTransport(string name)
    {
        if (!_options.Servers.TryGetValue(name, out var sOpts))
            throw new KeyNotFoundException($"Unknown MCP server '{name}'.");

        return _transports.GetOrAdd(name, _ =>
        {
            var t = new StdioUpstreamTransport(sOpts, _options.ProtocolVersion,
                _loggerFactory.CreateLogger<StdioUpstreamTransport>()).Start();
            return t;
        });
    }

    /// <summary>Builds a fresh engine bound to the named server's upstream.</summary>
    public ProxyEngine CreateEngine(string name)
    {
        var transport = GetTransport(name);
        return new ProxyEngine(transport, _loggerFactory.CreateLogger<ProxyEngine>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, t) in _transports)
        {
            try { await t.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing upstream transport."); }
        }
        _transports.Clear();
    }
}
