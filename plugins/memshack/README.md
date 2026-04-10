# MemShack Codex Plugin

This is a repo-local Codex plugin stub for MemShack.

## What It Includes

- `.codex-plugin/plugin.json`
  Marketplace-facing plugin metadata.
- `.mcp.json`
  A repo-local MCP server config pointing at `src/MemShack.McpServer`.
- `hooks.json`
  Hook references that point at the repo's Bash hook scripts.

## Intentional Limits

- The hook commands assume Bash is available.
- The MCP config assumes a repo-local checkout with `dotnet` on `PATH`.
- This plugin metadata is meant for local development and ordering in Codex, not
  as a published marketplace artifact.

## Recommended First Step

Run:

```powershell
mems mcp
```

to get the explicit MCP setup command for your current checkout.

For the fuller setup notes, see [../../docs/mcp-setup.md](../../docs/mcp-setup.md).
