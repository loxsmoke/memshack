# MemShack Instructions For Codex

Use MemShack as the local memory system for this repo.

## Startup

- When returning to an existing project, run `mems wake-up --wing <wing>` if a
  wing is already established.
- Before asking repeated questions, try `mems search "<query>" --wing <wing>`.

## Saving Memory

- After a meaningful decision, fix, migration, or architecture change, make
  sure the relevant source/docs live in the project tree and run:

```powershell
mems mine <project-dir> --wing <wing>
```

- If the project is not initialized yet, run:

```powershell
mems init <project-dir>
```

before mining.

## Tooling

- For MCP integration, use `mems mcp`.
- For optional stop/pre-compact hooks, use `mems hook`.
- If the managed Chroma sidecar needs to be stopped explicitly, use the
  MemShack-only command:

```powershell
mems shutdowndb
```

## Boundaries

- Hook assets are Bash-first today.
- Plugin metadata is repo-local and meant as scaffolding, not a full packaged
  marketplace release.
