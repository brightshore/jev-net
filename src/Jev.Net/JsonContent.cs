using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Jev.Net;

/// <summary>
/// Text, a JSON object, or a JSON array — the shape the API accepts for <c>state</c>, a question's
/// <c>instructions</c>, and every criteria description. A bare number, boolean or top-level null is not
/// content (nulls remain valid NESTED inside an object or array).
///
/// <para>Converts implicitly from <see cref="string"/> and from <see cref="JsonNode"/>
/// (<see cref="JsonObject"/>, <see cref="JsonArray"/>); <c>JsonContent.From</c> serializes anything else.
/// The node is deep-cloned on the way in and again on the way out, so neither the caller's tree nor a
/// question that is reused across requests is ever re-parented.</para>
/// </summary>
public sealed class JsonContent
{
    private readonly JsonNode? _node;

    private JsonContent(JsonNode? node) => _node = node;

    /// <summary>
    /// An explicit JSON <c>null</c>. Only meaningful where the wire format allows one: a choice label
    /// left undescribed, or a noul criteria outcome sent as null rather than omitted.
    /// </summary>
    public static JsonContent Null { get; } = new(null);

    /// <summary>Whether this is <see cref="Null"/>.</summary>
    public bool IsNull => _node is null;

    /// <summary>Text content.</summary>
    public static implicit operator JsonContent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new JsonContent(JsonValue.Create(text));
    }

    /// <summary>Object or array content (or a string value). Anything else is rejected.</summary>
    public static implicit operator JsonContent(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return FromNode(node);
    }

    /// <summary>
    /// Serialize <paramref name="value"/> (a dictionary, a list, a record, an anonymous type…) into content.
    /// </summary>
    /// <remarks>Reflection-based. In a trimmed or Native AOT app use the <see cref="JsonTypeInfo{T}"/> overload,
    /// or build a <see cref="JsonObject"/>/<see cref="JsonArray"/> directly.</remarks>
    /// <exception cref="TypeSafeException">The value cannot be encoded as JSON, or encodes to something
    /// that is not text, an object, or an array.</exception>
    [RequiresUnreferencedCode(TypeSafeClient.ReflectionJson)]
    [RequiresDynamicCode(TypeSafeClient.ReflectionJson)]
    public static JsonContent From<T>(T value, JsonSerializerOptions? options = null)
    {
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(value, options ?? Json.Relaxed);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new TypeSafeException("The request body could not be encoded as JSON", error);
        }

        if (node is null)
        {
            throw new TypeSafeException("Content must be text, a JSON object, or a JSON array; got null.");
        }

        return FromNode(node);
    }

    /// <summary>Serialize <paramref name="value"/> with source-generated metadata — trim- and AOT-safe.</summary>
    /// <exception cref="TypeSafeException">The value encodes to something that is not text, an object, or an array.</exception>
    public static JsonContent From<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(value, typeInfo);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new TypeSafeException("The request body could not be encoded as JSON", error);
        }

        return node is null
            ? throw new TypeSafeException("Content must be text, a JSON object, or a JSON array; got null.")
            : FromNode(node);
    }

    /// <summary>A detached copy of the underlying node; null for <see cref="Null"/>.</summary>
    public JsonNode? ToNode() => _node?.DeepClone();

    /// <inheritdoc />
    public override string ToString() => _node is null ? "null" : Json.Write(_node);

    private static JsonContent FromNode(JsonNode node)
    {
        var kind = node.GetValueKind();
        if (kind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new TypeSafeException($"Content must be text, a JSON object, or a JSON array; got {kind}.");
        }

        return new JsonContent(node.DeepClone());
    }
}

/// <summary>Shared serializer settings.</summary>
internal static class Json
{
    /// <summary>Compact output that leaves non-ASCII text (and quotes inside strings) readable.</summary>
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A node as compact JSON, through a writer rather than a serializer — no type metadata involved,
    /// so it behaves identically under trimming and Native AOT.</summary>
    public static byte[] WriteBytes(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Writer))
        {
            node.WriteTo(writer);
        }

        return buffer.ToArray();
    }

    public static string Write(JsonNode node) => System.Text.Encoding.UTF8.GetString(WriteBytes(node));

    /// <summary>Lenient response decoding: empty → null, JSON → a node, anything else → the text itself.</summary>
    public static JsonNode? Deserialize(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(content);
            // Force materialization so a duplicate-key object fails HERE, inside the try, not at first access.
            if (node is not null) _ = WriteBytes(node);
            return node;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return JsonValue.Create(System.Text.Encoding.UTF8.GetString(content));
        }
    }
}
