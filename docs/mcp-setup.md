# MemShack MCP Setup

## Recommended First Step

Run:

```powershell
mems mcp
```

That prints the exact MCP server command for the current checkout, including an
optional `--palace` override when you pass one to `mems`.

## Current Setup Model

Today, MemShack documents MCP as a repo-checkout workflow:

- the packaged `mems` .NET tool is the normal CLI entrypoint
- the MCP server is started from the repo checkout with `dotnet run --project`
- `mems mcp` exists to print the correct command without making you rebuild it
  by hand

This keeps the MCP setup close to the current Python guidance while matching the
actual C# project layout.

## Claude Code Example

From the MemShack repo root:

```powershell
mems mcp
```

Then copy the printed command into Claude Code. It will look like:

```powershell
claude mcp add mempalace -- dotnet run --project C:\path\to\memshack\src\MemShack.McpServer -- --palace C:\path\to\palace
```

If you omit `--palace`, the server uses the default MemPalace-compatible path
under `~/.mempalace/palace`.

## Codex And Repo-Local Assets

MemShack also ships repo-local integration assets:

- `mems hook`
  Prints or exports hook files for Claude Code and Codex.
- `mems instructions`
  Prints or exports repo-local instruction assets.
- `plugins/memshack/`
  Contains repo-local Codex plugin metadata, `.mcp.json`, and hook references.

See:

- [../hooks/README.md](../hooks/README.md)
- [../instructions/README.md](../instructions/README.md)
- [../plugins/memshack/README.md](../plugins/memshack/README.md)
- [../integrations/openclaw/SKILL.md](../integrations/openclaw/SKILL.md)

## OpenClaw / ClawHub

MemShack now ships a repo-local OpenClaw skill asset. Start with `mems mcp`,
then adapt the printed `dotnet run --project ...` command into your OpenClaw or
ClawHub MCP configuration.

## Notes

- The MCP tool names and JSON-RPC surface stay aligned with the MemPalace
  storage contract.
- `mems shutdowndb` is a MemShack-only helper and is not part of the upstream
  Python MCP setup.
- For local CLI-only use, you do not need MCP at all. `mems search`,
  `mems wake-up`, and the other CLI commands work independently.
