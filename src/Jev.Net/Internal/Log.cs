using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jev.Net.Internal;

/// <summary>
/// The SDK's logger: whatever the host supplied, under an optional floor from <c>TYPESAFE_LOG_LEVEL</c>, with
/// credential-bearing headers redacted at the ONE place headers are rendered — so no call site can leak one.
/// </summary>
internal sealed class SdkLog(ILogger inner, LogLevel? floor) : ILogger
{
    public static SdkLog Create(ILoggerFactory? factory, Func<string, string?> env) =>
        new(factory?.CreateLogger(Protocol.LoggerName) ?? NullLogger.Instance, FloorFrom(env(TypeSafeDefaults.LogLevelEnv)));

    /// <summary>debug / info / warn / warning / error / off → a level; anything else → no floor.</summary>
    public static LogLevel? FloorFrom(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "off" => LogLevel.None,
        _ => null,
    };

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && (floor is null || (floor != LogLevel.None && logLevel >= floor)) && inner.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    public static bool IsSecret(string header) =>
        Protocol.SecretHeaders.Contains(header)
        || header.Contains("token", StringComparison.OrdinalIgnoreCase)
        || header.Contains("secret", StringComparison.OrdinalIgnoreCase);

    public static string Render(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
        "{" + string.Join(", ", headers.Select(h => $"{h.Key}: {(IsSecret(h.Key) ? "***" : string.Join(", ", h.Value))}")) + "}";

    public static string Render(IReadOnlyDictionary<string, string> headers) =>
        "{" + string.Join(", ", headers.Select(h => $"{h.Key}: {(IsSecret(h.Key) ? "***" : h.Value)}")) + "}";
}
