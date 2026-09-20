# Jev.Net

[![CI](https://github.com/brightshore/jev-net/actions/workflows/ci.yml/badge.svg)](https://github.com/brightshore/jev-net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Jev.Net.svg?logo=nuget)](https://www.nuget.org/packages/Jev.Net)
[![MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A lightweight .NET client for the [TypeSafe AI](https://typesafe.ai) API — System One / **Jev**. Ask small typed
questions about text or structured state and get calibrated probabilities back that your code can act on.

> A community package. Not affiliated with or endorsed by TypeSafe AI.

```bash
dotnet add package Jev.Net
```

## Why this one

**One dependency.** `Microsoft.Extensions.Logging.Abstractions`, and nothing else. Retries, backoff,
`Retry-After` and per-attempt timeouts are about a hundred lines of its own, so it drops into a library, a CLI,
a desktop app or a worker without changing what your dependency graph looks like. That promise is enforced:
CI reads the packed `.nuspec` and fails on a second dependency. Targets `net8.0` and `net10.0`, and is
**trim- and Native AOT-compatible** — a smoke app is published as a native binary and run on every build.

**The Python SDK, in C#.** It is a faithful port of the official
[`typesafe-sdk`](https://github.com/typesafe-ai/typesafe-sdk-python) (read at 0.7.0): the same request shape,
the same protected headers, the same exception hierarchy and the same **message text**, the same retry rules,
the same dotted field paths when a response is malformed. Its test suite is a mirror of upstream's, file by
file — so behaviour you read about in TypeSafe's docs, or debugged once in Python, is the behaviour you get
here. Where .NET forces a difference, it is listed at the [bottom](#differences-from-the-python-sdk).

### Looking for something else?

[**TypeSafeAI.Net**](https://github.com/Hawxy/TypeSafeAI.Net) by Hawxy is an excellent community SDK with a
different focus, and it got here first: dependency-injection and `IHttpClientFactory` integration, enum-typed
choices, a `QuestionSet` builder with typed handles, Native AOT support, and a companion package of
`Microsoft.Extensions.AI` middleware (guardrails, routing, evaluators). If that is the shape of your app, use
it — the two projects make different trade-offs on purpose, and both speak the same API.

## Quick start

<!-- snippet: quickstart -->
```csharp
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
```

Questions are independent and answered in one round trip, so ask everything about a state together.

## Typed answers by name

Derive from `SystemOneResponse` and declare answer-typed properties; each is filled from the answer of the same
name (`[JsonPropertyName]`, else the property name — exact, case-insensitive, then snake_case). Every such
property is **required** — a missing answer, or one of the wrong kind, is a
`TypeSafeApiResponseValidationException` naming the field — unless you mark it `[OptionalAnswer]`.

<!-- snippet: ticket-class + ticket-call -->
```csharp
sealed class Ticket : SystemOneResponse
{
    public NoulAnswer Billing { get; set; } = null!;
    public ChoiceAnswer Tone { get; set; } = null!;
    [OptionalAnswer] public ScoreAnswer? Urgency { get; set; }
}

var ticket = await client.SystemOneAsync<Ticket>(state, questions);
```

To read the body into a type that is entirely yours, pass JSON metadata — source-generated for trimmed and AOT
apps, or reflection-based when that doesn't matter:

<!-- snippet: own-type -->
```csharp
var viaSourceGen  = await client.SystemOneAsync(state, questions, options: null, MyJsonContext.Default.MyEnvelope); // AOT-safe
var viaReflection = await client.SystemOneAsync<MyEnvelope>(state, questions, options: null, ResponseJson.SnakeCase);
```

## State, instructions and criteria: `JsonContent`

Everywhere the API takes "text, an object, or an array", the SDK takes a `JsonContent`. It converts implicitly
from `string` and from `JsonNode`; `JsonContent.From(value)` serializes anything else (pass a `JsonTypeInfo<T>`
as the second argument in a trimmed or AOT app). Nodes are deep-cloned in
and out, so one `JsonObject` can appear in several questions and a question can be sent any number of times —
`System.Text.Json` nodes have a single parent, and encoding by reference would throw on the second use.

`null` means *omit* for an optional field; `JsonContent.Null` means *send an explicit null*. A choice label
mapped to `null` is sent as `null` (undescribed — interpreted by its name).

**Limits.** The API documents at most 255 choice options and 2–10 score levels. Jev.Net, like the Python SDK,
does not enforce these client-side — an out-of-range question comes back as a
`TypeSafeUnprocessableEntityException` naming the field. Only an empty question set and an empty score rubric are
rejected before the network.

## Raw questions

A `JsonObject` converts implicitly to a `Question` and is sent exactly as given — the escape hatch for fields or
question types the API adds before this SDK models them. Only its structure is checked (`type` present; `choice`
and `score` have `criteria`; a score rubric is nonempty); its schema is left to the API.

## Testing code that uses the client

`TypeSafeClient` implements `ITypeSafeClient`, so your code can take the interface and your tests can hand it a
fake — and the fake can return *genuine* responses, decoded and validated exactly as the client would:

<!-- snippet: testing -->
```csharp
var raw = new RawHttpResponse(200, headers: null, Encoding.UTF8.GetBytes(recordedJson));
var response = SystemOneResponse.FromHttpResponse(raw);      // or FromHttpResponse<Ticket>(raw)
```

The same call turns a cached or replayed body back into a response; a non-2xx snapshot throws the matching
exception. To fake at the HTTP level instead, give the real client a `Handler`.

## Samples

[`samples/Jev.Net.Samples`](samples/Jev.Net.Samples) has three small programs in the shape of TypeSafe's
cookbooks — each keeps the workflow in code and asks the model only for the judgment:

| Sample | The idea |
| --- | --- |
| [`IntentRouting`](samples/Jev.Net.Samples/IntentRouting.cs) | Pick a handler, ask for each branch's argument *speculatively* in the same round trip, and send low-confidence or no-match cases to a person. |
| [`ValueSelection`](samples/Jev.Net.Samples/ValueSelection.cs) | Select, don't generate: code finds every candidate amount, the model only points at the total, code parses it — so it cannot invent a number. |
| [`CompositeScoring`](samples/Jev.Net.Samples/CompositeScoring.cs) | Score narrow dimensions once, then re-rank under different weights with no further calls. |

```bash
dotnet run --project samples/Jev.Net.Samples -- routing     # needs TYPESAFE_API_KEY; the tests run all three offline
```

The C# blocks in this README are regions of [`ReadmeSnippets.cs`](samples/Jev.Net.Samples/ReadmeSnippets.cs) in
that project: they are compiled on every build, and a test fails if the two drift apart.

## Errors

| Exception | When |
| --- | --- |
| `TypeSafeException` | Base. Also: no API key, invalid timeout, empty questions, empty score rubric. |
| `TypeSafeApiException` | Any non-2xx. `Status`, `Body`, `Headers`, `Endpoint`, `RequestId`. |
| `…BadRequest` / `Authentication` / `PermissionDenied` / `NotFound` / `UnprocessableEntity` | 400 / 401 / 403 / 404 / 422 |
| `TypeSafeRateLimitException` | 429. `RetryAfter` is the server's requested wait. |
| `TypeSafeInternalServerException` | 5xx, including the API's 529 "service overloaded" — retried by default |
| `TypeSafeApiConnectionException` | No HTTP response at all. |
| `TypeSafeApiTimeoutException` | The per-attempt timeout elapsed (derives from the connection exception). |
| `TypeSafeApiResponseValidationException` | A 2xx whose body is structurally wrong. `FieldPath` names the first bad field: `answers.tone.confidence`, `models[1].name`. |

`Message` reads `POST https://api.typesafe.ai/v1/systemone: 429 <server's explanation> (request_id=…)`. The
endpoint never carries credentials, a query string, or a fragment.

Caller cancellation is **not** a timeout: cancelling your token surfaces as `OperationCanceledException`, is never
retried, and also interrupts a wait between retries.

## Retries

`RetryPolicy` defaults: 2 retries; 408, 429 and 5xx; connection and timeout failures; exponential backoff from
0.5s to 5s with up to 25% subtracted as jitter; `Retry-After` / `retry-after-ms` honored (however long); and a
30s total budget per call that stops *before* a wait that would exceed it, rethrowing the last error. Set it on
the client, or per call through `RequestOptions.Retry`. `RetryPolicy.None` disables retrying.

## Configuration

| Option | Environment | Default |
| --- | --- | --- |
| `ApiKey` | `TYPESAFE_API_KEY` | — (required) |
| `BaseUrl` | `TYPESAFE_BASE_URL` | `https://api.typesafe.ai` |
| `Model` | `TYPESAFE_DEFAULT_MODEL` | `jev-latest` |
| `Timeout` | | 10s per attempt; `Timeout.InfiniteTimeSpan` for none |
| `LoggerFactory` | `TYPESAFE_LOG_LEVEL` (a floor) | no logging |

Explicit options win; blank environment values count as unset. `Authorization`, `Accept`, `User-Agent`,
`X-TypeSafe-SDK` and `X-TypeSafe-Runtime` are protected — neither default nor per-call headers can replace them.

**Logging** (category `Jev.Net`): one Information line per attempt; headers and bodies at Debug.
Credential-bearing headers — and any header whose name contains `token` or `secret` — are redacted. **Bodies are
not**: they are whatever state you sent. Don't enable Debug where that matters.

Pin a model (`jev-1.13.0`, not `jev-latest`) wherever you have tuned thresholds against its probabilities.

## Observability

Traces and metrics come through `ActivitySource` and `Meter`, which ship in the .NET runtime — so
OpenTelemetry support costs **no dependency**, and nothing at all when nobody is listening.

<!-- snippet: telemetry -->
```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(TypeSafeTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(TypeSafeTelemetry.MeterName));
```

One client span covers one SDK call *including its retries* (`http.request.resend_count`); the individual HTTP
attempts appear beneath it from .NET's own `System.Net.Http` instrumentation. It carries the operation, status,
requested and answering model, token counts and the `x-typesafe-request-id`.

| Instrument | |
| --- | --- |
| `jev_net.client.request.duration` | Histogram, seconds — the whole call, waits included. Tagged with operation, status, `error.type`. |
| `jev_net.client.retries` | Counter — attempts after the first. |
| `jev_net.client.token.usage` | Counter — tagged `jev_net.token.type` = `input` \| `output`, and the answering model. |

**None of your content is recorded** — no state, questions, answers or headers. Two things you *configure*
are: the model name you asked for (`jev_net.request.model`), and your base URL's host and path (`server.address`,
`url.full` — never its credentials or query). A failed span's description is a fixed `HTTP 429`, not the
server's message, which could echo your request.

## Python SDK → Jev.Net

If you know `typesafe-sdk`, you already know this. Everything in the left column behaves the same on the right.

| Python | Jev.Net |
| --- | --- |
| `AsyncTypeSafeClient(api_key=…, model=…, base_url=…, timeout=…, headers=…, retry=…)` | `new TypeSafeClient(new TypeSafeClientOptions { ApiKey, Model, BaseUrl, Timeout, Headers, Retry })` |
| `transport=` / `http_client=` | `Handler` / `HttpClient` (mutually exclusive, as upstream) |
| `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL`, `TYPESAFE_LOG_LEVEL` | the same four variables, same precedence, blank = unset |
| `await client.system_one(state, questions, model=…, retry=…, timeout=…, extra_headers=…, extra_body=…)` | `await client.SystemOneAsync(state, questions, new SystemOneOptions { Model, Retry, Timeout, ExtraHeaders, ExtraBody })` |
| `await client.models.list()` | `await client.Models.ListAsync()` |
| `Noul(instructions=…, criteria={"true": …, "false": …})` | `new Noul(instructions, new NoulCriteria { True = …, False = … })` |
| `Choice(instructions=…, criteria={"calm": None, "angry": "…"})` | `new Choice(["calm", "angry"], instructions)` or a `Dictionary<string, JsonContent?>` with descriptions |
| `Score(instructions=…, criteria=["low", "high"])` | `new Score(["low", "high"], instructions)` |
| a raw `{"type": "noul", …}` dict | a `JsonObject` (converts implicitly to `Question`) |
| `JSONContent` (str, mapping, sequence) | `JsonContent` (string, `JsonObject`, `JsonArray`, or `JsonContent.From(…)`) |
| `result.nouls["q"].noul`, `.choices["q"].choice` / `.confidence` / `.probabilities`, `.scores["q"].score` / `.legend` | `result.Nouls["q"].Noul`, `.Choices["q"].Choice` / `.Confidence` / `.Probabilities`, `.Scores["q"].Score` / `.Legend` |
| `result.answers`, `.model`, `.usage.input_tokens` | `result.Answers`, `.Model`, `.Usage.InputTokens` |
| `result.request_id`, `result.raw_http_response` | `result.RequestId`, `result.RawHttpResponse` |
| `SystemOneResponse.from_http_response(response)` | `SystemOneResponse.FromHttpResponse(raw)`, with `RawHttpResponse.FromAsync(httpResponseMessage)` or its public constructor |
| `response_model=MyResponse` (a `SystemOneResponse` subclass with answer fields) | `SystemOneAsync<MyResponse>(…)` |
| `response_model=AnyPydanticModel` | `SystemOneAsync(state, questions, options, JsonTypeInfo<T>)` or `SystemOneAsync<T>(state, questions, options, JsonSerializerOptions)` |
| `RetryPolicy(max_retries, backoff_initial, backoff_max, backoff_jitter, http_statuses, respect_retry_after, api_connection_error, api_timeout_error, exceptions, predicate, timeout)` | `RetryPolicy { MaxRetries, BackoffInitial, BackoffMax, BackoffJitter, HttpStatuses, RespectRetryAfter, ApiConnectionError, ApiTimeoutError, Exceptions, Predicate, Timeout }` — same defaults |
| `TypeSafeError` → `TypeSafeAPIError` → `…BadRequestError`, `…RateLimitError`, … | `TypeSafeException` → `TypeSafeApiException` → `…BadRequestException`, `…RateLimitException`, … |
| `error.status`, `.body`, `.headers`, `.endpoint`, `.request_id`, `.retry_after_ms`, `.field_path` | `error.Status`, `.Body`, `.Headers`, `.Endpoint`, `.RequestId`, `.RetryAfter`, `.FieldPath` |
| `str(error)` | `error.Message` — the same text |
| unknown answer types skipped with a warning; unknown fields ignored | the same — and the skipped ones are kept in `result.UnmodeledAnswers` |
| `async with client:` | `await using var client = …` |

## Differences from the Python SDK

* **Async only.** No synchronous client; that is the .NET convention for HTTP.
* **Durations are `TimeSpan`**, so the NaN/infinite-seconds cases upstream validates cannot be written. There is
  one per-attempt timeout rather than httpx's connect/read/write/pool split.
* **`HttpMessageHandler` / `HttpClient`** stand in for httpx's transport / client. A supplied `HttpClient` is
  disposed with the SDK client, as upstream — set `DisposeHttpClient = false` for one from `IHttpClientFactory`.
  When you supply one, its own `Timeout` still caps every attempt.
* **Questions are a closed class hierarchy**, so "a question that is not a question" cannot be constructed; typed
  questions validate at construction with `ArgumentException`s rather than pydantic errors.
* **No covariant question maps** — `IReadOnlyDictionary` is invariant in its value; declare the dictionary as
  `Dictionary<string, Question>`.
* **Optional answers are marked, not inferred.** Python reads `Optional[...]`; here a property is required unless
  it carries `[OptionalAnswer]`. Nullability is metadata the trimmer removes, so inferring from it would make the
  same class validate differently in a Native AOT build.
* **Traces and metrics** (`ActivitySource` / `Meter`) — upstream has neither; this is what a .NET service expects.
* **`TimeProvider`** is accepted for the retry clock — a .NET addition. So are `ITypeSafeClient`,
  `result.UnmodeledAnswers` (the receiving half of raw questions), `ScoreAnswer.MostLikely` (the mode, beside the
  averaged `Score`) and `TypeSafeDefaults.SdkVersion`.
* It identifies itself as `jev-net/<version>`, not as the official SDK.

## Tests

```bash
dotnet test --solution Jev.Net.slnx
```

Offline and sub-second: a stub `HttpMessageHandler`, an injected environment reader, an injected delay, and a
manual clock. `LiveIntegrationTests` is the one class that calls the real API, and it is opt-in — it needs
`TYPESAFE_LIVE_TESTS=1` **and** `TYPESAFE_API_KEY`, and reports Inconclusive otherwise.

The suite was mutation-checked: the client was broken on purpose in nine places (protected headers, the retry
budget, header redaction, strict number decoding, node cloning, jitter, `retry-after-ms` precedence,
cancellation-vs-timeout, unknown answer types) and every break is caught.

`tests/Jev.Net.AotSmoke` is not a test project but a console app: CI publishes it with `PublishAot` and runs the
native binary on Linux and Windows, exercising request encoding, retries, answers-by-property-name,
source-generated JSON, and error mapping after the trimmer and AOT compiler have been through them.

## License

MIT. See [NOTICE](NOTICE) for the upstream attribution.
