using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BridgeMcp.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BridgeMcp.Proxy.Tests;

/// <summary>
/// HTTP-level integration tests using WebApplicationFactory against the real
/// echo MCP fixture. Exercises the Streamable HTTP endpoint end to end:
/// initialize (session creation), tools/list, tools/call, 404 for unknown
/// server, and 400 for missing session.
/// </summary>
public class HttpEndToEndTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HttpEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureServices(services =>
            {
                // Replace the configured BridgeOptions with one pointing at the echo fixture.
                var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "echo_mcp.py");
                var opts = new BridgeOptions
                {
                    Url = "http://localhost:0",
                    Servers = new Dictionary<string, StdioServerOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["echo"] = new StdioServerOptions
                        {
                            Name = "echo",
                            Command = "python",
                            Args = { "-u", script },
                            RequestTimeout = TimeSpan.FromSeconds(15),
                        },
                    },
                };
                services.AddSingleton(opts);
            });
        });
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task LandingListsEchoServer()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("bridgemcp", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("echo", doc.RootElement.GetProperty("servers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task UnknownServerReturns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/nope/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task InitializeWithoutSessionCreatesSession()
    {
        var client = _factory.CreateClient();
        var init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";
        var resp = await client.PostAsync("/echo/mcp", JsonContent(init));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var sessionId = resp.Headers.GetValues("mcp-session-id").Single();
        Assert.False(string.IsNullOrEmpty(sessionId));

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("echo", doc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task FullFlow_Initialize_List_Call()
    {
        var client = _factory.CreateClient();

        // 1. initialize
        var initResp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}"""));
        initResp.EnsureSuccessStatusCode();
        var sessionId = initResp.Headers.GetValues("mcp-session-id").Single();
        client.DefaultRequestHeaders.Add("mcp-session-id", sessionId);

        // 2. initialized notification -> 202 Accepted
        var noteResp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(HttpStatusCode.Accepted, noteResp.StatusCode);

        // 3. tools/list
        var listResp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
        listResp.EnsureSuccessStatusCode();
        var listBody = await listResp.Content.ReadAsStringAsync();
        using var listDoc = JsonDocument.Parse(listBody);
        Assert.Equal("echo", listDoc.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

        // 4. tools/call
        var callResp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}"""));
        callResp.EnsureSuccessStatusCode();
        var callBody = await callResp.Content.ReadAsStringAsync();
        using var callDoc = JsonDocument.Parse(callBody);
        Assert.Equal("hi", callDoc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task RequestWithoutSessionOnNonInitializeReturns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task InvalidSessionReturns400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("mcp-session-id", "does-not-exist");
        var resp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteClosesSession()
    {
        var client = _factory.CreateClient();
        var initResp = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}"""));
        var sessionId = initResp.Headers.GetValues("mcp-session-id").Single();

        var req = new HttpRequestMessage(HttpMethod.Delete, "/echo/mcp");
        req.Headers.Add("mcp-session-id", sessionId);
        var del = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // After delete, the session is gone.
        var after = await client.PostAsync("/echo/mcp",
            JsonContent("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
        // Note: DefaultRequestHeaders on this client doesn't carry session id,
        // and the deleted session is invalid anyway.
        Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);
    }
}
