---
name: memshack
description: "MemShack — local AI memory for OpenClaw/ClawHub via the MemPalace-compatible MCP tool surface. Semantic search, temporal knowledge graph, wings/rooms/drawers, and local Chroma managed by the .NET tool."
version: 0.1.0
homepage: https://github.com/loxsmoke/memshack
user-invocable: true
metadata:
  openclaw:
    emoji: "\U0001F3DB"
    os:
      - darwin
      - linux
      - win32
    requires:
      anyBins:
        - mems
        - dotnet
---

# MemShack — Local AI Memory System

You have access to a local memory palace via MCP tools. The palace stores verbatim conversation history and a temporal knowledge graph on the user's machine, using the MemPalace-compatible tool surface exposed by MemShack.

## Protocol — follow this every session

1. On wake-up, call `mempalace_status` to load the palace overview and AAAK dialect.
2. Before responding about a person, project, or prior event, call `mempalace_search` or `mempalace_kg_query` first.
3. If unsure, say you are checking and query the palace. Wrong is worse than slow.
4. After each session, call `mempalace_diary_write` to record what happened and what matters.
5. When facts change, invalidate the old knowledge-graph fact and add the new one.

## Notes for MemShack

- Local Chroma is managed automatically by `mems`. The first Chroma-backed run auto-downloads the official Chroma CLI if needed.
- The MCP tool names stay MemPalace-compatible, so you will still see tool names like `mempalace_search`.
- `mempalace_check_duplicate.threshold` in MemShack is a similarity score from `0` to `1`, where higher is stricter.
- `mems dedup` also uses similarity semantics. A threshold around `0.85` to `0.87` catches more near-duplicate chunks than the stricter default.

## OpenClaw / ClawHub setup

Use the MemShack helper first:

```bash
mems mcp
```

That prints the exact `dotnet run --project ...` command for the current checkout and optional `--palace` override.

Example OpenClaw MCP configuration:

```json
{
  "mcpServers": {
    "mempalace": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/memshack/src/MemShack.McpServer",
        "--",
        "--palace",
        "/path/to/palace"
      ]
    }
  }
}
```

If you installed the packaged .NET tool globally, `mems` handles the local Chroma auto-download flow for normal CLI commands such as `mine`, `search`, `dedup`, and `migrate`.

## Useful commands

```bash
mems init /path/to/project --yes
mems mine /path/to/project
mems search "what changed?"
mems dedup --dry-run
mems migrate --dry-run
mems wake-up
```

## Tooling surface

- `mempalace_search` — semantic search
- `mempalace_check_duplicate` — similarity-based duplicate check
- `mempalace_status` / `mempalace_list_wings` / `mempalace_list_rooms`
- `mempalace_kg_query` / `mempalace_kg_add` / `mempalace_kg_invalidate`
- `mempalace_traverse` / `mempalace_find_tunnels`
- `mempalace_add_drawer` / `mempalace_delete_drawer`
- `mempalace_diary_write` / `mempalace_diary_read`
