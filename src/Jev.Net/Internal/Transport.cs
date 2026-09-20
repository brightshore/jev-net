using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jev.Net.Internal;

/// <summary>An immutable description of one HTTP request to send — possibly several times.</summary>
internal sealed record PreparedRequest(
    HttpMethod Method, Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[]? Content, TimeSpan Timeout,
    string Operation, string? Model = null)
{
    public string Endpoint => $"{Method.Method} {Url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)}";
}

/// <summary>What the wire said about one SDK call, kept apart from the decoded result: a caller's own response
/// type carries no HTTP metadata, and telemetry must not depend on which overload was used.</summary>
internal sealed class CallFacts
{
    public int Attempts { get; set; }

    /// <summary>Status of the LAST response received, if any attempt got one.</summary>
    public int? Status { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>Shared HTTP request preparation, logging, retrying, and dispatch to response types.</summary>
internal sealed class Transport(HttpClient http, Config config, RetryPolicy retry, SdkLog log, TypeSafeClientOptions options)
{
    public SdkLog Log => log;

    private static readonly string Identity = $"{Protocol.SdkName}/{Protocol.Version}";

    private readonly TimeProvider _clock = options.TimeProvider ?? TimeProvider.System;
    private readonly Func<double> _random = options.Random ?? System.Random.Shared.NextDouble;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay = options.Delay;

    public Config Config => config;

    public PreparedRequest Prepare(
        HttpMethod method, string path, JsonNode? body, TimeSpan? timeout, IReadOnlyDictionary<string, string>? headers,
        string operation)
    {
        // Later entries win, case-insensitively: client defaults, then the call's extras, then the protected set.
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in config.DefaultHeaders) merged[name] = value;
        if (headers is not null)
        {
            foreach (var (name, value) in headers) merged[name] = value;
        }

        merged.Remove(Protocol.RetryCountHeader); // ours to set, per attempt
        merged[Protocol.AuthorizationHeader] = $"Bearer {config.ApiKey}";
        merged[Protocol.AcceptHeader] = Protocol.JsonContentType;
        merged[Protocol.UserAgentHeader] = Identity;
        merged[Protocol.SdkHeader] = Identity;
        merged[Protocol.RuntimeHeader] = Protocol.Runtime;

        byte[]? content = null;
        if (body is not null)
        {
            try
            {
                content = Json.WriteBytes(body);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
            {
                throw new TypeSafeException("The request body could not be encoded as JSON", error);
            }
        }

        // Content-* headers belong to the body, which sets its own; a caller's copy is dropped, not sent twice.
        foreach (var name in merged.Keys.Where(n => n.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            merged.Remove(name);
        }

        Uri url;
        try
        {
            url = new Uri(config.BaseUrl + path, UriKind.Absolute);
        }
        catch (UriFormatException error)
        {
            throw new TypeSafeException($"The base URL '{config.BaseUrl}' is not a valid absolute URL.", error);
        }

        var model = body?["model"] is JsonValue modelNode && modelNode.TryGetValue<string>(out var modelName) ? modelName : null;
        return new PreparedRequest(method, url, merged.ToList(), content, Timeouts.Checked(timeout ?? config.Timeout), operation, model);
    }

    /// <summary>One SDK call: a span and the metrics around <see cref="SendCoreAsync{T}"/>, which does the work.
    /// When nobody listens, StartActivity returns null and the instruments are no-ops.</summary>
    public async Task<T> SendAsync<T>(
        PreparedRequest request, RetryPolicy? overridePolicy, Func<RawHttpResponse, T> decode, CancellationToken ct) where T : class
    {
        using var activity = TypeSafeTelemetry.Source.StartActivity($"{request.Method.Method} {request.Url.AbsolutePath}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("jev_net.operation", request.Operation);
            activity.SetTag("http.request.method", request.Method.Method);
            activity.SetTag("server.address", request.Url.Host);
            activity.SetTag("url.full", request.Url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
        }

        var started = Stopwatch.GetTimestamp();
        var call = new CallFacts();
        T? result = null;
        Exception? failure = null;
        try
        {
            return result = await SendCoreAsync(request, overridePolicy, decode, call, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            TypeSafeTelemetry.Record(activity, request.Operation, request.Url, request.Model, Stopwatch.GetElapsedTime(started),
                call, result as SystemOneResponse, failure);
        }
    }

    private async Task<T> SendCoreAsync<T>(
        PreparedRequest request, RetryPolicy? overridePolicy, Func<RawHttpResponse, T> decode, CallFacts call, CancellationToken ct) where T : class
    {
        var policy = overridePolicy ?? retry;
        var started = _clock.GetTimestamp();
        var attempt = 0;

        while (true)
        {
            call.Attempts = ++attempt;
            Exception failure;
            try
            {
                var raw = await AttemptAsync(request, attempt, ct).ConfigureAwait(false);
                call.Status = raw.StatusCode;
                call.RequestId = raw.Headers.TryGetValue(Protocol.RequestIdHeader, out var id) ? id : null;
                return ResponseDecoder.Parse(raw, decode, _clock);
            }
            catch (Exception error) when (error is not OperationCanceledException && policy.Retryable(error))
            {
                failure = error;
            }

            // The order is tenacity's: work out the wait FIRST, then ask whether to stop — because the budget
            // check is "would this wait carry us past it", not "are we past it already".
            var wait = policy.Wait(attempt, failure, _clock, _random);
            var elapsed = _clock.GetElapsedTime(started);
            var stop = attempt >= policy.MaxRetries + 1
                || (policy.Timeout is { } budget && elapsed + wait >= budget);
            if (stop)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (_delay is not null)
            {
                await _delay(wait, ct).ConfigureAwait(false);
            }
            else if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task<RawHttpResponse> AttemptAsync(PreparedRequest request, int attempt, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(request.Method, request.Url);
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (attempt > 1)
        {
            var retries = (attempt - 1).ToString(CultureInfo.InvariantCulture);
            message.Headers.TryAddWithoutValidation(Protocol.RetryCountHeader, retries);
            log.LogInformation("{Method} {Url} retry {Retry}", request.Method.Method, request.Url, retries);
        }

        if (request.Content is not null)
        {
            message.Content = new ByteArrayContent(request.Content);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(Protocol.JsonContentType);
        }

        if (log.IsEnabled(LogLevel.Debug))
        {
            var headers = message.Headers.Concat(message.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
            log.LogDebug("{Method} {Url} -> headers={Headers} body={Body}", request.Method.Method, request.Url,
                SdkLog.Render(headers), request.Content is null ? "" : Encoding.UTF8.GetString(request.Content));
        }

        // The attempt's own deadline, linked to the caller's token so the two can be told apart afterwards:
        // the caller cancelling is THEIR decision and propagates untouched; the deadline firing is a timeout.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(request.Timeout);
        }

        var clock = Stopwatch.GetTimestamp();
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            var raw = new RawHttpResponse((int)response.StatusCode, HttpHeaderMap.From(response), body, request.Endpoint);

            log.LogInformation("{Method} {Url} <- {Status} in {Elapsed:0}ms (request {RequestId})",
                request.Method.Method, request.Url, raw.StatusCode, Stopwatch.GetElapsedTime(clock).TotalMilliseconds,
                raw.Headers.TryGetValue(Protocol.RequestIdHeader, out var id) ? id : "-");
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("{Method} {Url} <- headers={Headers} body={Body}", request.Method.Method, request.Url,
                    SdkLog.Render(raw.Headers), Encoding.UTF8.GetString(body));
            }

            return raw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            // Either our deadline fired, or HttpClient's own Timeout did (it surfaces as a cancellation too).
            log.LogInformation("{Method} {Url} <- {Error}", request.Method.Method, request.Url, "Timeout");
            throw new TypeSafeApiTimeoutException(request.Timeout, error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
        {
            log.LogInformation("{Method} {Url} <- {Error}", request.Method.Method, request.Url, error.GetType().Name);
            if (error is TimeoutException || error.InnerException is TimeoutException)
            {
                throw new TypeSafeApiTimeoutException(request.Timeout, error);
            }

            throw new TypeSafeApiConnectionException($"Connection error: {error.Message}", error);
        }
    }
}
