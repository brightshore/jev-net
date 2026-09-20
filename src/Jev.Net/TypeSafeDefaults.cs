using System.Reflection;

namespace Jev.Net;

/// <summary>Public environment-variable names and client defaults.</summary>
public static class TypeSafeDefaults
{
    /// <summary>Environment variable for the API key.</summary>
    public const string ApiKeyEnv = "TYPESAFE_API_KEY";

    /// <summary>Environment variable for the API base URL.</summary>
    public const string BaseUrlEnv = "TYPESAFE_BASE_URL";

    /// <summary>Environment variable for the default model.</summary>
    public const string DefaultModelEnv = "TYPESAFE_DEFAULT_MODEL";

    /// <summary>Environment variable for the SDK's own minimum log level (debug/info/warn/warning/error/off).</summary>
    public const string LogLevelEnv = "TYPESAFE_LOG_LEVEL";

    /// <summary>Default API base URL.</summary>
    public const string BaseUrl = "https://api.typesafe.ai";

    /// <summary>Default model name.</summary>
    public const string Model = "jev-latest";

    /// <summary>This package's version, e.g. <c>0.3.0</c> — what it sends in <c>User-Agent</c>. For bug reports and telemetry.</summary>
    public static string SdkVersion => Protocol.Version;

    /// <summary>Default timeout for each HTTP operation.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}

/// <summary>Internal protocol constants.</summary>
internal static class Protocol
{
    public const int MaxErrorBodyLength = 200;

    public const string SystemOnePath = "/v1/systemone";
    public const string ModelsPath = "/v1/models";
    public const string SdkName = "jev-net";
    public const string LoggerName = "Jev.Net";
    public const string JsonContentType = "application/json";

    public const string AuthorizationHeader = "Authorization";
    public const string AcceptHeader = "Accept";
    public const string ContentTypeHeader = "Content-Type";
    public const string UserAgentHeader = "User-Agent";
    public const string SdkHeader = "X-TypeSafe-SDK";
    public const string RuntimeHeader = "X-TypeSafe-Runtime";
    public const string RetryCountHeader = "X-TypeSafe-Retry-Count";
    public const string RequestIdHeader = "x-typesafe-request-id";
    public const string RetryAfterHeader = "retry-after";
    public const string RetryAfterMsHeader = "retry-after-ms";

    public static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "cookie", "set-cookie",
    };

    /// <summary>The package version, without the <c>+upstream.x</c> build metadata.</summary>
    public static readonly string Version = ReadVersion();

    public static readonly string Runtime =
        $"dotnet/{Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}; " +
        $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    private static string ReadVersion()
    {
        var informational = typeof(Protocol).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
