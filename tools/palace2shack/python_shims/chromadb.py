from __future__ import annotations

import json
import re
from pathlib import Path


_TOKEN_PATTERN = re.compile(r"\b[a-z0-9_]+\b", re.IGNORECASE)


class PersistentClient:
    def __init__(self, path: str):
        self._root = Path(path).expanduser().resolve()
        self._collections_path = self._root / "collections"
        self._collections_path.mkdir(parents=True, exist_ok=True)

    def get_collection(self, name: str):
        path = self._collection_path(name)
        if not path.is_file():
            raise ValueError(f"Collection not found: {name}")
        return _Collection(path)

    def create_collection(self, name: str):
        path = self._collection_path(name)
        if path.exists():
            raise ValueError(f"Collection already exists: {name}")
        _save_collection(path, {"name": name, "items": []})
        return _Collection(path)

    def get_or_create_collection(self, name: str):
        path = self._collection_path(name)
        if not path.exists():
            _save_collection(path, {"name": name, "items": []})
        return _Collection(path)

    def _collection_path(self, name: str) -> Path:
        return self._collections_path / f"{name}.json"


class _Collection:
    def __init__(self, path: Path):
        self._path = path

    def add(self, ids=None, documents=None, metadatas=None):
        ids = ids or []
        documents = documents or []
        metadatas = metadatas or []

        if not (len(ids) == len(documents) == len(metadatas)):
            raise ValueError("ids, documents, and metadatas must have matching lengths")

        payload = _load_collection(self._path)
        existing = {item["id"] for item in payload["items"]}

        for drawer_id, document, metadata in zip(ids, documents, metadatas):
            if drawer_id in existing:
                raise ValueError(f"already exists: {drawer_id}")

            payload["items"].append(
                {
                    "id": drawer_id,
                    "document": document,
                    "metadata": metadata or {},
                }
            )
            existing.add(drawer_id)

        _save_collection(self._path, payload)

    def get(self, ids=None, where=None, limit=None, offset=0, include=None):
        include = include or []
        items = _filter_items(_load_collection(self._path)["items"], ids=ids, where=where)
        start = max(offset or 0, 0)
        end = None if limit is None else start + limit
        items = items[start:end]

        result = {"ids": [item["id"] for item in items]}
        if not include or "documents" in include:
            result["documents"] = [item["document"] for item in items]
        if not include or "metadatas" in include:
            result["metadatas"] = [item["metadata"] for item in items]
        return result

    def query(self, query_texts=None, n_results=5, where=None, include=None):
        include = include or []
        query_texts = query_texts or []
        items = _filter_items(_load_collection(self._path)["items"], where=where)

        result = {
            "ids": [],
            "documents": [],
            "metadatas": [],
            "distances": [],
        }

        for query_text in query_texts:
            query_tokens = _tokenize(query_text)
            ranked = []
            for item in items:
                similarity = _calculate_similarity(query_tokens, _tokenize(item["document"]))
                if similarity > 0 or not query_tokens:
                    ranked.append((similarity, item["metadata"].get("source_file", ""), item))

            ranked.sort(key=lambda entry: (-entry[0], entry[1]))
            ranked = ranked[: n_results or 0]

            result["ids"].append([entry[2]["id"] for entry in ranked])
            result["documents"].append([entry[2]["document"] for entry in ranked])
            result["metadatas"].append([entry[2]["metadata"] for entry in ranked])
            result["distances"].append([round(1 - entry[0], 10) for entry in ranked])

        if include and "documents" not in include:
            result.pop("documents", None)
        if include and "metadatas" not in include:
            result.pop("metadatas", None)
        if include and "distances" not in include:
            result.pop("distances", None)
        return result

    def delete(self, ids=None):
        ids = set(ids or [])
        payload = _load_collection(self._path)
        payload["items"] = [item for item in payload["items"] if item["id"] not in ids]
        _save_collection(self._path, payload)

    def count(self):
        return len(_load_collection(self._path)["items"])


def _load_collection(path: Path):
    if not path.is_file():
        return {"name": path.stem, "items": []}
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def _save_collection(path: Path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2, sort_keys=True)


def _filter_items(items, ids=None, where=None):
    filtered = list(items)
    if ids is not None:
        id_set = set(ids)
        filtered = [item for item in filtered if item["id"] in id_set]
    if where:
        filtered = [item for item in filtered if _matches_where(item["metadata"], where)]
    return filtered


def _matches_where(metadata, where):
    if not where:
        return True
    if "$and" in where:
        return all(_matches_where(metadata, clause) for clause in where["$and"])
    return all(metadata.get(key) == value for key, value in where.items())


def _tokenize(text: str):
    return {match.group(0).lower() for match in _TOKEN_PATTERN.finditer(text or "")}


def _calculate_similarity(query_tokens, text_tokens):
    if not query_tokens or not text_tokens:
        return 0.0
    overlap = sum(1 for token in query_tokens if token in text_tokens)
    return overlap / float(len(query_tokens))
