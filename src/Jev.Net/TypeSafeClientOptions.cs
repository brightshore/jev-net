using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jev.Net;

/// <summary>
/// How a <see cref="TypeSafeClient"/> is configured. Explicit options take precedence over environment
/// variables; empty or whitespace-only environment values are ignored.
/// </summary>
public sealed class TypeSafeClientOptions
{
    /// <summary>Required API key; falls back to the <c>TYPESAFE_API_KEY</c> environment variable.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Default model; falls back to <c>TYPESAFE_DEFAULT_MODEL</c>, then <c>jev-latest</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Retry behavior; null means the <see cref="RetryPolicy"/> defaults. <see cref="RetryPolicy.None"/> disables retries.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Timeout for each HTTP attempt. Null inherits <see cref="HttpClient"/>'s own timeout when one is supplied,
    /// otherwise the SDK default of 10 seconds. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Additional headers sent with every request. Authentication, SDK identification and
    /// <c>Accept</c> are protected and cannot be overridden.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Optional custom message handler (the transport). Mutually exclusive with
    /// <see cref="HttpClient"/>; disposed when the SDK client is disposed.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Optional <see cref="System.Net.Http.HttpClient"/> to send through. Mutually exclusive with
    /// <see cref="Handler"/>. Disposed with the SDK client unless <see cref="DisposeHttpClient"/> is false.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Whether disposing the SDK client disposes a supplied <see cref="HttpClient"/>. Defaults to true,
    /// matching the Python SDK; set false for a client that came from <c>IHttpClientFactory</c> or is shared.</summary>
    public bool DisposeHttpClient { get; init; } = true;

    /// <summary>API root; falls back to <c>TYPESAFE_BASE_URL</c>, then <c>https://api.typesafe.ai</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Where the SDK logs (category <c>Jev.Net</c>): one Information line per attempt, and the wire
    /// (headers + bodies) at Debug. Secret headers are redacted; request and response BODIES are not — they
    /// contain whatever state you sent. <c>TYPESAFE_LOG_LEVEL</c> sets a floor on top of the logger's own filter.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>The clock behind the retry budget and <c>Retry-After</c> dates. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    // ── test seams ──────────────────────────────────────────────────────────────────────────────────────
    // Environment access goes through here because the suite runs its methods in parallel, and a process
    // environment is one shared mutable global.
    internal Func<string, string?>? EnvironmentReader { get; init; }

    /// <summary>Replaces the real wait between retries — so a test can record delays instead of sitting through them.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>The jitter source, in [0, 1).</summary>
    internal Func<double>? Random { get; init; }
}

/// <summary>Per-call overrides shared by every endpoint.</summary>
public class RequestOptions
{
    /// <summary>A retry policy for this call only.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>A per-attempt timeout for this call only.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Additional request headers. Authentication, SDK identification and <c>Accept</c> remain protected.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}

/// <summary>Per-call overrides for <see cref="TypeSafeClient.SystemOneAsync"/>.</summary>
public sealed class SystemOneOptions : RequestOptions
{
    /// <summary>Model override; null inherits the client default.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Additional top-level request-body fields, shallow-merged over the body after <c>state</c>, <c>model</c>
    /// and <c>questions</c> are set. Last write wins: a colliding key REPLACES the SDK's value, and object
    /// values are replaced rather than deep-merged.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?>? ExtraBody { get; init; }
}

/// <summary>Configuration after environment fallbacks and validation.</summary>
internal sealed record Config(
    string ApiKey, string BaseUrl, string DefaultModel, TimeSpan Timeout, IReadOnlyDictionary<string, string> DefaultHeaders)
{
    public static Config Resolve(TypeSafeClientOptions options, TimeSpan? inheritedTimeout)
    {
        var env = options.EnvironmentReader ?? Environment.GetEnvironmentVariable;

        string? FromEnv(string? value, string name, string? fallback = null)
        {
            if (value is not null) return value;
            var read = env(name)?.Trim();
            return string.IsNullOrEmpty(read) ? fallback : read;
        }

        var key = FromEnv(options.ApiKey, TypeSafeDefaults.ApiKeyEnv)
            ?? throw new TypeSafeException(
                $"No API key was provided. Pass ApiKey or set the {TypeSafeDefaults.ApiKeyEnv} environment variable.");

        return new Config(
            key,
            FromEnv(options.BaseUrl, TypeSafeDefaults.BaseUrlEnv, TypeSafeDefaults.BaseUrl)!.TrimEnd('/'),
            FromEnv(options.Model, TypeSafeDefaults.DefaultModelEnv, TypeSafeDefaults.Model)!,
            Timeouts.Checked(options.Timeout ?? inheritedTimeout ?? TypeSafeDefaults.Timeout),
            options.Headers ?? new Dictionary<string, string>());
    }

    // The key must never reach a log line or a debugger tooltip through the record's generated ToString.
    public override string ToString() => $"Config {{ BaseUrl = {BaseUrl}, DefaultModel = {DefaultModel}, Timeout = {Timeout} }}";
}
