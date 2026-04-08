# Palace2Shack Validation Report

Date: April 7, 2026

## Status

The Palace2Shack comparison suite now has both a fixture-backed parity harness in the C# test suite and a live Python-vs-C# snapshot comparison.

What was completed:

- normalized transcript comparisons against frozen fixture expectations
- scanned file list comparisons against a deterministic project corpus
- exchange chunk comparisons against the frozen plain-text transcript fixture
- project mining checks for room assignment, drawer-id stability, and metadata stability
- knowledge graph query comparisons against the seeded KG fixture
- search shape and filter comparisons against the seeded drawer fixture
- MCP status payload comparison against the frozen JSON-RPC envelope fixture
- live dual-run execution of the Python and C# implementations against the same fixture corpora
- generated live snapshot artifacts:
  - `docs/validation/palace2shack-csharp-live-snapshot.json`
  - `docs/validation/palace2shack-python-live-snapshot.json`
  - `docs/validation/palace2shack-live-comparison.json`
- cutover criteria and migration guidance

Live comparison result on April 7, 2026:

- exact match in 5 of 7 sections: project scan, project mining, knowledge graph, search, and MCP status
- mismatch in 2 of 7 sections: transcript normalization and conversation chunking

Runtime notes:

- the live Python run used the installed interpreter at `C:\Users\paulius\AppData\Local\Programs\Python\Python313\python.exe`
- this machine does not have the Python `chromadb` or `PyYAML` packages installed, so the live validation used local compatibility shims for those two dependencies while still executing the real MemPalace Python modules

## Validation Surface

Primary Palace2Shack parity coverage now lives in:

- `tests/MemShack.Tests/Parity/Palace2ShackParityTests.cs`
- `fixtures/phase0/`
- `fixtures/palace2shack/project-corpus/`

Supporting parity coverage already existed in:

- `tests/MemShack.Tests/Mcp/MemShackMcpServerTests.cs`
- `tests/MemShack.Tests/KnowledgeGraph/`
- `tests/MemShack.Tests/Search/`
- `tests/MemShack.Tests/Mining/`
- `tests/MemShack.ParityRunner/`
- `tools/palace2shack/live_validation.py`

## Intentional Differences

- UTF-8 BOM handling is now documented as an intentional difference. In the live run, stock Python MemPalace preserved the BOM in BOM-prefixed `.txt`, `.json`, and `.jsonl` fixtures, which caused transcript normalization to fall back to raw content and caused the first conversation chunk to retain a leading BOM. The C# port strips the BOM during file reads, which preserves the frozen expected normalized outputs and avoids raw JSON passthrough.
- Programmatic Wikipedia-backed entity research is now available in the C# entity registry through a dedicated lookup client, cache, and confirmation flow. It is still intentionally deferred as a CLI/MCP-facing feature, and it is not part of the deterministic Palace2Shack parity surface because the live network path is environment-dependent.
- The onboarding port is currently a programmatic/bootstrap flow, not a full interactive first-run CLI wizard.
- The vector store remains Chroma-compatible JSON-backed storage for the initial migration phase. Replacing the store is intentionally deferred until parity is fully proven.
- The MCP `mempalace_status` payload is a superset of the frozen Phase 0 response. It still preserves `total_drawers` and `wings`, but now also returns `rooms`, `palace_path`, `protocol`, and `aaak_dialect`.

## Ongoing Validation Criteria

The Python implementation can be retired when all of the following are true:

1. The full C# test suite is green, including the Palace2Shack parity tests.
2. The live dual-run check has been executed against the frozen fixture corpora and any remaining diffs are either fixed or explicitly accepted.
3. No contract regressions are found in collection names, metadata keys, config file paths, MCP tool names, or SQLite KG behavior.
4. Search, wake-up, CLI, and MCP smoke checks are approved on a real migrated palace directory.
5. The documented intentional differences are accepted by the maintainer.
6. A rollback path back to the Python CLI and MCP server remains available during the first production cutover.

## Recommendation

MemShack has completed the live dual-run step and is ready for continued shadow validation. The only live diff found on April 7, 2026 was the Python BOM-handling bug in transcript normalization and derived conversation chunking. If the maintainer accepts that BOM-stripping is the desired C# behavior, Python replacement can proceed once the remaining cutover criteria are satisfied.
