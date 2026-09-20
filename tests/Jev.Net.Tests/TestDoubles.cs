using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jev.Net.Tests;

/// <summary>What the stub saw of one request — captured eagerly, because the message is disposed after the send.</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, byte[] Content)
{
    public JsonNode? Json => Content.Length == 0 ? null : JsonNode.Parse(Content);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>The transport double: every request goes to a function, and is recorded on the way.</summary>
internal sealed class StubHandler(Func<SeenRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHandler(Func<SeenRequest, HttpResponseMessage> respond) : this((request, _) => Task.FromResult(respond(request))) { }

    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    public int DisposeCalls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(", ", header.Value);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);
        }

        var content = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var seen = new SeenRequest(request.Method, request.RequestUri!, headers, content);
        Requests.Enqueue(seen);
        var response = await respond(seen, cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeCalls++;
        base.Dispose(disposing);
    }
}

internal static class Http
{
    public static HttpResponseMessage Json(int status, string json, params (string Name, string Value)[] headers) =>
        Bytes(status, Encoding.UTF8.GetBytes(json), headers);

    public static HttpResponseMessage Json(int status, JsonNode? json, params (string Name, string Value)[] headers) =>
        Json(status, json?.ToJsonString() ?? "null", headers);

    public static HttpResponseMessage Bytes(int status, byte[] content, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(content) };
        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}

/// <summary>Builds clients the way the upstream suite's factory does: retries OFF unless a test asks.</summary>
internal static class Clients
{
    public static readonly Func<string, string?> NoEnvironment = _ => null;

    public static TypeSafeClient Create(
        StubHandler handler,
        bool retries = false,
        string? apiKey = "test-key",
        string? model = null,
        RetryPolicy? retry = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? baseUrl = null,
        ILoggerFactory? loggerFactory = null,
        Func<string, string?>? env = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? random = null,
        TimeProvider? clock = null) =>
        new(new TypeSafeClientOptions
        {
            ApiKey = apiKey,
            Model = model,
            Retry = retry ?? (retries ? null : RetryPolicy.None),
            Timeout = timeout,
            Headers = headers,
            Handler = handler,
            BaseUrl = baseUrl,
            LoggerFactory = loggerFactory,
            EnvironmentReader = env ?? NoEnvironment,
            Delay = delay,
            Random = random,
            TimeProvider = clock,
        });

    public static TypeSafeClient Create(Func<SeenRequest, HttpResponseMessage> respond, bool retries = false) =>
        Create(new StubHandler(respond), retries);

    public static readonly Dictionary<string, Question> OneQuestion = new() { ["q"] = new Noul("?") };
}

/// <summary>A clock tests move by hand: the retry budget and Retry-After dates read it, nothing else does.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks;

    public ManualClock() : this(DateTimeOffset.FromUnixTimeSeconds(1_000_000)) { }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>Collects rendered log lines with their level.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory, ILogger
{
    public LogLevel Minimum { get; init; } = LogLevel.Trace;

    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public string Text => string.Join("\n", Entries.Select(e => e.Message));

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel)) Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

internal static class JsonTemplate
{
    /// <summary>Fill hole <c>&lt;&lt;n&gt;&gt;</c> of a JSON template. C#'s interpolated raw strings reject the runs of
    /// closing braces that nested JSON is made of, so templates are plain raw strings with numbered holes.</summary>
    public static string With(this string template, int hole, object value) =>
        template.Replace($"<<{hole}>>", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
}

internal static class JsonAssert
{
    public static void Equal(JsonNode? actual, string expectedJson)
    {
        var expected = JsonNode.Parse(expectedJson);
        JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            $"expected {expected?.ToJsonString() ?? "null"} but got {actual?.ToJsonString() ?? "null"}");
    }
}
