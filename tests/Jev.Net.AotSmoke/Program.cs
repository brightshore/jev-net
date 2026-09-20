using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jev.Net;

// Exit code 0 = every check passed in the native binary. Each check names itself, so a CI failure says which
// path the trimmer or the AOT compiler broke.
var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(ok || detail is null ? "" : $" — {detail}")}");
    if (!ok) failures++;
}

const string Body = """
    {"model": "jev-latest", "usage": {"input_tokens": 12, "output_tokens": 3}, "answers": {
      "billing": {"type": "noul", "noul": 0.98},
      "tone": {"type": "choice", "choice": "angry", "confidence": 0.9, "probabilities": {"calm": 0.1, "angry": 0.9}},
      "urgency": {"type": "score", "score": 1.7, "confidence": 0.8,
                  "legend": {"0": "can wait", "1": "this week", "2": "today"},
                  "probabilities": {"0": 0.1, "1": 0.1, "2": 0.8}}}}
    """;

var questions = new Dictionary<string, Question>
{
    ["billing"] = new Noul("Is this about billing?", new NoulCriteria { True = "Payments or invoices" }),
    ["tone"] = new Choice(["calm", "angry"], "What is the tone?"),
    ["urgency"] = new Score(["can wait", "this week", "today"], "How urgent?"),
    ["raw"] = new JsonObject { ["type"] = "noul", ["instructions"] = "Spam?", ["weight"] = 3 },
};

JsonNode? sent = null;
var attempts = 0;
using var client = new TypeSafeClient(new TypeSafeClientOptions
{
    ApiKey = "aot-smoke",
    Retry = new RetryPolicy { BackoffInitial = TimeSpan.Zero },
    Handler = new Stub(async request =>
    {
        attempts++;
        sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
        return attempts == 1
            ? new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent("""{"message": "warming up"}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }),
});

// 1. The plain path: request encoding, a retry, strict decoding, the grouped views.
var state = JsonContent.From(new Ticket("Charged twice", ["billing"]), SmokeJson.Default.Ticket);
var result = await client.SystemOneAsync(state, questions);
Check("retried the 503 then succeeded", attempts == 2);
Check("source-generated state was encoded", sent?["state"]?["subject"]?.GetValue<string>() == "Charged twice");
Check("typed questions were encoded", sent?["questions"]?["tone"]?["criteria"]?.AsObject().ContainsKey("angry") == true);
Check("raw question passed through", sent?["questions"]?["raw"]?["weight"]?.GetValue<int>() == 3);
Check("noul decoded", result.Nouls["billing"].Noul == 0.98);
Check("choice decoded", result.Choices["tone"].Choice == "angry");
Check("score legend keyed by int", result.Scores["urgency"].Legend[2].GetValue<string>() == "today");

// 2. Answers by property name — the one place the library reflects. If the trimmer dropped the properties
//    (or their attributes), these come back null or the optional one throws.
var typed = await client.SystemOneAsync<TicketAnswers>(state, questions);
Check("derived response: property filled by name", typed.Billing?.Noul == 0.98);
Check("derived response: [JsonPropertyName] honoured", typed.HowUrgent?.Score == 1.7);
Check("derived response: [OptionalAnswer] left null", typed.NotAsked is null);

// 3. A caller's own type, through source-generated metadata.
var envelope = await client.SystemOneAsync(state, questions, SmokeJson.Default.Envelope);
Check("JsonTypeInfo response", envelope is { Model: "jev-latest", Usage.InputTokens: 12 });

// 4. Failures keep their shape: mapping, message text, and the located validation path.
using var failing = new TypeSafeClient(new TypeSafeClientOptions
{
    ApiKey = "aot-smoke",
    Retry = RetryPolicy.None,
    Handler = new Stub(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
        ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("""{"detail": [{"loc": ["body", "q"], "msg": "Slow down"}]}"""), Headers = { { "retry-after-ms", "125" } } }
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"model": "m", "usage": {}, "answers": {"n": {"type": "noul", "noul": "0.5"}}}""") })),
});

try
{
    await failing.Models.ListAsync();
    Check("429 mapped", false, "no exception");
}
catch (TypeSafeRateLimitException error)
{
    Check("429 mapped with Retry-After", error.RetryAfter == TimeSpan.FromMilliseconds(125));
    Check("server message extracted", error.Message.Contains("q: Slow down", StringComparison.Ordinal), error.Message);
}

try
{
    await failing.SystemOneAsync("x", questions);
    Check("malformed response rejected", false, "no exception");
}
catch (TypeSafeApiResponseValidationException error)
{
    Check("malformed response located", error.FieldPath == "answers.n.noul", error.FieldPath);
}

Console.WriteLine(failures == 0 ? "AOT smoke: all checks passed" : $"AOT smoke: {failures} FAILED");
return failures == 0 ? 0 : 1;

internal sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
}

internal sealed class TicketAnswers : SystemOneResponse
{
    public NoulAnswer? Billing { get; set; }

    [JsonPropertyName("urgency")] public ScoreAnswer? HowUrgent { get; set; }

    [OptionalAnswer] public NoulAnswer? NotAsked { get; set; }
}

internal sealed record Ticket(string Subject, string[] Tags);

internal sealed record Envelope(string Model, EnvelopeUsage Usage);

internal sealed record EnvelopeUsage(int InputTokens, int OutputTokens);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Ticket))]
[JsonSerializable(typeof(Envelope))]
internal sealed partial class SmokeJson : JsonSerializerContext;
