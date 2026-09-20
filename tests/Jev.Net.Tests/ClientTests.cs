using System.Text;

namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_clients.py</c>.</summary>
[TestClass]
public sealed class ClientTests
{
    internal const string Result = """
        {
          "model": "jev-latest",
          "usage": {"input_tokens": 12, "output_tokens": 3},
          "answers": {
            "spam": {"type": "noul", "noul": 0.98},
            "tone": {"type": "choice", "choice": "friendly", "confidence": 0.9, "probabilities": {"friendly": 0.9, "hostile": 0.1}},
            "quality": {"type": "score", "score": 1.7, "confidence": 0.8,
                        "legend": {"0": "bad", "1": "ok", "2": "great"},
                        "probabilities": {"0": 0.1, "1": 0.1, "2": 0.8}}
          }
        }
        """;

    private const string Card = """{"name": "jev-latest", "description": "Fast model", "release_date": "2026-08-01"}""";

    private static JsonObject Raw(string json) => (JsonObject)JsonNode.Parse(json)!;

    [TestMethod]
    [DataRow("typed")]
    [DataRow("raw")]
    [DataRow("mixed")]
    public async Task RoundTrip(string form)
    {
        var raw = new Dictionary<string, Question>
        {
            ["spam"] = Raw("""{"type": "noul", "instructions": "Spam?"}"""),
            ["tone"] = Raw("""{"type": "choice", "instructions": "Tone?", "criteria": {"friendly": null, "hostile": null}}"""),
            ["quality"] = Raw("""{"type": "score", "instructions": "Quality?", "criteria": ["bad", "ok", "great"]}"""),
        };
        var questions = form == "raw" ? raw : new Dictionary<string, Question>
        {
            ["spam"] = form == "typed" ? new Noul("Spam?") : raw["spam"],
            ["tone"] = new Choice(["friendly", "hostile"], "Tone?"),
            ["quality"] = new Score(["bad", "ok", "great"], "Quality?"),
        };

        var handler = new StubHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.Url.ToString().Should().Be("https://api.typesafe.ai/v1/systemone");
            JsonAssert.Equal(request.Json, """
                {
                  "state": {"document": "Hello 🌍"},
                  "model": "jev-latest",
                  "questions": {
                    "spam": {"type": "noul", "instructions": "Spam?"},
                    "tone": {"type": "choice", "instructions": "Tone?", "criteria": {"friendly": null, "hostile": null}},
                    "quality": {"type": "score", "instructions": "Quality?", "criteria": ["bad", "ok", "great"]}
                  }
                }
                """);
            request.Header("content-type").Should().Be("application/json");
            return Http.Json(200, Result);
        });

        using var client = Clients.Create(handler);
        var result = await client.SystemOneAsync(new JsonObject { ["document"] = "Hello 🌍" }, questions);

        result.Nouls.Should().BeSameAs(result.Nouls, "the groups are computed once");
        result.Choices.Should().BeSameAs(result.Choices);
        result.Scores.Should().BeSameAs(result.Scores);
        result.Model.Should().Be("jev-latest");
        result.Usage.Should().Be(new Usage(12, 3));
        result.Nouls.Keys.Should().BeEquivalentTo("spam");
        result.Choices.Keys.Should().BeEquivalentTo("tone");
        result.Scores.Keys.Should().BeEquivalentTo("quality");
        result.Nouls["spam"].Noul.Should().Be(0.98);
        result.Choices["tone"].Choice.Should().Be("friendly");
        result.Choices["tone"].Confidence.Should().Be(0.9);
        result.Choices["tone"].Probabilities.Should().BeEquivalentTo(new Dictionary<string, double> { ["friendly"] = 0.9, ["hostile"] = 0.1 });
        result.Scores["quality"].Score.Should().Be(1.7);
        result.Scores["quality"].Confidence.Should().Be(0.8);
        result.Scores["quality"].Legend.Keys.Should().BeEquivalentTo([0, 1, 2]);
        result.Scores["quality"].Legend[2].GetValue<string>().Should().Be("great");
        result.Scores["quality"].Probabilities.Should().BeEquivalentTo(new Dictionary<int, double> { [0] = 0.1, [1] = 0.1, [2] = 0.8 });
        result.Answers["quality"].Should().BeSameAs(result.Scores["quality"]);
        result.Answers["spam"].Should().BeSameAs(result.Nouls["spam"]);
        result.Answers["tone"].Should().BeSameAs(result.Choices["tone"]);
        result.Answers.Keys.Should().BeEquivalentTo("spam", "tone", "quality");
    }

    [TestMethod]
    public async Task ExtraBody_Is_A_Shallow_Last_Write_Wins_Merge()
    {
        var handler = new StubHandler(request =>
        {
            JsonAssert.Equal(request.Json, """
                {"state": "hi", "model": "override-model", "questions": {"q": {"type": "noul", "instructions": "?"}},
                 "beam_width": 4, "nullable": null}
                """);
            return Http.Json(200, Result);
        });

        using var client = Clients.Create(handler);
        await client.SystemOneAsync("hi", Clients.OneQuestion, new SystemOneOptions
        {
            Model = "call-model",
            ExtraBody = new Dictionary<string, JsonNode?> { ["model"] = "override-model", ["beam_width"] = 4, ["nullable"] = null },
        });
        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    public void Unserializable_Content_Is_Rejected_Before_Any_Request_Exists()
    {
        var act = () => JsonContent.From(new { Bad = (Action)(() => { }) });
        act.Should().Throw<TypeSafeException>().WithMessage("*could not be encoded as JSON*");
    }

    [TestMethod]
    public async Task Raw_Questions_Pass_Through_With_Fields_The_Sdk_Does_Not_Model()
    {
        const string questions = """
            {"q": {"type": "noul", "instructions": "Spam?", "weight": 3, "nested": {"k": null}},
             "choice": {"type": "choice", "criteria": {"a": null}, "weight": 2},
             "score": {"type": "score", "criteria": ["good"], "weight": 1}}
            """;
        var handler = new StubHandler(request =>
        {
            JsonAssert.Equal(request.Json, """{"state": "hi", "model": "jev-latest", "questions": <<0>>}""".With(0, questions));
            return Http.Json(200, Result);
        });

        using var client = Clients.Create(handler);
        await client.SystemOneAsync("hi", Raw(questions).ToDictionary(p => p.Key, p => (Question)(JsonObject)p.Value!.DeepClone()));
        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    [DataRow("""{"type": "noul", "instructions": 1}""")]
    [DataRow("""{"type": "choice", "criteria": ["invalid", "shape"]}""")]
    public async Task Raw_Question_Schema_Validation_Is_Left_To_The_Api(string question)
    {
        var handler = new StubHandler(request =>
        {
            JsonAssert.Equal(request.Json!["questions"]!["q"], question);
            return Http.Json(422, """{"detail": "Invalid question"}""");
        });

        using var client = Clients.Create(handler);
        var act = () => client.SystemOneAsync("x", new Dictionary<string, Question> { ["q"] = Raw(question) });
        (await act.Should().ThrowAsync<TypeSafeUnprocessableEntityException>()).WithMessage("*Invalid question*");
    }

    [TestMethod]
    public async Task Rich_Descriptions_Survive_Both_Directions()
    {
        const string criteria = """{"summary": "duplicated", "examples": ["charged twice"]}""";
        var handler = new StubHandler(request =>
        {
            JsonAssert.Equal(request.Json, """
                {"state": "a ticket", "model": "custom", "questions": {
                  "duplicate": {"type": "noul", "instructions": {"question": "Duplicate?"}, "criteria": {"true": <<0>>}},
                  "team": {"type": "choice", "instructions": "Team?", "criteria": {"billing": <<0>>, "other": null}},
                  "risk": {"type": "score", "instructions": "Risk?", "criteria": [<<0>>]}}}
                """.With(0, criteria));
            return Http.Json(200, """
                {"model": "custom", "usage": {"input_tokens": 1, "output_tokens": 1}, "answers": {
                  "risk": {"type": "score", "score": 0, "confidence": 1, "legend": {"0": <<0>>}, "probabilities": {"0": 1}}}}
                """.With(0, criteria));
        });

        using var client = Clients.Create(handler);
        var result = await client.SystemOneAsync("a ticket", new Dictionary<string, Question>
        {
            ["duplicate"] = new Noul(Raw("""{"question": "Duplicate?"}"""), new NoulCriteria { True = Raw(criteria) }),
            ["team"] = new Choice(new Dictionary<string, JsonContent?> { ["billing"] = Raw(criteria), ["other"] = null }, "Team?"),
            ["risk"] = new Score([Raw(criteria)], "Risk?"),
        }, new SystemOneOptions { Model = "custom" });

        JsonAssert.Equal(result.Scores["risk"].Legend[0], criteria);
        result.Scores["risk"].Score.Should().Be(0, "an integer literal is a valid number for a float field");
    }

    [TestMethod]
    public async Task Models_Shape()
    {
        var handler = new StubHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.Url.AbsolutePath.Should().Be("/v1/models");
            request.Content.Should().BeEmpty();
            return Http.Json(200, """{"models": [<<0>>]}""".With(0, Card));
        });

        using var client = Clients.Create(handler);
        (await client.Models.ListAsync()).Models.Should().Equal(new ModelMetadata("jev-latest", "Fast model", "2026-08-01"));
    }

    [TestMethod]
    public async Task Models_Ignore_Unknown_Fields_But_Keep_Them_In_The_Raw_Response()
    {
        using var client = Clients.Create(_ => Http.Json(200, """
            {"models": [{"name": "jev-latest", "description": "Fast model", "release_date": "2026-08-01",
                         "context_window": 128000, "pricing": null}]}
            """));
        var response = await client.Models.ListAsync();
        response.Models.Should().ContainSingle().Which.Name.Should().Be("jev-latest");
        response.RawHttpResponse.Json()!["models"]![0]!["context_window"]!.GetValue<int>().Should().Be(128000);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("""{"models": "bad"}""")]
    [DataRow("""{"models": [{"name": "x"}]}""")]
    [DataRow("not json at all")]
    public async Task Invalid_Models_Response(string body)
    {
        using var client = Clients.Create(_ => Http.Json(200, body));
        await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiResponseValidationException>();
    }

    [TestMethod]
    public async Task Validation_Happens_Before_The_Network()
    {
        var handler = new StubHandler(_ => throw new AssertFailedException("Invalid questions reached the network"));
        using var client = Clients.Create(handler);

        (await client.Invoking(c => c.SystemOneAsync("x", new Dictionary<string, Question>()))
            .Should().ThrowAsync<TypeSafeException>()).WithMessage("*At least one question*");
        (await client.Invoking(c => c.SystemOneAsync("x", new Dictionary<string, Question> { ["rating"] = new Score(Array.Empty<string>(), "?") }))
            .Should().ThrowAsync<TypeSafeException>()).WithMessage("*\"rating\" has no criteria*");
        (await client.Invoking(c => c.SystemOneAsync(JsonContent.Null, Clients.OneQuestion))
            .Should().ThrowAsync<TypeSafeException>()).WithMessage("*State must be*");
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(400, typeof(TypeSafeBadRequestException))]
    [DataRow(401, typeof(TypeSafeAuthenticationException))]
    [DataRow(403, typeof(TypeSafePermissionDeniedException))]
    [DataRow(404, typeof(TypeSafeNotFoundException))]
    [DataRow(422, typeof(TypeSafeUnprocessableEntityException))]
    [DataRow(429, typeof(TypeSafeRateLimitException))]
    [DataRow(500, typeof(TypeSafeInternalServerException))]
    [DataRow(503, typeof(TypeSafeInternalServerException))]
    [DataRow(408, typeof(TypeSafeApiException))]
    [DataRow(409, typeof(TypeSafeApiException))]
    [DataRow(302, typeof(TypeSafeApiException))]
    public async Task Error_Mapping(int status, Type expected)
    {
        using var client = Clients.Create(_ => Http.Json(status, """{"detail": {"message": "Server explanation"}}""",
            ("x-typesafe-request-id", "req_123"), ("retry-after-ms", "125")));

        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiException>()).Which;

        caught.GetType().Should().Be(expected, "the mapping is exact, not merely assignable");
        caught.Status.Should().Be(status);
        JsonAssert.Equal(caught.Body, """{"detail": {"message": "Server explanation"}}""");
        caught.RequestId.Should().Be("req_123");
        caught.Message.Should().Be($"GET https://api.typesafe.ai/v1/models: {status} Server explanation (request_id=req_123)");
        caught.Headers["retry-after-ms"].Should().Be("125");
        if (caught is TypeSafeRateLimitException limited)
        {
            limited.RetryAfter.Should().Be(TimeSpan.FromMilliseconds(125));
        }
    }

    [TestMethod]
    [DataRow("""{"error": "error", "message": "message", "detail": "detail"}""", "error")]
    [DataRow("""{"error": {"message": "nested error"}, "message": "message"}""", "nested error")]
    [DataRow("""{"message": "message", "detail": "detail"}""", "message")]
    [DataRow("""{"detail": "detail"}""", "detail")]
    [DataRow("""{"detail": {"message": "nested detail"}}""", "nested detail")]
    [DataRow("""{"detail": [{"loc": ["body", "questions", "q", "score", "criteria", 0], "msg": "Invalid"}, {"msg": "Missing"}, {}]}""",
        "questions.q.score.criteria.0: Invalid; Missing")]
    [DataRow("plain text", "plain text")]
    [DataRow("""{"unexpected":true}""", """{"unexpected":true}""")]
    public async Task Error_Messages(string body, string message)
    {
        using var client = Clients.Create(_ => Http.Bytes(400, Encoding.UTF8.GetBytes(body)));
        var caught = (await client.Invoking(c => c.Models.ListAsync()).Should().ThrowAsync<TypeSafeApiException>()).Which;
        caught.Message.Should().Be($"GET https://api.typesafe.ai/v1/models: 400 {message}");
    }

    [TestMethod]
    [DataRow("connect", typeof(TypeSafeApiConnectionException))]
    [DataRow("io", typeof(TypeSafeApiConnectionException))]
    [DataRow("timeout", typeof(TypeSafeApiTimeoutException))]
    [DataRow("httpclient-timeout", typeof(TypeSafeApiTimeoutException))]
    public async Task Transport_Errors(string kind, Type expected)
    {
        Exception thrown = kind switch
        {
            "connect" => new HttpRequestException("failed"),
            "io" => new IOException("failed"),
            "timeout" => new TimeoutException("failed"),
            _ => new TaskCanceledException("failed", new TimeoutException()),
        };
        using var client = Clients.Create(new StubHandler(_ => throw thrown));

        var caught = (await client.Invoking(c => c.Models.ListAsync(new RequestOptions { Timeout = TimeSpan.FromSeconds(1.25) }))
            .Should().ThrowAsync<TypeSafeApiConnectionException>()).Which;

        caught.GetType().Should().Be(expected);
        caught.InnerException.Should().BeSameAs(thrown);
        if (caught is TypeSafeApiTimeoutException timedOut)
        {
            timedOut.Timeout.Should().Be(TimeSpan.FromSeconds(1.25));
            timedOut.Message.Should().Be("Request timed out (timeout=1.25s).");
        }
    }

    [TestMethod]
    public async Task The_Per_Attempt_Deadline_Is_Real_And_A_Per_Call_Timeout_Overrides_The_Client()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Http.Json(200, "{}");
        });
        using var client = Clients.Create(handler, timeout: TimeSpan.FromMinutes(5));

        var caught = (await client.Invoking(c => c.Models.ListAsync(new RequestOptions { Timeout = TimeSpan.FromMilliseconds(40) }))
            .Should().ThrowAsync<TypeSafeApiTimeoutException>()).Which;
        caught.Timeout.Should().Be(TimeSpan.FromMilliseconds(40));
    }

    [TestMethod]
    public async Task Headers_Are_Protected_And_Nothing_Secret_Is_Logged()
    {
        var injected = new Dictionary<string, string>
        {
            ["authorization"] = "injected-secret", ["accept"] = "text/plain", ["user-agent"] = "wrong",
            ["x-typesafe-sdk"] = "wrong", ["x-typesafe-runtime"] = "wrong",
        };
        var handler = new StubHandler(request =>
        {
            request.Url.ToString().Should().Be("https://example.test/prefix/v1/systemone");
            request.Header("authorization").Should().Be("Bearer test-key");
            request.Header("accept").Should().Be("application/json");
            request.Header("user-agent").Should().MatchRegex(@"^jev-net/\d+\.\d+\.\d+");
            request.Header("x-typesafe-sdk").Should().Be(request.Header("user-agent"));
            request.Header("x-typesafe-runtime").Should().StartWith("dotnet/");
            request.Headers.Should().NotContainKey("x-typesafe-retry-count");
            request.Header("x-team").Should().Be("call");
            request.Header("x-default").Should().Be("kept");
            request.Header("content-type").Should().Be("application/json");
            return Http.Json(200, Result, ("set-cookie", "response-secret"), ("x-typesafe-request-id", "req_log"));
        });

        var logs = new ListLoggerFactory();
        using var client = Clients.Create(handler, baseUrl: "https://example.test/prefix///", loggerFactory: logs,
            headers: new Dictionary<string, string>(injected)
            {
                ["X-Team"] = "default", ["X-Default"] = "kept", ["X-API-Key"] = "key-secret", ["cookie"] = "cookie-secret",
            });

        await client.SystemOneAsync("hello", Clients.OneQuestion, new SystemOneOptions
        {
            ExtraHeaders = new Dictionary<string, string>(injected)
            {
                ["x-team"] = "call", ["x-typesafe-retry-count"] = "99", ["content-type"] = "wrong",
            },
        });

        handler.Requests.Should().HaveCount(1);
        foreach (var secret in new[] { "test-key", "injected-secret", "key-secret", "cookie-secret", "response-secret" })
        {
            logs.Text.Should().NotContain(secret);
        }

        logs.Text.Should().Contain("req_log").And.Contain("hello", "bodies are logged at Debug, and say so in the docs");
    }

    [TestMethod]
    public void An_Owned_Handler_Is_Disposed_With_The_Client()
    {
        var handler = new StubHandler(_ => Http.Json(200, "{}"));
        var client = Clients.Create(handler);
        client.Dispose();
        client.Dispose();
        handler.DisposeCalls.Should().Be(1);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task A_Supplied_HttpClient_Is_Disposed_Unless_Told_Otherwise(bool dispose)
    {
        var handler = new StubHandler(_ => Http.Json(200, """{"models": []}"""));
        var http = new HttpClient(handler);
        await using (var client = new TypeSafeClient(new TypeSafeClientOptions
        {
            ApiKey = "k", HttpClient = http, DisposeHttpClient = dispose, EnvironmentReader = Clients.NoEnvironment,
        }))
        {
            (await client.Models.ListAsync()).Models.Should().BeEmpty();
        }

        handler.DisposeCalls.Should().Be(dispose ? 1 : 0);
        http.Dispose();
    }

    [TestMethod]
    public async Task A_Disposed_Client_Refuses_Work()
    {
        var client = Clients.Create(_ => Http.Json(200, Result));
        client.Dispose();
        await client.Invoking(c => c.SystemOneAsync("x", Clients.OneQuestion)).Should().ThrowAsync<ObjectDisposedException>();
    }

    // Both rows matter. WITH retries a mis-classified cancellation still ends as a cancellation — the retry
    // wait observes the same token — so only the no-retry row can tell "cancelled" from "timed out". Found by
    // mutation: the single retrying version of this test passed against code that reported a timeout.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Caller_Cancellation_Propagates_As_Cancellation_Not_As_A_Timeout(bool retries)
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Http.Json(200, "{}");
        });
        using var client = Clients.Create(handler, retries: retries);

        var call = client.Models.ListAsync(cancellationToken: cts.Token);
        await entered.Task;
        cts.Cancel();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().HaveCount(1, "a cancelled call is never retried");
    }
}
