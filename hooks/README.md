# MemShack Hooks

These Bash hooks give MemShack the same basic auto-save checkpoints that the
Python MemPalace repo ships for Claude Code and Codex CLI.

## Files

- `memshack_save_hook.sh`
  Triggers every `SAVE_INTERVAL` user messages and blocks the tool long enough
  for the assistant to save important memory.
- `memshack_precompact_hook.sh`
  Always blocks right before compaction so the assistant can save everything
  before context is dropped.

## Setup

Use `mems hook` to print ready-to-paste JSON snippets and the resolved asset
paths. If you want a copy in another location, run:

```powershell
mems hook --output-dir C:\path\to\hooks
```

Then point Claude Code or Codex CLI at the exported files.

## Notes

- These are Bash scripts. On Windows, run them with Git Bash or WSL.
- If you set `MEMPAL_DIR`, the hooks can optionally auto-run:

```bash
mems mine <dir> --mode convos
```

- The scripts default to `MEMSHACK_COMMAND=mems`, so they work with the .NET
  tool instead of the Python package.
- Hook state is written under `~/.mempalace/hook_state/`.
- If you prefer a simpler integration path, use `mems mcp` and skip hooks.

## Intentional Differences From Upstream Python

- MemShack keeps the hooks Bash-only for now instead of adding platform-native
  PowerShell variants.
- MemShack keeps `shutdowndb` as a separate CLI command rather than mixing DB
  lifecycle into the hook workflow.
