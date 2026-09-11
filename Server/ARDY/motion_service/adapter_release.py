"""Evidence-bound preview releases; creation never activates a service or model."""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
from datetime import datetime, timezone
from pathlib import Path

from .adapter_selection import load_candidate_cpu
from .protocol import FEATURE_CONTRACT, QWEN_MODEL_SHA256

ROOT = Path(__file__).resolve().parents[3]
ACTIVE_RELATIVE = Path("Server/ARDY/runtime/active-adapter-release.json")
EVIDENCE_KEYS = {"selection", "nativeFinal", "featureFinal", "httpLive", "unityLive", "unityTrace"}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_json(data):
    def invalid(value):
        raise ValueError("Nonfinite value in release evidence: " + value)
    return json.loads(data, parse_constant=invalid)


def release_id(manifest):
    value = {key: value for key, value in manifest.items() if key != "releaseId"}
    return "ardy-preview-" + digest(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode())[:24]


def validate_evidence(evidence, checkpoint_sha, selection_sha, baseline_sha):
    """Validate complete declared runs and exact identity binding, not naturalness."""
    if set(evidence) != EVIDENCE_KEYS:
        raise ValueError("Release requires selection, final native/features, live HTTP, Unity and its completed trace")
    selection, native, feature, http, unity, trace = [evidence[k] for k in
        ("selection", "nativeFinal", "featureFinal", "httpLive", "unityLive", "unityTrace")]
    if selection.get("checkpoint_sha256") != checkpoint_sha:
        raise ValueError("Release selection checkpoint differs")
    if (feature.get("status") != "complete" or feature.get("explicit_final_test") is not True
            or feature.get("used_for_selection") is not False
            or feature.get("plan", {}).get("lockedCandidateSha256") != checkpoint_sha
            or feature["plan"].get("selectionSha256") != selection_sha):
        raise ValueError("Feature evidence is not the completed final audit of this selection")
    records = feature.get("per_record", [])
    supported = {r["id"] for r in records if r["track"] == "supported_upper_body"}
    boundaries = {r["id"] for r in records if r["track"] == "capability_boundary"}
    if len(records) != 80 or len(supported) != 64 or len(boundaries) != 16 or supported & boundaries:
        raise ValueError("Final feature evidence does not cover all 64 supported and 16 boundary descriptions")
    plan = native.get("plan", {})
    adapters = plan.get("adapters", {})
    if (native.get("status") != "complete" or plan.get("finalTest") is not True or plan.get("split") != "test"
            or set(plan.get("recordIds", [])) != supported or len(plan["recordIds"]) != 64
            or plan.get("seeds") != [0, 1] or len(plan.get("histories", {})) != 1 or "cold" in plan["histories"]
            or len(adapters) != 2 or {a["sha256"] for a in adapters.values()} != {checkpoint_sha, baseline_sha}
            or plan.get("windows") != 3 or plan.get("continuationHistoryFrames") != 16):
        raise ValueError("Native evidence must complete the frozen supported-set teacher/old/candidate, seed 0/1, grounded-16 audit")
    expected = set(itertools.product(supported, ["teacher", *adapters], plan["histories"], [0, 1]))
    results = native.get("results", [])
    keys = [(r["recordId"], r["condition"], r["history"], r["seed"]) for r in results]
    if len(keys) != len(expected) or set(keys) != expected:
        raise ValueError("Final native audit has missing or duplicate trajectories")
    for row in results:
        windows = row.get("windows", [])
        expected_sha = None if row["condition"] == "teacher" else adapters[row["condition"]]["sha256"]
        if ([w["window"] for w in windows] != [0, 1, 2] or any(w["historyFrames"] != 16 for w in windows)
                or row.get("conditionProvenance", {}).get("adapterSha256") != expected_sha):
            raise ValueError("Final native trajectory windows or adapter provenance differ")
    if (http.get("passed") is not True or http.get("mode") != "locked-candidate-live-http"
            or http.get("checkpointSha256") != checkpoint_sha or http.get("selectionSha256") != selection_sha
            or http.get("initialHealth", {}).get("backend", {}).get("adapterSha256") != checkpoint_sha
            or not http.get("cases") or len(http["cases"]) != len(http.get("recordIds", []))
            or any(c.get("generatedFrames") != 120 or len(c.get("responses", [])) != 3 for c in http["cases"])):
        raise ValueError("Live HTTP evidence does not establish this locked candidate's complete windows")
    if (unity.get("status") != "passed" or unity.get("receivedChunks") != 3 or unity.get("timelineFrames") != 120
            or unity.get("diagnosticResponses") != 3 or trace.get("finishReason") != "completed"):
        raise ValueError("Unity evidence must show a completed real three-window playback")
    requests = [json.loads(entry["json"]) for entry in trace.get("entries", []) if entry.get("kind") == "request"]
    responses = [entry for entry in trace.get("entries", []) if entry.get("kind") == "response"]
    if len(requests) != 3 or len(responses) != 3:
        raise ValueError("Unity completed trace needs exactly three real request/response pairs")
    for index, (request, entry) in enumerate(zip(requests, responses)):
        response = json.loads(entry["json"])
        provenance = response.get("provenance", {})
        expected_history = request.get("initialHistory", {}).get("conditioningFrames", 16) if index == 0 else 16
        if (entry.get("httpStatus") != 200 or request.get("chunkIndex") != index
                or any(response.get(k) != request.get(k) for k in ("characterId", "turnId", "revision", "requestId", "chunkIndex"))
                or any(request.get(k) != trace.get(k) for k in ("characterId", "turnId", "revision", "description", "seed"))
                or response.get("newFrames") != 40 or response.get("startFrame") != index*40
                or response.get("final") is not (index == 2) or response.get("historyFrames") != expected_history
                or response.get("clip", {}).get("source", {}).get("adapterSha256") != checkpoint_sha
                or len(response["clip"].get("frames", [])) != 40
                or provenance.get("featureContract") != FEATURE_CONTRACT or provenance.get("modelSha256") != QWEN_MODEL_SHA256
                or provenance.get("featureSource") != ("live-qwen" if index == 0 else "live-condition-reused")
                or provenance.get("featureRequestId") != requests[0]["requestId"]):
            raise ValueError("Unity trace response does not bind the actual candidate, Qwen contract and motion lifecycle")


def load_release_cpu(root, manifest_path):
    manifest_bytes = Path(manifest_path).read_bytes()
    manifest = read_json(manifest_bytes)
    if (manifest.get("schema") != 1 or manifest.get("mode") != "verified-preview"
            or manifest.get("naturalnessAccepted") is not False or manifest.get("defaultInitialConditioningFrames") != 16
            or manifest.get("featureContract") != FEATURE_CONTRACT or manifest.get("releaseId") != release_id(manifest)):
        raise ValueError("Invalid or changed preview release manifest")
    evidence, hashes = {}, {}
    for key, reference in manifest["evidence"].items():
        data = Path(reference["path"]).read_bytes()
        hashes[key] = digest(data)
        if hashes[key] != reference["sha256"]:
            raise ValueError("Preview release evidence changed: " + key)
        evidence[key] = read_json(data)
    if Path(evidence["unityLive"]["diagnosticTracePath"]).resolve() != Path(manifest["evidence"]["unityTrace"]["sourcePath"]).resolve():
        raise ValueError("Archived Unity trace was not the trace named by its live report")
    model, payload, info = load_candidate_cpu(root, manifest["evidence"]["selection"]["path"])
    if info["adapterSha256"] != manifest["checkpointSha256"] or info["adapterSelectionSha256"] != hashes["selection"]:
        raise ValueError("Preview release candidate changed")
    validate_evidence(evidence, info["adapterSha256"], hashes["selection"], payload["baseline_checkpoint_sha256"])
    info.update(adapterMode="verified-preview", candidate=False, previewReleaseVerified=True,
                releaseId=manifest["releaseId"], releaseManifestSha256=digest(manifest_bytes), releaseEvidenceSha256=hashes,
                naturalnessAccepted=False, defaultInitialConditioningFrames=16,
                candidateUsage="Evidence-verified motion preview; no guarantee of arbitrary-action success or naturalness")
    return model, payload, info


def create_release(root, selection, native_final, feature_final, http_live, unity_live, output):
    root, output = Path(root).resolve(), Path(output).resolve()
    if output == root / ACTIVE_RELATIVE:
        raise ValueError("Creation never writes the active release; choose a reviewable inactive manifest")
    directory = output.parent / (output.stem + "-evidence")
    if output.exists() or directory.exists():
        raise FileExistsError("Release manifest and evidence archive must be new")
    paths = {"selection": Path(selection).resolve(), "nativeFinal": Path(native_final).resolve(),
             "featureFinal": Path(feature_final).resolve(), "httpLive": Path(http_live).resolve(), "unityLive": Path(unity_live).resolve()}
    data = {key: path.read_bytes() for key, path in paths.items()}
    documents = {key: read_json(value) for key, value in data.items()}
    paths["unityTrace"] = Path(documents["unityLive"]["diagnosticTracePath"]).resolve()
    data["unityTrace"] = paths["unityTrace"].read_bytes()
    documents["unityTrace"] = read_json(data["unityTrace"])
    _, payload, info = load_candidate_cpu(root, paths["selection"])
    validate_evidence(documents, info["adapterSha256"], digest(data["selection"]), payload["baseline_checkpoint_sha256"])
    directory.mkdir(parents=True)
    references = {}
    for key, value in data.items():
        destination = paths[key] if key == "selection" else directory / (key + ".json")
        if key != "selection":
            destination.write_bytes(value)  # Archive the bounded Unity trace before its eight-file ring rotates.
        references[key] = {"path": str(destination), "sha256": digest(value), "sourcePath": str(paths[key])}
    manifest = {"schema": 1, "mode": "verified-preview", "createdUtc": datetime.now(timezone.utc).isoformat(),
                "checkpointSha256": info["adapterSha256"], "featureContract": FEATURE_CONTRACT,
                "defaultInitialConditioningFrames": 16, "naturalnessAccepted": False, "evidence": references,
                "scope": "Completed final feature/native audits and real HTTP/Unity integration. No universal action-success or naturalness approval; original checkpoint remains available."}
    manifest["releaseId"] = release_id(manifest)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2, allow_nan=False)
    load_release_cpu(root, output)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("create")
    for name in ("selection", "native-final", "feature-final", "http-live", "unity-live", "output"):
        create.add_argument("--" + name, type=Path, required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "create":
        value = create_release(ROOT, args.selection, args.native_final, args.feature_final, args.http_live, args.unity_live, args.output)
        print(json.dumps({"releaseId": value["releaseId"], "manifest": str(args.output), "activated": False}))
    else:
        _, _, info = load_release_cpu(ROOT, args.manifest)
        print(json.dumps(info))


if __name__ == "__main__":
    main()
