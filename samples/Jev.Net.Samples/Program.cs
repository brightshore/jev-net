using Jev.Net;
using Jev.Net.Samples;

// dotnet run --project samples/Jev.Net.Samples -f net10.0 -- routing|total|ranking
// (-f because the project targets two frameworks when JevTestAllFrameworks is set, and `dotnet run` then insists.)
// These call the REAL API and spend (a very few) tokens, so they need TYPESAFE_API_KEY.

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TypeSafeDefaults.ApiKeyEnv)))
{
    Console.Error.WriteLine($"Set {TypeSafeDefaults.ApiKeyEnv} to run a sample. (The test suite runs all three offline.)");
    return 2;
}

await using var client = new TypeSafeClient();

switch (args.FirstOrDefault())
{
    case "routing":
        foreach (var message in new[] { "I was charged twice, I want all of it back.", "Please stop my plan today.", "asdf qwerty" })
        {
            Console.WriteLine($"{message}\n  -> {await IntentRouting.RouteAsync(client, message)}");
        }

        return 0;

    case "total":
        const string invoice = "Widgets $1,100.00\nShipping $24.50\nSubtotal $1,124.50\nTax $80.00\nTotal due $1,204.50";
        Console.WriteLine($"Total: {await ValueSelection.InvoiceTotalAsync(client, invoice)}");
        return 0;

    case "ranking":
        var judged = new List<CompositeScoring.Judgments>();
        foreach (var ticket in new[] { "Typo on the pricing page.", "Checkout is failing for every customer since 9am!!", "How do I export to CSV?" })
        {
            judged.Add(await CompositeScoring.JudgeAsync(client, ticket));
        }

        Console.WriteLine("Support order:    " + string.Join(" | ", CompositeScoring.Rank(judged, CompositeScoring.Weights.Support).Select(j => j.Ticket)));
        Console.WriteLine("Quick-wins order: " + string.Join(" | ", CompositeScoring.Rank(judged, CompositeScoring.Weights.QuickWins).Select(j => j.Ticket)));
        return 0;

    default:
        Console.Error.WriteLine("Usage: dotnet run --project samples/Jev.Net.Samples -f net10.0 -- routing|total|ranking");
        return 2;
}
