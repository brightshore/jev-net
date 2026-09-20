namespace Jev.Net.Samples;

/// <summary>
/// Route a request to a handler — and know when not to. After TypeSafe's intent-routing and confidence-gated
/// routing patterns (https://docs.typesafe.ai/patterns/intent-routing).
///
/// The model picks; CODE owns what happens next. One request asks for the intent AND, speculatively, for the
/// argument each branch would need — they run in parallel and cannot see each other, so the extra questions cost
/// tokens but no latency, and only the chosen branch's answer is read.
/// </summary>
public static class IntentRouting
{
    public const double MinimumConfidence = 0.6;

    public abstract record Route;

    public sealed record Refund(bool WantsFullAmount) : Route;

    public sealed record Cancel(bool Immediately) : Route;

    public sealed record Talk : Route;

    /// <summary>Nothing fit, or the model could not tell the options apart. A person decides.</summary>
    public sealed record HumanReview(string Why) : Route;

    private static readonly Dictionary<string, Question> Questions = new()
    {
        ["intent"] = new Choice(
            new Dictionary<string, JsonContent?>
            {
                ["refund"] = "The customer wants money back for something already paid.",
                ["cancel"] = "The customer wants to stop a subscription or an order.",
                ["talk"] = "The customer wants a conversation, an explanation, or has a general question.",
                // Without a no-match option the model must pick SOMETHING, and picks it confidently.
                ["none_of_these"] = "The message is about something else entirely, or is not a request.",
            },
            "What is the customer asking us to do?"),

        // Speculative: each premise is stated, because these cannot see the intent answer.
        ["refund_full"] = new Noul("Assuming this is a refund request: does the customer want the full amount back, rather than part of it?"),
        ["cancel_now"] = new Noul("Assuming this is a cancellation: does the customer want it to take effect immediately, rather than at the end of the period?"),
    };

    public static async Task<Route> RouteAsync(ITypeSafeClient typesafe, string message, CancellationToken ct = default)
    {
        var result = await typesafe.SystemOneAsync(message, Questions, cancellationToken: ct);
        var intent = result.Choices["intent"];

        // Confidence is how PEAKED the distribution is - "did one option clearly win" - not "is this correct".
        // Below the line the options looked alike to the model, which is exactly when a person should look.
        if (intent.Confidence < MinimumConfidence)
        {
            return new HumanReview($"unsure between options (confidence {intent.Confidence:0.00})");
        }

        return intent.Choice switch
        {
            "refund" => new Refund(result.Nouls["refund_full"].Noul >= 0.5),
            "cancel" => new Cancel(result.Nouls["cancel_now"].Noul >= 0.5),
            "talk" => new Talk(),
            _ => new HumanReview("no handler fits this message"),
        };
    }
}
