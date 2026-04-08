# Phase 0 Fixtures

This folder contains representative fixture data captured from the current Python implementation.

Purpose:

- freeze file and payload shapes before the C# rewrite changes them
- provide stable inputs for future C# unit and integration tests
- keep examples close to the source repo semantics without requiring the Python runtime to execute

Contents:

- `config/`
  - sample global and project-local config files
- `transcripts/`
  - sample inputs for each transcript normalization path
- `drawers/`
  - sample drawer ids, texts, and metadata based on the Python test fixtures
- `kg/`
  - sample entities and triples based on the Python knowledge graph fixtures
- `mcp/`
  - sample JSON-RPC request and response envelopes
