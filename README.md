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
a desktop app or a worker without changing what your dependency graph looks like. Targets `net8.0` and `net10.0`.

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
name (`[JsonPropertyName]`, else the property name — exact, case-insensitive, then snake_case). A missing answer
or one of the wrong kind is a `TypeSafeApiResponseValidationException` naming the field; a nullable property is
optional.

```csharp
sealed class Ticket : SystemOneResponse
{
    public NoulAnswer Billing { get; set; } = null!;
    public ChoiceAnswer Tone { get; set; } = null!;
}

var ticket = await client.SystemOneAsync<Ticket>(state, questions);
```

Any other `TResponse` is deserialized from the body (snake_case, case-insensitive).

## State, instructions and criteria: `JsonContent`

Everywhere the API takes "text, an object, or an array", the SDK takes a `JsonContent`. It converts implicitly
from `string` and from `JsonNode`; `JsonContent.From(value)` serializes anything else. Nodes are deep-cloned in
and out, so one `JsonObject` can appear in several questions and a question can be sent any number of times —
`System.Text.Json` nodes have a single parent, and encoding by reference would throw on the second use.

`null` means *omit* for an optional field; `JsonContent.Null` means *send an explicit null*. A choice label
mapped to `null` is sent as `null` (undescribed — interpreted by its name).

## Raw questions

A `JsonObject` converts implicitly to a `Question` and is sent exactly as given — the escape hatch for fields or
question types the API adds before this SDK models them. Only its structure is checked (`type` present; `choice`
and `score` have `criteria`; a score rubric is nonempty); its schema is left to the API.

## Errors

| Exception | When |
| --- | --- |
| `TypeSafeException` | Base. Also: no API key, invalid timeout, empty questions, empty score rubric. |
| `TypeSafeApiException` | Any non-2xx. `Status`, `Body`, `Headers`, `Endpoint`, `RequestId`. |
| `…BadRequest` / `Authentication` / `PermissionDenied` / `NotFound` / `UnprocessableEntity` | 400 / 401 / 403 / 404 / 422 |
| `TypeSafeRateLimitException` | 429. `RetryAfter` is the server's requested wait. |
| `TypeSafeInternalServerException` | 5xx |
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
* **`TimeProvider`** is accepted for the retry clock — a .NET addition.
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

## Releasing

Versioning is [changesets](.changeset/README.md): `changerig add` in the PR, `shiprig release` on `main`. That
pushes a `Jev.Net@x.y.z` tag, and the tag runs `release.yml`, which packs and publishes to nuget.org through
**trusted publishing** (OIDC) — there is no API key in this repository.

## License

MIT. See [NOTICE](NOTICE) for the upstream attribution.
