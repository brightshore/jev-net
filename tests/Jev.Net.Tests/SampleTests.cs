using Jev.Net.Samples;

namespace Jev.Net.Tests;

/// <summary>The samples, run offline. They are documentation that has to keep working, and each one's point —
/// the confidence gate, "cannot choose what you did not offer", re-ranking without a call — is asserted.</summary>
[TestClass]
public sealed class SampleTests
{
    private static string Answers(string answers) =>
        """{"model": "jev-latest", "usage": {"input_tokens": 1, "output_tokens": 1}, "answers": <<0>>}""".With(0, answers);

    private static string Intent(string choice, double confidence, double refundFull = 0.1, double cancelNow = 0.1) => Answers("""
        {"intent": {"type": "choice", "choice": "<<0>>", "confidence": <<1>>, "probabilities": {"<<0>>": 1.0}},
         "refund_full": {"type": "noul", "noul": <<2>>}, "cancel_now": {"type": "noul", "noul": <<3>>}}
        """.With(0, choice).With(1, confidence).With(2, refundFull).With(3, cancelNow));

    [TestMethod]
    public async Task Routing_Reads_Only_The_Chosen_Branchs_Speculative_Answer()
    {
        using var client = Clients.Create(_ => Http.Json(200, Intent("refund", 0.95, refundFull: 0.9, cancelNow: 0.9)));
        (await IntentRouting.RouteAsync(client, "charged twice")).Should().Be(new IntentRouting.Refund(WantsFullAmount: true));

        using var cancel = Clients.Create(_ => Http.Json(200, Intent("cancel", 0.95, refundFull: 0.9, cancelNow: 0.2)));
        (await IntentRouting.RouteAsync(cancel, "stop my plan")).Should().Be(new IntentRouting.Cancel(Immediately: false));
    }

    [TestMethod]
    public async Task Routing_Sends_Low_Confidence_And_No_Match_To_A_Person()
    {
        using var unsure = Clients.Create(_ => Http.Json(200, Intent("refund", 0.41)));
        (await IntentRouting.RouteAsync(unsure, "hmm")).Should().BeOfType<IntentRouting.HumanReview>()
            .Which.Why.Should().Contain("0.41");

        using var none = Clients.Create(_ => Http.Json(200, Intent("none_of_these", 0.99)));
        (await IntentRouting.RouteAsync(none, "asdf")).Should().BeOfType<IntentRouting.HumanReview>();
    }

    [TestMethod]
    public async Task One_Round_Trip_Carries_The_Intent_And_Every_Speculative_Question()
    {
        var handler = new StubHandler(_ => Http.Json(200, Intent("talk", 0.9)));
        using var client = Clients.Create(handler);
        (await IntentRouting.RouteAsync(client, "hello")).Should().Be(new IntentRouting.Talk());

        handler.Requests.Should().ContainSingle().Which.Json!["questions"]!.AsObject().Select(q => q.Key)
            .Should().BeEquivalentTo("intent", "refund_full", "cancel_now");
    }

    private const string Invoice = "Widgets $1,100.00\nShipping $24.50\nSubtotal $1,124.50\nTax $80.00\nTotal due $1,204.50";

    [TestMethod]
    public async Task The_Model_Points_At_A_Candidate_And_Code_Parses_It()
    {
        var handler = new StubHandler(request =>
        {
            // The model is offered exactly what the regex found, plus a way to say "none".
            var criteria = request.Json!["questions"]!["total"]!["criteria"]!.AsObject();
            criteria.Select(c => c.Key).Should().Equal("candidate_0", "candidate_1", "candidate_2", "candidate_3", "candidate_4", "none");
            criteria["candidate_4"]!.GetValue<string>().Should().Contain("$1,204.50");
            return Http.Json(200, Answers("""{"total": {"type": "choice", "choice": "candidate_4", "confidence": 0.97, "probabilities": {"candidate_4": 0.97}}}"""));
        });
        using var client = Clients.Create(handler);

        (await ValueSelection.InvoiceTotalAsync(client, Invoice)).Should().Be(1204.50m);
    }

    [TestMethod]
    public async Task No_Candidates_Means_No_Call_And_None_Means_No_Value()
    {
        var silent = new StubHandler(_ => throw new AssertFailedException("nothing to choose from - no request should be made"));
        using var client = Clients.Create(silent);
        (await ValueSelection.InvoiceTotalAsync(client, "Thank you for your business.")).Should().BeNull();
        silent.Requests.Should().BeEmpty();

        using var none = Clients.Create(_ => Http.Json(200, Answers("""{"total": {"type": "choice", "choice": "none", "confidence": 0.9, "probabilities": {"none": 0.9}}}""")));
        (await ValueSelection.InvoiceTotalAsync(none, Invoice)).Should().BeNull();
    }

    [TestMethod]
    public async Task Changing_The_Weights_Reorders_The_Queue_Without_Another_Call()
    {
        static string Scores(double impact, double frustration, double effort)
        {
            static string One(double score, int levels) => """{"type": "score", "score": <<0>>, "confidence": 0.9, "legend": {<<1>>}, "probabilities": {}}"""
                .With(0, score).With(1, string.Join(", ", Enumerable.Range(0, levels).Select(i => $"\"{i}\": \"level {i}\"")));
            return Answers("""{"impact": <<0>>, "frustration": <<1>>, "effort": <<2>>}""".With(0, One(impact, 4)).With(1, One(frustration, 4)).With(2, One(effort, 3)));
        }

        var bodies = new Dictionary<string, string>
        {
            ["outage"] = Scores(impact: 3, frustration: 3, effort: 2),   // matters most, hardest
            ["typo"] = Scores(impact: 0.3, frustration: 0, effort: 0),   // matters least, trivial
        };
        var handler = new StubHandler(request => Http.Json(200, bodies[request.Json!["state"]!.GetValue<string>()]));
        using var client = Clients.Create(handler);

        var judged = new[] { await CompositeScoring.JudgeAsync(client, "outage"), await CompositeScoring.JudgeAsync(client, "typo") };
        judged[0].Should().Be(new CompositeScoring.Judgments("outage", 1.0, 1.0, 1.0), "each score is normalized by its own rubric's top level");

        CompositeScoring.Rank(judged, CompositeScoring.Weights.Support).Select(j => j.Ticket).Should().Equal("outage", "typo");
        CompositeScoring.Rank(judged, CompositeScoring.Weights.QuickWins).Select(j => j.Ticket).Should().Equal("typo", "outage");
        handler.Requests.Should().HaveCount(2, "two tickets judged once each - re-ranking cost nothing");
    }

    [TestMethod]
    public async Task The_Readme_Snippets_Run_Not_Just_Compile()
    {
        using var client = Clients.Create(_ => Http.Json(200, Answers("""
            {"billing": {"type": "noul", "noul": 0.9},
             "tone": {"type": "choice", "choice": "angry", "confidence": 0.9, "probabilities": {"angry": 0.9}}}
            """)));

        var ticket = await ReadmeSnippets.TypedAnswers(client, "x", Clients.OneQuestion);
        ticket.Billing.Noul.Should().Be(0.9);
        ticket.Urgency.Should().BeNull("it is marked [OptionalAnswer] in the README's own example");

        var (viaSourceGen, viaReflection) = await ReadmeSnippets.OwnType(client, "x", Clients.OneQuestion);
        viaSourceGen.Should().Be(new MyEnvelope("jev-latest")).And.Be(viaReflection);

        ReadmeSnippets.Testing(ClientTests.Result).Nouls["spam"].Noul.Should().Be(0.98);
    }
}
