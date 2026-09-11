"""Validated, non-pickle feature bundles shared by export and training."""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

import numpy as np


def read_records(path):
    rows = [json.loads(line) for line in Path(path).read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    ids = [row["id"] for row in rows]
    if not rows or len(ids) != len(set(ids)):
        raise ValueError("Dataset must have nonempty, unique IDs")
    groups = {}
    for row in rows:
        if not isinstance(row['id'],str) or not re.fullmatch(r'[A-Za-z0-9_.-]+',row['id']):
            raise ValueError('IDs must be safe filenames using letters, digits, dots, underscores or hyphens')
        if row["split"] not in {"train", "val", "test"} or not row["text"].strip():
            raise ValueError("Invalid split or empty text")
        group = row["semantic_group"]
        if group in groups and groups[group] != row["split"]:
            raise ValueError(f"Semantic group leaked across splits: {group}")
        groups[group] = row["split"]
    return rows


def records_hash(rows):
    return hashlib.sha256(json.dumps(rows, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


def write_bundle(path, features, rows, metadata):
    features = np.asarray(features, dtype=np.float32)
    expected = 2048 if metadata["kind"] == "qwen" else 4096
    if metadata["kind"] not in {"qwen", "teacher"}:
        raise ValueError("Unknown feature kind")
    if features.shape != (len(rows), expected) or not np.isfinite(features).all():
        raise ValueError(f"Expected finite feature matrix [{len(rows)}, {expected}]")
    if np.any(np.linalg.norm(features, axis=1) < 1e-8):
        raise ValueError("Zero feature vector")
    meta = {**metadata, "schema": 1, "records_sha256": records_hash(rows)}
    if not meta.get("feature_contract"):
        raise ValueError("Feature provenance/contract is required")
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as stream:
        np.savez(stream, features=features, ids=np.array([r["id"] for r in rows]),
                 metadata=np.array(json.dumps(meta, sort_keys=True)))


def read_bundle(path, rows, kind):
    with np.load(path, allow_pickle=False) as data:
        features = data["features"].astype(np.float32)
        ids = data["ids"].tolist()
        meta = json.loads(str(data["metadata"]))
    dimension = 2048 if kind == "qwen" else 4096
    if (ids != [r["id"] for r in rows] or meta.get("records_sha256") != records_hash(rows)
            or meta.get("kind") != kind or meta.get("schema") != 1):
        raise ValueError("Feature IDs, text manifest, schema or kind do not match")
    if features.shape != (len(rows), dimension) or not np.isfinite(features).all():
        raise ValueError("Invalid feature values or dimension")
    if not meta.get("feature_contract") or np.any(np.linalg.norm(features, axis=1) < 1e-8):
        raise ValueError("Missing provenance or zero feature")
    return features, meta
