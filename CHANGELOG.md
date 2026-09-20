# Changelog

## 0.3.0

All additive.

- `SystemOneResponse.FromHttpResponse` / `ListModelsResponse.FromHttpResponse`, a public `RawHttpResponse`
  constructor and `RawHttpResponse.FromAsync(HttpResponseMessage)`: build a genuine, validated response from a
  cache, a recording, or a test. The Python SDK's `from_http_response`.
- `ITypeSafeClient` and `IModelsResource`, so code that uses the client can be given a fake.
- `SystemOneResponse.UnmodeledAnswers`: answers whose type this SDK does not model, kept as sent. `Answers` is
  unchanged (it still omits them, as the Python SDK does).
- `ScoreAnswer.MostLikely`: the most probable level, beside the averaged `Score`.
- `TypeSafeDefaults.SdkVersion`.

## 0.2.1

- **Fixed:** `TypeSafeRateLimitException.RetryAfter` now reads the client's `TimeProvider`. With an HTTP-date
  `Retry-After` and a non-system clock it disagreed with the delay the SDK actually waited.
- The default HTTP handler now asks for compressed responses (as the Python SDK's does) and recycles pooled
  connections every two minutes, so a long-lived client follows DNS changes. Unaffected if you supply your own
  `Handler` or `HttpClient`.
- `net8.0` is now tested, not just built; packing fails on a breaking change to the public API.

## 0.2.0

- **Trim- and Native AOT-compatible.** The library builds with the trim and AOT analyzers as errors, and a smoke
  app is published as a native binary and run in CI on Linux and Windows.
- `SystemOneAsync(state, questions, options, JsonTypeInfo<T>)` and `JsonContent.From(value, JsonTypeInfo<T>)` for
  source-generated JSON.
- **Changed:** reading the body into an arbitrary type now takes its JSON settings explicitly —
  `SystemOneAsync<T>(state, questions, options, ResponseJson.SnakeCase)` (reflection) or the `JsonTypeInfo<T>` overload.
  `options` stays the third parameter on every overload, so passing `null` for it is never ambiguous.
  `SystemOneAsync<T>(state, questions)` is now for `SystemOneResponse` subclasses only, which need a public
  parameterless constructor.
- **Changed:** an answer property on a `SystemOneResponse` subclass is required unless marked
  `[OptionalAnswer]`. 0.1.0 inferred this from nullability, which does not survive trimming.
- Package icon; the one-dependency promise is now checked in CI against the packed `.nuspec`.

## 0.1.0

First release. A port of the official TypeSafe AI Python SDK (`typesafe-sdk` 0.7.0) to .NET:

- `TypeSafeClient` with `SystemOneAsync` and `Models.ListAsync`.
- Typed `Noul`, `Choice` and `Score` questions, plus raw JSON questions for anything the API adds first.
- Typed answers with probabilities and confidence; derive from `SystemOneResponse` for answers by name.
- The Python SDK's error model and message text; malformed responses name the first bad field.
- Retries with exponential backoff and jitter, `Retry-After` / `retry-after-ms`, and a total budget per call.
- Caller cancellation is never reported as a timeout and never retried.
- One dependency: `Microsoft.Extensions.Logging.Abstractions`. Targets `net8.0` and `net10.0`.
