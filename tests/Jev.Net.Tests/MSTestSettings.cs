// Every test builds its own client over its own stub handler, reads the environment through an injected
// reader, and waits through an injected delay — nothing is shared, so methods run side by side.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
