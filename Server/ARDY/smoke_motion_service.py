"""Check the already-running formal HTTP service without restarting it or loading models."""
from __future__ import annotations

import argparse
import hashlib
import json
import time
import uuid
from pathlib import Path

import httpx
import numpy as np

from Server.ARDY.motion_service.protocol import FEATURE_CONTRACT, QWEN_MODEL_SHA256

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8093")
    parser.add_argument("--feature-url", default="http://127.0.0.1:8080")
    parser.add_argument("--launcher-pid", type=int)
    parser.add_argument("--report", type=Path, default=ROOT/"Tools/MotionAdapter/reports/ardy-motion-service-unified-smoke.json")
    args = parser.parse_args()
    report = {"schema": 1, "mode": "running-formal-service-live-http-smoke", "serviceUrl": args.url,
              "featureUrl": args.feature_url, "assertions": [], "responses": []}
    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        report["assertions"].append(label)
    rows = [json.loads(line) for line in (ROOT/"Tools/MotionAdapter/runtime/prompts.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    description = next(row["text"] for row in rows if row["id"] == "motion-00053")
    character = "unified-smoke-"+uuid.uuid4().hex
    identity = {"characterId": character, "turnId": "formal-release-smoke", "revision": 1}
    try:
        with httpx.Client(base_url=args.url, timeout=25) as client:
            deadline = time.monotonic()+20
            while True:
                try:
                    response = client.get("/health")
                    response.raise_for_status()
                    break
                except httpx.HTTPError:
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(.1)
            health = response.json()
            check(health["ready"] and health["backend"]["languageModelsLoaded"] is False, "formal resident service ready, no LM loaded")
            check(health["features"]["baseUrl"] == args.feature_url, "formal default feature URL points to the unified chat entry")
            request = {**identity, "requestId": uuid.uuid4().hex, "description": description,
                       "mask": "UpperBody", "seed": 42, "chunkIndex": 0, "timeoutMs": 20000, "maxChunks": 3}
            feature_request = request["requestId"]
            for index in range(2):
                request["chunkIndex"] = index
                if index:
                    request["requestId"] = uuid.uuid4().hex
                response = client.post("/v1/motion/generate", json=request)
                check(response.status_code == 200, f"chunk {index}: HTTP 200")
                result = response.json()
                check(all(result[key] == value for key,value in identity.items()) and result["requestId"] == request["requestId"],
                      f"chunk {index}: request identity matches")
                check(result["newFrames"] == 40 and result["startFrame"] == index*40 and result["fps"] == 20,
                      f"chunk {index}: 40 new 20 FPS frames")
                check(result["historyFrames"] == (16 if index else 0), f"chunk {index}: correct history length")
                provenance = result["provenance"]
                check(provenance["featureSource"] == ("live-condition-reused" if index else "live-qwen"),
                      f"chunk {index}: live feature source/reuse explicit")
                check(provenance["featureContract"] == FEATURE_CONTRACT and provenance["modelSha256"] == QWEN_MODEL_SHA256,
                      f"chunk {index}: exact feature contract and model SHA256")
                check(provenance["featureRequestId"] == feature_request, f"chunk {index}: same revision condition binding")
                quats = np.array([[[q[k] for k in "xyzw"] for q in frame["globalRotations"]] for frame in result["clip"]["frames"]])
                check(quats.shape == (40,27,4) and np.isfinite(quats).all() and np.max(np.abs(np.linalg.norm(quats,axis=-1)-1)) < 1e-6,
                      f"chunk {index}: finite normalized Core27 quaternion output")
                report["responses"].append({k: result[k] for k in ("requestId","chunkIndex","historySource","provenance","timings")})
            cancel_body = {**identity, "requestId":uuid.uuid4().hex, "reason":"formal-smoke-complete"}
            start = time.perf_counter()
            response = client.post("/v1/motion/cancel", json=cancel_body)
            report["cancelResponseMs"] = (time.perf_counter()-start)*1000
            check(response.status_code == 200 and response.json()["cancelled"], "Unity-shaped cancel succeeds")
            request.update(chunkIndex=2,requestId=uuid.uuid4().hex)
            response = client.post("/v1/motion/generate",json=request)
            check(response.status_code == 409 and response.json()["error"]["code"] == "cancelled_revision", "cancelled revision rejects a later continuation")
            report["health"] = client.get("/health").json()
            check(report["health"]["features"]["verified"] and report["health"]["features"]["modelSha256"] == QWEN_MODEL_SHA256,
                  "health records verified live model contract")
            check(report["health"]["queue"]["waiting"] == 0 and report["health"]["queue"]["running"] == 0,
                  "queue drained and service remains available")
        if args.launcher_pid:
            import psutil
            launcher = psutil.Process(args.launcher_pid)
            expected = ["-m","Server.ARDY.motion_service.app","--port","8093"]
            check(launcher.cmdline()[1:] == expected, "recorded launcher identity verified")
            children = [p for p in launcher.children(recursive=True) if p.name().lower() == "python.exe"]
            check(len(children) == 1 and children[0].cmdline()[1:] == expected, "single formal service Python child verified")
            report["launcherPid"] = launcher.pid
            report["serverPid"] = children[0].pid
        report["passed"] = True
    except Exception as error:
        report["passed"],report["error"] = False,repr(error)
        raise
    finally:
        report["assertionCount"] = len(report["assertions"])
        report["moduleSha256"] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (ROOT/"Server/ARDY/motion_service").glob("*.py")}
        report["scope"] = "Default local feature URL changed to 8080 after the full live/Unity regressions; this smoke verifies the formal 8093 service without launching another ARDY instance."
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding="utf-8")
        if report["passed"]:
            (ROOT/"Server/ARDY/runtime/motion-service-running.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
        print(json.dumps({k:report.get(k) for k in ("passed","assertionCount","launcherPid","serverPid","cancelResponseMs")}),flush=True)


if __name__ == "__main__":
    main()
