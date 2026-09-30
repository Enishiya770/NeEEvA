"""Export the live Spark Qwen features needed by the ARDY adapter trainer.

This is a one-time preparation step for a Spark that has the existing
``/neeeva/motion-features`` worker.  It deliberately records the response
contract and model SHA next to the feature matrix so a later service cannot
silently consume features from another model.
"""
from __future__ import annotations

import argparse
import json
import urllib.request
from pathlib import Path

import numpy as np


CONTRACT = "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0"
MODEL_SHA256 = "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4"
TEMPLATE = "Motion description: {text}\nRepresentation:"


def post(url: str, payload: dict, timeout: float) -> dict:
    request = urllib.request.Request(
        url.rstrip("/") + "/neeeva/motion-features",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prompts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--feature-url", default="http://127.0.0.1:8890")
    parser.add_argument("--timeout", type=float, default=30.0)
    args = parser.parse_args()
    rows = [json.loads(line) for line in args.prompts.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    embeddings = []
    for index, row in enumerate(rows, 1):
        value = post(args.feature_url, {
            "text": row["text"],
            "request_id": "ardy-prepare-" + row["id"],
            "feature_contract": CONTRACT,
        }, args.timeout)
        checks = (
            value.get("dimension") == 2048,
            value.get("protocol") == "qwen-motion-raw-last-v1",
            value.get("feature_source") == "live-qwen",
            value.get("request_id") == "ardy-prepare-" + row["id"],
            value.get("text") == row["text"],
            value.get("feature_contract") == CONTRACT,
            value.get("pooling") == "last_input_token_raw",
            value.get("template") == TEMPLATE,
            value.get("model_sha256", "").lower() == MODEL_SHA256,
            value.get("shared_model") is False,
        )
        if not all(checks):
            raise RuntimeError(f"Feature contract mismatch at row {index}: {value}")
        vector = np.asarray(value["embedding"], dtype=np.float32)
        if vector.shape != (2048,) or not np.isfinite(vector).all() or np.linalg.norm(vector) <= 1e-8:
            raise RuntimeError(f"Invalid embedding at row {index}")
        embeddings.append(vector)
        if index == 1 or index % 50 == 0 or index == len(rows):
            print(json.dumps({"encoded": index, "total": len(rows)}), flush=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    metadata = {
        "schema": 1,
        "kind": "qwen",
        "feature_contract": CONTRACT,
        "feature_source": "live-qwen",
        "protocol": "qwen-motion-raw-last-v1",
        "pooling": "last_input_token_raw",
        "template": TEMPLATE,
        "model_sha256": MODEL_SHA256,
        "shared_model": False,
    }
    records_hash = __import__("hashlib").sha256(
        json.dumps(rows, ensure_ascii=False, sort_keys=True).encode("utf-8")
    ).hexdigest()
    metadata["records_sha256"] = records_hash
    with args.output.open("wb") as stream:
        np.savez(stream, features=np.asarray(embeddings, dtype=np.float32),
                 ids=np.asarray([row["id"] for row in rows]),
                 metadata=np.asarray(json.dumps(metadata, sort_keys=True)))
    print(json.dumps({"output": str(args.output), "rows": len(rows), "records_sha256": records_hash}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
