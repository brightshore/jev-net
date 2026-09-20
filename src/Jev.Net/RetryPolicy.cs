using System.Globalization;

namespace Jev.Net;

/// <summary>Configuration for SDK retry behavior.</summary>
/// <example>
/// <code>
/// var client = new TypeSafeClient(new TypeSafeClientOptions
/// {
///     Retry = new RetryPolicy { MaxRetries = 3, Timeout = TimeSpan.FromSeconds(10) },
/// });
/// </code>
/// </example>
public sealed record RetryPolicy
{
    /// <summary>The statuses retried by default: 408, 429, and every 5xx.</summary>
    public static IReadOnlySet<int> DefaultHttpStatuses { get; } =
        new HashSet<int>(Enumerable.Range(500, 100).Append(408).Append(429));

    /// <summary>No retries at all.</summary>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>Maximum retries after the initial attempt; <c>0</c> disables retries.</summary>
    public int MaxRetries
    {
        get;
        init => field = value >= 0 ? value : throw new TypeSafeException("MaxRetries must be a non-negative integer.");
    } = 2;

    /// <summary>First backoff delay, doubled each attempt up to <see cref="BackoffMax"/>; zero disables backoff.</summary>
    public TimeSpan BackoffInitial
    {
        get;
        init => field = NonNegative(value, nameof(BackoffInitial));
    } = TimeSpan.FromSeconds(0.5);

    /// <summary>Maximum backoff delay; zero disables backoff.</summary>
    public TimeSpan BackoffMax
    {
        get;
        init => field = NonNegative(value, nameof(BackoffMax));
    } = TimeSpan.FromSeconds(5);

    /// <summary>Fraction of each backoff delay randomly subtracted, between 0 and 1.</summary>
    public double BackoffJitter
    {
        get;
        init => field = value is >= 0 and <= 1 ? value : throw new TypeSafeException("BackoffJitter must be between zero and one.");
    } = 0.25;

    /// <summary>HTTP status codes that are retried.</summary>
    public IReadOnlySet<int> HttpStatuses { get; init; } = DefaultHttpStatuses;

    /// <summary>Whether to honor <c>Retry-After</c> and <c>retry-after-ms</c> response headers.</summary>
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>Whether to retry <see cref="TypeSafeApiConnectionException"/>.</summary>
    public bool ApiConnectionError { get; init; } = true;

    /// <summary>Whether to retry <see cref="TypeSafeApiTimeoutException"/>.</summary>
    public bool ApiTimeoutError { get; init; } = true;

    /// <summary>Additional exception types that trigger a retry, on top of the built-in rules.</summary>
    public IReadOnlySet<Type> Exceptions { get; init; } = new HashSet<Type>();

    /// <summary>An optional predicate called with the thrown exception; returning true triggers a retry in
    /// addition to the other rules.</summary>
    public Func<Exception, bool>? Predicate { get; init; }

    /// <summary>
    /// Total retry budget per SDK call, including the initial attempt and delays; null disables the limit.
    /// Stops before a retry whose delay would reach or exceed the budget, rethrowing the last error.
    /// </summary>
    public TimeSpan? Timeout
    {
        get;
        init => field = value is null ? null : Timeouts.Checked(value.Value, allowInfinite: false);
    } = TimeSpan.FromSeconds(30);

    internal bool Retryable(Exception error)
    {
        var builtin = error switch
        {
            TypeSafeApiTimeoutException => ApiTimeoutError,
            TypeSafeApiConnectionException => ApiConnectionError,
            TypeSafeApiException api => HttpStatuses.Contains(api.Status),
            _ => false,
        };
        return builtin
            || Exceptions.Any(type => type.IsInstanceOfType(error))
            || (Predicate is not null && Predicate(error));
    }

    /// <summary>How long to wait after <paramref name="attempt"/> (1-based) failed with <paramref name="error"/>.</summary>
    internal TimeSpan Wait(int attempt, Exception error, TimeProvider clock, Func<double> random)
    {
        if (RespectRetryAfter && error is TypeSafeApiException api && RetryAfter.Parse(api.Headers, clock) is { } requested)
        {
            return requested; // Server-requested delays are always honored, however long.
        }

        return Backoff.ToTimeSpan(Backoff.Seconds(
            attempt, BackoffInitial.TotalSeconds, BackoffMax.TotalSeconds, BackoffJitter, random));
    }

    private static TimeSpan NonNegative(TimeSpan value, string name) =>
        value >= TimeSpan.Zero && value != System.Threading.Timeout.InfiniteTimeSpan
            ? value
            : throw new TypeSafeException($"{name} must be a non-negative, finite duration.");
}

/// <summary>Exponential backoff with subtractive jitter, in seconds — a line-for-line port of upstream's.</summary>
internal static class Backoff
{
    public static double Seconds(int attempt, double initial, double maximum, double jitter, Func<double> random)
    {
        if (initial == 0 || maximum == 0)
        {
            return 0.0;
        }

        var exponent = attempt - 1;
        var exponential = exponent >= Math.Log2(maximum) - Math.Log2(initial) ? maximum : Math.ScaleB(initial, exponent);
        var delay = exponential * (1 - (random() * jitter));
        return Math.Min(exponential, Math.Round(delay, 3, MidpointRounding.ToEven));
    }

    public static TimeSpan ToTimeSpan(double seconds) =>
        seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
}

/// <summary>Reads <c>retry-after-ms</c> (milliseconds) then <c>Retry-After</c> (seconds, or an HTTP date).</summary>
internal static class RetryAfter
{
    public static TimeSpan? Parse(IReadOnlyDictionary<string, string> headers, TimeProvider clock) =>
        ParseMilliseconds(headers, clock) is { } ms ? Backoff.ToTimeSpan(ms / 1000) : null;

    public static double? ParseMilliseconds(IReadOnlyDictionary<string, string> headers, TimeProvider clock)
    {
        foreach (var (name, multiplier) in new[] { (Protocol.RetryAfterMsHeader, 1.0), (Protocol.RetryAfterHeader, 1000.0) })
        {
            if (!headers.TryGetValue(name, out var raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                trimmed = "0";
            }

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                if (value >= 0)
                {
                    var delay = value * multiplier;
                    if (double.IsFinite(delay))
                    {
                        return delay;
                    }
                }
                else if (name == Protocol.RetryAfterHeader)
                {
                    return null;
                }
            }
            else if (name == Protocol.RetryAfterHeader
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                return Math.Max(0.0, (when - clock.GetUtcNow()).TotalMilliseconds);
            }
        }

        return null;
    }
}

internal static class Timeouts
{
    /// <summary>A timeout must be positive; <see cref="Timeout.InfiniteTimeSpan"/> means "no timeout" where allowed.</summary>
    public static TimeSpan Checked(TimeSpan timeout, bool allowInfinite = true)
    {
        if (allowInfinite && timeout == Timeout.InfiniteTimeSpan)
        {
            return timeout;
        }

        return timeout > TimeSpan.Zero
            ? timeout
            : throw new TypeSafeException("timeout must be a positive, finite duration.");
    }
}
