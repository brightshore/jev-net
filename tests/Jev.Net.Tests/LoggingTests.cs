using Microsoft.Extensions.Logging;
using Jev.Net.Internal;

namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_logging.py</c>.</summary>
[TestClass]
public sealed class LoggingTests
{
    public static IEnumerable<object[]> SecretHeaderCases() =>
        from status in new[] { 200, 400, 429 }
        from header in new[]
        {
            "Authorization", "Proxy-Authorization", "X-API-Key", "API-Key", "Cookie", "Set-Cookie",
            "X-Access-Token", "X-Client-Secret", "x-MiXeD-ToKeN",
        }
        select new object[] { status, header };

    [TestMethod]
    [DynamicData(nameof(SecretHeaderCases))]
    public async Task Secret_Headers_Are_Redacted_In_Both_Directions(int status, string header)
    {
        var logs = new ListLoggerFactory();
        var handler = new StubHandler(_ => Http.Json(status, status == 200 ? """{"models": []}""" : """{"message": "failure"}""",
            (header, "response-credential"), ("x-visible", "response-visible")));
        using var client = Clients.Create(handler, apiKey: "auth-credential", loggerFactory: logs,
            headers: new Dictionary<string, string> { [header] = "request-credential", ["x-visible"] = "request-visible" },
            retry: new RetryPolicy { BackoffInitial = TimeSpan.FromMilliseconds(1), BackoffMax = TimeSpan.FromMilliseconds(1) });

        if (status == 200)
        {
            await client.Models.ListAsync();
        }
        else
        {
            await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiException>();
        }

        handler.Requests.Should().HaveCount(status == 429 ? 3 : 1);
        logs.Text.Should().Contain("request-visible").And.Contain("response-visible").And.Contain("***");
        foreach (var secret in new[] { "auth-credential", "request-credential", "response-credential" })
        {
            logs.Text.Should().NotContain(secret);
        }

        if (status == 429)
        {
            logs.Text.Should().Contain("retry 1").And.Contain("retry 2");
        }
    }

    [TestMethod]
    [DataRow(LogLevel.Debug, true, true)]
    [DataRow(LogLevel.Information, true, false)]
    [DataRow(LogLevel.Warning, false, false)]
    public async Task The_Hosts_Level_Controls_What_Is_Emitted(LogLevel minimum, bool info, bool debug)
    {
        var logs = new ListLoggerFactory { Minimum = minimum };
        using var client = Clients.Create(new StubHandler(_ => Http.Json(200, """{"models": []}""")), loggerFactory: logs);
        await client.Models.ListAsync();

        logs.Entries.Any(e => e.Level == LogLevel.Information).Should().Be(info);
        logs.Entries.Any(e => e.Level == LogLevel.Debug).Should().Be(debug);
        if (info)
        {
            logs.Entries.Where(e => e.Level == LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("GET");
        }

        if (!debug)
        {
            logs.Text.Should().NotContain("headers=");
        }
    }

    [TestMethod]
    [DataRow("info", false)]
    [DataRow("off", false)]
    [DataRow("debug", true)]
    [DataRow("bogus", true)]
    public async Task The_Environment_Level_Is_A_Floor_Under_The_Hosts_Filter(string value, bool debug)
    {
        var logs = new ListLoggerFactory();
        using var client = Clients.Create(new StubHandler(_ => Http.Json(200, """{"models": []}""")), loggerFactory: logs,
            env: name => name == TypeSafeDefaults.LogLevelEnv ? value : null);
        await client.Models.ListAsync();

        logs.Entries.Any(e => e.Level == LogLevel.Debug).Should().Be(debug);
        if (value == "off") logs.Entries.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("debug", LogLevel.Debug)]
    [DataRow(" INFO ", LogLevel.Information)]
    [DataRow("warn", LogLevel.Warning)]
    [DataRow("warning", LogLevel.Warning)]
    [DataRow("error", LogLevel.Error)]
    [DataRow("off", LogLevel.None)]
    [DataRow("bogus", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void Floor_From_Environment(string? value, LogLevel? expected) => SdkLog.FloorFrom(value).Should().Be(expected);

    [TestMethod]
    public async Task No_Logger_Is_Fine()
    {
        using var client = Clients.Create(_ => Http.Json(200, """{"models": []}"""));
        (await client.Models.ListAsync()).Models.Should().BeEmpty();
    }
}
