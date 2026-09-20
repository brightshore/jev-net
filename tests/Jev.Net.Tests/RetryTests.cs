using System.Globalization;

namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_retry.py</c>. Time is a <see cref="ManualClock"/> and waits go through
/// the delay hook, so the whole class runs in milliseconds and asserts on EXACT delays.</summary>
[TestClass]
public sealed class RetryTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static Dictionary<string, string> Headers(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    private static Task Call(TypeSafeClient client, string resource, RetryPolicy? retry = null) => resource == "models"
        ? client.Models.ListAsync(new RequestOptions { Retry = retry })
        : client.SystemOneAsync("x", Clients.OneQuestion, new SystemOneOptions { Retry = retry });

    [TestMethod]
    public void Invalid_Policy_Values_Are_Rejected_At_Construction()
    {
        FluentActions.Invoking(() => new RetryPolicy { Timeout = TimeSpan.Zero }).Should().Throw<TypeSafeException>().WithMessage("*timeout*");
        FluentActions.Invoking(() => new RetryPolicy { Timeout = TimeSpan.FromSeconds(-1) }).Should().Throw<TypeSafeException>();
        FluentActions.Invoking(() => new RetryPolicy { Timeout = Timeout.InfiniteTimeSpan }).Should().Throw<TypeSafeException>();
        FluentActions.Invoking(() => new RetryPolicy { BackoffInitial = TimeSpan.FromSeconds(-1) }).Should().Throw<TypeSafeException>().WithMessage("*BackoffInitial*");
        FluentActions.Invoking(() => new RetryPolicy { BackoffMax = TimeSpan.FromSeconds(-1) }).Should().Throw<TypeSafeException>().WithMessage("*BackoffMax*");
        FluentActions.Invoking(() => new RetryPolicy { MaxRetries = -1 }).Should().Throw<TypeSafeException>().WithMessage("*MaxRetries*");
        foreach (var jitter in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity })
        {
            FluentActions.Invoking(() => new RetryPolicy { BackoffJitter = jitter }).Should().Throw<TypeSafeException>().WithMessage("*BackoffJitter*");
        }

        new RetryPolicy { Timeout = null }.Timeout.Should().BeNull("null is how the budget is switched off");
    }

    [TestMethod]
    [DataRow(0.0, 5.0, false)]
    [DataRow(0.5, 0.0, false)]
    [DataRow(0.0, 0.0, true)]
    public async Task Zero_Backoff_Still_Retries(double initial, double maximum, bool recover)
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var handler = new StubHandler(_ => ++attempts == 3 && recover ? Http.Json(200, """{"models": []}""") : Http.Json(500, "{}"));
        using var client = Clients.Create(handler,
            retry: new RetryPolicy { BackoffInitial = TimeSpan.FromSeconds(initial), BackoffMax = TimeSpan.FromSeconds(maximum) },
            delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });

        if (recover)
        {
            await client.Models.ListAsync();
        }
        else
        {
            await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeInternalServerException>();
        }

        attempts.Should().Be(3);
        delays.Should().Equal(TimeSpan.Zero, TimeSpan.Zero);
    }

    // (budget seconds or -1 for none, seconds each attempt takes, server-requested delay, expected attempts)
    [TestMethod]
    [DataRow("models", -1.0, 1.0, 0.5, 3)]
    [DataRow("models", 30.0, 10.0, 5.0, 2)]
    [DataRow("models", 2.5, 0.75, 0.5, 2)]
    [DataRow("models", 2.0, 1.0, 0.0, 2)]
    [DataRow("models", 1.0, 0.0, 1.0, 1)]
    [DataRow("models", 1.0, 0.0, 60.0, 1)]
    [DataRow("system_one", -1.0, 1.0, 0.5, 3)]
    [DataRow("system_one", 30.0, 10.0, 5.0, 2)]
    [DataRow("system_one", 2.5, 0.75, 0.5, 2)]
    [DataRow("system_one", 2.0, 1.0, 0.0, 2)]
    [DataRow("system_one", 1.0, 0.0, 1.0, 1)]
    [DataRow("system_one", 1.0, 0.0, 60.0, 1)]
    public async Task The_Retry_Budget_Stops_Before_A_Wait_That_Would_Exceed_It(
        string resource, double budget, double duration, double delay, int attempts)
    {
        var clock = new ManualClock();
        var delays = new List<TimeSpan>();
        var seen = 0;
        var handler = new StubHandler(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(duration));
            return Http.Json(429, """{"message": "attempt <<0>>"}""".With(0, ++seen), ("Retry-After", delay.ToString(CultureInfo.InvariantCulture)));
        });
        using var client = Clients.Create(handler, clock: clock,
            retry: new RetryPolicy { Timeout = budget < 0 ? null : TimeSpan.FromSeconds(budget) },
            delay: (wait, _) => { delays.Add(wait); clock.Advance(wait); return Task.CompletedTask; });

        for (var call = 0; call < 2; call++) // each SDK call gets a fresh budget
        {
            seen = 0;
            delays.Clear();
            var caught = (await FluentActions.Awaiting(() => Call(client, resource)).Should().ThrowAsync<TypeSafeRateLimitException>()).Which;
            caught.Message.Should().EndWith($"attempt {attempts}", "the LAST error is the one rethrown");
            seen.Should().Be(attempts);
            delays.Should().Equal(Enumerable.Repeat(TimeSpan.FromSeconds(delay), attempts - 1));
        }
    }

    [TestMethod]
    [DataRow(408, 3)]
    [DataRow(429, 3)]
    [DataRow(500, 3)]
    [DataRow(503, 3)]
    [DataRow(599, 3)]
    [DataRow(529, 3)] // the API's documented "service overloaded": retried, and surfaced as a server error
    [DataRow(400, 1)]
    [DataRow(401, 1)]
    [DataRow(403, 1)]
    [DataRow(404, 1)]
    [DataRow(409, 1)]
    [DataRow(422, 1)]
    [DataRow(302, 1)]
    public async Task Default_Retry_Statuses(int status, int attempts)
    {
        var handler = new StubHandler(_ => Http.Json(status, """{"message": "failed"}""", ("retry-after-ms", "0")));
        using var client = Clients.Create(handler, retries: true);

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiException>()).Which;

        caught.Status.Should().Be(status);
        if (status >= 500) caught.Should().BeOfType<TypeSafeInternalServerException>();
        handler.Requests.Select(r => r.Header("x-typesafe-retry-count")).Should().Equal(new string?[] { null, "1", "2" }.Take(attempts));
    }

    [TestMethod]
    [DataRow("connect")]
    [DataRow("timeout")]
    [DataRow("io")]
    public async Task A_Connection_Failure_Is_Retried_And_Recovers_With_Jittered_Backoff(string kind)
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var handler = new StubHandler(_ => ++attempts < 3
            ? throw (kind switch { "connect" => new HttpRequestException("failed"), "timeout" => new TimeoutException("failed"), _ => new IOException("failed") })
            : Http.Json(200, """{"models": []}"""));
        using var client = Clients.Create(handler, retries: true, delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });

        (await client.Models.ListAsync()).Models.Should().BeEmpty();

        attempts.Should().Be(3);
        delays[0].TotalSeconds.Should().BeInRange(0.375, 0.5);
        delays[1].TotalSeconds.Should().BeInRange(0.75, 1.0);
    }

    [TestMethod]
    [DataRow("Retry-After", "2", null, null, 2.0)]
    [DataRow("retry-after-ms", "125", null, null, 0.125)]
    [DataRow("retry-after-ms", "0", "Retry-After", "50", 0.0)]
    [DataRow("Retry-After", "60", null, null, 60.0)]
    public async Task A_Server_Requested_Delay_Is_Honored(string name, string value, string? name2, string? value2, double seconds)
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var headers = name2 is null ? new[] { (name, value) } : [(name, value), (name2, value2!)];
        var handler = new StubHandler(_ => ++attempts == 1 ? Http.Json(429, "{}", headers) : Http.Json(200, """{"models": []}"""));
        using var client = Clients.Create(handler, retry: new RetryPolicy { Timeout = null },
            delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });

        await client.Models.ListAsync();

        delays.Should().Equal(TimeSpan.FromSeconds(seconds));
    }

    [TestMethod]
    [DataRow(null, null, null, null, null)]
    [DataRow("Retry-After", "bad", null, null, null)]
    [DataRow("Retry-After", "-1", null, null, null)]
    [DataRow("retry-after-ms", "NaN", "Retry-After", "1.5", 1500.0)]
    [DataRow("retry-after-ms", "-1", "Retry-After", "2", 2000.0)]
    [DataRow("Retry-After", "", null, null, 0.0)]
    [DataRow("retry-after-ms", "inf", null, null, null)]
    [DataRow("retry-after-ms", "Infinity", null, null, null)]
    [DataRow("retry-after-ms", "bad", "Retry-After", "2", 2000.0)]
    [DataRow("Retry-After", "1e308", null, null, null)]
    public void Parse_Retry_After(string? name, string? value, string? name2, string? value2, double? expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (name is not null) headers[name] = value!;
        if (name2 is not null) headers[name2] = value2!;
        RetryAfter.ParseMilliseconds(headers, TimeProvider.System).Should().Be(expected);
    }

    [TestMethod]
    public void Backoff_Doubles_To_A_Cap_Jitter_Subtracts_And_Dates_Are_Read_Against_The_Clock()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var future = DateTimeOffset.FromUnixTimeSeconds(1_000_010).ToString("r", CultureInfo.InvariantCulture);
        var past = DateTimeOffset.FromUnixTimeSeconds(999_990).ToString("r", CultureInfo.InvariantCulture);
        RetryAfter.ParseMilliseconds(Headers(("Retry-After", future)), clock).Should().Be(10_000);
        RetryAfter.ParseMilliseconds(Headers(("Retry-After", past)), clock).Should().Be(0);

        var policy = new RetryPolicy();
        var plain = new TypeSafeApiException(500, null, NoHeaders);
        foreach (var (attempt, expected) in new[] { (1, 0.5), (2, 1.0), (3, 2.0), (4, 4.0), (5, 5.0), (20, 5.0) })
        {
            policy.Wait(attempt, plain, clock, () => 0.0).TotalSeconds.Should().Be(expected);
        }

        policy.Wait(1, plain, clock, () => 1.0).TotalSeconds.Should().Be(0.375);

        // Server-requested delays are always honored, however long.
        foreach (var (header, expected) in new[] { (("Retry-After", "61"), 61.0), (("retry-after-ms", "60001"), 60.001), (("Retry-After", future), 10.0) })
        {
            policy.Wait(1, new TypeSafeRateLimitException(429, null, Headers(header)), clock, () => 1.0).TotalSeconds.Should().BeApproximately(expected, 1e-9);
        }

        // An unparseable header falls back to backoff.
        policy.Wait(1, new TypeSafeRateLimitException(429, null, Headers(("Retry-After", "bad"))), clock, () => 1.0).TotalSeconds.Should().Be(0.375);
    }

    [TestMethod]
    public async Task The_Exceptions_RetryAfter_Reads_The_Same_Clock_The_Retry_Loop_Waits_On()
    {
        // The manual clock sits in 1970; the header names a moment ten seconds after it. Read against the
        // SYSTEM clock that date is decades in the past, i.e. "wait 0" - which is what the public property used
        // to say while the retry loop, on the injected clock, correctly waited ten seconds.
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var inTenSeconds = DateTimeOffset.FromUnixTimeSeconds(1_000_010).ToString("r", CultureInfo.InvariantCulture);
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ => Http.Json(429, "{}", ("Retry-After", inTenSeconds)));
        using var client = Clients.Create(handler, clock: clock, retry: new RetryPolicy { MaxRetries = 1, Timeout = null },
            delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeRateLimitException>()).Which;

        delays.Should().Equal(TimeSpan.FromSeconds(10));
        caught.RetryAfter.Should().Be(TimeSpan.FromSeconds(10), "the property and the wait must agree");
    }

    [TestMethod]
    public void Wait_Options()
    {
        var error = new TypeSafeRateLimitException(429, null, Headers(("Retry-After", "5")));
        new RetryPolicy().Wait(1, error, TimeProvider.System, () => 0.0).TotalSeconds.Should().Be(5.0);
        new RetryPolicy { RespectRetryAfter = false }.Wait(1, error, TimeProvider.System, () => 0.0).TotalSeconds.Should().Be(0.5);
        new RetryPolicy { BackoffInitial = TimeSpan.FromSeconds(0.2), RespectRetryAfter = false }
            .Wait(1, error, TimeProvider.System, () => 0.0).TotalSeconds.Should().Be(0.2);
    }

    [TestMethod]
    [DataRow(1e-300, 1e300, 1, 0.0)]
    [DataRow(1e-300, 1e300, 2000, 1e300)]
    [DataRow(1e308, 1e308, 1, 1e308)]
    [DataRow(0.5, 0.0006, 1, 0.0006)]
    public void Backoff_Survives_Extreme_Values(double initial, double maximum, int attempt, double expected)
    {
        Backoff.Seconds(attempt, initial, maximum, 0.25, () => 0.0).Should().Be(expected);
        Backoff.ToTimeSpan(1e300).Should().Be(TimeSpan.MaxValue, "a delay no TimeSpan can hold saturates rather than throwing");
    }

    [TestMethod]
    [DataRow(1, 3)]
    [DataRow(3, 1)]
    public async Task A_Per_Call_Policy_Overrides_The_Client_Policy(int clientAttempts, int callAttempts)
    {
        RetryPolicy Attempts(int n) => new() { MaxRetries = n - 1, BackoffInitial = TimeSpan.Zero };
        var handler = new StubHandler(_ => Http.Json(500, "{}"));
        using var client = Clients.Create(handler, retry: Attempts(clientAttempts));

        await FluentActions.Awaiting(() => Call(client, "system_one", Attempts(callAttempts))).Should().ThrowAsync<TypeSafeInternalServerException>();
        handler.Requests.Should().HaveCount(callAttempts);

        handler.Requests.Clear();
        await FluentActions.Awaiting(() => Call(client, "system_one")).Should().ThrowAsync<TypeSafeInternalServerException>();
        handler.Requests.Should().HaveCount(clientAttempts, "the override was for that call only");
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 2)]
    [DataRow(4, 5)]
    public async Task Max_Retries(int maxRetries, int attempts)
    {
        var handler = new StubHandler(_ => Http.Json(503, "{}"));
        using var client = Clients.Create(handler, retry: new RetryPolicy { MaxRetries = maxRetries, BackoffInitial = TimeSpan.Zero });
        await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeInternalServerException>();
        handler.Requests.Should().HaveCount(attempts);
    }

    [TestMethod]
    [DataRow(409, 3)]
    [DataRow(500, 1)]
    public async Task Custom_Statuses_Replace_The_Defaults(int status, int attempts)
    {
        var handler = new StubHandler(_ => Http.Json(status, "{}"));
        using var client = Clients.Create(handler,
            retry: new RetryPolicy { HttpStatuses = new HashSet<int> { 409 }, BackoffInitial = TimeSpan.Zero });
        await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiException>();
        handler.Requests.Should().HaveCount(attempts);
    }

    [TestMethod]
    [DataRow("exceptions")]
    [DataRow("predicate")]
    public async Task Extra_Exception_Types_And_A_Predicate_Widen_What_Is_Retried(string how)
    {
        var policy = how == "exceptions"
            ? new RetryPolicy { Exceptions = new HashSet<Type> { typeof(TypeSafeBadRequestException) }, BackoffInitial = TimeSpan.Zero }
            : new RetryPolicy { Predicate = error => error is TypeSafeApiException { Status: 400 }, BackoffInitial = TimeSpan.Zero };
        var handler = new StubHandler(_ => Http.Json(400, "{}"));
        using var client = Clients.Create(handler, retry: policy);

        await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeBadRequestException>();
        handler.Requests.Should().HaveCount(3, "400 is not retried by default, so three attempts means the extra rule fired");
    }

    [TestMethod]
    [DataRow("timeout")]
    [DataRow("connect")]
    public async Task An_Exhausted_Transport_Retry_Rethrows_The_Sdk_Error(string kind)
    {
        var handler = new StubHandler(_ => throw (kind == "timeout" ? new TimeoutException("failed") : new HttpRequestException("failed")));
        using var client = Clients.Create(handler, retry: new RetryPolicy { BackoffInitial = TimeSpan.Zero });

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiConnectionException>()).Which;

        (caught is TypeSafeApiTimeoutException).Should().Be(kind == "timeout");
        handler.Requests.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task An_Exhausted_Retry_Preserves_The_Final_Http_Error()
    {
        var seen = 0;
        var handler = new StubHandler(_ => Http.Json(500 + ++seen, """{"message": "attempt <<0>>"}""".With(0, seen)));
        using var client = Clients.Create(handler, retry: new RetryPolicy { BackoffInitial = TimeSpan.Zero });

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeInternalServerException>()).Which;

        caught.Status.Should().Be(503);
        caught.Message.Should().EndWith("attempt 3");
    }

    [TestMethod]
    public async Task A_Pending_Retry_Wait_Can_Be_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        var handler = new StubHandler(_ => Http.Json(503, "{}"));
        using var client = Clients.Create(handler, retries: true, delay: async (_, ct) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        var call = client.Models.ListAsync(cancellationToken: cts.Token);
        await waiting.Task;
        cts.Cancel();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Concurrent_Calls_Keep_Separate_Retry_State()
    {
        // Each call fails exactly once, keyed on its own state, then succeeds. Shared attempt state would show
        // up as a retry-count header of 2+, or as a call that never gets its retry.
        var failed = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var handler = new StubHandler(request =>
        {
            var state = request.Json!["state"]!.GetValue<string>();
            return failed.TryAdd(state, true) ? Http.Json(503, "{}") : Http.Json(200, ClientTests.Result);
        });
        using var client = Clients.Create(handler, retry: new RetryPolicy { BackoffInitial = TimeSpan.Zero });

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => client.SystemOneAsync($"call-{i}", Clients.OneQuestion)));

        handler.Requests.Should().HaveCount(32);
        handler.Requests.Select(r => r.Header("x-typesafe-retry-count")).Should().OnlyContain(h => h == null || h == "1");
        handler.Requests.Count(r => r.Header("x-typesafe-retry-count") == "1").Should().Be(16);
    }
}
