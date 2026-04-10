# LoxSmoke.Mems

`LoxSmoke.Mems` is the MemShack CLI packaged as a .NET tool.

Project home: [https://github.com/loxsmoke/memshack](https://github.com/loxsmoke/memshack)

After installation, invoke it with:

```powershell
mems
```

## Install

Global install:

```powershell
dotnet tool install --global LoxSmoke.Mems
```

Local manifest install:

```powershell
dotnet new tool-manifest
dotnet tool install --local LoxSmoke.Mems
```

On the first real semantic command such as `mems mine` or `mems search`,
MemShack auto-downloads the official Chroma CLI into `~/.mempalace/chroma/`
when needed and starts it automatically for the selected palace.

## Core Commands

```powershell
mems init <dir> [--yes]
mems mine <dir>
mems search <query>
mems status
mems wake-up
mems compress
mems split <dir>
mems mcp
mems shutdowndb
```

## MCP And Runtime Notes

- `mems mcp` prints the current repo-checkout MCP server command.
- `mems shutdowndb` stops the managed local Chroma process for a palace.
- For detailed setup guidance, see the repo docs:
  - `docs/tool-installation.md`
  - `docs/mcp-setup.md`
  - `docs/migration-guide.md`

## Storage Compatibility

The tool keeps the MemPalace-compatible storage contract:

- config under `~/.mempalace`
- palace data under `~/.mempalace/palace`
- collection names `mempalace_drawers` and `mempalace_compressed`
- knowledge graph at `~/.mempalace/knowledge_graph.sqlite3`

## More Docs

See [https://github.com/loxsmoke/memshack](https://github.com/loxsmoke/memshack) for install and migration details.
