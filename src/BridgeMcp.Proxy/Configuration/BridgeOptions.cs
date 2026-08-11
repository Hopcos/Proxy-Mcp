using System.ComponentModel.DataAnnotations;

namespace BridgeMcp.Configuration;

/// <summary>
/// Configuration for a single proxied STDIO MCP server: how to launch the
/// child process and which capabilities to advertise.
/// </summary>
public sealed class StdioServerOptions
{
    /// <summary>Logical name used in the HTTP path (e.g. <c>codebase-memory-mcp</c>).</summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>Executable to launch (resolved on PATH unless absolute).</summary>
    [Required]
    public string Command { get; init; } = string.Empty;

    /// <summary>Arguments passed to <see cref="Command"/>.</summary>
    public List<string> Args { get; init; } = new();

    /// <summary>Environment variables set on the child process.</summary>
    public Dictionary<string, string> Env { get; init; } = new();

    /// <summary>Working directory for the child process; null inherits the proxy's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Per-server timeout for a single upstream request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Seconds to wait for the child to exit on shutdown before killing it.</summary>
    public int ShutdownGraceSeconds { get; init; } = 5;
}

/// <summary>
/// Root configuration: the HTTP listener and the set of servers to proxy.
/// </summary>
public sealed class BridgeOptions
{
    /// <summary>Address prefix to listen on, e.g. <c>http://localhost:8787</c>.</summary>
    public string Url { get; init; } = "http://localhost:8787";

    /// <summary>Optional bearer-token shared secret. Empty disables auth (local-only).</summary>
    public string AuthToken { get; init; } = string.Empty;

    /// <summary>Allowed <c>Origin</c> header values for the DNS-rebinding guard. Empty = allow all.</summary>
    public List<string> AllowedOrigins { get; init; } = new();

    /// <summary>The servers to proxy. Keyed by <see cref="StdioServerOptions.Name"/>.</summary>
    public Dictionary<string, StdioServerOptions> Servers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Protocol version advertised to upstream during the proxy's own handshake.</summary>
    public string ProtocolVersion { get; init; } = "2025-06-18";
}
