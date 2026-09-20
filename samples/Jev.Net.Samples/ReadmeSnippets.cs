using System.Text;
using System.Text.Json.Serialization;

namespace Jev.Net.Samples;

// Every ```csharp block in README.md is one or more of the regions below, verbatim (ReadmeTests checks it).
// To change a README example, change it HERE - then the compiler has seen it before a reader does.
// Region names are the markers in the README: <!-- snippet: quickstart -->.

internal static class ReadmeSnippets
{
    public static async Task<(double Billing, string Tone, double Urgency)> QuickStart()
    {
        #region snippet:quickstart
        await using var client = new TypeSafeClient();            // key from TYPESAFE_API_KEY

        var result = await client.SystemOneAsync(
            "I was charged twice. Please help.",
            new Dictionary<string, Question>
            {
                ["billing"] = new Noul("Is this about billing?"),
                ["tone"]    = new Choice(["calm", "frustrated", "angry"], "What is the customer's tone?"),
                ["urgency"] = new Score(["can wait", "this week", "today"], "How urgent is this ticket?"),
            });

        double billing = result.Nouls["billing"].Noul;            // probability of YES — 0.5 is "unsure", not "medium"
        string tone    = result.Choices["tone"].Choice;
        double urgency = result.Scores["urgency"].Score;          // may fall between rubric levels
        #endregion

        return (billing, tone, urgency);
    }

    public static async Task<Ticket> TypedAnswers(ITypeSafeClient client, JsonContent state, Dictionary<string, Question> questions)
    {
        #region snippet:ticket-call
        var ticket = await client.SystemOneAsync<Ticket>(state, questions);
        #endregion

        return ticket;
    }

    public static async Task<(MyEnvelope, MyEnvelope)> OwnType(ITypeSafeClient client, JsonContent state, Dictionary<string, Question> questions)
    {
        #region snippet:own-type
        var viaSourceGen  = await client.SystemOneAsync(state, questions, options: null, MyJsonContext.Default.MyEnvelope); // AOT-safe
        var viaReflection = await client.SystemOneAsync<MyEnvelope>(state, questions, options: null, ResponseJson.SnakeCase);
        #endregion

        return (viaSourceGen, viaReflection);
    }

    // Compiled against look-alikes below rather than the OpenTelemetry packages: what this proves is that
    // TypeSafeTelemetry's constants exist under these names, which is the part of the snippet that is OURS.
    public static void Telemetry(OpenTelemetryLookalike.Services services)
    {
        #region snippet:telemetry
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(TypeSafeTelemetry.ActivitySourceName))
            .WithMetrics(metrics => metrics.AddMeter(TypeSafeTelemetry.MeterName));
        #endregion
    }

    public static SystemOneResponse Testing(string recordedJson)
    {
        #region snippet:testing
        var raw = new RawHttpResponse(200, headers: null, Encoding.UTF8.GetBytes(recordedJson));
        var response = SystemOneResponse.FromHttpResponse(raw);      // or FromHttpResponse<Ticket>(raw)
        #endregion

        return response;
    }
}

#region snippet:ticket-class
sealed class Ticket : SystemOneResponse
{
    public NoulAnswer Billing { get; set; } = null!;
    public ChoiceAnswer Tone { get; set; } = null!;
    [OptionalAnswer] public ScoreAnswer? Urgency { get; set; }
}
#endregion

public sealed record MyEnvelope(string Model);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MyEnvelope))]
public sealed partial class MyJsonContext : JsonSerializerContext;

/// <summary>Just enough of OpenTelemetry's builder shape for the README's telemetry snippet to compile here
/// without the samples project taking the OpenTelemetry packages.</summary>
public static class OpenTelemetryLookalike
{
    public sealed class Services
    {
        public List<string> Sources { get; } = [];

        public List<string> Meters { get; } = [];

        public Services AddOpenTelemetry() => this;

        public Services WithTracing(Action<Services> configure) { configure(this); return this; }

        public Services WithMetrics(Action<Services> configure) { configure(this); return this; }

        public Services AddSource(string name) { Sources.Add(name); return this; }

        public Services AddMeter(string name) { Meters.Add(name); return this; }
    }
}
