# MemShack Instruction Assets

These instruction files are meant to be copied into your editor or assistant
setup so MemShack gets used consistently during real work.

## Files

- `codex.md`
  Repo-local guidance for Codex users.
- `claude-code.md`
  Repo-local guidance for Claude Code users.

## Setup

Use:

```powershell
mems instructions
```

to print the resolved asset paths, or:

```powershell
mems instructions --output-dir C:\path\to\instructions
```

to export the files somewhere stable for copy/paste.

## Notes

- These are text assets, not executable integrations.
- The actual MCP wiring still lives behind `mems mcp`.
- The actual hook scripts live under `hooks/`.
- The repo-local Codex plugin metadata lives under `plugins/memshack/`.
