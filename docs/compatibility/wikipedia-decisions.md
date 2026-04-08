# Wikipedia Decisions

## Python Baseline

- State in original Python MemPalace: Wikipedia research existed only at the entity-registry layer through `EntityRegistry.research(...)` and `confirm_research(...)`.
- Not exposed in Python CLI: there was no dedicated CLI subcommand for Wikipedia lookup or confirmation.
- Not exposed in Python MCP: there was no MCP tool for Wikipedia lookup or confirmation.

## C# Port

- Status: supported programmatically in the C# entity registry; still deferred as a user-facing CLI/MCP feature.
- Current behavior: `EntityRegistry.Research(...)` performs Wikipedia summary lookup through a dedicated lookup client, persists `wiki_cache`, and supports confirmation flow through `ConfirmResearch(...)`.
- Reason: this keeps parity with the Python surface while making the feature more testable and maintainable in C#. CLI/MCP exposure would add extra UX and transport decisions that are not required for the initial cutover.
