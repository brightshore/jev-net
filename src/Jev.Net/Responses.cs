using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jev.Net;

/// <summary>A snapshot of the HTTP response an SDK object was decoded from.</summary>
public sealed class RawHttpResponse
{
    /// <summary>
    /// Build a response snapshot by hand — for a cache, a replay layer, or a test that wants a real
    /// <see cref="SystemOneResponse"/> without a network. Header lookup is case-insensitive.
    /// </summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="headers">Response headers; null for none.</param>
    /// <param name="content">The response body, as received.</param>
    /// <param name="endpoint">The request method and URL for error messages, e.g. <c>POST https://…/v1/systemone</c>.</param>
    public RawHttpResponse(
        int statusCode, IReadOnlyDictionary<string, string>? headers, ReadOnlyMemory<byte> content, string? endpoint = null)
    {
        StatusCode = statusCode;
        Headers = headers is null
            ? HttpHeaderMap.Empty
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        Content = content;
        Endpoint = endpoint;
    }

    /// <summary>Snapshot an <see cref="HttpResponseMessage"/>: status, headers and the fully-read body. The
    /// message is not disposed; the endpoint is taken from its request, without credentials, query or fragment.</summary>
    public static async Task<RawHttpResponse> FromAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var request = response.RequestMessage;
        var endpoint = request?.RequestUri is { IsAbsoluteUri: true } uri
            ? $"{request.Method.Method} {uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)}"
            : null;
        return new RawHttpResponse((int)response.StatusCode, HttpHeaderMap.From(response), body, endpoint);
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

    internal int? StatusCode => _raw?.StatusCode;

    internal string? RequestIdOrNull => _raw is not null && _raw.Headers.TryGetValue(Protocol.RequestIdHeader, out var id) ? id : null;
}

/// <summary>
/// Answers grouped by question type, with model and usage metadata.
///
/// <para>Derive from this and declare <see cref="NoulAnswer"/> / <see cref="ChoiceAnswer"/> /
/// <see cref="ScoreAnswer"/> properties to get named, typed answers back from
/// <c>SystemOneAsync&lt;TResponse&gt;</c>: each property is filled from the answer of the same name (its
/// <see cref="JsonPropertyNameAttribute"/>, else its name — exact, case-insensitive, then snake_case). Every such
/// property is REQUIRED unless it carries <see cref="OptionalAnswerAttribute"/>.</para>
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

    /// <summary>
    /// Answers whose <c>type</c> this SDK version does not model, keyed by question name, exactly as the API
    /// sent them — the receiving half of <see cref="RawQuestion"/>. They are deliberately NOT in
    /// <see cref="Answers"/>, which matches the Python SDK's <c>answers</c> (it omits them too).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, JsonObject> UnmodeledAnswers { get; internal set; } = new Dictionary<string, JsonObject>();

    /// <summary>
    /// Decode a response you already hold — from a cache, a recording, or your own HTTP call — with the same
    /// validation the client applies. A non-2xx status throws the matching <see cref="TypeSafeApiException"/>;
    /// a body that does not fit throws <see cref="TypeSafeApiResponseValidationException"/>.
    /// </summary>
    public static SystemOneResponse FromHttpResponse(RawHttpResponse response) => FromHttpResponse<SystemOneResponse>(response);

    /// <summary><see cref="FromHttpResponse(RawHttpResponse)"/> into your own <see cref="SystemOneResponse"/> subclass.</summary>
    public static TResponse FromHttpResponse<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TResponse>(
        RawHttpResponse response) where TResponse : SystemOneResponse, new()
    {
        ArgumentNullException.ThrowIfNull(response);
        return ResponseDecoder.Parse(response, raw => ResponseDecoder.SystemOne<TResponse>(raw, NullLogger.Instance), TimeProvider.System);
    }

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

/// <summary>
/// Marks an answer property on a <see cref="SystemOneResponse"/> subclass as optional: left null when the
/// response has no answer of that name, instead of failing validation.
/// </summary>
/// <remarks>An attribute rather than "is the property nullable?", because nullability metadata is exactly what
/// the trimmer removes — the same class would validate differently in a Native AOT build.</remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptionalAnswerAttribute : Attribute;

/// <summary>Ready-made serializer settings for reading a response body into your own type.</summary>
public static class ResponseJson
{
    /// <summary>Case-insensitive, <c>snake_case</c> property names — how the API spells its fields.</summary>
    public static JsonSerializerOptions SnakeCase { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

/// <summary>The models available to the account.</summary>
public sealed class ListModelsResponse : TypeSafeResponse
{
    /// <summary>The available models.</summary>
    public IReadOnlyList<ModelMetadata> Models { get; internal set; } = [];

    /// <summary>Decode a models response you already hold, with the client's own validation and error mapping.</summary>
    public static ListModelsResponse FromHttpResponse(RawHttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ResponseDecoder.Parse(response, ResponseDecoder.Models, TimeProvider.System);
    }
}

/// <summary>
/// Turns a successful body into a response object, strictly: a string where a number belongs is an error, not
/// a coercion, and the FIRST bad field is named by its dotted path. Hand-rolled over <see cref="JsonElement"/>
/// because those paths are the contract — a serializer's exception text is not.
/// </summary>
internal static class ResponseDecoder
{
    /// <summary>The envelope every endpoint shares: a non-2xx is the matching API exception; otherwise decode,
    /// then hang the raw HTTP response on anything that can carry it.</summary>
    public static T Parse<T>(RawHttpResponse raw, Func<RawHttpResponse, T> decode, TimeProvider clock) where T : class
    {
        if (!raw.IsSuccess)
        {
            throw ErrorMessages.ApiError(raw.StatusCode, Json.Deserialize(raw.Content.Span), raw.Headers, raw.Endpoint, clock);
        }

        var result = decode(raw);
        if (result is TypeSafeResponse attached)
        {
            attached.Attach(raw);
        }

        return result;
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

    public static ListModelsResponse Models(RawHttpResponse raw)
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

    public static T SystemOne<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        RawHttpResponse raw, ILogger logger) where T : SystemOneResponse, new()
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
        var unmodeled = new Dictionary<string, JsonObject>();
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
                        unmodeled[entry.Name] = JsonNode.Parse(entry.Value.GetRawText())!.AsObject();
                        break;
                }
            }
        }

        var response = new T();
        response.Model = model;
        response.Usage = usage;
        response.Answers = answers;
        response.UnmodeledAnswers = unmodeled;
        Lift(raw, response, answers);
        return response;
    }

    /// <summary>Fill a derived response's typed answer properties from the answers of the same name.</summary>
    private static void Lift<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        RawHttpResponse raw, T response, Dictionary<string, Answer> answers) where T : SystemOneResponse
    {
        if (typeof(T) == typeof(SystemOneResponse)) return;

        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(Answer).IsAssignableFrom(property.PropertyType) || property.SetMethod is null) continue;

            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            var key = answers.ContainsKey(name) ? name
                : answers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                ?? answers.Keys.FirstOrDefault(k => k == JsonNamingPolicy.SnakeCaseLower.ConvertName(name));

            if (key is null || !property.PropertyType.IsInstanceOfType(answers[key]))
            {
                if (key is null && property.IsDefined(typeof(OptionalAnswerAttribute), inherit: true)) continue;
                throw raw.Invalid(key ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(name));
            }

            property.SetValue(response, answers[key]);
        }
    }

    public static T Custom<T>(RawHttpResponse raw, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(raw.Content.Span, typeInfo) ?? throw raw.Invalid("");
        }
        catch (JsonException error)
        {
            throw raw.Invalid(FieldPath(error.Path), error);
        }
    }

    [RequiresUnreferencedCode(TypeSafeClient.ReflectionJson)]
    [RequiresDynamicCode(TypeSafeClient.ReflectionJson)]
    public static T Custom<T>(RawHttpResponse raw, JsonSerializerOptions options) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw.Content.Span, options) ?? throw raw.Invalid("");
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
