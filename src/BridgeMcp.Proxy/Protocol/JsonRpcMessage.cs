using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeMcp.Protocol;

/// <summary>
/// Discriminator for the kinds of JSON-RPC 2.0 message a wire frame can carry.
/// </summary>
public enum JsonRpcMessageKind
{
    Request,
    Response,
    Notification,
    Batch,
    Unknown,
}

/// <summary>
/// A single JSON-RPC 2.0 message, parsed loosely. The proxy forwards payloads
/// verbatim and therefore never needs to understand the meaning of <c>params</c>,
/// <c>result</c> or <c>error.data</c>; they are kept as raw <see cref="JsonElement"/>
/// so round-tripping is lossless.
/// </summary>
public sealed class JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>Request id for requests/responses/errors; null for notifications.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; init; }

    /// <summary>Classifies the frame after parsing. See <see cref="Classify"/>.</summary>
    [JsonIgnore]
    public JsonRpcMessageKind Kind => Classify(this);

    public bool IsRequest() => Method is not null && Id.HasValue;
    public bool IsNotification() => Method is not null && !Id.HasValue;
    public bool IsResponse() => Id.HasValue && Result.HasValue && Method is null && Error is null;
    public bool IsError() => Id.HasValue && Error is not null && Method is null;

    public static JsonRpcMessageKind Classify(JsonRpcMessage m) => m switch
    {
        { Method: not null, Id: not null } => JsonRpcMessageKind.Request,
        { Method: not null, Id: null } => JsonRpcMessageKind.Notification,
        { Error: not null, Id: not null } => JsonRpcMessageKind.Response, // error is a response
        { Result: not null, Id: not null } => JsonRpcMessageKind.Response,
        _ => JsonRpcMessageKind.Unknown,
    };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
}

/// <summary>
/// Standard JSON-RPC 2.0 error codes as defined by the spec and the MCP layer.
/// </summary>
public static class JsonRpcErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int McpBad = -32000; // MCP-defined server error band start
}
