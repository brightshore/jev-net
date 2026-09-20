namespace Jev.Net.Tests;

/// <summary>
/// Mirrors upstream <c>tests/test_integration.py</c>: the ONE class that talks to the real API. Opt-in, like the
/// Postgres lanes — it needs <c>TYPESAFE_LIVE_TESTS=1</c> AND a key, so an ordinary local run and every CI leg
/// stay offline (and free). A key that merely happens to be exported does not start spending it.
/// </summary>
[TestClass]
public sealed class LiveIntegrationTests
{
    private sealed class TicketResponse : SystemOneResponse
    {
        public NoulAnswer Billing { get; set; } = null!;
        public ChoiceAnswer Tone { get; set; } = null!;
        public ScoreAnswer Urgency { get; set; } = null!;
    }

    private static readonly Dictionary<string, Question> Questions = new()
    {
        ["billing"] = (JsonObject)JsonNode.Parse("""
            {"type": "noul", "instructions": "Is this ticket about billing?",
             "criteria": {"true": {"meaning": "Payments or invoices", "examples": ["charged twice"]}}}
            """)!,
        ["tone"] = new Choice(["calm", "frustrated", "angry"], "What is the customer's tone?"),
        ["urgency"] = new Score(["can wait", "this week", "today"], "How urgent is this ticket?"),
    };

    private static readonly JsonObject State = new()
    {
        ["subject"] = "Charged twice this month",
        ["body"] = "I see two charges of $49. I only have one account. Please fix this ASAP.",
    };

    private static TypeSafeClient LiveClient()
    {
        if (Environment.GetEnvironmentVariable("TYPESAFE_LIVE_TESTS") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TypeSafeDefaults.ApiKeyEnv)))
        {
            Assert.Inconclusive($"Live test: set TYPESAFE_LIVE_TESTS=1 and {TypeSafeDefaults.ApiKeyEnv} to run it.");
        }

        return new TypeSafeClient(new TypeSafeClientOptions { Timeout = TimeSpan.FromSeconds(120) });
    }

    [TestMethod]
    public async Task Live_Models()
    {
        using var client = LiveClient();
        var models = (await client.Models.ListAsync()).Models;
        models.Should().NotBeEmpty();
        models.Should().OnlyContain(m => m.Name.Length > 0 && m.ReleaseDate.Length > 0);
    }

    [TestMethod]
    public async Task Live_Questions()
    {
        using var client = LiveClient();
        var result = await client.SystemOneAsync(State, Questions);

        result.Model.Should().NotBeEmpty();
        result.Nouls["billing"].Noul.Should().BeInRange(0, 1);
        result.Choices["tone"].Choice.Should().BeOneOf("calm", "frustrated", "angry");
        result.Choices["tone"].Probabilities.Values.Sum().Should().BeApproximately(1, 0.1);
        result.Scores["urgency"].Score.Should().BeInRange(0, 2);
        result.Scores["urgency"].Legend.Keys.Should().BeEquivalentTo([0, 1, 2]);
        result.Scores["urgency"].Probabilities.Values.Sum().Should().BeApproximately(1, 0.1);
        result.RequestId.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task Live_Typed_Response()
    {
        using var client = LiveClient();
        var result = await client.SystemOneAsync<TicketResponse>(State, Questions);

        result.Billing.Should().BeSameAs(result.Nouls["billing"]);
        result.Tone.Should().BeSameAs(result.Choices["tone"]);
        result.Urgency.Should().BeSameAs(result.Scores["urgency"]);
    }
}
