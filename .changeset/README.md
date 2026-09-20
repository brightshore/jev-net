# Changesets

Record a user-facing change in the same PR that makes it:

```bash
changerig add -t fix -m "A short paragraph for the person reading the changelog."
```

`shiprig release` on `main` turns the pending changesets into a version bump and `CHANGELOG.md`, tags
`Jev.Net@x.y.z`, and pushes — and the tag is what publishes. Don't hand-edit versions or the changelog.
