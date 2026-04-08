<div align="center">

<img src="assets/memshack_logo.png" alt="MemPalace" width="280">

</div>

# MemShack

> [!IMPORTANT]
> This repository is the C#/.NET migration of the original [MemPalace](https://github.com/milla-jovovich/mempalace) codebase.
> It exists so the MemPalace system can be maintained, packaged, and run as a modern .NET CLI and MCP server while preserving the existing MemPalace storage contracts, tool surface, and migration path for current users.
> This is not a greenfield rewrite with a new data model. Compatibility with the original MemPalace behavior and on-disk data is the reason this repo exists.
>
> [Original readme file](https://github.com/milla-jovovich/mempalace/blob/main/README.md)

MemShack is the .NET implementation of the MemPalace tooling stack.

Repository: [https://github.com/loxsmoke/memshack](https://github.com/loxsmoke/memshack)

It keeps the current MemPalace-compatible defaults:

- config under `~/.mempalace`
- palace data under `~/.mempalace/palace`
- knowledge graph at `~/.mempalace/knowledge_graph.sqlite3`
- collection names `mempalace_drawers` and `mempalace_compressed`

## Install

### Prerequisites

- .NET 10 SDK or later
- a compatible .NET 10 runtime for running the installed tool

### Install The Tool From A Published Package

Package ID: `LoxSmoke.Mems`

Command name: `mems`

Linux or macOS or Windows global install:

```bash
dotnet tool install --global LoxSmoke.Mems
mems --help
```

### Build And Install Locally From This Repo

```bash
dotnet pack MemShack.slnx
dotnet tool install --global LoxSmoke.Mems --add-source ./src/MemShack.Cli/nuget
mems --help
```

These commands use the package version defined in `src/MemShack.Cli/MemShack.Cli.csproj`.

If `LoxSmoke.Mems` is already installed globally, replace the install step with:

```bash
dotnet tool update --global LoxSmoke.Mems --add-source ./src/MemShack.Cli/nuget
```

## Common Commands

```powershell
mems init <dir> [--yes]
mems mine <dir>
mems search <query>
mems status
mems wake-up
mems compress
mems split <dir>
```

Use `--palace <path>` when you want to point the CLI at a different palace directory.

## Migration Notes

If you are moving from the Python MemPalace implementation, start here:

- [Migration Guide](docs/migration-guide.md)
- [Tool Installation](docs/tool-installation.md)
- [Palace2Shack Validation Report](docs/validation/palace2shack-validation-report.md)
