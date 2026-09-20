using System.Text;

namespace Jev.Net.Tests;

/// <summary>The additive surface of 0.3.0: responses you can build yourself, answers the SDK does not model,
/// the client as an interface, the version constant, and the mode of a score.</summary>
[TestClass]
public sealed class SurfaceTests
{
    private const string WithMystery = """
        {"model": "test", "usage": {"input_tokens": 1, "output_tokens": 1},
         "answers": {"spam": {"type": "noul", "noul": 0.9},
                     "mystery": {"type": "aurora", "value": 3, "nested": {"k": null}}}}
        """;

    private static RawHttpResponse Raw(int status, string body, params (string, string)[] headers) =>
        new(status, headers.ToDictionary(h => h.Item1, h => h.Item2), Encoding.UTF8.GetBytes(body), "POST https://cache.test/v1/systemone");

    [TestMethod]
    public async Task An_Unmodeled_Answer_Is_Kept_Beside_Answers_Not_Inside_It()
    {
        using var client = Clients.Create(_ => Http.Json(200, WithMystery));
        var result = await client.SystemOneAsync("x", Clients.OneQuestion);

        result.Answers.Keys.Should().BeEquivalentTo(["spam"], "Answers matches the Python SDK, which omits unknown types");
        result.UnmodeledAnswers.Keys.Should().BeEquivalentTo("mystery");
        JsonAssert.Equal(result.UnmodeledAnswers["mystery"], """{"type": "aurora", "value": 3, "nested": {"k": null}}""");
    }

    [TestMethod]
    public void A_Response_Can_Be_Built_From_A_Snapshot_With_The_Clients_Own_Validation()
    {
        var result = SystemOneResponse.FromHttpResponse(Raw(200, ClientTests.Result, ("X-TypeSafe-Request-Id", "req-cached")));

        result.Nouls["spam"].Noul.Should().Be(0.98);
        result.RequestId.Should().Be("req-cached", "headers given in any case are looked up in any case");
        result.RawHttpResponse.StatusCode.Should().Be(200);

        var typed = SystemOneResponse.FromHttpResponse<Ticket>(Raw(200, ClientTests.Result));
        typed.Spam.Should().BeSameAs(typed.Nouls["spam"]);

        ListModelsResponse.FromHttpResponse(Raw(200, """{"models": [{"name": "m", "description": "d", "release_date": "2026-01-01"}]}"""))
            .Models.Should().ContainSingle().Which.Name.Should().Be("m");
    }

    [TestMethod]
    public void Building_From_A_Snapshot_Fails_The_Same_Way_The_Client_Does()
    {
        FluentActions.Invoking(() => SystemOneResponse.FromHttpResponse(Raw(429, """{"message": "slow down"}""", ("retry-after-ms", "125"))))
            .Should().Throw<TypeSafeRateLimitException>()
            .Where(e => e.RetryAfter == TimeSpan.FromMilliseconds(125))
            .WithMessage("POST https://cache.test/v1/systemone: 429 slow down");

        FluentActions.Invoking(() => SystemOneResponse.FromHttpResponse(Raw(200, """{"model": "m", "usage": {}, "answers": {"n": {"type": "noul"}}}""")))
            .Should().Throw<TypeSafeApiResponseValidationException>().Which.FieldPath.Should().Be("answers.n.noul");
    }

    [TestMethod]
    public async Task A_Snapshot_Can_Be_Taken_From_A_Real_HttpResponseMessage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://user:pw@api.test/v1/systemone?token=secret#frag");
        using var message = Http.Json(200, ClientTests.Result, ("x-typesafe-request-id", "req-live"));
        message.RequestMessage = request;

        var raw = await RawHttpResponse.FromAsync(message);

        raw.Endpoint.Should().Be("POST https://api.test/v1/systemone", "no credentials, query or fragment");
        SystemOneResponse.FromHttpResponse(raw).RequestId.Should().Be("req-live");
    }

    private sealed class Ticket : SystemOneResponse
    {
        public NoulAnswer Spam { get; set; } = null!;
    }

    // ── the client as an interface ──────────────────────────────────────────────────────────────────────

    private sealed class Triage(ITypeSafeClient typesafe)
    {
        public async Task<bool> IsSpam(string text) =>
            (await typesafe.SystemOneAsync(text, new Dictionary<string, Question> { ["spam"] = new Noul("Spam?") })).Nouls["spam"].Noul > 0.5;
    }

    private sealed class FakeClient(string body) : ITypeSafeClient
    {
        public IModelsResource Models => throw new NotSupportedException();

        public Task<SystemOneResponse> SystemOneAsync(JsonContent state, IReadOnlyDictionary<string, Question> questions,
            SystemOneOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(SystemOneResponse.FromHttpResponse(Raw(200, body)));

        public Task<TResponse> SystemOneAsync<TResponse>(JsonContent state, IReadOnlyDictionary<string, Question> questions,
            SystemOneOptions? options = null, CancellationToken cancellationToken = default) where TResponse : SystemOneResponse, new() =>
            Task.FromResult(SystemOneResponse.FromHttpResponse<TResponse>(Raw(200, body)));

        public Task<TResponse> SystemOneAsync<TResponse>(JsonContent state, IReadOnlyDictionary<string, Question> questions,
            SystemOneOptions? options, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseTypeInfo,
            CancellationToken cancellationToken = default) where TResponse : class => throw new NotSupportedException();

        public Task<TResponse> SystemOneAsync<TResponse>(JsonContent state, IReadOnlyDictionary<string, Question> questions,
            SystemOneOptions? options, JsonSerializerOptions responseSerializerOptions,
            CancellationToken cancellationToken = default) where TResponse : class => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task Code_That_Depends_On_The_Interface_Can_Be_Handed_A_Fake_Returning_Genuine_Responses()
    {
        (await new Triage(new FakeClient(ClientTests.Result)).IsSpam("buy now")).Should().BeTrue();

        using var real = Clients.Create(_ => Http.Json(200, ClientTests.Result));
        ITypeSafeClient asInterface = real;
        (await new Triage(asInterface).IsSpam("buy now")).Should().BeTrue();
        asInterface.Models.Should().BeSameAs(real.Models);
    }

    // ── small things ────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_Sdk_Version_Is_Public_And_Is_What_The_User_Agent_Says()
    {
        TypeSafeDefaults.SdkVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[\w.]+)?$");
        TypeSafeDefaults.SdkVersion.Should().NotContain("+", "build metadata is not part of the version people quote");
    }

    [TestMethod]
    public void MostLikely_Is_The_Mode_Which_Is_Not_Always_Where_The_Average_Lands()
    {
        ScoreAnswer Score(double score, params (int Level, double P)[] levels) =>
            new(score, 0.5, new Dictionary<int, JsonNode>(), levels.ToDictionary(l => l.Level, l => l.P));

        // Opinion split between the extremes: the average says "1", a level almost nobody chose.
        var split = Score(1.0, (0, 0.48), (1, 0.04), (2, 0.48));
        split.MostLikely.Should().Be(0, "a tie goes to the lowest level");
        Math.Round(split.Score).Should().Be(1);

        Score(1.7, (0, 0.1), (1, 0.1), (2, 0.8)).MostLikely.Should().Be(2);
        Score(0).MostLikely.Should().BeNull("no probabilities, no mode");
    }
}
