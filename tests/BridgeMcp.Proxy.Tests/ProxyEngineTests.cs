using System.Text.Json;
using BridgeMcp.Core;
using BridgeMcp.Protocol;
using BridgeMcp.Proxy.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BridgeMcp.Proxy.Tests;

public class ProxyEngineTests
{
    private static JsonRpcMessage Req(object id, string method) => new()
    {
        Id = JsonSerializer.SerializeToElement(id),
        Method = method,
    };

    private static async Task WaitForSentAsync(FakeUpstreamTransport upstream, int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (upstream.Sent.Count < count && !cts.IsCancellationRequested)
                await Task.WhenAny(upstream.WhenRequestSent, Task.Delay(10, cts.Token));
        }
        catch (TaskCanceledException) { }
        Assert.True(upstream.Sent.Count >= count,
            $"Expected {count} sent requests but found {upstream.Sent.Count}.");
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SingleRequestIsForwardedAndResponseReturned()
    {
        var upstream = new FakeUpstreamTransport();
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var frame = Frame.Single(Req(1, "tools/list"));
        var task = engine.ProcessAsync(frame, default);

        await WaitForSentAsync(upstream, 1, Timeout);
        var sent = upstream.Sent[0];
        Assert.Equal("tools/list", sent.Method);

        upstream.DeliverResponse(sent.Id, new JsonRpcMessage
        {
            Id = sent.Id,
            Result = JsonSerializer.SerializeToElement(new { tools = new[] { new { name = "add" } } }),
        });

        var reply = await task;
        Assert.NotNull(reply);
        Assert.False(reply!.IsBatch);
        Assert.True(reply.Message!.IsResponse());
        Assert.Equal("add", reply.Message.Result!.Value.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task NotificationIsForwardedAndReturnsNull()
    {
        var upstream = new FakeUpstreamTransport();
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var frame = Frame.Single(new JsonRpcMessage { Method = "notifications/initialized" });
        var reply = await engine.ProcessAsync(frame, default);

        Assert.Null(reply);
        Assert.Single(upstream.Sent);
        Assert.Equal("notifications/initialized", upstream.Sent[0].Method);
    }

    [Fact]
    public async Task BatchReturnsBatchInOrder()
    {
        var upstream = new FakeUpstreamTransport();
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var reqs = new[] { Req(1, "tools/list"), Req(2, "tools/list"), Req(3, "tools/list") };
        var task = engine.ProcessAsync(Frame.Batch(reqs), default);

        await WaitForSentAsync(upstream, 3, Timeout);

        // Resolve in reverse order to prove ordering is preserved regardless.
        upstream.DeliverResponse(reqs[2].Id, new JsonRpcMessage { Id = reqs[2].Id, Result = JsonSerializer.SerializeToElement(3) });
        upstream.DeliverResponse(reqs[0].Id, new JsonRpcMessage { Id = reqs[0].Id, Result = JsonSerializer.SerializeToElement(1) });
        upstream.DeliverResponse(reqs[1].Id, new JsonRpcMessage { Id = reqs[1].Id, Result = JsonSerializer.SerializeToElement(2) });

        var reply = await task;
        Assert.True(reply!.IsBatch);
        var results = reply.Messages!;
        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].Result!.Value.GetInt32());
        Assert.Equal(2, results[1].Result!.Value.GetInt32());
        Assert.Equal(3, results[2].Result!.Value.GetInt32());
    }

    [Fact]
    public async Task MixedBatchOfRequestsAndNotificationsOnlyRepliesToRequests()
    {
        var upstream = new FakeUpstreamTransport();
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var frame = Frame.Batch(new JsonRpcMessage[]
        {
            Req(1, "tools/list"),
            new() { Method = "notifications/initialized" },
        });
        var task = engine.ProcessAsync(frame, default);

        await WaitForSentAsync(upstream, 2, Timeout);
        upstream.DeliverResponse(upstream.Sent.First(s => s.IsRequest()).Id, new JsonRpcMessage
        {
            Id = upstream.Sent.First(s => s.IsRequest()).Id,
            Result = JsonSerializer.SerializeToElement(new { ok = true }),
        });

        var reply = await task;
        Assert.True(reply!.IsBatch);
        Assert.Single(reply.Messages!);
        Assert.True(reply.Messages![0].IsResponse());
    }

    [Fact]
    public async Task UpstreamErrorIsForwardedAsIs()
    {
        var upstream = new FakeUpstreamTransport();
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var req = Req(42, "tools/call");
        var task = engine.ProcessAsync(Frame.Single(req), default);

        await WaitForSentAsync(upstream, 1, Timeout);
        upstream.DeliverError(req.Id, -32601, "method not found");

        var reply = await task;
        Assert.True(reply!.Message!.IsError());
        Assert.Equal(-32601, reply.Message.Error!.Code);
        Assert.Equal("method not found", reply.Message.Error.Message);
    }

    [Fact]
    public async Task ParseErrorFrameIsReturnedUnchanged()
    {
        var upstream = new FakeUpstreamTransport();
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var errFrame = Frame.FromError(JsonRpcErrors.ParseError, "bad", id: null);
        var reply = await engine.ProcessAsync(errFrame, default);

        Assert.Same(errFrame, reply);
        Assert.Empty(upstream.Sent);
    }

    [Fact]
    public async Task UpstreamExceptionProducesProxyError()
    {
        // A disconnected fake throws immediately from SendRequestAsync, so the
        // engine wraps it into a JSON-RPC error for the client.
        var upstream = new FakeUpstreamTransport { IsConnected = false };
        var engine = new ProxyEngine(upstream, NullLogger<ProxyEngine>.Instance);

        var req = Req(7, "tools/list");
        var reply = await engine.ProcessAsync(Frame.Single(req), default);

        Assert.True(reply!.Message!.IsError());
        Assert.Equal(JsonRpcErrors.InternalError, reply.Message.Error!.Code);
    }
}
