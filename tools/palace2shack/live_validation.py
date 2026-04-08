from __future__ import annotations

import argparse
import contextlib
import importlib
import io
import json
import os
import sys
import tempfile
from pathlib import Path


def main() -> int:
    args = parse_args()
    repo_root = find_repo_root()
    shims_path = repo_root / "tools" / "palace2shack" / "python_shims"
    mempalace_root = Path(args.mempalace_root).expanduser().resolve()

    sys.path.insert(0, str(shims_path))
    sys.path.insert(0, str(mempalace_root))

    snapshot = build_snapshot(repo_root)

    if args.output:
        output_path = Path(args.output).expanduser().resolve()
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(json.dumps(snapshot, indent=2, sort_keys=True), encoding="utf-8")

    if args.compare:
        compare_path = Path(args.compare).expanduser().resolve()
        other = json.loads(compare_path.read_text(encoding="utf-8"))
        comparison = build_comparison(snapshot, other)

        if args.report:
            report_path = Path(args.report).expanduser().resolve()
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps(comparison, indent=2, sort_keys=True), encoding="utf-8")

        print(json.dumps(comparison, indent=2, sort_keys=True))
        return 0 if comparison["match"] else 1

    print(json.dumps(snapshot, indent=2, sort_keys=True))
    return 0


def parse_args():
    parser = argparse.ArgumentParser(description="Run the live MemPalace-vs-MemShack Palace2Shack validation.")
    parser.add_argument("--mempalace-root", default=r"C:\dev\mempalace")
    parser.add_argument("--output")
    parser.add_argument("--compare")
    parser.add_argument("--report")
    return parser.parse_args()


def build_snapshot(repo_root: Path):
    transcripts_dir = repo_root / "fixtures" / "phase0" / "transcripts"
    phase0_root = repo_root / "fixtures" / "phase0"
    palace2shack_root = repo_root / "fixtures" / "palace2shack"

    transcript_files = [
        "plain-text-transcript.txt",
        "claude-code-session.jsonl",
        "codex-session.jsonl",
        "slack-export.json",
        "chatgpt-conversation.json",
        "claude-flat-messages.json",
    ]

    normalize_module = importlib.import_module("mempalace.normalize")
    convo_module = importlib.import_module("mempalace.convo_miner")
    miner_module = importlib.import_module("mempalace.miner")
    kg_module = importlib.import_module("mempalace.knowledge_graph")
    search_module = importlib.import_module("mempalace.searcher")

    snapshot = {
        "transcripts": {},
        "conversation_chunks": [],
        "project_scan": [],
        "project_mining": {},
        "knowledge_graph": {},
        "search": {},
        "mcp_status": {},
    }

    for file_name in transcript_files:
        path = transcripts_dir / file_name
        snapshot["transcripts"][file_name] = normalize_text(normalize_module.normalize(str(path)))

    transcript_content = (transcripts_dir / "plain-text-transcript.txt").read_text(encoding="utf-8")
    snapshot["conversation_chunks"] = [
        normalize_text(chunk["content"])
        for chunk in convo_module.chunk_exchanges(transcript_content)
    ]

    corpus_path = palace2shack_root / "project-corpus"
    snapshot["project_scan"] = sorted(
        str(path.relative_to(corpus_path)).replace("\\", "/")
        for path in miner_module.scan_project(str(corpus_path), respect_gitignore=True)
    )

    with tempfile.TemporaryDirectory(prefix="mempalace-palace2shack-mine-") as palace_dir:
        with contextlib.redirect_stdout(io.StringIO()):
            miner_module.mine(
                str(corpus_path),
                palace_path=palace_dir,
                agent="palace2shack-live",
            )
        collection = miner_module.get_collection(palace_dir)
        records = collection.get(include=["documents", "metadatas"])
        drawer_snapshots = []
        for drawer_id, metadata in sorted(
            zip(records["ids"], records["metadatas"]),
            key=lambda item: item[0],
        ):
            drawer_snapshots.append(
                "|".join(
                    [
                        drawer_id,
                        metadata.get("wing", ""),
                        metadata.get("room", ""),
                        str(Path(metadata.get("source_file", "")).resolve().relative_to(corpus_path)).replace("\\", "/"),
                        str(metadata.get("chunk_index", "")),
                        metadata.get("added_by", ""),
                    ]
                )
            )
        snapshot["project_mining"] = {
            "drawers_filed": len(drawer_snapshots),
            "drawer_snapshots": drawer_snapshots,
        }

    with tempfile.TemporaryDirectory(prefix="mempalace-palace2shack-kg-") as temp_dir:
        db_path = Path(temp_dir) / "knowledge_graph.sqlite3"
        graph = kg_module.KnowledgeGraph(str(db_path))
        fixture = read_json(phase0_root / "kg" / "seeded-kg.json")

        for entity in fixture["entities"]:
            graph.add_entity(entity["name"], entity["type"])

        for triple in fixture["triples"]:
            graph.add_triple(
                triple["subject"],
                triple["predicate"],
                triple["object"],
                valid_from=triple.get("valid_from"),
                valid_to=triple.get("valid_to"),
            )

        snapshot["knowledge_graph"] = {
            "alice_outgoing": sorted(format_fact(fact) for fact in graph.query_entity("Alice")),
            "works_at_2024": sorted(format_fact(fact) for fact in graph.query_relationship("works_at", as_of="2024-06-01")),
            "works_at_2025": sorted(format_fact(fact) for fact in graph.query_relationship("works_at", as_of="2025-06-01")),
            "max_timeline": sorted(format_fact(fact) for fact in graph.timeline("Max")),
            "stats": graph.stats(),
        }

    with tempfile.TemporaryDirectory(prefix="mempalace-palace2shack-search-") as palace_dir:
        seed_drawer_collection(phase0_root / "drawers" / "seeded-drawers.json", palace_dir)
        snapshot["search"] = {
            "auth": normalize_hits(search_module.search_memories("JWT authentication", palace_path=palace_dir)),
            "notes": normalize_hits(search_module.search_memories("ChromaDB", palace_path=palace_dir, wing="notes")),
            "backend": normalize_hits(search_module.search_memories("authentication database", palace_path=palace_dir, room="backend", n_results=5)),
        }

    with tempfile.TemporaryDirectory(prefix="mempalace-palace2shack-mcp-") as palace_dir:
        seed_drawer_collection(phase0_root / "drawers" / "seeded-drawers.json", palace_dir)
        previous_palace_path = os.environ.get("MEMPALACE_PALACE_PATH")
        os.environ["MEMPALACE_PALACE_PATH"] = palace_dir
        try:
            sys.modules.pop("mempalace.mcp_server", None)
            mcp_module = importlib.import_module("mempalace.mcp_server")
            request = read_json(phase0_root / "mcp" / "tools-call-status-request.json")
            response = mcp_module.handle_request(request)
            payload = json.loads(response["result"]["content"][0]["text"])
            snapshot["mcp_status"] = {
                "total_drawers": payload["total_drawers"],
                "wings": payload["wings"],
                "rooms": payload["rooms"],
                "has_protocol": bool(payload.get("protocol")),
                "has_aaak_dialect": bool(payload.get("aaak_dialect")),
            }
        finally:
            if previous_palace_path is None:
                os.environ.pop("MEMPALACE_PALACE_PATH", None)
            else:
                os.environ["MEMPALACE_PALACE_PATH"] = previous_palace_path

    return snapshot


def seed_drawer_collection(drawer_fixture: Path, palace_dir: str):
    import chromadb

    fixture = read_json(drawer_fixture)
    client = chromadb.PersistentClient(path=palace_dir)
    collection = client.get_or_create_collection("mempalace_drawers")
    collection.add(
        ids=[item["id"] for item in fixture],
        documents=[item["text"] for item in fixture],
        metadatas=[item["metadata"] for item in fixture],
    )


def normalize_hits(result):
    return [
        {
            "wing": hit["wing"],
            "room": hit["room"],
            "source_file": hit["source_file"],
            "similarity": round(hit["similarity"], 3),
            "text": hit["text"],
        }
        for hit in result["results"]
    ]


def format_fact(fact):
    return "|".join(
        [
            fact.get("subject", ""),
            fact.get("predicate", ""),
            fact.get("object", ""),
            fact.get("valid_from") or "",
            fact.get("valid_to") or "",
        ]
    )


def build_comparison(python_snapshot, csharp_snapshot):
    sections = []
    for section_name in [
        "transcripts",
        "conversation_chunks",
        "project_scan",
        "project_mining",
        "knowledge_graph",
        "search",
        "mcp_status",
    ]:
        sections.append(
            {
                "name": section_name,
                "match": python_snapshot.get(section_name) == csharp_snapshot.get(section_name),
            }
        )

    return {
        "python_interpreter": sys.executable,
        "python_version": sys.version.split()[0],
        "local_dependency_shims": ["chromadb", "yaml"],
        "match": all(section["match"] for section in sections),
        "sections": sections,
    }


def find_repo_root():
    current = Path(__file__).resolve().parent
    while current != current.parent:
        if (current / "fixtures").is_dir() and (current / "src").is_dir() and (current / "MemShack.slnx").is_file():
            return current
        current = current.parent
    raise FileNotFoundError("Could not locate the MemShack repo root.")


def normalize_text(value: str):
    return value.replace("\r\n", "\n").strip()


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


if __name__ == "__main__":
    raise SystemExit(main())
