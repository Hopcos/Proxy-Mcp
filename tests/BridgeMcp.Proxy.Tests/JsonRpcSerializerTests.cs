using System.Text.Json;
using BridgeMcp.Protocol;
using BridgeMcp.Transports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BridgeMcp.Proxy.Tests;

public class JsonRpcSerializerTests
{
    private static JsonRpcMessage Make(string json) =>
        JsonRpcSerializer.ParseFrame(System.Text.Encoding.UTF8.GetBytes(json)).Message!;

    [Fact]
    public void ParsesRequestWithNumberId()
    {
        var f = JsonRpcSerializer.ParseFrame("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""u8.ToArray());
        Assert.False(f.IsBatch);
        var m = f.Message!;
        Assert.True(m.IsRequest());
        Assert.Equal("tools/list", m.Method);
        Assert.Equal("1", m.Id!.Value.GetRawText());
    }

    [Fact]
    public void ParsesRequestWithStringId()
    {
        var m = Make("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"x"}}""");
        Assert.True(m.IsRequest());
        Assert.Equal("abc", m.Id!.Value.GetString());
    }

    [Fact]
    public void ParsesNotification()
    {
        var m = Make("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.True(m.IsNotification());
        Assert.Null(m.Id);
    }

    [Fact]
    public void ParsesResponse()
    {
        var m = Make("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""");
        Assert.True(m.IsResponse());
        Assert.False(m.IsError());
    }

    [Fact]
    public void ParsesError()
    {
        var m = Make("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"not found"}}""");
        Assert.True(m.IsError());
        Assert.Equal(-32601, m.Error!.Code);
    }

    [Fact]
    public void ParsesBatch()
    {
        var f = JsonRpcSerializer.ParseFrame(
            """[{"jsonrpc":"2.0","id":1,"method":"a"},{"jsonrpc":"2.0","id":2,"method":"b"}]"""u8.ToArray());
        Assert.True(f.IsBatch);
        Assert.Equal(2, f.Messages!.Count);
    }

    [Fact]
    public void InvalidJsonYieldsParseError()
    {
        var f = JsonRpcSerializer.ParseFrame("not json"u8.ToArray());
        Assert.True(f.IsError);
        Assert.Equal(JsonRpcErrors.ParseError, f.Message!.Error!.Code);
    }

    [Fact]
    public void InvalidRequestYieldsInvalidRequestError()
    {
        // Missing method and id — not a valid JSON-RPC message.
        var f = JsonRpcSerializer.ParseFrame("""{"jsonrpc":"2.0","foo":"bar"}"""u8.ToArray());
        Assert.True(f.IsError);
        Assert.Equal(JsonRpcErrors.InvalidRequest, f.Message!.Error!.Code);
    }

    [Fact]
    public void EmptyBatchIsInvalid()
    {
        var f = JsonRpcSerializer.ParseFrame("[]"u8.ToArray());
        Assert.True(f.IsError);
        Assert.Equal(JsonRpcErrors.InvalidRequest, f.Message!.Error!.Code);
    }

    [Fact]
    public void RoundTripsSingleMessageLosslessly()
    {
        var m = Make("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"add","arguments":{"a":1,"b":2}}}""");
        var json = JsonRpcSerializer.Serialize(m);
        var m2 = JsonRpcSerializer.ParseFrame(System.Text.Encoding.UTF8.GetBytes(json)).Message!;
        Assert.Equal("tools/call", m2.Method);
        Assert.Equal("add", m2.Params!.Value.GetProperty("name").GetString());
    }
}
