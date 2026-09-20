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
    [GeneratedRegex(@"\$\s?\d{1,3}(?:,\d{3})*(?:\.\d{2})?|\$\s?\d+(?:\.\d{2})?")]
    private static partial Regex Money();

    public static async Task<decimal?> InvoiceTotalAsync(ITypeSafeClient typesafe, string invoiceText, CancellationToken ct = default)
    {
        var candidates = Money().Matches(invoiceText).Select(m => m.Value).Distinct().ToList();
        if (candidates.Count == 0)
        {
            return null; // nothing to choose from - no call made
        }

        // Labels are opaque ids; the descriptions carry the text. A label that IS the value invites the model
        // to reason about the string rather than about the document.
        var criteria = candidates
            .Select((value, index) => (Label: $"candidate_{index}", Value: value))
            .ToDictionary(c => c.Label, c => (JsonContent?)$"The amount {c.Value}, where it appears in the invoice");
        criteria["none"] = "None of the listed amounts is the total amount due.";

        var result = await typesafe.SystemOneAsync(
            invoiceText,
            new Dictionary<string, Question>
            {
                ["total"] = new Choice(criteria, "Which amount is the TOTAL the customer must pay - after tax and discounts, not a line item or a subtotal?"),
            },
            cancellationToken: ct);

        var choice = result.Choices["total"].Choice;
        if (choice == "none" || !choice.StartsWith("candidate_", StringComparison.Ordinal))
        {
            return null;
        }

        var picked = candidates[int.Parse(choice["candidate_".Length..], CultureInfo.InvariantCulture)];
        return decimal.Parse(picked.Replace("$", "").Replace(",", "").Trim(), CultureInfo.InvariantCulture);
    }
}
