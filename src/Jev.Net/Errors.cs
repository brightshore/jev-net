using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jev.Net;

/// <summary>Base exception for SDK failures.</summary>
public class TypeSafeException : Exception
{
    public TypeSafeException(string message) : base(message) { }

    public TypeSafeException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>An unsuccessful HTTP response with its body and request metadata.</summary>
public class TypeSafeApiException : TypeSafeException
{
    /// <summary>Describe an HTTP failure, with an optional message override.</summary>
    /// <param name="status">HTTP response status code.</param>
    /// <param name="body">The server's JSON error body, plain response text (as a string value), or null for an empty body.</param>
    /// <param name="headers">HTTP response headers.</param>
    /// <param name="message">Overrides the message derived from <paramref name="body"/>; empty means "status only".</param>
    /// <param name="endpoint">The request method and URL, without credentials, query or fragment.</param>
    public TypeSafeApiException(
        int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
        : base(Format(status, body, headers, message, endpoint))
    {
        Status = status;
        Body = body;
        Headers = headers ?? HttpHeaderMap.Empty;
        Endpoint = endpoint;
    }

    /// <summary>HTTP response status code.</summary>
    public int Status { get; }

    /// <summary>The server's JSON error body, plain response text, or null for an empty body.</summary>
    public JsonNode? Body { get; }

    /// <summary>HTTP response headers (case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The request method and URL, without credentials, query parameters, or fragment, when available.</summary>
    public string? Endpoint { get; }

    /// <summary>The <c>x-typesafe-request-id</c> response header, or null if absent.</summary>
    public string? RequestId => Headers.TryGetValue(Protocol.RequestIdHeader, out var id) ? id : null;

    private static string Format(
        int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message, string? endpoint)
    {
        message ??= DescribeBody(body);
        var text = message.Length > 0
            ? $"{status.ToString(CultureInfo.InvariantCulture)} {message}"
            : status.ToString(CultureInfo.InvariantCulture);
        if (endpoint is not null)
        {
            text = $"{endpoint}: {text}";
        }

        if (headers is not null && headers.TryGetValue(Protocol.RequestIdHeader, out var requestId))
        {
            text += $" (request_id={requestId})";
        }

        return text;
    }

    private static string DescribeBody(JsonNode? body)
    {
        if (ErrorMessages.Extract(body) is { Length: > 0 } detail)
        {
            return detail;
        }

        if (body is null)
        {
            return "status code (no body)";
        }

        var raw = body.GetValueKind() == JsonValueKind.String ? body.GetValue<string>() : Json.Write(body);
        return raw.Length > Protocol.MaxErrorBodyLength ? raw[..Protocol.MaxErrorBodyLength] + "…" : raw;
    }
}

/// <summary>The request was invalid (400).</summary>
public sealed class TypeSafeBadRequestException(
    int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
    : TypeSafeApiException(status, body, headers, message, endpoint);

/// <summary>Authentication failed (401).</summary>
public sealed class TypeSafeAuthenticationException(
    int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
    : TypeSafeApiException(status, body, headers, message, endpoint);

/// <summary>Access was denied (403).</summary>
public sealed class TypeSafePermissionDeniedException(
    int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
    : TypeSafeApiException(status, body, headers, message, endpoint);

/// <summary>The resource was not found (404).</summary>
public sealed class TypeSafeNotFoundException(
    int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
    : TypeSafeApiException(status, body, headers, message, endpoint);

/// <summary>The request failed server validation (422).</summary>
public sealed class TypeSafeUnprocessableEntityException(
    int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
    : TypeSafeApiException(status, body, headers, message, endpoint);

/// <summary>The rate limit was exceeded (429).</summary>
public sealed class TypeSafeRateLimitException : TypeSafeApiException
{
    public TypeSafeRateLimitException(
        int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
        : this(status, body, headers, message, endpoint, TimeProvider.System) { }

    // An HTTP-date Retry-After is relative to "now", and "now" has to be the same clock the retry loop waits
    // on - otherwise this property and the delay the SDK actually took disagree under a virtual clock.
    internal TypeSafeRateLimitException(
        int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message, string? endpoint, TimeProvider clock)
        : base(status, body, headers, message, endpoint)
    {
        RetryAfter = Jev.Net.RetryAfter.Parse(Headers, clock);
    }

    /// <summary>The server's requested wait, or null if it gave none that could be read.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The server failed to process the request (5xx).</summary>
public sealed class TypeSafeInternalServerException(
    int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string? message = null, string? endpoint = null)
    : TypeSafeApiException(status, body, headers, message, endpoint);

/// <summary>A request failed without an HTTP response.</summary>
public class TypeSafeApiConnectionException : TypeSafeException
{
    public TypeSafeApiConnectionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>A request exceeded its configured timeout.</summary>
public sealed class TypeSafeApiTimeoutException : TypeSafeApiConnectionException
{
    public TypeSafeApiTimeoutException(TimeSpan timeout, Exception? innerException = null)
        : base($"Request timed out (timeout={timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s).", innerException)
    {
        Timeout = timeout;
    }

    /// <summary>The timeout setting used for the request.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>A successful HTTP response whose body was missing or structurally invalid required data.</summary>
public sealed class TypeSafeApiResponseValidationException : TypeSafeApiException
{
    public TypeSafeApiResponseValidationException(
        int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers, string fieldPath, string? endpoint = null,
        Exception? cause = null)
        : base(status, body, headers, $"Invalid response data at '{fieldPath}'.", endpoint)
    {
        FieldPath = fieldPath;
        Cause = cause;
    }

    /// <summary>Dotted path to the offending field, such as <c>answers.tone.confidence</c> or <c>models[1].name</c>.</summary>
    public string FieldPath { get; }

    /// <summary>The deserializer failure behind this, when a custom response type was being read.</summary>
    public Exception? Cause { get; }
}

internal static class ErrorMessages
{
    public static TypeSafeApiException ApiError(
        int status, JsonNode? body, IReadOnlyDictionary<string, string> headers, string? endpoint, TimeProvider clock) => status switch
    {
        400 => new TypeSafeBadRequestException(status, body, headers, null, endpoint),
        401 => new TypeSafeAuthenticationException(status, body, headers, null, endpoint),
        403 => new TypeSafePermissionDeniedException(status, body, headers, null, endpoint),
        404 => new TypeSafeNotFoundException(status, body, headers, null, endpoint),
        422 => new TypeSafeUnprocessableEntityException(status, body, headers, null, endpoint),
        429 => new TypeSafeRateLimitException(status, body, headers, null, endpoint, clock),
        >= 500 => new TypeSafeInternalServerException(status, body, headers, null, endpoint),
        _ => new TypeSafeApiException(status, body, headers, null, endpoint),
    };

    /// <summary>The server's own explanation, from whichever of the shapes it uses. An EMPTY string is returned
    /// as such (the caller then falls back to the raw body), matching upstream.</summary>
    public static string? Extract(JsonNode? body)
    {
        if (body is null) return null;
        if (body.GetValueKind() == JsonValueKind.String)
        {
            var text = body.GetValue<string>();
            return text.Length > 0 ? text : null;
        }

        if (body is not JsonObject obj) return null;

        var error = obj["error"];
        var message = obj["message"];
        var detail = obj["detail"];

        if (AsString(error) is { } errorText) return errorText;
        if (error is JsonObject errorObj && AsString(errorObj["message"]) is { } nestedError) return nestedError;
        if (AsString(message) is { } messageText) return messageText;
        if (AsString(detail) is { } detailText) return detailText;
        if (detail is JsonObject detailObj && AsString(detailObj["message"]) is { } nestedDetail) return nestedDetail;
        if (detail is JsonArray entries)
        {
            var parts = new List<string>();
            foreach (var entry in entries)
            {
                if (entry is not JsonObject item || AsString(item["msg"]) is not { } msg) continue;
                var path = item["loc"] is JsonArray loc
                    ? string.Join('.', loc.Select(Segment).Where(s => s != "body"))
                    : "";
                parts.Add(path.Length > 0 ? $"{path}: {msg}" : msg);
            }

            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }

        return null;
    }

    private static string? AsString(JsonNode? node) =>
        node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static string Segment(JsonNode? node) => node switch
    {
        null => "None",
        _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
        _ => Json.Write(node),
    };
}

/// <summary>Case-insensitive, read-only response headers. Repeated headers are joined with <c>", "</c>.</summary>
internal static class HttpHeaderMap
{
    public static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> From(HttpResponseMessage response)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            map[header.Key] = string.Join(", ", header.Value);
        }

        return map;
    }
}
