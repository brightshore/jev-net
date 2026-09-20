# Security

Please report a vulnerability privately, through GitHub's
[security advisories](https://github.com/brightshore/jev-net/security/advisories/new) for this repository,
rather than in a public issue.

Things worth knowing when you assess this library:

- It sends your `state` and questions to the TypeSafe AI API over HTTPS, authenticated with your API key as a
  bearer token. **The handler the SDK creates for itself does not follow redirects**, so the token is not
  replayed to another host. That guarantee covers the default only: if you supply your own
  `TypeSafeClientOptions.Handler` or `HttpClient`, its redirect policy applies and the SDK cannot override it —
  `HttpClientHandler` and `SocketsHttpHandler` both follow redirects unless you set `AllowAutoRedirect = false`.
- Credential-bearing headers are redacted from its logs. **Request and response bodies are not** - at Debug
  level they are logged as-is, and they contain whatever you sent.
- It is a community package, not affiliated with TypeSafe AI. Problems with the API itself belong with them.
