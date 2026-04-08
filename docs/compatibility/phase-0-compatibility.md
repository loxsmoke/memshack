# Phase 0 Compatibility Notes

## Purpose

This document freezes the Python runtime contracts that the first C# migration slices must preserve.

It is based on direct inspection of the current Python implementation in `C:\dev\mempalace`.

## Primary Sources Inspected

- `mempalace/cli.py`
- `mempalace/mcp_server.py`
- `mempalace/config.py`
- `mempalace/miner.py`
- `mempalace/convo_miner.py`
- `mempalace/searcher.py`
- `mempalace/layers.py`
- `mempalace/knowledge_graph.py`
- `mempalace/palace_graph.py`
- `mempalace/normalize.py`
- `tests/conftest.py`
- `tests/test_miner.py`
- `tests/test_convo_miner.py`
- `tests/test_mcp_server.py`
- `tests/test_knowledge_graph.py`
- `tests/test_normalize.py`

## CLI Contract

Global option:

- `--palace <path>`
  - optional
  - defaults to the path resolved by `~/.mempalace/config.json` or the built-in default palace path

Commands and arguments:

- `init <dir>`
  - required positional: `dir`
  - optional flags: `--yes`
  - behavior: scans for entities, may write `<project>/entities.json`, detects rooms, initializes global config
- `mine <dir>`
  - required positional: `dir`
  - optional flags: `--mode projects|convos`, `--wing`, `--no-gitignore`, `--include-ignored`, `--agent`, `--limit`, `--dry-run`, `--extract exchange|general`
  - note: `--extract` only changes conversation mining behavior
- `search <query>`
  - required positional: `query`
  - optional flags: `--wing`, `--room`, `--results`
- `compress`
  - optional flags: `--wing`, `--dry-run`, `--config`
- `wake-up`
  - optional flags: `--wing`
- `split <dir>`
  - required positional: `dir`
  - optional flags: `--output-dir`, `--dry-run`, `--min-sessions`
- `repair`
  - no command-specific options
- `status`
  - no command-specific options

Dispatch rule:

- if no subcommand is supplied, the parser prints help and exits without running a handler

## MCP Transport Contract

Transport:

- JSON-RPC 2.0 over stdio
- one JSON request per line on stdin
- one JSON response per line on stdout when a response is required

Supported top-level methods:

- `initialize`
- `notifications/initialized`
- `tools/list`
- `tools/call`

Initialize response shape:

- `jsonrpc: "2.0"`
- `id: <same request id>`
- `result.protocolVersion: "2024-11-05"`
- `result.capabilities.tools: {}`
- `result.serverInfo.name: "mempalace"`
- `result.serverInfo.version: <package version>`

Tool call response envelope:

- successful tool calls return:
  - `jsonrpc: "2.0"`
  - `id: <same request id>`
  - `result.content[0].type: "text"`
  - `result.content[0].text: <JSON string produced by json.dumps(result, indent=2)>`
- unknown tool and unknown method errors use code `-32601`
- tool runtime exceptions return code `-32000`

Argument coercion behavior in `tools/call`:

- if a schema field is declared as `integer`, Python coerces non-int values using `int(value)` before invoking the handler
- if a schema field is declared as `number`, Python coerces non-numeric values using `float(value)` before invoking the handler

## MCP Tool Surface

Current tool names: 19 total

- `mempalace_status`
  - args: none
- `mempalace_list_wings`
  - args: none
- `mempalace_list_rooms`
  - optional args: `wing`
- `mempalace_get_taxonomy`
  - args: none
- `mempalace_get_aaak_spec`
  - args: none
- `mempalace_kg_query`
  - required args: `entity`
  - optional args: `as_of`, `direction`
- `mempalace_kg_add`
  - required args: `subject`, `predicate`, `object`
  - optional args: `valid_from`, `source_closet`
- `mempalace_kg_invalidate`
  - required args: `subject`, `predicate`, `object`
  - optional args: `ended`
- `mempalace_kg_timeline`
  - optional args: `entity`
- `mempalace_kg_stats`
  - args: none
- `mempalace_traverse`
  - required args: `start_room`
  - optional args: `max_hops`
- `mempalace_find_tunnels`
  - optional args: `wing_a`, `wing_b`
- `mempalace_graph_stats`
  - args: none
- `mempalace_search`
  - required args: `query`
  - optional args: `limit`, `wing`, `room`
- `mempalace_check_duplicate`
  - required args: `content`
  - optional args: `threshold`
- `mempalace_add_drawer`
  - required args: `wing`, `room`, `content`
  - optional args: `source_file`, `added_by`
- `mempalace_delete_drawer`
  - required args: `drawer_id`
- `mempalace_diary_write`
  - required args: `agent_name`, `entry`
  - optional args: `topic`
- `mempalace_diary_read`
  - required args: `agent_name`
  - optional args: `last_n`

## Storage Contract

### Chroma collection names

These names are part of the current runtime contract and should stay stable early in the migration.

- main drawer collection: `mempalace_drawers`
- compressed collection: `mempalace_compressed`

### Drawer id formats

Project and conversation mining:

- `drawer_{wing}_{room}_{md5(source_file + chunk_index)[:16]}`

MCP add drawer:

- `drawer_{wing}_{room}_{md5(content[:100] + datetime.now().isoformat())[:16]}`

Diary entries:

- `diary_{wing}_{YYYYMMDD_HHMMSS}_{md5(entry[:50])[:8]}`

### Drawer metadata keys

Common keys written by project mining and most direct writes:

- `wing`
- `room`
- `source_file`
- `chunk_index`
- `added_by`
- `filed_at`

Conversation mining adds:

- `ingest_mode`
- `extract_mode`

Diary writes add:

- `hall`
- `topic`
- `type`
- `agent`
- `date`

Read paths also depend on these optional keys when present:

- `importance`
- `emotional_weight`
- `weight`

`palace_graph.py` reads these metadata keys to derive tunnels and room connectivity:

- `room`
- `wing`
- `hall`
- `date`

## Knowledge Graph Contract

Default database path:

- `~/.mempalace/knowledge_graph.sqlite3`

Current tables:

- `entities`
- `triples`

`entities` columns:

- `id TEXT PRIMARY KEY`
- `name TEXT NOT NULL`
- `type TEXT DEFAULT 'unknown'`
- `properties TEXT DEFAULT '{}'`
- `created_at TEXT DEFAULT CURRENT_TIMESTAMP`

`triples` columns:

- `id TEXT PRIMARY KEY`
- `subject TEXT NOT NULL`
- `predicate TEXT NOT NULL`
- `object TEXT NOT NULL`
- `valid_from TEXT`
- `valid_to TEXT`
- `confidence REAL DEFAULT 1.0`
- `source_closet TEXT`
- `source_file TEXT`
- `extracted_at TEXT DEFAULT CURRENT_TIMESTAMP`

Current indexes:

- `idx_triples_subject`
- `idx_triples_object`
- `idx_triples_predicate`
- `idx_triples_valid`

Entity id normalization:

- lowercase the name
- replace spaces with underscores
- remove apostrophes

Examples:

- `Alice` -> `alice`
- `Dr. Chen` -> `dr._chen`

Triple id format:

- `t_{sub_id}_{pred}_{obj_id}_{md5(valid_from + now_iso)[:8]}`

Predicate normalization:

- lowercase the predicate
- replace spaces with underscores

Temporal query semantics:

- current facts are represented by `valid_to IS NULL`
- `as_of` filters include facts where `valid_from` is null or `<= as_of`, and `valid_to` is null or `>= as_of`

## File And Config Locations

The following paths are part of the currently observed runtime contract.

Global config files:

- `~/.mempalace/config.json`
- `~/.mempalace/people_map.json`
- `~/.mempalace/identity.txt`
- `~/.mempalace/knowledge_graph.sqlite3`
- `~/.mempalace/known_names.json`

Project-local files:

- `mempalace.yaml`
- `mempal.yaml` (legacy fallback)
- `entities.json`

Notes:

- `mempalace init` writes `entities.json` when entities are confirmed
- `miner.py` reads `mempalace.yaml` and falls back to `mempal.yaml`
- `split_mega_files.py` reads `~/.mempalace/known_names.json`
- the inspected runtime code uses `config.json`, not `wing_config.json`

## Supported Transcript Input Shapes

Current normalization logic explicitly supports these representative shapes:

- plain text transcript with `>` user-turn markers
- Claude Code JSONL
- OpenAI Codex CLI JSONL with `session_meta` plus `event_msg`
- Claude.ai flat messages JSON
- Claude.ai privacy export JSON with `chat_messages`
- ChatGPT `mapping` tree JSON
- Slack JSON exports with `type: message`
- plain text fallback when no known format matches

## Fixture Inventory

The `fixtures/phase0` folder in this solution contains representative examples for:

- config files
- transcript formats
- seeded drawer records
- seeded knowledge graph records
- MCP request and response envelopes

These fixtures are intended to become parity tests for the C# implementation.
