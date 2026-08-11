using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeMcp.Protocol;

/// <summary>
/// Helpers for parsing and serializing JSON-RPC 2.0 wire frames, including
/// batch arrays. The proxy must preserve unknown fields and ordering, so it
/// works against the <see cref="JsonDocument"/> / <see cref="JsonElement"/>
/// representation rather than strongly-typed DTOs.
/// </summary>
public static class JsonRpcSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Preserve numbers exactly (no int/double coercion) and keep unknown members.
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses a raw JSON byte buffer into a frame result. A frame is either a
    /// single JSON-RPC message or a JSON array of messages (a batch).
    /// </summary>
    public static Frame ParseFrame(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException)
        {
            return Frame.FromError(JsonRpcErrors.ParseError, "Parse error: Invalid JSON", id: null);
        }

        try
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var messages = new List<JsonRpcMessage>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var parsed = el.Deserialize<JsonRpcMessage>(Options);
                    if (parsed is null || parsed.Kind == JsonRpcMessageKind.Unknown)
                    {
                        return Frame.FromError(JsonRpcErrors.InvalidRequest, "Invalid Request", id: null);
                    }
                    messages.Add(parsed);
                }
                if (messages.Count == 0)
                {
                    // An empty batch is itself invalid per JSON-RPC 2.0.
                    return Frame.FromError(JsonRpcErrors.InvalidRequest, "Invalid Request: empty batch", id: null);
                }
                return Frame.Batch(messages);
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var single = doc.RootElement.Deserialize<JsonRpcMessage>(Options);
                if (single is null || single.Kind == JsonRpcMessageKind.Unknown)
                {
                    return Frame.FromError(JsonRpcErrors.InvalidRequest, "Invalid Request", id: single?.Id);
                }
                return Frame.Single(single);
            }

            return Frame.FromError(JsonRpcErrors.InvalidRequest, "Invalid Request", id: null);
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// <summary>Serializes a single message back to its canonical JSON form.</summary>
    public static string Serialize(JsonRpcMessage message) =>
        JsonSerializer.Serialize(message, Options);

    /// <summary>Serializes a batch (array) of messages.</summary>
    public static string SerializeBatch(IEnumerable<JsonRpcMessage> messages) =>
        JsonSerializer.Serialize(messages, Options);

    /// <summary>Serializes any frame (single or batch) to wire JSON.</summary>
    public static string SerializeFrame(Frame frame) => frame switch
    {
        { IsBatch: true, Messages: var msgs } when msgs is not null => SerializeBatch(msgs),
        { Message: var m } when m is not null => Serialize(m),
        _ => string.Empty,
    };
}

/// <summary>
/// A parsed JSON-RPC frame: either a single message or a batch of messages.
/// A parse/invalid-request failure is represented as an error frame.
/// </summary>
public sealed class Frame
{
    public bool IsBatch { get; private init; }
    public bool IsError { get; private init; }
    public JsonRpcMessage? Message { get; private init; }
    public IReadOnlyList<JsonRpcMessage>? Messages { get; private init; }

    public static Frame Single(JsonRpcMessage m) => new() { Message = m };
    public static Frame Batch(IReadOnlyList<JsonRpcMessage> ms) => new() { IsBatch = true, Messages = ms };

    public static Frame FromError(int code, string message, JsonElement? id) => new()
    {
        IsError = true,
        Message = new JsonRpcMessage
        {
            Error = new JsonRpcError { Code = code, Message = message },
            Id = id,
        },
    };

    /// <summary>All messages in the frame (a batch yields many, a single yields one).</summary>
    public IEnumerable<JsonRpcMessage> AllMessages =>
        IsBatch ? Messages! : Message is not null ? new[] { Message } : Array.Empty<JsonRpcMessage>();

    /// <summary>Requests in this frame (have a method and an id) that expect a response.</summary>
    public IEnumerable<JsonRpcMessage> Requests => AllMessages.Where(m => m.IsRequest());

    /// <summary>Notifications in this frame (have a method, no id) that expect no response.</summary>
    public IEnumerable<JsonRpcMessage> Notifications => AllMessages.Where(m => m.IsNotification());
}
