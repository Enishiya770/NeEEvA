"""Merge validated old/new feature components without rewriting their contracts."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from .data import read_bundle, read_records, records_hash, write_bundle


HERE = Path(__file__).resolve().parent


def file_sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def teacher_compatibility(old_meta, new_meta, old_rows):
    old, new = old_meta["provenance"], new_meta["provenance"]
    for metadata in [old_meta, new_meta]:
        digest = hashlib.sha256(json.dumps(metadata["provenance"], sort_keys=True).encode()).hexdigest()
        if digest != metadata["feature_contract"]:
            raise ValueError("Teacher component contract does not hash its actual provenance")
    if new.get("cached_teacher_reference_contract") != old_meta["feature_contract"]:
        raise ValueError("New teacher is not verified against this old teacher contract")
    if new.get("cached_teacher_reference_records_sha256") != old_meta["records_sha256"]:
        raise ValueError("New teacher's cached verification descriptions do not match")
    for key in ["repos", "ardy_revision", "effective_model_name", "max_length", "pooling_mode", "output_scale", "packages", "preset"]:
        if old.get(key) != new.get(key):
            raise ValueError(f"Teacher semantic/runtime provenance differs: {key}")
    if new.get("batch_size") != 1 or new.get("construction") != "unchanged official LLM2Vec.from_pretrained factory":
        raise ValueError("New teacher must use the validated official batch-size-1 construction")
    expected = {old_rows[i]["id"]: old_rows[i]["text"] for i in [4, 53]}
    observed = {row["id"]: row for row in new.get("cached_teacher_comparisons", [])}
    for identity, text in expected.items():
        proof = observed.get(identity, {})
        if (proof.get("text") != text or proof.get("relative_l2") != 0
                or proof.get("max_abs_error") != 0 or not np.isfinite(proof.get("cosine", float("nan")))
                or proof.get("cosine", 0) < .999999):
            raise ValueError(f"Missing exact current-model/cache control evidence for {identity}")
    return {"method": "Two current-model control encodings exactly equal the immutable old cached vectors",
            "old_contract": old_meta["feature_contract"], "new_contract": new_meta["feature_contract"],
            "controls": [observed[identity] for identity in expected],
            "scope": "Numerical teacher feature compatibility; not proof of downstream motion quality"}


def merge(args):
    paths = {name: Path(getattr(args, name)).resolve() for name in [
        "old_dataset", "old_qwen", "old_teacher", "old_checkpoint", "new_dataset", "new_qwen", "new_teacher"]}
    hashes = {name: file_sha256(path) for name, path in paths.items()}
    old_rows, new_rows = read_records(paths["old_dataset"]), read_records(paths["new_dataset"])
    if len(old_rows) != 2000:
        raise ValueError("The retained original component must contain its 2000 original rows")
    oq, oqm = read_bundle(paths["old_qwen"], old_rows, "qwen")
    ot, otm = read_bundle(paths["old_teacher"], old_rows, "teacher")
    nq, nqm = read_bundle(paths["new_qwen"], new_rows, "qwen")
    nt, ntm = read_bundle(paths["new_teacher"], new_rows, "teacher")
    if oqm["feature_contract"] != nqm["feature_contract"]:
        raise ValueError("Old and new Qwen features must have exactly the same deployed feature contract")
    compatibility = teacher_compatibility(otm, ntm, old_rows)
    import torch
    baseline = torch.load(paths["old_checkpoint"], map_location="cpu", weights_only=True)
    if (baseline.get("architecture") != "mlp" or baseline.get("schema") != 1
            or baseline.get("qwen_contract") != oqm["feature_contract"]
            or baseline.get("teacher_contract") != otm["feature_contract"]
            or baseline.get("dataset_sha256") != records_hash(old_rows)):
        raise ValueError("The retained MLP checkpoint is not bound to these original components")
    old_text_splits = {}
    for row in old_rows:
        old_text_splits.setdefault(row["text"].strip(), set()).add(row["split"])
    for row in new_rows:
        previous = old_text_splits.get(row["text"].strip())
        if previous and previous != {row["split"]}:
            raise ValueError("An exact old description would cross a train/validation/test boundary")
    output = args.output_directory.resolve()
    outputs = {name: output / filename for name, filename in {
        "dataset": "dataset.jsonl", "qwen": "qwen.npz", "teacher": "teacher.npz", "manifest": "merge.json"}.items()}
    if any(path.exists() for path in outputs.values()):
        raise FileExistsError("Merged artifacts require new output paths")
    output.mkdir(parents=True, exist_ok=True)
    # Preserve the entire old JSONL byte prefix. Only the new file's optional BOM
    # is removed so that it cannot appear in the middle of a JSONL document.
    old_bytes = paths["old_dataset"].read_bytes()
    separator = b"" if old_bytes.endswith(b"\n") else b"\n"
    joined = old_bytes + separator + paths["new_dataset"].read_text(encoding="utf-8-sig").encode("utf-8")
    outputs["dataset"].write_bytes(joined)
    try:
        rows = read_records(outputs["dataset"])
        if rows != old_rows + new_rows:
            raise ValueError("Merged manifest changed input record contents or order")
    except Exception:
        # Keep a diagnostic manifest, but do not publish any feature bundles.
        raise
    ranges = {"old": [0, len(old_rows)], "new": [len(old_rows), len(rows)]}
    components = {"old": {"records_sha256": records_hash(old_rows), "qwen": oqm, "teacher": otm},
                  "new": {"records_sha256": records_hash(new_rows), "qwen": nqm, "teacher": ntm}}
    teacher_descriptor = {"schema": 1, "kind": "composite-teacher", "components": components,
                          "compatibility": compatibility, "ranges": ranges}
    composite_contract = hashlib.sha256(json.dumps(teacher_descriptor, sort_keys=True).encode()).hexdigest()
    write_bundle(outputs["qwen"], np.concatenate([oq, nq]), rows,
                 {"kind": "qwen", "feature_contract": oqm["feature_contract"],
                  "provenance": {"kind": "composite", "components": {"old": oqm, "new": nqm}, "ranges": ranges}})
    write_bundle(outputs["teacher"], np.concatenate([ot, nt]), rows,
                 {"kind": "teacher", "feature_contract": composite_contract, "provenance": teacher_descriptor})
    if any(file_sha256(path) != hashes[name] for name, path in paths.items()):
        raise RuntimeError("A source artifact changed during merge; merged artifacts must not be used")
    report = {"schema": 1, "passed": True, "ranges": ranges, "records_sha256": records_hash(rows),
              "source_paths": {key: str(value) for key, value in paths.items()}, "source_sha256": hashes,
              "outputs": {key: str(value) for key, value in outputs.items()},
              "output_sha256": {key: file_sha256(value) for key, value in outputs.items() if key != "manifest"},
              "old_jsonl_byte_prefix_preserved": joined.startswith(old_bytes), "source_bytes_unchanged": True,
              "qwen_contract": oqm["feature_contract"], "teacher_contract": composite_contract,
              "teacher_compatibility": compatibility, "components": components}
    outputs["manifest"].write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name, default in {"old-dataset": "prompts.jsonl", "old-qwen": "qwen-2000.npz",
                          "old-teacher": "teacher-2000.npz", "old-checkpoint": "mlp-seed1.pt"}.items():
        parser.add_argument("--" + name, type=Path, default=HERE / "runtime" / default)
    for name in ["new-dataset", "new-qwen", "new-teacher", "output-directory"]:
        parser.add_argument("--" + name, type=Path, required=True)
    result = merge(parser.parse_args())
    print(json.dumps({"passed": True, "ranges": result["ranges"], "manifest": result["outputs"]["manifest"]}))


if __name__ == "__main__":
    main()
