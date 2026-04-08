# Palace2Shack Fixtures

This folder contains deterministic corpora used by the cutover validation suite.

Purpose:

- provide a stable project corpus for scan, room, mining, and drawer-id checks
- keep Palace2Shack validation independent from ad hoc temp-file setups
- support repeatable cutover verification even when the Python runtime is unavailable

Contents:

- `project-corpus/`
  - small repo-shaped fixture with config, source files, docs, notes, and gitignored paths
