# MemPalace to MemShack Migration Guide

## Audience

This guide is for existing MemPalace users who want to move to the C# implementation without changing their stored palace data contracts.

## What Stays Compatible

- config directory location: `~/.mempalace`
- default palace directory location: `~/.mempalace/palace`
- knowledge graph database path: `~/.mempalace/knowledge_graph.sqlite3`
- collection names: `mempalace_drawers` and `mempalace_compressed`
- MCP tool names and high-level JSON-RPC method surface
- bootstrap file names such as `identity.txt`, `aaak_entities.md`, and `critical_facts.md`

## Before You Switch

1. Back up the full `.mempalace` directory, including the palace directory and the SQLite KG file.
2. Run `dotnet test C:\dev\memshack\MemShack.slnx`.
3. Review `docs/validation/palace2shack-validation-report.md`.
4. Review `docs/tool-installation.md` for the packaged-tool install flow.
5. Review `docs/mcp-setup.md` if your current workflow depends on MCP clients.

## Recommended Cutover Path

1. Keep the existing palace path unchanged.
2. Start with read-oriented checks:
   - `mems status`
   - `mems search <query>`
   - `mems wake-up`
3. Run write paths in lower-risk order:
   - `mems mine <dir> --dry-run`
   - `mems compress --dry-run`
   - `mems split <dir> --dry-run`
4. Validate MCP clients against the C# server while keeping the Python server available for rollback.
5. Switch automation or client entrypoints only after the parity suite and smoke checks pass.

## Known Differences at First Cutover

- Programmatic Wikipedia-backed entity research is available in the C# entity registry, but there is still no dedicated CLI or MCP surface for it.
- The onboarding port is bootstrap-oriented rather than a fully interactive CLI wizard.
- The initial migration intentionally keeps the Chroma-compatible storage contract instead of changing the vector store at the same time.
- MemShack auto-downloads and manages the Chroma CLI on first semantic use by default, instead of expecting the Python package environment.
- `mems shutdowndb` is a MemShack-only command for stopping the managed local Chroma process for a palace.
- MCP setup is documented through `mems mcp` and the repo-checkout server command in `docs/mcp-setup.md`.

## Versioning Note

The MemShack NuGet/tool version is independent from the Python MemPalace
package version.

Treat these as the compatibility checkpoints that matter during cutover:

- storage contract compatibility
- CLI and MCP behavior
- migration and rollback guidance
- runtime requirements such as Chroma and SQLite

Do not assume a matching Python version number is required for a safe cutover.

## Rollback

If a cutover issue appears:

1. Stop the C# CLI or MCP server.
2. Point your workflow back to the Python CLI or MCP entrypoint.
3. Restore the backed-up `.mempalace` directory if any write-path issue affected stored data.
4. Re-run the parity suite after fixes before attempting another cutover.

## Practical Rule

Do not replace the Python implementation in production until the documented live Python-vs-C# diff has been reviewed and accepted for your rollout.
