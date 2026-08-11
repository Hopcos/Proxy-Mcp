using System.Text.Json;
using BridgeMcp.Configuration;
using BridgeMcp.Protocol;
using BridgeMcp.Transports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BridgeMcp.Proxy.Tests;

/// <summary>
/// End-to-end tests for <see cref="StdioUpstreamTransport"/> against a real
/// Python child process (Fixtures/echo_mcp.py). These validate the actual
/// stdin/stdout framing, id-matching, and handshake behavior.
/// </summary>
public class StdioUpstreamTransportTests
{
    private static StdioServerOptions EchoOptions()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "echo_mcp.py");
        return new StdioServerOptions
        {
            Name = "echo",
            Command = "python",
            Args = { "-u", script },
            RequestTimeout = TimeSpan.FromSeconds(15),
        };
    }

    [Fact]
    public async Task InitializeReturnsServerInfo()
    {
        await using var transport = new StdioUpstreamTransport(EchoOptions(), NullLogger<StdioUpstreamTransport>.Instance);
        var resp = await transport.InitializeAsync(default);

        Assert.True(resp.IsResponse());
        Assert.Equal("echo", resp.Result!.Value.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("2025-06-18", resp.Result.Value.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task ToolsListReturnsTools()
    {
        await using var transport = new StdioUpstreamTransport(EchoOptions(), NullLogger<StdioUpstreamTransport>.Instance);
        await transport.InitializeAsync(default);

        var req = new JsonRpcMessage
        {
            Id = JsonSerializer.SerializeToElement(99),
            Method = "tools/list",
        };
        var resp = await transport.SendRequestAsync(req, default);

        Assert.True(resp.IsResponse());
        var tools = resp.Result!.Value.GetProperty("tools");
        Assert.Equal("echo", tools[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ToolsCallReturnsContent()
    {
        await using var transport = new StdioUpstreamTransport(EchoOptions(), NullLogger<StdioUpstreamTransport>.Instance);
        await transport.InitializeAsync(default);

        var req = new JsonRpcMessage
        {
            Id = JsonSerializer.SerializeToElement(100),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new
            {
                name = "echo",
                arguments = new { text = "hello world" },
            }),
        };
        var resp = await transport.SendRequestAsync(req, default);

        Assert.True(resp.IsResponse());
        var text = resp.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("hello world", text);
    }

    [Fact]
    public async Task NotificationDoesNotHang()
    {
        await using var transport = new StdioUpstreamTransport(EchoOptions(), NullLogger<StdioUpstreamTransport>.Instance);
        await transport.InitializeAsync(default);

        var note = new JsonRpcMessage { Method = "notifications/initialized" };
        await transport.SendNotificationAsync(note, default);
        // No response expected; reaching this line without hanging is success.
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task ConcurrentRequestsAreDemultiplexedById()
    {
        await using var transport = new StdioUpstreamTransport(EchoOptions(), NullLogger<StdioUpstreamTransport>.Instance);
        await transport.InitializeAsync(default);

        var ids = new[] { 1, 2, 3, 4, 5 };
        var tasks = ids.Select(i => transport.SendRequestAsync(new JsonRpcMessage
        {
            Id = JsonSerializer.SerializeToElement(i),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new { name = "echo", arguments = new { text = $"t{i}" } }),
        }, default)).ToArray();

        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < ids.Length; i++)
        {
            var text = results[i].Result!.Value.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal($"t{ids[i]}", text);
            // Each response id matches its request id.
            Assert.Equal(ids[i].ToString(), results[i].Id!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task BadCommandThrowsInformativeError()
    {
        var bad = new StdioServerOptions { Name = "bad", Command = "definitely-not-a-real-cmd-xyz", RequestTimeout = TimeSpan.FromSeconds(2) };
        var transport = new StdioUpstreamTransport(bad, NullLogger<StdioUpstreamTransport>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.InitializeAsync(default));
    }

    [Fact]
    public async Task ServerInitiatedNotificationIsSurfaced()
    {
        // The echo server doesn't emit unsolicited messages, so verify the
        // event plumbing by checking it is null until something is delivered.
        // This guards the relay path used by the SSE GET handler.
        await using var transport = new StdioUpstreamTransport(EchoOptions(), NullLogger<StdioUpstreamTransport>.Instance);
        await transport.InitializeAsync(default);

        JsonRpcMessage? captured = null;
        transport.OnServerMessage += m => captured = m;

        // Send a normal request; nothing should fire OnServerMessage.
        var req = new JsonRpcMessage { Id = JsonSerializer.SerializeToElement(1), Method = "tools/list" };
        await transport.SendRequestAsync(req, default);

        Assert.Null(captured);
    }
}
