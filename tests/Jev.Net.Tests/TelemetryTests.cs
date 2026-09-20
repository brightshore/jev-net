using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Jev.Net.Tests;

/// <summary>
/// Listeners are process-wide and this suite runs its methods in parallel, so every test talks to its OWN host
/// name and the capture keeps only what carries it — <c>server.address</c> is on every span and measurement.
/// </summary>
[TestClass]
public sealed class TelemetryTests
{
    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();

        public Capture()
        {
            Host = $"otel-{Guid.NewGuid():N}.test";
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == TypeSafeTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (Equals(activity.GetTagItem("server.address"), Host)) Spans.Enqueue(activity);
                },
            };
            ActivitySource.AddActivityListener(_activities);

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TypeSafeTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<double>((i, value, tags, _) => Keep(i, value, tags));
            _meters.SetMeasurementEventCallback<long>((i, value, tags, _) => Keep(i, value, tags));
            _meters.Start();
        }

        public string Host { get; }

        public string BaseUrl => $"https://{Host}";

        public ConcurrentQueue<Activity> Spans { get; } = new();

        public ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = new();

        private void Keep(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (Equals(map.GetValueOrDefault("server.address"), Host)) Measurements.Enqueue((instrument.Name, value, map));
        }

        public IEnumerable<(double Value, Dictionary<string, object?> Tags)> Of(string instrument) =>
            Measurements.Where(m => m.Instrument == instrument).Select(m => (m.Value, m.Tags));

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }
    }

    [TestMethod]
    public async Task A_Call_Is_One_Client_Span_With_What_Happened_And_Nothing_You_Sent()
    {
        using var capture = new Capture();
        using var client = Clients.Create(new StubHandler(_ => Http.Json(200, ClientTests.Result, ("x-typesafe-request-id", "req-otel"))),
            baseUrl: $"https://user:hunter2@{capture.Host}");

        await client.SystemOneAsync("the customer's PRIVATE message", new Dictionary<string, Question> { ["q"] = new Noul("a PRIVATE question?") },
            new SystemOneOptions { Model = "jev-1.13.0" });

        var span = capture.Spans.Should().ContainSingle().Which;
        span.DisplayName.Should().Be("POST /v1/systemone");
        span.Kind.Should().Be(ActivityKind.Client);
        span.Status.Should().Be(ActivityStatusCode.Unset);
        span.GetTagItem("jev_net.operation").Should().Be("system_one");
        span.GetTagItem("http.request.method").Should().Be("POST");
        span.GetTagItem("http.response.status_code").Should().Be(200);
        span.GetTagItem("http.request.resend_count").Should().Be(0);
        span.GetTagItem("jev_net.request.model").Should().Be("jev-1.13.0");
        span.GetTagItem("jev_net.response.model").Should().Be("jev-latest");
        span.GetTagItem("jev_net.usage.input_tokens").Should().Be(12);
        span.GetTagItem("jev_net.request_id").Should().Be("req-otel");
        span.GetTagItem("url.full").Should().Be($"https://{capture.Host}/v1/systemone", "no credentials, no query");

        var everything = string.Join(" ", span.TagObjects.Select(t => $"{t.Key}={t.Value}")) + span.DisplayName + span.StatusDescription;
        everything.Should().NotContain("PRIVATE").And.NotContain("hunter2").And.NotContain("test-key");
    }

    [TestMethod]
    public async Task Duration_Retries_And_Tokens_Are_Measured()
    {
        using var capture = new Capture();
        var attempts = 0;
        using var client = Clients.Create(new StubHandler(_ => ++attempts < 3 ? Http.Json(503, "{}") : Http.Json(200, ClientTests.Result)),
            baseUrl: capture.BaseUrl, retry: new RetryPolicy { BackoffInitial = TimeSpan.Zero });

        await client.SystemOneAsync("x", Clients.OneQuestion);

        capture.Spans.Should().ContainSingle("retries are inside the one span").Which.GetTagItem("http.request.resend_count").Should().Be(2);
        capture.Of(TypeSafeTelemetry.Retries).Should().ContainSingle().Which.Value.Should().Be(2);

        var duration = capture.Of(TypeSafeTelemetry.RequestDuration).Should().ContainSingle().Which;
        duration.Value.Should().BeGreaterThanOrEqualTo(0);
        duration.Tags["jev_net.operation"].Should().Be("system_one");
        duration.Tags["http.response.status_code"].Should().Be(200);
        duration.Tags.Should().NotContainKey("error.type");

        capture.Of(TypeSafeTelemetry.TokenUsage).Select(t => (t.Tags["jev_net.token.type"], t.Value))
            .Should().BeEquivalentTo([((object?)"input", 12.0), ((object?)"output", 3.0)]);
    }

    [TestMethod]
    public async Task A_Failure_Marks_The_Span_And_Tags_The_Measurement()
    {
        using var capture = new Capture();
        // An error body in no known shape: the exception's MESSAGE falls back to this raw text - which here echoes
        // the request. The span must not.
        using var client = Clients.Create(new StubHandler(_ => Http.Json(429, """{"echo": "the customer's PRIVATE message"}""", ("x-typesafe-request-id", "req-429"))),
            baseUrl: capture.BaseUrl);

        await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeRateLimitException>();

        var span = capture.Spans.Should().ContainSingle().Which;
        span.DisplayName.Should().Be("GET /v1/models");
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().Be("HTTP 429");
        string.Join(" ", span.TagObjects.Select(t => $"{t.Value}")).Should().NotContain("PRIVATE");
        span.GetTagItem("error.type").Should().Be("429");
        span.GetTagItem("http.response.status_code").Should().Be(429);
        span.GetTagItem("jev_net.request_id").Should().Be("req-429");
        capture.Of(TypeSafeTelemetry.RequestDuration).Should().ContainSingle().Which.Tags["error.type"].Should().Be("429");
        capture.Of(TypeSafeTelemetry.TokenUsage).Should().BeEmpty();
    }

    [TestMethod]
    public async Task Transport_Failures_And_Cancellation_Are_Named()
    {
        using var capture = new Capture();
        using var broken = Clients.Create(new StubHandler(_ => throw new HttpRequestException("no route")), baseUrl: capture.BaseUrl);
        await broken.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiConnectionException>();

        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        using var slow = Clients.Create(new StubHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Http.Json(200, "{}");
        }), baseUrl: capture.BaseUrl);
        var call = slow.Models.ListAsync(cancellationToken: cts.Token);
        await entered.Task;
        cts.Cancel();
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();

        capture.Spans.Select(s => s.GetTagItem("error.type")).Should().BeEquivalentTo(["TypeSafeApiConnectionException", "cancelled"]);
        capture.Spans.Should().OnlyContain(s => s.Status == ActivityStatusCode.Error);
    }

    [TestMethod]
    public void The_Names_Are_Public_Constants_So_Nobody_Has_To_Guess_Them()
    {
        TypeSafeTelemetry.ActivitySourceName.Should().Be("Jev.Net");
        TypeSafeTelemetry.MeterName.Should().Be("Jev.Net");
        new[] { TypeSafeTelemetry.RequestDuration, TypeSafeTelemetry.Retries, TypeSafeTelemetry.TokenUsage }
            .Should().OnlyContain(name => name.StartsWith("jev_net.client.", StringComparison.Ordinal));
    }
}
