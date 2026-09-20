using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jev.Net;

/// <summary>An answer to a single question, identified by its <see cref="Type"/>.</summary>
public abstract record Answer
{
    private protected Answer() { }

    /// <summary>The wire discriminator: <c>noul</c>, <c>choice</c>, or <c>score</c>.</summary>
    public abstract string Type { get; }
}

/// <summary>A yes/no answer.</summary>
/// <param name="Noul">Probability of a yes answer or a true statement, from 0 to 1. Values near 0.5 mean the
/// model found yes and no about equally likely — uncertainty, not "medium".</param>
public sealed record NoulAnswer(double Noul) : Answer
{
    /// <inheritdoc />
    public override string Type => "noul";
}

/// <summary>A selected label and its probabilities.</summary>
/// <param name="Choice">The label with the highest probability among the question's criteria.</param>
/// <param name="Confidence">How peaked the distribution is, from 0 to 1.</param>
/// <param name="Probabilities">Probability of each label, summing to approximately 1.</param>
public sealed record ChoiceAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : Answer
{
    /// <inheritdoc />
    public override string Type => "choice";
}

/// <summary>An expected score with its rubric and probabilities.</summary>
/// <param name="Score">The probability-weighted average of the rubric levels; may fall between integers.</param>
/// <param name="Confidence">How peaked the distribution is, from 0 to 1.</param>
/// <param name="Legend">The requested rubric descriptions keyed by integer score.</param>
/// <param name="Probabilities">Probability of each level, keyed by integer score.</param>
public sealed record ScoreAnswer(
    double Score, double Confidence, IReadOnlyDictionary<int, JsonNode> Legend, IReadOnlyDictionary<int, double> Probabilities) : Answer
{
    /// <inheritdoc />
    public override string Type => "score";

    /// <summary>
    /// The single most probable level (the lowest, on a tie), or null when the API reported no probabilities.
    /// <see cref="Score"/> is the probability-weighted AVERAGE and can land between levels, or on a level
    /// nobody thinks is likely when opinion is split between the extremes — this is the mode, for when you need
    /// one rubric level to show or branch on. Read <see cref="Confidence"/> before trusting either.
    /// </summary>
    /// <remarks>
    /// Only levels that exist in <see cref="Legend"/> are candidates, so the result can always be looked up
    /// there. The decoder does not REJECT a probability whose level the legend lacks — the Python SDK doesn't,
    /// and the raw map stays available in <see cref="Probabilities"/> — but a level the rubric never defined is
    /// not something to hand back as "the answer". (With an empty legend, every probability is a candidate.)
    /// </remarks>
    [JsonIgnore]
    public int? MostLikely
    {
        get
        {
            var candidates = Legend.Count == 0 ? Probabilities : Probabilities.Where(level => Legend.ContainsKey(level.Key));
            return candidates.OrderByDescending(level => level.Value).ThenBy(level => level.Key)
                .Select(level => (int?)level.Key).FirstOrDefault();
        }
    }
}

/// <summary>Token counts for a request, when reported by the API.</summary>
/// <param name="InputTokens">Billable input tokens, or null when not reported.</param>
/// <param name="OutputTokens">Output tokens, or null when not reported.</param>
public sealed record Usage(int? InputTokens = null, int? OutputTokens = null);

/// <summary>Metadata describing a single available model.</summary>
/// <param name="Name">Model name or alias accepted by a request's model field.</param>
/// <param name="Description">Human-readable description of the model and its capabilities.</param>
/// <param name="ReleaseDate">Model release date, formatted as YYYY-MM-DD.</param>
public sealed record ModelMetadata(string Name, string Description, string ReleaseDate);
