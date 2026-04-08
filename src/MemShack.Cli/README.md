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

## Core Commands

```powershell
mems init <dir> [--yes]
mems mine <dir>
mems search <query>
mems status
mems wake-up
mems compress
mems split <dir>
```

## Storage Compatibility

The tool keeps the MemPalace-compatible storage contract:

- config under `~/.mempalace`
- palace data under `~/.mempalace/palace`
- collection names `mempalace_drawers` and `mempalace_compressed`
- knowledge graph at `~/.mempalace/knowledge_graph.sqlite3`

## More Docs

See [https://github.com/loxsmoke/memshack](https://github.com/loxsmoke/memshack) for install and migration details.
