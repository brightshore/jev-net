namespace Jev.Net.Tests;

/// <summary>Mirrors upstream <c>tests/test_questions.py</c> and <c>tests/test_types.py</c>.</summary>
[TestClass]
public sealed class QuestionTests
{
    private static JsonObject Raw(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static async Task<JsonNode> Sent(JsonContent state, Dictionary<string, Question> questions, string? model = null)
    {
        var handler = new StubHandler(_ => Http.Json(200, """{"model": "jev-latest", "usage": {}, "answers": {}}"""));
        using var client = Clients.Create(handler);
        await client.SystemOneAsync(state, questions, new SystemOneOptions { Model = model });
        return handler.Requests.Single().Json!;
    }

    [TestMethod]
    public void Encoding_Omits_Only_Fields_Left_Unset()
    {
        JsonAssert.Equal(new Noul().ToJson(), """{"type": "noul"}""");
        JsonAssert.Equal(new Choice(["a"]).ToJson(), """{"type": "choice", "criteria": {"a": null}}""");
        JsonAssert.Equal(new Score(["good"]).ToJson(), """{"type": "score", "criteria": ["good"]}""");
        JsonAssert.Equal(new Noul("", new NoulCriteria()).ToJson(), """{"type": "noul", "instructions": "", "criteria": {}}""");
        JsonAssert.Equal(new Noul(new JsonArray(), new NoulCriteria { True = JsonContent.Null }).ToJson(),
            """{"type": "noul", "instructions": [], "criteria": {"true": null}}""");
    }

    [TestMethod]
    public void Discriminators_Are_Automatic_And_Questions_Stay_Editable()
    {
        Question[] questions = [new Noul("Spam?"), new Choice(["calm"], "Tone?"), new Score(["good"], "Quality?")];
        questions.Select(q => q.Type).Should().Equal("noul", "choice", "score");
        questions.Select(q => q.ToJson()["type"]!.GetValue<string>()).Should().Equal("noul", "choice", "score");

        var noul = new Noul("Spam?") { Instructions = "Updated?" };
        noul.ToJson()["instructions"]!.GetValue<string>().Should().Be("Updated?");
    }

    [TestMethod]
    [DataRow("""{"type": "noul", "instructions": "Spam?", "weight": 3, "criteria": {"future": "kept"}}""")]
    [DataRow("""{"type": "future", "nested": {"k": null}}""")]
    [DataRow("""{"type": "score", "criteria": ["good"], "weight": 3}""")]
    public async Task Raw_Questions_Are_Sent_As_Given_And_Never_Mutated(string json)
    {
        var raw = Raw(json);
        var body = await Sent("x", new Dictionary<string, Question> { ["raw"] = raw, ["typed"] = new Noul("Spam?") });

        JsonAssert.Equal(body["questions"], """{"raw": <<0>>, "typed": {"type": "noul", "instructions": "Spam?"}}""".With(0, json));
        JsonAssert.Equal(raw, json);
        raw.Parent.Should().BeNull("the caller's object must not be re-parented into the request body");
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"instructions": "Missing type"}""")]
    [DataRow("""{"type": "choice"}""")]
    [DataRow("""{"type": "score"}""")]
    [DataRow("""{"type": ""}""")]
    [DataRow("""{"type": null}""")]
    [DataRow("""{"type": 1}""")]
    [DataRow("""{"type": ["future"]}""")]
    public void Raw_Questions_Need_Their_Structural_Keys(string json)
    {
        FluentActions.Invoking(() => Question.ValidateAll(new Dictionary<string, Question> { ["invalid"] = Raw(json) }))
            .Should().Throw<TypeSafeException>().WithMessage("*Question \"invalid\"*");
        FluentActions.Invoking(() => Question.ValidateAll(new Dictionary<string, Question> { ["invalid"] = null! }))
            .Should().Throw<TypeSafeException>().WithMessage("*Question \"invalid\"*");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Empty_Score_Criteria_Is_Rejected(bool raw)
    {
        Question question = raw ? Raw("""{"type": "score", "instructions": "Quality?", "criteria": []}""") : new Score(Array.Empty<string>(), "Quality?");
        FluentActions.Invoking(() => Question.ValidateAll(new Dictionary<string, Question> { ["rating"] = question }))
            .Should().Throw<TypeSafeException>().WithMessage("*\"rating\" has no criteria*");
    }

    [TestMethod]
    public void Typed_Questions_Reject_Bad_Shapes_At_Construction()
    {
        FluentActions.Invoking(() => new Choice((IReadOnlyDictionary<string, JsonContent?>)null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new Score([JsonContent.Null])).Should().Throw<ArgumentException>().WithMessage("*score level*");
        FluentActions.Invoking(() => new Score(["ok"]) { Criteria = [null!] }).Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Content_Is_Text_An_Object_Or_An_Array_And_Nothing_Else()
    {
        FluentActions.Invoking(() => { JsonContent _ = JsonValue.Create(42); }).Should().Throw<TypeSafeException>().WithMessage("*Number*");
        FluentActions.Invoking(() => { JsonContent _ = JsonValue.Create(true); }).Should().Throw<TypeSafeException>();
        FluentActions.Invoking(() => JsonContent.From<object?>(null)).Should().Throw<TypeSafeException>().WithMessage("*null*");
        FluentActions.Invoking(() => JsonContent.From(3.5)).Should().Throw<TypeSafeException>();

        ((JsonContent)"text").ToNode()!.GetValue<string>().Should().Be("text");
        JsonAssert.Equal(((JsonContent)Raw("""{"key": null}""")).ToNode(), """{"key": null}""");
        JsonAssert.Equal(JsonContent.From(new object?[] { "item", null }).ToNode(), """["item", null]""");
        JsonContent.Null.IsNull.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("""{"true": "Yes"}""")]
    [DataRow("""{"false": "No"}""")]
    [DataRow("""{"true": "Yes", "false": "No"}""")]
    [DataRow("""{"true": {"summary": "Unsolicited", "examples": ["Buy now"]}}""")]
    public void Optional_Noul_Criteria(string? criteria)
    {
        NoulCriteria? typed = null;
        if (criteria is not null)
        {
            var parsed = Raw(criteria);
            typed = new NoulCriteria
            {
                True = parsed.ContainsKey("true") ? (JsonContent)parsed["true"]! : null,
                False = parsed.ContainsKey("false") ? (JsonContent)parsed["false"]! : null,
            };
        }

        var expected = criteria is null
            ? """{"type": "noul", "instructions": "Spam?"}"""
            : """{"type": "noul", "instructions": "Spam?", "criteria": <<0>>}""".With(0, criteria);
        JsonAssert.Equal(new Noul("Spam?", typed).ToJson(), expected);
    }

    [TestMethod]
    public async Task Array_Inputs_Encode_At_Every_Level()
    {
        const string instructions = """["Read the message", {"context": null}]""";
        const string description = """["Example", null]""";
        JsonArray Instructions() => (JsonArray)JsonNode.Parse(instructions)!;
        JsonArray Description() => (JsonArray)JsonNode.Parse(description)!;

        var body = await Sent((JsonArray)JsonNode.Parse("""[{"message": "Classify"}, null]""")!, new Dictionary<string, Question>
        {
            ["yes"] = new Noul(Instructions(), new NoulCriteria { True = Description(), False = JsonContent.Null }),
            ["label"] = new Choice(new Dictionary<string, JsonContent?> { ["a"] = Description(), ["b"] = null }, Instructions()),
            ["rating"] = new Score([Description()], Instructions()),
        });

        JsonAssert.Equal(body["state"], """[{"message": "Classify"}, null]""");
        JsonAssert.Equal(body["questions"], """
            {"yes": {"type": "noul", "instructions": <<0>>, "criteria": {"true": <<1>>, "false": null}},
             "label": {"type": "choice", "instructions": <<0>>, "criteria": {"a": <<1>>, "b": null}},
             "rating": {"type": "score", "instructions": <<0>>, "criteria": [<<1>>]}}
            """.With(0, instructions).With(1, description));
    }

    [TestMethod]
    public async Task Raw_Optional_Fields_Preserve_An_Explicit_Null()
    {
        const string questions = """
            {"yes": {"type": "noul", "instructions": null, "criteria": null},
             "label": {"type": "choice", "instructions": null, "criteria": {"a": null}},
             "rating": {"type": "score", "instructions": null, "criteria": ["good"]}}
            """;
        var body = await Sent("x", Raw(questions).ToDictionary(p => p.Key, p => (Question)(JsonObject)p.Value!.DeepClone()));
        JsonAssert.Equal(body["questions"], questions);
    }

    [TestMethod]
    public async Task Nested_Nulls_Are_Values_Not_Omissions()
    {
        var body = await Sent(Raw("""{"missing": null, "items": [null, {"nested": null}]}"""), new Dictionary<string, Question>
        {
            ["label"] = new Choice(
                new Dictionary<string, JsonContent?> { ["a"] = JsonContent.Null, ["b"] = Raw("""{"extra": null}""") },
                Raw("""{"text": "Classify", "extra": null}""")),
        });

        JsonAssert.Equal(body["state"], """{"missing": null, "items": [null, {"nested": null}]}""");
        JsonAssert.Equal(body["questions"]!["label"], """
            {"type": "choice", "instructions": {"text": "Classify", "extra": null}, "criteria": {"a": null, "b": {"extra": null}}}
            """);
    }

    [TestMethod]
    public async Task Ordinary_Dotnet_Containers_Encode_Through_From()
    {
        var state = JsonContent.From(new Dictionary<string, object?> { ["items"] = new object?[] { "a", null } });
        var body = await Sent(state, new Dictionary<string, Question>
        {
            ["rating"] = new Score([JsonContent.From(new[] { "low", "lower" }), "high"]),
        }, model: "jev-latest");

        JsonAssert.Equal(body, """
            {"state": {"items": ["a", null]}, "model": "jev-latest",
             "questions": {"rating": {"type": "score", "criteria": [["low", "lower"], "high"]}}}
            """);
    }

    [TestMethod]
    public async Task One_Question_Object_Can_Be_Sent_Any_Number_Of_Times()
    {
        // The .NET-specific hazard: a JsonNode has ONE parent, so encoding by reference would work once and
        // throw "node already has a parent" on the second send.
        var shared = Raw("""{"summary": "shared"}""");
        var questions = new Dictionary<string, Question>
        {
            ["a"] = new Choice(new Dictionary<string, JsonContent?> { ["x"] = shared, ["y"] = shared }, shared),
            ["b"] = new Score([shared, shared], shared),
        };

        var first = await Sent(shared, questions);
        var second = await Sent(shared, questions);

        JsonNode.DeepEquals(first, second).Should().BeTrue();
        shared.Parent.Should().BeNull();
    }
}
