# Upstream Parity TODO

This file tracks the meaningful upstream Python features and hardening work that
still matter for MemShack parity. It is intentionally more exhaustive than a
normal backlog, but it still does not try to list every version bump or CI-only
commit from upstream.

## 1. Completed

- [x] 1.1 Re-mine modified project files when `source_mtime` changes, not just when the source path is new.
- [x] 1.2 Use the actual detected room for project `mine` summary counts.
- [x] 1.3 Bring MCP basics closer to upstream:
  - [x] 1.3.1 `--palace` support for `MemShack.McpServer`
  - [x] 1.3.2 Protocol version negotiation during `initialize`
  - [x] 1.3.3 Null-argument-safe tool calls
  - [x] 1.3.4 Generic internal error envelopes for `tools/call`
  - [x] 1.3.5 `mems mcp` setup guidance command
- [x] 1.4 Keep `mems shutdowndb` as a MemShack-specific CLI command.

## 2. Mining And Storage Parity

- [x] 2.1 Skip symlinks during project scanning/mining, matching upstream safety behavior.
- [x] 2.2 Skip oversized project files during mining with an explicit max-file-size policy.
- [x] 2.3 Add a normalization safety limit for extremely large input files.
- [x] 2.4 Review and align the full project scan skip-directory list with upstream `miner.py`, while keeping MemShack-specific additions for `.dotnet`, `.nuget`, `bin`, and `obj`.
- [x] 2.5 Move mined drawer IDs from MD5-style hashes to SHA-256-style hashes.
- [x] 2.6 Reconfirm that conversation mining should remain path-only for skip checks while project mining uses `source_mtime`.
- [x] 2.7 Recheck room detection/path keyword behavior against the current upstream miner after the recent fixes.
- [x] 2.8 Recheck file ordering and scan stability across Windows/Linux/macOS after the recent traversal changes; exact traversal order remains filesystem-dependent by design, while the intended file set and current-directory-before-descend behavior are covered by tests.

## 3. MCP Server Parity

- [x] 3.1 Harden MCP write tools with input sanitization for wing, room, entity, agent, and content fields.
- [x] 3.2 Make `add_drawer` use deterministic SHA-256-style IDs instead of timestamp-based/MD5-style IDs.
- [x] 3.3 Make `diary_write` use deterministic SHA-256-style IDs where upstream now does.
- [x] 3.4 Make MCP write operations idempotent where upstream now treats existing records as success/no-op.
- [x] 3.5 Add MCP write-ahead logging for drawer, diary, and knowledge-graph mutations.
- [x] 3.6 Review duplicate-check behavior and response payloads against upstream MCP semantics.
- [x] 3.7 Bound any large metadata enumeration paths in MCP reads so they do not grow unbounded.
- [x] 3.8 Review whether MemShack should cache/reuse vector-store clients similarly to the upstream MCP server's collection/client caching; current server instances already reuse a single configured `IVectorStore`.
- [x] 3.9 Align `no palace found` / search error payloads more closely with upstream where useful.
- [x] 3.10 Decide whether to add the Python-style `mcp` command/setup wording to more docs and examples beyond the CLI helper.

## 4. Knowledge Graph And SQLite Parity

- [x] 4.1 Enable SQLite WAL mode in the knowledge-graph store.
- [x] 4.2 Review whether KG connection lifetime should move closer to the upstream long-lived connection model; keep short-lived connections in MemShack to avoid lingering file locks, and rely on WAL for the durability/concurrency improvement.
- [x] 4.3 Review KG triple ID hashing and move triple IDs from MD5-style hashes to SHA-256-style hashes.
- [x] 4.4 Recheck KG query/stat output parity after WAL and connection changes.
- [x] 4.5 Reconfirm timeline limits and relationship-query bounds against the latest upstream behavior; both global and entity timelines now cap at 100, while relationship queries remain unbounded to match upstream.

## 5. CLI, Config, And UX Parity

- [x] 5.1 Tighten config directory permissions where the platform supports it.
- [x] 5.2 Tighten config file permissions where the platform supports it.
- [x] 5.3 Review whether `search` error handling should mirror the latest Python CLI more closely; `search` now prints Python-style `No palace found at ...` guidance before suggesting `init` and `mine`.
- [x] 5.4 Decide whether to add Python-style `hook` subcommands to the C# CLI; add a lightweight `mems hook` guidance command now, while leaving full hook assets to section 6.
- [x] 5.5 Decide whether to add Python-style `instructions` subcommands to the C# CLI; add a lightweight `mems instructions` guidance command now, while leaving packaged instruction assets to section 6.
- [x] 5.6 Review whether any other Python CLI help/output wording changes should be mirrored in MemShack; help output now includes the new guidance commands and the updated search failure wording.

## 6. Hooks, Plugins, And Integrations

- [x] 6.1 Decide whether MemShack should add Codex hook support analogous to upstream `hooks_cli.py`; ship repo-local Bash hook assets plus actionable `mems hook` guidance/export support.
- [x] 6.2 Decide whether MemShack should add Claude hook/plugin support analogous to the upstream plugin assets; ship Claude-ready hook JSON snippets and repo-local plugin metadata.
- [x] 6.3 Decide whether MemShack should add packaged instruction assets similar to upstream command instructions; ship exportable instruction markdown assets and `mems instructions`.
- [x] 6.4 Decide whether MemShack should publish plugin metadata/marketplace files similar to upstream; add a repo-local `plugins/memshack` manifest and `.agents/plugins/marketplace.json`.
- [x] 6.5 Document any intentional C# deviations if we choose not to implement the upstream hook/plugin surface; document the Bash-first hook limitation, repo-local plugin scope, and continued `shutdowndb` difference in the shipped docs.

## 7. Tests And Benchmarks

- [x] 7.1 Expand CLI coverage toward the newer upstream CLI surface and failure modes.
- [x] 7.2 Expand entity detector coverage toward upstream test depth.
- [x] 7.3 Expand room detector coverage toward upstream test depth.
- [x] 7.4 Expand general memory extractor coverage toward upstream test depth.
- [x] 7.5 Expand normalization coverage toward upstream test depth and large-file guards.
- [x] 7.6 Expand onboarding coverage toward upstream test depth.
- [x] 7.7 Expand spellcheck coverage toward upstream test depth.
- [x] 7.8 Expand layers/wake-up coverage toward upstream test depth.
- [x] 7.9 Expand MCP coverage toward upstream test depth, including write hardening and error envelopes.
- [x] 7.10 Add or port search/recall benchmark coverage where it is useful for MemShack.
- [x] 7.11 Add or port stress tests for Chroma/vector-store behavior where they are useful for MemShack.
- [x] 7.12 Review CI matrix coverage against upstream Windows/macOS/Linux expectations.

## 8. Docs And Release Hygiene

- [x] 8.1 Review README parity and decide which upstream documentation changes belong in MemShack.
- [x] 8.2 Review MCP setup docs and examples for parity with the latest Python guidance.
- [x] 8.3 Review packaging/install docs after the Chroma auto-download direction.
- [x] 8.4 Document intentional MemShack-only features, especially `shutdowndb`.
- [x] 8.5 Review versioning/release metadata differences and decide which are product-significant versus upstream churn.

## 9. Upstream Follow-Ups After 2026-04-10 Fetch

- [x] 9.1 Add a `mems migrate` recovery path analogous to upstream `mempalace migrate`, focused on recovering palaces created with incompatible ChromaDB on-disk versions by reading `chroma.sqlite3` directly, rebuilding a fresh palace, backing up before swap, and supporting `--dry-run`.
- [x] 9.2 Add an explicit `dedup` command rather than folding the flow into `repair`, and include the missing wing-scoped duplicate cleanup flow that upstream now exposes with `--wing`.
- [x] 9.3 Review duplicate-threshold semantics and docs against the latest upstream guidance. MemShack now documents its explicit choice to keep similarity-threshold semantics for `dedup` and MCP `check_duplicate`, instead of mirroring upstream Chroma cosine-distance wording.
- [x] 9.4 Add regression coverage for duplicate cleanup / repair behavior analogous to the newer upstream `repair.py` + `dedup.py` test expansion, including service and CLI coverage for `dedup` and `migrate`.
- [x] 9.5 Port the Codex stop-hook message-counting fix into MemShack's Bash save hook so Codex `event_msg` / `user_message` transcripts trigger auto-save thresholds the same way upstream now does.
- [x] 9.6 Add hook coverage for both Claude-style and Codex-style transcript counting so the Bash hook behavior is locked in by tests rather than only documented.
- [x] 9.7 Add an OpenClaw / ClawHub skill analogous to upstream `integrations/openclaw/SKILL.md`, using the current C# MCP tool surface and setup commands instead of the Python package entrypoint.
- [x] 9.8 Ship the OpenClaw skill asset plus MemShack-specific setup instructions for local Chroma, `mems mcp`, and the packaged .NET tool flow.
