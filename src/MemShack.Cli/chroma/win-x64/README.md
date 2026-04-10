This directory is reserved for the bundled Windows x64 Chroma sidecar.

Expected packaged binary path:

`chroma/win-x64/chroma.exe`

When a real Chroma binary is placed here before packing the .NET tool, `mems`
can discover it under `AppContext.BaseDirectory` and start it automatically on
first use.
