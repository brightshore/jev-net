# Changelog

## 0.2.0

- **Trim- and Native AOT-compatible.** The library builds with the trim and AOT analyzers as errors, and a smoke
  app is published as a native binary and run in CI on Linux and Windows.
- `SystemOneAsync(…, JsonTypeInfo<T>)` and `JsonContent.From(value, JsonTypeInfo<T>)` for source-generated JSON.
- **Changed:** reading the body into an arbitrary type now takes its JSON settings explicitly —
  `SystemOneAsync<T>(state, questions, ResponseJson.SnakeCase)` (reflection) or the `JsonTypeInfo<T>` overload.
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
