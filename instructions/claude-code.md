# MemShack Instructions For Claude Code

Use MemShack as the persistent memory layer for this repo.

## Startup

- Use `mems wake-up --wing <wing>` when resuming a known project.
- Use `mems search "<query>" --wing <wing>` before repeating research that may
  already exist in the palace.

## Saving Memory

- After important decisions, migrations, debugging sessions, or user-approved
  changes, make sure the files are present locally and run:

```powershell
mems mine <project-dir> --wing <wing>
```

- If the project has never been initialized for MemShack, run:

```powershell
mems init <project-dir>
```

first.

## Integrations

- `mems mcp` prints the MCP setup command.
- `mems hook` prints ready-to-paste Claude Code hook JSON plus hook asset paths.
- `mems instructions` prints the paths to these instruction files.

## Intentional MemShack Differences

- Hooks are Bash scripts today rather than native Windows hook executables.
- `mems shutdowndb` exists in C# even though it is not part of the original
  Python CLI surface.
