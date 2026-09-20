# Changelog

## 0.1.0

First release. A port of the official TypeSafe AI Python SDK (`typesafe-sdk` 0.7.0) to .NET:

- `TypeSafeClient` with `SystemOneAsync` and `Models.ListAsync`.
- Typed `Noul`, `Choice` and `Score` questions, plus raw JSON questions for anything the API adds first.
- Typed answers with probabilities and confidence; derive from `SystemOneResponse` for answers by name.
- The Python SDK's error model and message text; malformed responses name the first bad field.
- Retries with exponential backoff and jitter, `Retry-After` / `retry-after-ms`, and a total budget per call.
- Caller cancellation is never reported as a timeout and never retried.
- One dependency: `Microsoft.Extensions.Logging.Abstractions`. Targets `net8.0` and `net10.0`.
