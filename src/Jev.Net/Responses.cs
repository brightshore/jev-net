using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jev.Net;

/// <summary>A snapshot of the HTTP response an SDK object was decoded from.</summary>
public sealed class RawHttpResponse
{
    internal RawHttpResponse(int statusCode, IReadOnlyDictionary<string, string> headers, byte[] content, string? endpoint)
    {
        StatusCode = statusCode;
        Headers = headers;
        Content = content;
        Endpoint = endpoint;
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Response headers (case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The response body, as received.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>The request method and URL, without credentials, query or fragment, when known.</summary>
    public string? Endpoint { get; }

    internal bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The body parsed as JSON — including any field this SDK version does not model.</summary>
    public JsonNode? Json() => JsonNode.Parse(Content.Span);

    internal TypeSafeApiResponseValidationException Invalid(string path, Exception? cause = null) =>
        new(StatusCode, Jev.Net.Json.Deserialize(Content.Span), Headers, path, Endpoint, cause);
}

/// <summary>A response object with its originating HTTP response attached.</summary>
public abstract class TypeSafeResponse
{
    private RawHttpResponse? _raw;

    /// <summary>The <c>x-typesafe-request-id</c> response header.</summary>
    /// <exception cref="TypeSafeException">The response carried none.</exception>
    [JsonIgnore]
    public string RequestId =>
        _raw is not null && _raw.Headers.TryGetValue(Protocol.RequestIdHeader, out var id)
            ? id
            : throw new TypeSafeException("The response did not include a request ID.");

    /// <summary>The underlying HTTP response: status, headers, and body.</summary>
    /// <exception cref="TypeSafeException">This object was not decoded from an HTTP response.</exception>
    [JsonIgnore]
    public RawHttpResponse RawHttpResponse =>
        _raw ?? throw new TypeSafeException("The response was not created from a raw HTTP response.");

    internal void Attach(RawHttpResponse raw) => _raw = raw;
}

/// <summary>
/// Answers grouped by question type, with model and usage metadata.
///
/// <para>Derive from this and declare <see cref="NoulAnswer"/> / <see cref="ChoiceAnswer"/> /
/// <see cref="ScoreAnswer"/> properties to get named, typed answers back from
/// <see cref="TypeSafeClient.SystemOneAsync{TResponse}"/>: each property is filled from the answer of the same
/// name (its <see cref="JsonPropertyNameAttribute"/>, else its name — exact, case-insensitive, then snake_case).</para>
/// </summary>
public class SystemOneResponse : TypeSafeResponse
{
    private IReadOnlyDictionary<string, NoulAnswer>? _nouls;
    private IReadOnlyDictionary<string, ChoiceAnswer>? _choices;
    private IReadOnlyDictionary<string, ScoreAnswer>? _scores;

    /// <summary>The model that answered; may differ from the alias supplied in the request.</summary>
    public string Model { get; internal set; } = "";

    /// <summary>Token usage for the request.</summary>
    public Usage Usage { get; internal set; } = new();

    /// <summary>All answer objects keyed by question name. Answer types this SDK does not model are absent
    /// (they remain readable through <see cref="TypeSafeResponse.RawHttpResponse"/>).</summary>
    public IReadOnlyDictionary<string, Answer> Answers { get; internal set; } = new Dictionary<string, Answer>();

    /// <summary>Yes/no answers keyed by question name.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, NoulAnswer> Nouls => _nouls ??= Group<NoulAnswer>();

    /// <summary>Choice answers keyed by question name.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ChoiceAnswer> Choices => _choices ??= Group<ChoiceAnswer>();

    /// <summary>Score answers keyed by question name.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ScoreAnswer> Scores => _scores ??= Group<ScoreAnswer>();

    private Dictionary<string, T> Group<T>() where T : Answer =>
        Answers.Where(pair => pair.Value is T).ToDictionary(pair => pair.Key, pair => (T)pair.Value);
}

/// <summary>The models available to the account.</summary>
public sealed class ListModelsResponse : TypeSafeResponse
{
    /// <summary>The available models.</summary>
    public IReadOnlyList<ModelMetadata> Models { get; internal set; } = [];
}

/// <summary>
/// Turns a successful body into a response object, strictly: a string where a number belongs is an error, not
/// a coercion, and the FIRST bad field is named by its dotted path. Hand-rolled over <see cref="JsonElement"/>
/// because those paths are the contract — a serializer's exception text is not.
/// </summary>
internal static class ResponseDecoder
{
    private static readonly JsonSerializerOptions CustomModel = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static T Parse<T>(RawHttpResponse raw, ILogger logger) where T : class
    {
        if (!raw.IsSuccess)
        {
            throw ErrorMessages.ApiError(raw.StatusCode, Json.Deserialize(raw.Content.Span), raw.Headers, raw.Endpoint);
        }

        object result;
        if (typeof(T) == typeof(ListModelsResponse))
        {
            result = Models(raw);
        }
        else if (typeof(SystemOneResponse).IsAssignableFrom(typeof(T)))
        {
            result = SystemOne(raw, typeof(T), logger);
        }
        else
        {
            result = Custom<T>(raw);
        }

        if (result is TypeSafeResponse attached)
        {
            attached.Attach(raw);
        }

        return (T)result;
    }

    private static JsonDocument Document(RawHttpResponse raw)
    {
        try
        {
            return JsonDocument.Parse(raw.Content);
        }
        catch (JsonException error)
        {
            throw raw.Invalid("", error);
        }
    }

    private static ListModelsResponse Models(RawHttpResponse raw)
    {
        using var document = Document(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw raw.Invalid("");
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) throw raw.Invalid("models");

        var list = new List<ModelMetadata>();
        var index = 0;
        foreach (var card in models.EnumerateArray())
        {
            var at = $"models[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (card.ValueKind != JsonValueKind.Object) throw raw.Invalid(at);
            list.Add(new ModelMetadata(
                String(raw, card, "name", at), String(raw, card, "description", at), String(raw, card, "release_date", at)));
            index++;
        }

        return new ListModelsResponse { Models = list };
    }

    private static SystemOneResponse SystemOne(RawHttpResponse raw, Type type, ILogger logger)
    {
        using var document = Document(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw raw.Invalid("");

        var model = String(raw, root, "model", "");
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            throw raw.Invalid("usage");
        }

        var usage = new Usage(OptionalInt(raw, usageElement, "input_tokens", "usage"), OptionalInt(raw, usageElement, "output_tokens", "usage"));

        var answers = new Dictionary<string, Answer>();
        if (root.TryGetProperty("answers", out var answersElement))
        {
            if (answersElement.ValueKind != JsonValueKind.Object) throw raw.Invalid("answers");
            foreach (var entry in answersElement.EnumerateObject())
            {
                var at = $"answers.{entry.Name}";
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("type", out var tag) || tag.ValueKind != JsonValueKind.String)
                {
                    throw raw.Invalid($"{at}.type");
                }

                switch (tag.GetString())
                {
                    case "noul":
                        answers[entry.Name] = new NoulAnswer(Number(raw, entry.Value, "noul", at));
                        break;
                    case "choice":
                        answers[entry.Name] = new ChoiceAnswer(
                            String(raw, entry.Value, "choice", at),
                            Number(raw, entry.Value, "confidence", at),
                            Map<string, double>(raw, entry.Value, "probabilities", at, key => key, (element, path) => Number(raw, element, path)));
                        break;
                    case "score":
                        answers[entry.Name] = new ScoreAnswer(
                            Number(raw, entry.Value, "score", at),
                            Number(raw, entry.Value, "confidence", at),
                            Map<int, JsonNode>(raw, entry.Value, "legend", at, IntKey, (element, path) => Description(raw, element, path)),
                            Map<int, double>(raw, entry.Value, "probabilities", at, IntKey, (element, path) => Number(raw, element, path)));
                        break;
                    default:
                        // Forward-compat: skip answer kinds a future API adds rather than failing the response.
                        logger.LogWarning("Ignoring answer {Name} with unrecognized type {Type}", entry.Name, tag.GetString());
                        break;
                }
            }
        }

        SystemOneResponse response;
        try
        {
            response = (SystemOneResponse)Activator.CreateInstance(type, nonPublic: true)!;
        }
        catch (MissingMethodException error)
        {
            throw new TypeSafeException($"{type.Name} needs a parameterless constructor to be used as a response type.", error);
        }

        response.Model = model;
        response.Usage = usage;
        response.Answers = answers;
        Lift(raw, response, answers);
        return response;
    }

    /// <summary>Fill a derived response's typed answer properties from the answers of the same name.</summary>
    private static void Lift(RawHttpResponse raw, SystemOneResponse response, Dictionary<string, Answer> answers)
    {
        var type = response.GetType();
        if (type == typeof(SystemOneResponse)) return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(Answer).IsAssignableFrom(property.PropertyType) || property.SetMethod is null) continue;

            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            var key = answers.ContainsKey(name) ? name
                : answers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                ?? answers.Keys.FirstOrDefault(k => k == JsonNamingPolicy.SnakeCaseLower.ConvertName(name));

            if (key is null || !property.PropertyType.IsInstanceOfType(answers[key]))
            {
                var nullable = new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;
                if (key is null && nullable) continue;
                throw raw.Invalid(key ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(name));
            }

            property.SetValue(response, answers[key]);
        }
    }

    private static T Custom<T>(RawHttpResponse raw) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw.Content.Span, CustomModel) ?? throw raw.Invalid("");
        }
        catch (JsonException error)
        {
            throw raw.Invalid(FieldPath(error.Path), error);
        }
    }

    /// <summary><c>$.models[1].name</c> → <c>models[1].name</c>.</summary>
    internal static string FieldPath(string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || jsonPath == "$") return "";
        var path = jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');
        return path;
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";

    private static string String(RawHttpResponse raw, JsonElement parent, string name, string prefix) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw raw.Invalid(Join(prefix, name));

    private static double Number(RawHttpResponse raw, JsonElement parent, string name, string prefix) =>
        parent.TryGetProperty(name, out var value) ? Number(raw, value, Join(prefix, name)) : throw raw.Invalid(Join(prefix, name));

    private static double Number(RawHttpResponse raw, JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : throw raw.Invalid(path);

    private static int? OptionalInt(RawHttpResponse raw, JsonElement parent, string name, string prefix)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw raw.Invalid(Join(prefix, name));
    }

    private static JsonNode Description(RawHttpResponse raw, JsonElement value, string path) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array
            ? JsonNode.Parse(value.GetRawText())!
            : throw raw.Invalid(path);

    private static object? IntKey(string key) =>
        int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var level) ? level : null;

    private static Dictionary<TKey, TValue> Map<TKey, TValue>(
        RawHttpResponse raw, JsonElement parent, string name, string prefix,
        Func<string, object?> key, Func<JsonElement, string, TValue> read) where TKey : notnull
    {
        var at = Join(prefix, name);
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object) throw raw.Invalid(at);

        var map = new Dictionary<TKey, TValue>();
        foreach (var entry in element.EnumerateObject())
        {
            var path = $"{at}.{entry.Name}";
            if (key(entry.Name) is not TKey typed) throw raw.Invalid(path);
            map[typed] = read(entry.Value, path);
        }

        return map;
    }
}
