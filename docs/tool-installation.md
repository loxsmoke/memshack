# MemShack .NET Tool Installation

## Package And Command

- NuGet package: `LoxSmoke.Mems`
- installed command: `mems`
- project home: [https://github.com/loxsmoke/memshack](https://github.com/loxsmoke/memshack)

## Prerequisites

- .NET 10 SDK or later is required to install, update, and uninstall the tool with `dotnet tool ...`
- running the installed tool requires a compatible .NET 10 runtime; the .NET 10 SDK already includes that runtime
- the packaged CLI targets `net10.0`
- the package includes the CLI assemblies plus the SQLite runtime assets needed by the current storage layer
- the tool reads and writes MemPalace-compatible data under `~/.mempalace` unless you override paths

## First-Run Chroma Behavior

The package does not bundle a Chroma executable.

Instead, MemShack uses this default flow:

1. `mems mine`, `mems search`, `mems wake-up`, and other semantic commands try
   to use managed local Chroma.
2. If no configured or system Chroma binary is available yet, MemShack
   auto-downloads the official Chroma CLI into `~/.mempalace/chroma/bin/<rid>/`.
3. MemShack starts that Chroma process automatically for the current palace.

You do not need any extra Chroma configuration for the default local setup.

If you want a different setup, configure one of:

- `chroma_url`
  Use an external Chroma server.
- `chroma_binary_path`
  Use a specific local Chroma executable.
- `vector_store_backend: compatibility`
  Force the legacy JSON compatibility backend instead of Chroma.

## Global Install

```powershell
dotnet tool install --global LoxSmoke.Mems
mems --help
```

`mems` is the generated tool command for global installs. If the shell cannot find it, make sure the .NET tools directory is on `PATH`.

- Windows default: `%USERPROFILE%\\.dotnet\\tools`
- Linux/macOS default: `$HOME/.dotnet/tools`

## Local Manifest Install

```powershell
dotnet new tool-manifest
dotnet tool install --local LoxSmoke.Mems
dotnet tool run mems -- --help
```

Local tools are repo-scoped. They are invoked as `dotnet tool run mems` or `dotnet mems`, not as a bare `mems` command.

## Tool-Path Install

```powershell
dotnet tool install LoxSmoke.Mems --tool-path .\.tools
.\.tools\mems.exe --help
```

For `--tool-path` installs, the generated command is still `mems`, but you either run it by path or add that tool-path directory to `PATH`.

## Common Commands

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

`mems shutdowndb` is a MemShack-specific helper that stops the managed local
Chroma process for the selected palace.

## Path Notes

- config directory: `~/.mempalace`
- default palace path: `~/.mempalace/palace`
- default knowledge graph path: `~/.mempalace/knowledge_graph.sqlite3`

Use `--palace <path>` when you want to point the CLI at a different palace directory.

If you switch palaces frequently and want to stop the managed database for one
of them explicitly, use:

```powershell
mems --palace C:\path\to\palace shutdowndb
```

## Contributor Packaging Commands

- pack the local tool package to the project-local package folder: `dotnet pack .\src\MemShack.Cli\MemShack.Cli.csproj -c Release`
  The package is written to `src\MemShack.Cli\nuget` by default.
- run the local install smoke checks: `powershell -ExecutionPolicy Bypass -File .\tools\test-tool-install.ps1`
- run the local install smoke checks from bash: `./tools/test-tool-install.sh`
