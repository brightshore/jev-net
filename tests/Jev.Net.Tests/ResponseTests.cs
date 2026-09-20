using System.Text.Json.Serialization;

namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_responses.py</c> and <c>test_pydantic_response_models.py</c>.</summary>
[TestClass]
public sealed class ResponseTests
{
    [TestMethod]
    [DataRow("{}", "model")]
    [DataRow("""{"n": {"type": "noul"}}""", "answers.n.noul")]
    [DataRow("""{"n": {"type": "noul", "noul": "0.5"}}""", "answers.n.noul")]
    [DataRow("""{"c": {"type": "choice", "choice": "a", "probabilities": {}}}""", "answers.c.confidence")]
    [DataRow("""{"c": {"type": "choice", "confidence": 0.5, "probabilities": {}}}""", "answers.c.choice")]
    [DataRow("""{"c": {"type": "choice", "choice": "a", "confidence": 0.5, "probabilities": {"a": "high"}}}""", "answers.c.probabilities.a")]
    [DataRow("""{"s": {"type": "score", "score": 1.0, "confidence": 1.0, "legend": [], "probabilities": {}}}""", "answers.s.legend")]
    [DataRow("""{"s": {"type": "score", "score": 1.0, "confidence": 1.0, "legend": {"x": "bad"}, "probabilities": {}}}""", "answers.s.legend.x")]
    [DataRow("""{"s": {"type": "score", "score": 1.0, "confidence": 1.0, "legend": {"0": 7}, "probabilities": {}}}""", "answers.s.legend.0")]
    [DataRow("""{"c": "not-a-mapping"}""", "answers.c.type")]
    [DataRow("""{"c": {"type": 5}}""", "answers.c.type")]
    public async Task A_Malformed_Response_Names_The_First_Bad_Field(string answers, string fieldPath)
    {
        var body = fieldPath == "model"
            ? """{"usage": {"input_tokens": 1, "output_tokens": 1}, "answers": <<0>>}""".With(0, answers)
            : """{"usage": {"input_tokens": 1, "output_tokens": 1}, "answers": <<0>>, "model": "test"}""".With(0, answers);
        using var client = Clients.Create(_ => Http.Json(200, body, ("x-typesafe-request-id", "req-123")));

        var caught = (await client.Invoking(c => c.SystemOneAsync("x", Clients.OneQuestion))
            .Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which;

        caught.FieldPath.Should().Be(fieldPath);
        caught.Status.Should().Be(200);
        caught.RequestId.Should().Be("req-123");
        JsonAssert.Equal(caught.Body, body);
        caught.Message.Should().Be(
            $"POST https://api.typesafe.ai/v1/systemone: 200 Invalid response data at '{fieldPath}'. (request_id=req-123)");
    }

    [TestMethod]
    [DataRow("""{"model": "m", "answers": {}}""", "usage")]
    [DataRow("""{"model": "m", "usage": {"input_tokens": "many"}}""", "usage.input_tokens")]
    [DataRow("""{"model": "m", "usage": {}, "answers": []}""", "answers")]
    [DataRow("[]", "")]
    public async Task Envelope_Fields_Are_Validated_Too(string body, string fieldPath)
    {
        using var client = Clients.Create(_ => Http.Json(200, body));
        (await client.Invoking(c => c.SystemOneAsync("x", Clients.OneQuestion))
            .Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which.FieldPath.Should().Be(fieldPath);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("description")]
    [DataRow("release_date")]
    public async Task A_Nested_Missing_Field_Is_Located_By_Index(string missing)
    {
        var good = new JsonObject { ["name"] = "test", ["description"] = "Test model", ["release_date"] = "2026-09-14" };
        var bad = (JsonObject)good.DeepClone();
        bad.Remove(missing);
        using var client = Clients.Create(_ => Http.Json(200, new JsonObject { ["models"] = new JsonArray(good, bad) }));

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which;

        caught.FieldPath.Should().Be($"models[1].{missing}");
        caught.Message.Should().EndWith($"200 Invalid response data at 'models[1].{missing}'.");
    }

    [TestMethod]
    public async Task A_Response_Carries_Its_Request_Id_And_Raw_Http_Response()
    {
        using var client = Clients.Create(_ => Http.Json(200, ClientTests.Result, ("x-typesafe-request-id", "req-7"), ("x-extra", "kept")));
        var result = await client.SystemOneAsync("x", Clients.OneQuestion);

        result.RequestId.Should().Be("req-7");
        result.RawHttpResponse.StatusCode.Should().Be(200);
        result.RawHttpResponse.Headers["X-EXTRA"].Should().Be("kept", "header lookup is case-insensitive");
        result.RawHttpResponse.Endpoint.Should().Be("POST https://api.typesafe.ai/v1/systemone");
        JsonAssert.Equal(result.RawHttpResponse.Json(), ClientTests.Result);
    }

    [TestMethod]
    public async Task Http_Metadata_Is_Not_Part_Of_The_Serialized_Response()
    {
        using var client = Clients.Create(_ => Http.Json(200, ClientTests.Result, ("x-typesafe-request-id", "req-7")));
        var result = await client.SystemOneAsync("x", Clients.OneQuestion);

        var json = JsonSerializer.SerializeToNode(result)!.AsObject();
        json.Select(p => p.Key).Should().BeEquivalentTo("Model", "Usage", "Answers");
    }

    [TestMethod]
    public async Task Missing_Metadata_Throws_On_Access_Rather_Than_Returning_Null()
    {
        new SystemOneResponse().Invoking(r => r.RawHttpResponse).Should().Throw<TypeSafeException>().WithMessage("*raw HTTP response*");

        using var client = Clients.Create(_ => Http.Json(200, ClientTests.Result));
        var result = await client.SystemOneAsync("x", Clients.OneQuestion);
        result.Invoking(r => r.RequestId).Should().Throw<TypeSafeException>().WithMessage("*request ID*");
    }

    [TestMethod]
    public async Task Unknown_Extra_Fields_Are_Tolerated()
    {
        const string body = """
            {"model": "test",
             "usage": {"input_tokens": 1, "output_tokens": 1, "reasoning_tokens": 9, "billing_units": 1},
             "answers": {"spam": {"type": "noul", "noul": 0.9, "explanation": "spammy"}},
             "brand_new_top_level": true}
            """;
        using var client = Clients.Create(_ => Http.Json(200, body));
        var result = await client.SystemOneAsync("x", Clients.OneQuestion);

        result.Nouls["spam"].Noul.Should().Be(0.9);
        result.Usage.Should().Be(new Usage(1, 1));
        JsonAssert.Equal(result.RawHttpResponse.Json(), body);
    }

    [TestMethod]
    public async Task Usage_Counts_Are_Optional()
    {
        using var client = Clients.Create(_ => Http.Json(200, """{"model": "jev-latest", "usage": {"output_tokens": null}, "answers": {}}"""));
        var result = await client.SystemOneAsync("x", Clients.OneQuestion);
        result.Usage.Should().Be(new Usage(null, null));
        result.Answers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task An_Unknown_Answer_Type_Is_Skipped_Not_Raised()
    {
        var logs = new ListLoggerFactory();
        var handler = new StubHandler(_ => Http.Json(200, """
            {"model": "test", "usage": {"input_tokens": 1, "output_tokens": 1},
             "answers": {"spam": {"type": "noul", "noul": 0.9}, "mystery": {"type": "aurora", "value": 3}}}
            """));
        using var client = Clients.Create(handler, loggerFactory: logs);

        var result = await client.SystemOneAsync("text", Clients.OneQuestion);

        result.Answers.Keys.Should().BeEquivalentTo("spam");
        result.RawHttpResponse.Json()!["answers"]!["mystery"]!["type"]!.GetValue<string>().Should().Be("aurora");
        logs.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("aurora"));
    }

    [TestMethod]
    public async Task A_Legend_Preserves_Nested_Json()
    {
        const string level = """{"summary": "bad", "examples": ["a", null, {"deep": [1, 2.5, true]}]}""";
        using var client = Clients.Create(_ => Http.Json(200, """
            {"model": "m", "usage": {}, "answers": {"s": {"type": "score", "score": 0.0, "confidence": 1.0,
              "legend": {"0": <<0>>, "1": ["array", "level"]}, "probabilities": {"0": 1.0, "1": 0.0}}}}
            """.With(0, level)));
        var score = (await client.SystemOneAsync("x", Clients.OneQuestion)).Scores["s"];

        JsonAssert.Equal(score.Legend[0], level);
        JsonAssert.Equal(score.Legend[1], """["array", "level"]""");
    }

    [TestMethod]
    public void Answers_Are_Immutable_Values_With_Their_Discriminator()
    {
        new NoulAnswer(0.5).Should().Be(new NoulAnswer(0.5));
        new NoulAnswer(0.5).Type.Should().Be("noul");
        new ChoiceAnswer("a", 1, new Dictionary<string, double>()).Type.Should().Be("choice");
        new ScoreAnswer(1, 1, new Dictionary<int, JsonNode>(), new Dictionary<int, double>()).Type.Should().Be("score");
        typeof(NoulAnswer).GetProperty(nameof(NoulAnswer.Noul))!.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit), "init-only is how a record stays frozen");
    }

    // ── custom response types ───────────────────────────────────────────────────────────────────────────

    private sealed class TicketResponse : SystemOneResponse
    {
        public NoulAnswer Spam { get; set; } = null!;
        public ChoiceAnswer Tone { get; set; } = null!;
        [JsonPropertyName("quality")] public ScoreAnswer HowGood { get; set; } = null!;
        [OptionalAnswer] public NoulAnswer? NotAsked { get; set; }
    }

    private sealed class WrongKind : SystemOneResponse
    {
        public ChoiceAnswer Spam { get; set; } = null!;
    }

    private sealed class MissingAnswer : SystemOneResponse
    {
        public NoulAnswer BillingIssue { get; set; } = null!;
    }

    // Nullable, but NOT marked optional: nullability is metadata the trimmer removes, so it must not be what
    // decides this — or the same class would validate differently in a Native AOT build.
    private sealed class NullableButRequired : SystemOneResponse
    {
        public NoulAnswer? BillingIssue { get; set; }
    }

    [TestMethod]
    public async Task A_Derived_Response_Gets_Its_Answers_By_Name_And_Type()
    {
        using var client = Clients.Create(_ => Http.Json(200, ClientTests.Result, ("x-typesafe-request-id", "req-1")));
        var result = await client.SystemOneAsync<TicketResponse>("x", Clients.OneQuestion);

        result.Spam.Should().BeSameAs(result.Nouls["spam"]);
        result.Tone.Should().BeSameAs(result.Choices["tone"]);
        result.HowGood.Should().BeSameAs(result.Scores["quality"]);
        result.NotAsked.Should().BeNull("[OptionalAnswer] is what makes a missing answer acceptable");
        result.RequestId.Should().Be("req-1");
    }

    [TestMethod]
    public async Task A_Derived_Response_Whose_Answer_Is_Missing_Or_The_Wrong_Kind_Is_A_Validation_Error()
    {
        using var client = Clients.Create(_ => Http.Json(200, ClientTests.Result));

        (await client.Invoking(c => c.SystemOneAsync<WrongKind>("x", Clients.OneQuestion))
            .Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which.FieldPath.Should().Be("spam");
        (await client.Invoking(c => c.SystemOneAsync<MissingAnswer>("x", Clients.OneQuestion))
            .Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which.FieldPath.Should().Be("billing_issue");
        (await client.Invoking(c => c.SystemOneAsync<NullableButRequired>("x", Clients.OneQuestion))
            .Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which.FieldPath.Should().Be("billing_issue");
    }

    [TestMethod]
    [DataRow("source-generated")]
    [DataRow("reflection")]
    public async Task Any_Other_Type_Is_Deserialized_From_The_Body_With_A_Located_Failure(string how)
    {
        Task<Envelope> Ask(TypeSafeClient client) => how == "reflection"
            ? client.SystemOneAsync<Envelope>("x", Clients.OneQuestion, ResponseJson.SnakeCase)
            : client.SystemOneAsync("x", Clients.OneQuestion, TestJsonContext.Default.Envelope);

        using var ok = Clients.Create(_ => Http.Json(200, ClientTests.Result, ("x-typesafe-request-id", "req-9")));
        (await Ask(ok)).Should().Be(new Envelope("jev-latest", new EnvelopeUsage(12, 3)));

        using var bad = Clients.Create(_ => Http.Json(200, """{"model": "m", "usage": {"input_tokens": "many", "output_tokens": 1}}"""));
        var caught = (await FluentActions.Awaiting(() => Ask(bad)).Should().ThrowAsync<TypeSafeApiResponseValidationException>()).Which;
        caught.FieldPath.Should().Be("usage.input_tokens");
        caught.Cause.Should().BeOfType<JsonException>();

        using var failed = Clients.Create(_ => Http.Json(401, """{"message": "no"}"""));
        await FluentActions.Awaiting(() => Ask(failed)).Should().ThrowAsync<TypeSafeAuthenticationException>();
    }
}

internal sealed record Envelope(string Model, EnvelopeUsage Usage);

internal sealed record EnvelopeUsage(int InputTokens, int OutputTokens);

internal sealed record Ticket(string Subject, string[] Tags);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Ticket))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
