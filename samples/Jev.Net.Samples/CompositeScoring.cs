namespace Jev.Net.Samples;

/// <summary>
/// Score the dimensions once; let code decide what they add up to. After TypeSafe's composite-scoring pattern
/// (https://docs.typesafe.ai/patterns/composite-scoring).
///
/// "How urgent is this ticket?" as ONE question bakes a policy into a prompt. Three narrow scores are reusable
/// data: the weights are yours, they can differ per team, and changing them re-ranks the queue WITHOUT asking
/// the model again — the judgments have not changed, only what you make of them.
/// </summary>
public static class CompositeScoring
{
    /// <summary>The raw judgments for one ticket, each normalized to 0..1. Keep these; they outlive any weighting.</summary>
    public sealed record Judgments(string Ticket, double Impact, double Frustration, double Effort);

    public sealed record Weights(double Impact, double Frustration, double Effort)
    {
        public static Weights Support { get; } = new(Impact: 0.5, Frustration: 0.4, Effort: -0.1);

        /// <summary>The same judgments, read by a team that wants quick wins first.</summary>
        public static Weights QuickWins { get; } = new(Impact: 0.3, Frustration: 0.1, Effort: -0.6);
    }

    private static readonly Dictionary<string, Question> Questions = new()
    {
        ["impact"] = new Score(
            ["Cosmetic or a question; nothing is blocked", "One person is slowed down or working around it",
             "A team or a paying workflow is blocked", "Money, data or many customers are affected right now"],
            "How much is affected by the problem described?"),
        ["frustration"] = new Score(
            ["Calm and neutral", "Mildly annoyed", "Clearly frustrated", "Angry, or threatening to leave"],
            "How frustrated does the writer appear?"),
        ["effort"] = new Score(
            ["A known answer or a setting change", "Needs investigation by support", "Needs an engineer"],
            "How much work does resolving this appear to need?"),
    };

    public static async Task<Judgments> JudgeAsync(ITypeSafeClient typesafe, string ticket, CancellationToken ct = default)
    {
        var result = await typesafe.SystemOneAsync(ticket, Questions, cancellationToken: ct);

        // Score is the probability-weighted average over the rubric, so divide by the top level to normalize.
        double Normalized(string name) => result.Scores[name].Score / (result.Scores[name].Legend.Count - 1);

        return new Judgments(ticket, Normalized("impact"), Normalized("frustration"), Normalized("effort"));
    }

    /// <summary>Pure arithmetic. No client, no tokens - call it as often as the weights change.</summary>
    public static IReadOnlyList<Judgments> Rank(IEnumerable<Judgments> judged, Weights weights) =>
        judged.OrderByDescending(j => (j.Impact * weights.Impact) + (j.Frustration * weights.Frustration) + (j.Effort * weights.Effort))
            .ToList();
}
