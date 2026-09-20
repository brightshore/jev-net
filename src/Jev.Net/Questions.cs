using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jev.Net;

/// <summary>
/// A question about the supplied state: <see cref="Noul"/>, <see cref="Choice"/>, <see cref="Score"/>, or a
/// <see cref="RawQuestion"/> passed through untouched. A <see cref="JsonObject"/> converts implicitly to a raw
/// question, so typed and raw questions mix freely in one dictionary.
/// </summary>
public abstract class Question
{
    private protected Question() { }

    /// <summary>The wire discriminator: <c>noul</c>, <c>choice</c>, <c>score</c>, or whatever a raw question names.</summary>
    public abstract string Type { get; }

    /// <summary>The wire form. Optional fields left unset are omitted; nulls the caller supplied are kept.</summary>
    public abstract JsonObject ToJson();

    /// <summary>Validation that must pass before anything reaches the network.</summary>
    internal abstract void Validate(string name);

    /// <summary>A raw question object, sent exactly as given.</summary>
    public static implicit operator Question(JsonObject raw) => new RawQuestion(raw);

    /// <summary>Reject an empty question set, an empty score rubric, and structurally unusable raw questions.</summary>
    internal static void ValidateAll(IReadOnlyDictionary<string, Question> questions)
    {
        if (questions is null || questions.Count == 0)
        {
            throw new TypeSafeException("At least one question is required.");
        }

        foreach (var (name, question) in questions)
        {
            if (question is null)
            {
                throw new TypeSafeException(
                    $"Question \"{name}\" must be a question object or a dictionary with a nonempty string \"type\".");
            }

            question.Validate(name);
        }
    }

    private protected static void RejectEmptyScoreCriteria(string name) =>
        throw new TypeSafeException($"Score question \"{name}\" has no criteria; at least one score is required.");
}

/// <summary>Optional descriptions of a noul's yes and no outcomes.</summary>
/// <remarks>Leave a side <c>null</c> to omit it; set it to <see cref="JsonContent.Null"/> to send an explicit null.</remarks>
public sealed class NoulCriteria
{
    /// <summary>What counts as a yes answer.</summary>
    public JsonContent? True { get; init; }

    /// <summary>What counts as a no answer.</summary>
    public JsonContent? False { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        if (True is not null) json["true"] = True.ToNode();
        if (False is not null) json["false"] = False.ToNode();
        return json;
    }
}

/// <summary>A yes/no question with optional descriptions for either outcome.</summary>
public sealed class Noul : Question
{
    /// <summary>Create a noul question.</summary>
    /// <param name="instructions">The yes/no question or statement to evaluate; optional.</param>
    /// <param name="criteria">Optional descriptions of the yes and no outcomes.</param>
    public Noul(JsonContent? instructions = null, NoulCriteria? criteria = null)
    {
        Instructions = instructions;
        Criteria = criteria;
    }

    /// <inheritdoc />
    public override string Type => "noul";

    /// <summary>The question to ask, as text, a JSON object, or an array; optional.</summary>
    public JsonContent? Instructions { get; set; }

    /// <summary>Optional descriptions of the yes and no outcomes.</summary>
    public NoulCriteria? Criteria { get; set; }

    /// <inheritdoc />
    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Instructions is not null) json["instructions"] = Instructions.ToNode();
        if (Criteria is not null) json["criteria"] = Criteria.ToJson();
        return json;
    }

    internal override void Validate(string name) { }
}

/// <summary>A question that selects between named alternatives.</summary>
public sealed class Choice : Question
{
    private IReadOnlyDictionary<string, JsonContent?> _criteria;

    /// <summary>Create a choice question.</summary>
    /// <param name="criteria">Labels mapped to descriptions; a <c>null</c> description leaves the label to be
    /// interpreted by its name alone.</param>
    /// <param name="instructions">The question to ask; optional.</param>
    public Choice(IReadOnlyDictionary<string, JsonContent?> criteria, JsonContent? instructions = null)
    {
        _criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        Instructions = instructions;
    }

    /// <summary>Labels with no descriptions — the common case.</summary>
    public Choice(IEnumerable<string> labels, JsonContent? instructions = null)
        : this(ToCriteria(labels), instructions) { }

    /// <inheritdoc />
    public override string Type => "choice";

    /// <summary>The question to ask, as text, a JSON object, or an array; optional.</summary>
    public JsonContent? Instructions { get; set; }

    /// <summary>Labels mapped to text, object, or array descriptions, or null for undescribed labels.</summary>
    public IReadOnlyDictionary<string, JsonContent?> Criteria
    {
        get => _criteria;
        set => _criteria = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <inheritdoc />
    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Instructions is not null) json["instructions"] = Instructions.ToNode();
        var criteria = new JsonObject();
        foreach (var (label, description) in _criteria)
        {
            criteria[label] = description?.ToNode();
        }

        json["criteria"] = criteria;
        return json;
    }

    internal override void Validate(string name) { }

    private static Dictionary<string, JsonContent?> ToCriteria(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var criteria = new Dictionary<string, JsonContent?>();
        foreach (var label in labels)
        {
            criteria[label] = null;
        }

        return criteria;
    }
}

/// <summary>A question that assigns a score using an ordered rubric.</summary>
public sealed class Score : Question
{
    private IReadOnlyList<JsonContent> _criteria;

    /// <summary>Create a score question.</summary>
    /// <param name="criteria">A nonempty, ordered list of descriptions, one per score from zero.</param>
    /// <param name="instructions">What to rate; optional.</param>
    public Score(IReadOnlyList<JsonContent> criteria, JsonContent? instructions = null)
    {
        _criteria = Checked(criteria);
        Instructions = instructions;
    }

    /// <summary>A rubric of plain text levels.</summary>
    public Score(IEnumerable<string> levels, JsonContent? instructions = null)
        : this((levels ?? throw new ArgumentNullException(nameof(levels))).Select(l => (JsonContent)l).ToList(), instructions) { }

    /// <inheritdoc />
    public override string Type => "score";

    /// <summary>What to rate, as text, a JSON object, or an array; optional.</summary>
    public JsonContent? Instructions { get; set; }

    /// <summary>The ordered rubric; each description's position is its score, starting at zero.</summary>
    public IReadOnlyList<JsonContent> Criteria
    {
        get => _criteria;
        set => _criteria = Checked(value);
    }

    /// <inheritdoc />
    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Instructions is not null) json["instructions"] = Instructions.ToNode();
        var criteria = new JsonArray();
        foreach (var level in _criteria)
        {
            criteria.Add(level.ToNode());
        }

        json["criteria"] = criteria;
        return json;
    }

    internal override void Validate(string name)
    {
        if (_criteria.Count == 0) RejectEmptyScoreCriteria(name);
    }

    // A rubric level has no "undescribed" form — its description IS the level — so a null is a caller bug,
    // caught at construction like every other typed-question shape error.
    private static IReadOnlyList<JsonContent> Checked(IReadOnlyList<JsonContent> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Any(level => level is null || level.IsNull))
        {
            throw new ArgumentException("A score level must be text, a JSON object, or a JSON array; got null.", nameof(criteria));
        }

        return criteria;
    }
}

/// <summary>
/// A question object sent exactly as given — the escape hatch for fields or question types the API adds
/// before this SDK models them. Only its structure is checked; its schema is left to the API.
/// </summary>
public sealed class RawQuestion : Question
{
    private readonly JsonObject _raw;

    /// <summary>Wrap a raw question object. The object is not copied or modified.</summary>
    public RawQuestion(JsonObject raw) => _raw = raw ?? throw new ArgumentNullException(nameof(raw));

    /// <inheritdoc />
    public override string Type =>
        _raw["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "";

    /// <summary>The object as supplied.</summary>
    public JsonObject Raw => _raw;

    /// <inheritdoc />
    public override JsonObject ToJson() => (JsonObject)_raw.DeepClone();

    internal override void Validate(string name)
    {
        var type = Type;
        if (type.Length == 0)
        {
            throw new TypeSafeException(
                $"Question \"{name}\" must be a question object or a dictionary with a nonempty string \"type\".");
        }

        if (type is "choice" or "score" && !_raw.ContainsKey("criteria"))
        {
            throw new TypeSafeException($"Question \"{name}\" requires \"criteria\".");
        }

        if (type == "score" && IsEmpty(_raw["criteria"]))
        {
            RejectEmptyScoreCriteria(name);
        }
    }

    // "Empty" the way the upstream check reads it: null, [], {}, "", 0, false.
    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>().Length == 0,
            JsonValueKind.False => true,
            JsonValueKind.Number => node.ToJsonString() is "0" or "0.0" or "-0",
            _ => false,
        },
    };
}
