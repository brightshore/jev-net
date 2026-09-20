using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Jev.Net;

/// <summary>
/// The names to subscribe to. Jev.Net emits traces and metrics through <see cref="ActivitySource"/> and
/// <see cref="Meter"/>, which ship in the .NET runtime — so this costs no dependency, and with nobody listening
/// the transport skips the bookkeeping altogether (one null check and one flag read per call). With OpenTelemetry:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(TypeSafeTelemetry.ActivitySourceName))
///     .WithMetrics(m => m.AddMeter(TypeSafeTelemetry.MeterName));
/// </code>
/// </summary>
/// <remarks>
/// One span covers one SDK call INCLUDING its retries (<c>http.request.resend_count</c> says how many). The
/// individual HTTP attempts are not duplicated here: they come from .NET's own <c>System.Net.Http</c>
/// instrumentation, and appear beneath this span only if you subscribe to that too
/// (<c>AddHttpClientInstrumentation()</c>, or <c>AddSource("System.Net.Http")</c>).
/// <para><b>None of your content is recorded</b> — no state, no questions, no answers, no headers. Two things you
/// CONFIGURE are: the model name you asked for (<c>jev_net.request.model</c>) and the host and path of your
/// base URL (<c>server.address</c>, <c>url.full</c>, without credentials or query). Keep secrets out of both.</para>
/// <para>That describes THIS span. The per-attempt child spans belong to <c>System.Net.Http</c>'s instrumentation
/// and follow its rules; to keep them clean too, the SDK never puts a base URL's <c>user:password@</c> on the
/// request it sends, and never adds a query string.</para>
/// </remarks>
public static class TypeSafeTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name: <c>Jev.Net</c>.</summary>
    public const string ActivitySourceName = "Jev.Net";

    /// <summary>The <see cref="Meter"/> name: <c>Jev.Net</c>.</summary>
    public const string MeterName = "Jev.Net";

    /// <summary>Histogram, seconds: the duration of one SDK call, retries and waits included.</summary>
    public const string RequestDuration = "jev_net.client.request.duration";

    /// <summary>Counter: retries performed (attempts after the first).</summary>
    public const string Retries = "jev_net.client.retries";

    /// <summary>Counter, tokens, tagged <c>jev_net.token.type</c> = <c>input</c> | <c>output</c>.</summary>
    public const string TokenUsage = "jev_net.client.token.usage";

    internal static readonly ActivitySource Source = new(ActivitySourceName, Protocol.Version);
    private static readonly Meter Metrics = new(MeterName, Protocol.Version);

    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>(
        RequestDuration, unit: "s", description: "Duration of a TypeSafe API call, including retries and the waits between them.");

    private static readonly Counter<long> RetryCount = Metrics.CreateCounter<long>(
        Retries, unit: "{retry}", description: "Retries performed after a failed attempt.");

    private static readonly Counter<long> Tokens = Metrics.CreateCounter<long>(
        TokenUsage, unit: "{token}", description: "Tokens reported by the API, by type.");

    /// <summary>Whether any metrics listener is subscribed. With no span and none of these, the transport skips
    /// its telemetry bookkeeping entirely.</summary>
    internal static bool MetricsEnabled => Duration.Enabled || RetryCount.Enabled || Tokens.Enabled;

    /// <summary>What one call measured, written to the span and the instruments together so they cannot disagree.</summary>
    internal static void Record(
        Activity? activity, string operation, Uri url, string? requestedModel, TimeSpan elapsed, Internal.CallFacts call,
        Exception? error)
    {
        var attempts = Math.Max(call.Attempts, 1);
        // From the wire, not from the decoded object: a caller's own response type (the JsonTypeInfo overloads)
        // is an ordinary class with no HTTP metadata on it, and would otherwise report no status at all.
        var status = (error as TypeSafeApiException)?.Status ?? call.Status;
        var errorType = error switch
        {
            null => null,
            // A 2xx whose BODY was unusable. The HTTP exchange succeeded, so "200" would be a lie about what
            // failed - and indistinguishable from a healthy call on a dashboard grouped by error.type.
            TypeSafeApiResponseValidationException => nameof(TypeSafeApiResponseValidationException),
            TypeSafeApiException api => api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OperationCanceledException => "cancelled",
            _ => error.GetType().Name,
        };
        var tags = new TagList
        {
            { "jev_net.operation", operation },
            { "server.address", url.Host },
        };
        if (status is not null) tags.Add("http.response.status_code", status.Value);
        if (errorType is not null) tags.Add("error.type", errorType);

        Duration.Record(elapsed.TotalSeconds, tags);
        if (attempts > 1)
        {
            RetryCount.Add(attempts - 1, new TagList { { "jev_net.operation", operation }, { "server.address", url.Host } });
        }

        if (error is null && call.ResponseModel is not null)
        {
            var model = new KeyValuePair<string, object?>("jev_net.response.model", call.ResponseModel);
            var server = new KeyValuePair<string, object?>("server.address", url.Host);
            if (call.InputTokens is { } input) Tokens.Add(input, model, server, new("jev_net.token.type", "input"));
            if (call.OutputTokens is { } output) Tokens.Add(output, model, server, new("jev_net.token.type", "output"));
        }

        if (activity is null)
        {
            return;
        }

        activity.SetTag("http.request.resend_count", attempts - 1);
        if (status is not null) activity.SetTag("http.response.status_code", status.Value);
        if (requestedModel is not null) activity.SetTag("jev_net.request.model", requestedModel);
        if (error is null && call.ResponseModel is not null)
        {
            activity.SetTag("jev_net.response.model", call.ResponseModel);
            if (call.InputTokens is { } input) activity.SetTag("jev_net.usage.input_tokens", input);
            if (call.OutputTokens is { } output) activity.SetTag("jev_net.usage.output_tokens", output);
        }

        var requestId = (error as TypeSafeApiException)?.RequestId ?? call.RequestId;
        if (requestId is not null) activity.SetTag("jev_net.request_id", requestId);

        if (error is not null)
        {
            activity.SetTag("error.type", errorType);
            // A FIXED description, never the exception's message: for an error body in no known shape the
            // message falls back to the raw response text, and a server is free to echo the request in that.
            activity.SetStatus(ActivityStatusCode.Error, error switch
            {
                TypeSafeApiResponseValidationException => "invalid response body",
                TypeSafeApiException api => $"HTTP {api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                OperationCanceledException => "cancelled",
                _ => error.GetType().Name,
            });
        }
    }
}
