using System.Globalization;
using System.Text.RegularExpressions;

namespace Jev.Net.Samples;

/// <summary>
/// Select, don't generate. After TypeSafe's pre-parsed value extraction cookbook
/// (https://docs.typesafe.ai/cookbooks/pre_parsed_value_extraction_cookbook).
///
/// "Which amount is the invoice total?" is a judgment; "what does $1,204.50 parse to?" is not. So CODE finds
/// every candidate, the model only chooses among them, and code parses the winner. The model cannot invent a
/// number, because it never writes one — it can only point. The flip side is the rule that matters: it also
/// cannot choose a value you did not offer, so candidate coverage is your job.
/// </summary>
public static partial class ValueSelection
{
    // Grouped (1,204.50) OR plain (1204.50) digits - decided as ONE alternation inside the amount, with a
    // guard after it that refuses to stop in the MIDDLE of a number. As two whole-pattern alternatives,
    // "$1204.50" matched the grouped one first and came out as "$120"; with a two-digit-only fraction, "$10.5"
    // came out as "$10". Either way the model was being offered a number that is not in the document - the one
    // thing this sample exists to make impossible.
    [GeneratedRegex(@"\$\s?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?(?!\d|[,.]\d)")]
    private static partial Regex Money();

    /// <summary>The API documents at most 255 choice options; one is spent on "none".</summary>
    public const int MaxCandidates = 254;

    public static async Task<decimal?> InvoiceTotalAsync(ITypeSafeClient typesafe, string invoiceText, CancellationToken ct = default)
    {
        var candidates = Money().Matches(invoiceText).Select(m => m.Value).Distinct().ToList();
        if (candidates.Count == 0)
        {
            return null; // nothing to choose from - no call made
        }

        // Past the limit the API answers 422. A total sits at the BOTTOM of an invoice, so keep the last ones -
        // and note what that does to the rule above: the model still cannot choose what it was not offered.
        if (candidates.Count > MaxCandidates)
        {
            candidates = candidates[^MaxCandidates..];
        }

        // Labels are opaque ids; the descriptions carry the text. A label that IS the value invites the model
        // to reason about the string rather than about the document.
        // label -> the text it stands for. The ONLY way back from an answer to an amount is a lookup in this map,
        // so nothing the response says can name a candidate that was not offered.
        var offered = candidates.Select((value, index) => (Label: $"candidate_{index}", Value: value))
            .ToDictionary(c => c.Label, c => c.Value, StringComparer.Ordinal);
        var criteria = offered.ToDictionary(c => c.Key, c => (JsonContent?)$"The amount {c.Value}, where it appears in the invoice");
        criteria["none"] = "None of the listed amounts is the total amount due.";

        var result = await typesafe.SystemOneAsync(
            invoiceText,
            new Dictionary<string, Question>
            {
                ["total"] = new Choice(criteria, "Which amount is the TOTAL the customer must pay - after tax and discounts, not a line item or a subtotal?"),
            },
            cancellationToken: ct);

        // The answer comes from a network. A missing answer, "none", "candidate_000", "candidate_99" - anything
        // that is not EXACTLY a label we offered - means "no value": never an exception, never a guess.
        if (!result.Choices.TryGetValue("total", out var answer) || !offered.TryGetValue(answer.Choice, out var picked))
        {
            return null;
        }

        return decimal.TryParse(picked.Replace("$", "").Replace(",", "").Trim(),
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total) ? total : null;
    }
}
