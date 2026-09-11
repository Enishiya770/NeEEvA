"""Eight bounded, real-HTTP composition diagnostics using the existing service."""
from __future__ import annotations

import copy
import json
import time
import uuid
from datetime import datetime, timezone

import numpy as np
import requests

from Server.ARDY.diagnose_composition_traces import OUT, ROOT, dump, read, sha, matrices, source_measure, target_measure, summarize
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY, load_core27_reference
from Tools.MotionAdapter.review_unity_right_candidates import VrmRestHierarchy


URL = "http://127.0.0.1:8093"
ADAPTER = "c1006ee8280f8314472acc3b19302ea7823de55dae3947e0b0fa34b6d7dfbeab"
TEXTS = {"original": "Both arms extend straight forward at shoulder level, palms facing inward, then wave side to side",
         "explicit-wrists": "Keeping both arms straight forward at shoulder height with elbows still, gently wave both hands side to side at the wrists twice."}


def health(session):
    response = session.get(URL+"/health", timeout=3)
    response.raise_for_status()
    value = response.json()
    if not value.get("ready") or value["backend"]["adapterSha256"] != ADAPTER or value["features"]["source"] != "live-qwen":
        raise RuntimeError("The existing service is not the verified live-Qwen preview")
    if value["queue"]["waiting"] or value["queue"]["running"]:
        raise RuntimeError("Existing service is busy; stop diagnostics without queueing behind user work")
    return value


def generated_history(frames):
    output = copy.deepcopy(frames[-16:])
    for frame in output:
        q = frame["globalRotations"]
        for i in [0, *range(19, 27)]:
            q[i] = {"x": 0., "y": 0., "z": 0., "w": 1.}
        a = np.array([q[1][k] for k in "xyzw"])
        b = np.array([q[3][k] for k in "xyzw"])
        if a @ b < 0:
            b = -b
        middle = a+b; middle /= np.linalg.norm(middle)
        q[2] = dict(zip("xyzw", map(float, middle)))
        q[11] = copy.deepcopy(q[10]); q[12] = copy.deepcopy(q[10])
        q[17] = copy.deepcopy(q[16]); q[18] = copy.deepcopy(q[16])
    return {"kind": "unity-upper-body-projection-v1", "fps": 20, "frames": output}


def main():
    output = OUT/"http-probes"
    if output.exists():
        raise ValueError("Diagnostic output already exists; do not overwrite or silently rerun")
    output.mkdir()
    archived = read(OUT/"archive-manifest.json")
    matches = [(row, read(row["archive"])) for row in archived["traces"] if read(row["archive"])["revision"] == 50]
    assert len(matches) == 1
    evidence, holding = matches[0]
    first = json.loads(next(e["json"] for e in holding["entries"] if e["kind"] == "request"))
    responses = [json.loads(e["json"]) for e in holding["entries"] if e["kind"] == "response"]
    history = {"archived-idle": first["initialHistory"], "generated-last16": generated_history(sum((r["clip"]["frames"] for r in responses), []))}
    for label, value in history.items():
        dump(output/(label+".json"), value)
    lock = read(ROOT/"Tools/MotionAdapter/upstream-lock.json")
    names, parents, neutral, _ = load_core27_reference(ROOT/("Server/ARDY/vendor/ardy-"+lock["ardy"]))
    neutral = (ARDY_TO_UNITY@neutral.T).T
    target = VrmRestHierarchy("Assets/Model/NEVA.vrm")
    character = "composition-diagnostic-"+uuid.uuid4().hex[:12]
    report = {"schema": 1, "status": "running", "startedUtc": datetime.now(timezone.utc).isoformat(),
              "endpoint": URL, "characterId": character, "adapterSha256": ADAPTER, "scriptSha256": sha(__file__),
              "maximumTrajectories": 8, "maxChunks": 3, "texts": TEXTS, "seeds": [0, 1],
              "holdingTraceArchive": evidence, "inputHistories": {label: {"path": str(output/(label+".json")), "sha256": sha(output/(label+".json"))} for label in history},
              "generatedHistoryMeaning": "Exactly the last sixteen returned frames from archived revision50, not a selected peak and not observed VRM playback. Root/legs identity, Spine1 quaternion midpoint of Spine/Spine2, hand endpoints copied from hands. The last sixteen frames are not guaranteed to be an actually held pose.",
              "policy": "Real existing service only, no new model or service; stop if queue is busy or HTTP fails. Every trajectory uses a private revision and is cancelled in finally. Explicit seeds initialize each private RNG equally.",
              "targetAssetAssumption": "NEVA.vrm asset-only reconstruction; the trace does not identify the actual scene avatar. No Animator/blend/render simulation.",
              "semanticPass": None, "naturalnessPass": None, "results": []}
    session = requests.Session()
    try:
        report["initialHealth"] = health(session)
        dump(output/"report.json", report)
        for text_id, description in TEXTS.items():
            for history_id, initial in history.items():
                for seed in (0, 1):
                    identity = {"characterId": character, "turnId": f"{text_id}-{history_id}-seed{seed}", "revision": len(report["results"])+1}
                    case_id = identity["turnId"]
                    case = {**identity, "id": case_id, "description": description, "seed": seed, "history": history_id, "chunks": []}
                    report["results"].append(case)
                    begin = time.perf_counter()
                    chunks = []
                    try:
                        for chunk in range(3):
                            health(session)
                            request = {**identity, "requestId": case_id+f"-chunk{chunk}", "description": description,
                                       "mask": "UpperBody", "chunkIndex": chunk, "seed": seed, "timeoutMs": 20000, "maxChunks": 3,
                                       "initialHistory": initial if chunk == 0 else None}
                            request_path = output/(case_id+f"-request-{chunk}.json")
                            dump(request_path, request)
                            started = time.perf_counter()
                            response = session.post(URL+"/v1/motion/generate", json=request, timeout=22)
                            response_path = output/(case_id+f"-response-{chunk}.json")
                            response_path.write_bytes(response.content)
                            row = {"request": str(request_path), "response": str(response_path), "responseSha256": sha(response_path),
                                   "httpStatus": response.status_code, "roundTripMilliseconds": (time.perf_counter()-started)*1000}
                            case["chunks"].append(row)
                            response.raise_for_status()
                            value = response.json()
                            if any(value[k] != request[k] for k in ("characterId", "turnId", "revision", "requestId", "chunkIndex")):
                                raise ValueError("Response identity mismatch")
                            if value["clip"]["source"]["adapterSha256"] != ADAPTER or value["newFrames"] != 40 or len(value["clip"]["frames"]) != 40:
                                raise ValueError("Response contract changed")
                            if value["provenance"]["featureSource"] != ("live-qwen" if chunk == 0 else "live-condition-reused"):
                                raise ValueError("Unexpected feature source")
                            row["provenance"] = value["provenance"]; row["timings"] = value["timings"]
                            chunks.append(value["clip"])
                            dump(output/"report.json", report)
                        frames = sum((c["frames"] for c in chunks), [])
                        clip = {**chunks[0], "id": case_id, "frames": frames, "source": {**chunks[0]["source"], "frameCount": 120,
                                "diagnosticAssembly": "Exact three HTTP response frame lists concatenated, no motion modified."}}
                        clip_path = output/(case_id+".json"); dump(clip_path, clip)
                        case["clip"] = str(clip_path); case["totalSeconds"] = time.perf_counter()-begin
                        measurements = {}; case["metrics"] = {}
                        for basis, function in (("core27", lambda r: source_measure(r, names, parents, neutral)),
                                                ("NEVA_fullblend_UpperBody", lambda r: target_measure(r, names, target))):
                            measured = function(matrices(frames)); initial_measured = function(matrices(initial["frames"]))
                            measurements[basis] = {"initialHistory": {k: v.tolist() for k, v in initial_measured.items()}, "returnedMotion": {k: v.tolist() for k, v in measured.items()}}
                            case["metrics"][basis] = {"initialHistory": summarize(initial_measured), "returnedMotion": summarize(measured)}
                        measurement_path = output/(case_id+".measurements.json"); dump(measurement_path, measurements)
                        case["frameMeasurements"] = str(measurement_path); case["status"] = "complete"
                    finally:
                        cancellation = {**identity, "requestId": case_id+"-diagnostic-cleanup", "reason": "bounded-composition-diagnostic-complete"}
                        cancel_response = session.post(URL+"/v1/motion/cancel", json=cancellation, timeout=3)
                        case["cancellation"] = {"request": cancellation, "httpStatus": cancel_response.status_code, "response": cancel_response.text}
                        dump(output/"report.json", report)
                    print(case_id+" complete; cancelled private revision", flush=True)
        report["finalHealth"] = health(session)
        report["status"] = "complete"
        dump(output/"render-manifest.json", {"clips": [{"id": c["id"], "path": c["clip"]} for c in report["results"]]})
    except Exception as error:
        report["status"] = "stopped"
        report["error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        report["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        dump(output/"report.json", report)
        session.close()


if __name__ == "__main__":
    main()
