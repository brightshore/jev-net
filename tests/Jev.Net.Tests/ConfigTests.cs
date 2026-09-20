namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_config.py</c>. The environment is an injected reader, never the real
/// process environment — these tests run beside each other.</summary>
[TestClass]
public sealed class ConfigTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    [TestMethod]
    public void Handler_And_HttpClient_Are_Mutually_Exclusive()
    {
        var handler = new StubHandler(_ => throw new AssertFailedException("Unexpected request"));
        using var http = new HttpClient(new StubHandler(_ => Http.Json(200, "{}")));

        FluentActions.Invoking(() => new TypeSafeClient(new TypeSafeClientOptions { ApiKey = "k", Handler = handler, HttpClient = http }))
            .Should().Throw<ArgumentException>().WithMessage("*mutually exclusive*");
        handler.DisposeCalls.Should().Be(0, "a rejected configuration must not consume what it was handed");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("request-model")]
    public async Task Model_Override(string? model)
    {
        var handler = new StubHandler(request =>
        {
            request.Json!["model"]!.GetValue<string>().Should().Be(model ?? "client-model");
            return Http.Json(200, ClientTests.Result);
        });
        using var client = Clients.Create(handler, model: "client-model");
        await client.SystemOneAsync("hello", Clients.OneQuestion, new SystemOneOptions { Model = model });
        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    [DataRow("default", "test-key", "https://api.typesafe.ai", "jev-latest")]
    [DataRow("env", "env-key", "https://env.test", "env-model")]
    [DataRow("constructor", "code-key", "https://code.test", "code-model")]
    public async Task Resolution(string source, string key, string url, string model)
    {
        var env = source == "default" ? Clients.NoEnvironment : Env(
            (TypeSafeDefaults.ApiKeyEnv, "  env-key  "), (TypeSafeDefaults.BaseUrlEnv, "  https://env.test///  "),
            (TypeSafeDefaults.DefaultModelEnv, "  env-model  "));
        var handler = new StubHandler(request =>
        {
            request.Header("authorization").Should().Be($"Bearer {key}");
            request.Url.ToString().Should().Be(url + "/v1/systemone");
            request.Json!["model"]!.GetValue<string>().Should().Be(model);
            return Http.Json(200, ClientTests.Result);
        });

        using var client = source switch
        {
            "constructor" => Clients.Create(handler, apiKey: "code-key", baseUrl: "https://code.test///", model: "code-model", env: env),
            "env" => Clients.Create(handler, apiKey: null, env: env),
            _ => Clients.Create(handler, env: env),
        };
        await client.SystemOneAsync("hello", Clients.OneQuestion);
        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\n ")]
    public void A_Missing_Key_Names_The_Variable_To_Set(string? value)
    {
        var env = value is null ? Clients.NoEnvironment : Env((TypeSafeDefaults.ApiKeyEnv, value));
        FluentActions.Invoking(() => new TypeSafeClient(new TypeSafeClientOptions { EnvironmentReader = env }))
            .Should().Throw<TypeSafeException>().WithMessage("*TYPESAFE_API_KEY*");
    }

    [TestMethod]
    public async Task Blank_Environment_Values_Count_As_Unset()
    {
        var handler = new StubHandler(request =>
        {
            request.Url.ToString().Should().Be("https://api.typesafe.ai/v1/systemone");
            request.Json!["model"]!.GetValue<string>().Should().Be("jev-latest");
            return Http.Json(200, ClientTests.Result);
        });
        using var client = Clients.Create(handler, env: Env(
            (TypeSafeDefaults.BaseUrlEnv, " \t "), (TypeSafeDefaults.DefaultModelEnv, " \t "), (TypeSafeDefaults.LogLevelEnv, " \t ")));
        await client.SystemOneAsync("x", Clients.OneQuestion);
        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task An_Invalid_Timeout_Is_Rejected_At_Construction_And_Per_Call(int seconds)
    {
        var timeout = TimeSpan.FromSeconds(seconds);
        FluentActions.Invoking(() => Clients.Create(new StubHandler(_ => Http.Json(200, "{}")), timeout: timeout))
            .Should().Throw<TypeSafeException>().WithMessage("*timeout*");

        var handler = new StubHandler(_ => throw new AssertFailedException("Unexpected request"));
        using var client = Clients.Create(handler);
        (await client.Invoking(c => c.Models.ListAsync(new RequestOptions { Timeout = timeout }))
            .Should().ThrowAsync<TypeSafeException>()).WithMessage("*timeout*");
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task An_Infinite_Timeout_Means_No_Deadline()
    {
        using var client = Clients.Create(new StubHandler(_ => Http.Json(200, """{"models": []}""")), timeout: Timeout.InfiniteTimeSpan);
        (await client.Models.ListAsync()).Models.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_Supplied_HttpClients_Timeout_Is_Inherited_When_None_Is_Given()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Http.Json(200, "{}");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(40) };
        using var client = new TypeSafeClient(new TypeSafeClientOptions
        {
            ApiKey = "k", HttpClient = http, Retry = RetryPolicy.None, EnvironmentReader = Clients.NoEnvironment,
        });

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiTimeoutException>()).Which;

        caught.Timeout.Should().Be(TimeSpan.FromMilliseconds(40));
        http.Timeout.Should().Be(TimeSpan.FromMilliseconds(40), "the supplied client is never reconfigured");
    }

    [TestMethod]
    public void The_Default_Handler_Asks_For_Compression_Recycles_Connections_And_Follows_No_Redirects()
    {
        // Asserted on the factory, not on the wire: Accept-Encoding is added by the decompression stage INSIDE
        // SocketsHttpHandler, below the point where any stub handler could sit and observe it.
        using var handler = TypeSafeClient.CreateDefaultHandler();

        handler.AutomaticDecompression.Should().Be(System.Net.DecompressionMethods.All);
        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(2));
        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [TestMethod]
    public void The_Key_Never_Appears_In_The_Configs_Text()
    {
        var config = Jev.Net.Config.Resolve(new TypeSafeClientOptions { ApiKey = "sk-very-secret", EnvironmentReader = Clients.NoEnvironment }, null);
        config.ToString().Should().NotContain("sk-very-secret").And.Contain("api.typesafe.ai");
    }

    [TestMethod]
    public async Task A_Malformed_Base_Url_Is_An_Sdk_Error()
    {
        using var client = Clients.Create(new StubHandler(_ => Http.Json(200, "{}")), baseUrl: "not a url");
        (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeException>()).WithMessage("*not a valid absolute URL*");
    }
}
