using System.Net.Http.Headers;
using BridgeMcp.Configuration;
using BridgeMcp.Http;
using BridgeMcp.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddSingleton<BridgeOptions>(_ => BindOptions())
    .AddSingleton<ServerRegistry>()
    .AddSingleton<SessionStore>()
    .AddSingleton<McpEndpointHandler>();

var app = builder.Build();
var opts = app.Services.GetRequiredService<BridgeOptions>();

foreach (var (name, s) in opts.Servers)
{
    if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Command))
        throw new InvalidOperationException($"Server '{name}' is missing a required 'name' or 'command'.");
}

app.UseRouting();

// Auth + DNS-rebinding guard, applied to the MCP endpoints only.
app.Use(async (ctx, next) =>
{
    if (IsServerPath(ctx.Request.Path))
    {
        if (!await AuthorizedAsync(ctx, opts))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }
        if (!OriginAllowed(ctx, opts))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Forbidden: invalid Origin");
            return;
        }
    }
    await next();
});

// Health/landing endpoint: lists the proxied servers and their HTTP URLs.
app.MapGet("/", (ServerRegistry reg) =>
{
    var payload = new
    {
        name = "bridgemcp",
        version = "1.0.0",
        servers = reg.ServerNames.Select(n => new
        {
            name = n,
            url = $"/{n}/mcp",
        }),
    };
    return Results.Json(payload);
});

// A single server's MCP endpoint: POST/GET/DELETE per the Streamable HTTP spec.
app.MapMethods("/{server}/mcp", new[] { "POST", "GET", "DELETE" },
    async (HttpContext ctx, string server, McpEndpointHandler h) =>
    {
        switch (ctx.Request.Method)
        {
            case "POST": await h.HandlePostAsync(ctx, server); break;
            case "GET": await h.HandleGetAsync(ctx, server); break;
            case "DELETE": await h.HandleDeleteAsync(ctx, server); break;
        }
    });

app.Lifetime.ApplicationStopping.Register(() =>
{
    var reg = app.Services.GetRequiredService<ServerRegistry>();
    reg.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

// Listen on the configured URL. A URL set via ASPNETCORE_URLS / --urls takes
// precedence because we only add ours when none are already configured.
if (app.Urls.Count == 0 && !string.IsNullOrWhiteSpace(opts.Url))
    app.Urls.Add(opts.Url);

app.Run();

static bool IsServerPath(PathString p) =>
    p.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

static async Task<bool> AuthorizedAsync(HttpContext ctx, BridgeOptions opts)
{
    if (string.IsNullOrEmpty(opts.AuthToken)) return true;
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(auth)) return false;
    if (!AuthenticationHeaderValue.TryParse(auth, out var hv) ||
        !string.Equals(hv.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        return false;
    return string.Equals(hv.Parameter, opts.AuthToken, StringComparison.Ordinal);
}

static bool OriginAllowed(HttpContext ctx, BridgeOptions opts)
{
    if (opts.AllowedOrigins.Count == 0) return true;
    var origin = ctx.Request.Headers.Origin.ToString();
    if (string.IsNullOrEmpty(origin)) return true; // non-browser clients
    return opts.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
}

static BridgeOptions BindOptions()
{
    var cfg = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("servers.json", optional: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json",
            optional: true)
        .AddEnvironmentVariables(prefix: "BRIDGEMCP_")
        .Build();

    var opts = new BridgeOptions();
    cfg.GetSection("Bridge").Bind(opts);

    // Allow a Claude-Code-style flat file too: { "mcpServers": { "name": { ... } } }
    var mcpServers = cfg.GetSection("mcpServers");
    if (mcpServers.Exists())
    {
        foreach (var child in mcpServers.GetChildren())
        {
            var so = new StdioServerOptions { Name = child.Key };
            child.Bind(so);
            opts.Servers[child.Key] = so;
        }
    }

    if (opts.Servers.Count == 0)
        throw new InvalidOperationException(
            "No servers configured. Add 'Bridge:Servers' to appsettings.json or a 'servers.json' file.");
    return opts;
}

// Exposed for test hosts that bootstrap the pipeline manually.
public partial class Program { }
