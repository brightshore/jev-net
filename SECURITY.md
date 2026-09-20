# Security

Please report a vulnerability privately, through GitHub's
[security advisories](https://github.com/brightshore/jev-net/security/advisories/new) for this repository,
rather than in a public issue.

Things worth knowing when you assess this library:

- It sends your `state` and questions to the TypeSafe AI API over HTTPS, authenticated with your API key as a
  bearer token. Redirects are never followed, so the token is not replayed to another host.
- Credential-bearing headers are redacted from its logs. **Request and response bodies are not** - at Debug
  level they are logged as-is, and they contain whatever you sent.
- It is a community package, not affiliated with TypeSafe AI. Problems with the API itself belong with them.
