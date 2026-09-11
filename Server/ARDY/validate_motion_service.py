"""Exercise real resident ARDY through HTTP; live Qwen or explicit fixture provenance."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import threading
import time
from pathlib import Path

import httpx
import numpy as np

from Server.ARDY.motion_service.app import create_app
from Server.ARDY.motion_service.backend import ArdyBackend, ROOT
from Server.ARDY.motion_service.protocol import GenerateRequest
from Server.ARDY.motion_service.service import FixtureFeatureProvider, LiveFeatureProvider, MotionService


class ObservedBackend(ArdyBackend):
    def __init__(self):
        super().__init__()
        self.entered = threading.Event()
        self.results = []

    def generate(self, request, embedding, previous):
        self.entered.set()
        value = super().generate(request, embedding, previous)
        self.results.append({"requestId": request.requestId, "generationMs": value["generationMs"],
                             "peakTorchAllocatedMiB": value["peakTorchAllocatedMiB"],
                             "historyFrames": value["historyFrames"], "projection": value["projection"]})
        return value


def projected_fixture():
    """Synthetic moving upper-arm rotations test conversion; not a real Unity capture."""
    from scipy.spatial.transform import Rotation
    frames = []
    for t in range(5):
        quats = np.tile([0., 0., 0., 1.], (27, 1))
        for joint in (14, 15, 16, 17, 18):
            quats[joint] = Rotation.from_euler("z", 25+t*2, degrees=True).as_quat()
        frames.append({"globalRotations": [dict(zip("xyzw", map(float, q))) for q in quats]})
    return {"kind": "unity-upper-body-projection-v1", "fps": 20, "frames": frames}


async def run(args):
    import uvicorn
    from Tools.MotionAdapter.data import read_records
    records = read_records(ROOT / "Tools/MotionAdapter/runtime/prompts.jsonl")
    description = next(row["text"] for row in records if row["id"] == "motion-00053")
    provider = LiveFeatureProvider(args.feature_url) if args.live else FixtureFeatureProvider(
        ROOT / "Tools/MotionAdapter/runtime/qwen-2000.npz", ROOT / "Tools/MotionAdapter/runtime/prompts.jsonl")
    backend = ObservedBackend()
    service = MotionService(backend, provider)
    server = uvicorn.Server(uvicorn.Config(create_app(service), host="127.0.0.1", port=args.port, log_level="warning"))
    task = asyncio.create_task(server.serve())
    while not server.started:
        if task.done():
            await task
            raise RuntimeError("Validation HTTP server did not start")
        await asyncio.sleep(.05)
    report = {"schema": 1, "mode": "live-qwen-and-real-ardy-http" if args.live else "cached-feature-fixture-and-real-ardy-http",
              "languageModelsLoadedByArdyService": False, "featureSource": provider.source,
              "initialHistoryFixture": "synthetic upper-arm curve; conversion test, not a Unity playback capture",
              "assertions": [], "chunks": []}
    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        report["assertions"].append(label)
    def body(character="http-neva", revision=1, chunk=0, **changes):
        value = dict(characterId=character, turnId="validation-turn", revision=revision,
                     requestId=f"{character}-{revision}-{chunk}", chunkIndex=chunk,
                     description=description, mask="UpperBody", seed=42, timeoutMs=20000, maxChunks=3)
        value.update(changes)
        return value
    try:
        async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{args.port}", timeout=25) as client:
            health = (await client.get("/health")).json()
            check(health["ready"] and health["backend"]["languageModelsLoaded"] is False, "resident ARDY ready with no LM")
            report["backend"] = health["backend"]
            previous_for_repeat = None
            for chunk in range(3):
                value = body(chunk=chunk, **({"initialHistory": projected_fixture()} if chunk == 0 else {}))
                if chunk == 1:
                    previous_for_repeat = dict(service.characters["http-neva"].backend_state)
                    # Another character runs between this character's windows.
                    other = await client.post("/v1/motion/generate", json=body(character="http-other"))
                    check(other.status_code == 200 and other.json()["historyFrames"] == 0, "other character cold start owns separate history")
                response = await client.post("/v1/motion/generate", json=value)
                if response.status_code != 200:
                    raise AssertionError(f"Generation returned {response.status_code}: {response.text}")
                result = response.json()
                check(result["startFrame"] == chunk*40 and result["newFrames"] == 40 and len(result["clip"]["frames"]) == 40,
                      f"chunk {chunk}: exactly 40 new frames, no returned history appended")
                check(result["historyFrames"] == 16, f"chunk {chunk}: 16 normalized history frames supplied")
                check(result["final"] == (chunk == 2), f"chunk {chunk}: correct final flag")
                expected_source = "live-condition-reused" if args.live and chunk > 0 else provider.source
                check(result["provenance"]["featureSource"] == expected_source, f"chunk {chunk}: honest feature provenance")
                check(result["clip"]["source"]["mode"] == "dynamic-ardy-native", f"chunk {chunk}: native dynamics distinct from baseline")
                if chunk == 0:
                    projection = result["provenance"]["initialHistoryProjection"]
                    check(projection["paddedFrames"] == 11 and projection["rotationRoundtripMaxAbs"] < 2e-5,
                          "projected upper-body history pads 5 to 16 and roundtrips through official representation")
                    check(projection["rootPositionMeters"][1] > .5 and min(projection["projectedFootWorldY"].values()) >= -1e-7,
                          "projected source pelvis stands above ground and synthetic feet are not buried")
                    check(abs(min(projection["projectedFootWorldY"].values())) < 1e-7,
                          "source foot/toe support plane is exactly ground Y=0")
                report["chunks"].append({k: result[k] for k in ("requestId","chunkIndex","historySource","historyFrames","timings","provenance")})
                output = ROOT / "Server/ARDY/runtime/live-service-validation" / ("live" if args.live else "fixture")
                output.mkdir(parents=True, exist_ok=True)
                (output/f"chunk-{chunk}.json").write_text(json.dumps(result, separators=(",", ":")), encoding="utf-8")
                if chunk == 1:
                    import torch
                    global_cpu, global_cuda = torch.get_rng_state().clone(), torch.cuda.get_rng_state().clone()
                    embedding = service.characters["http-neva"].feature["embedding"]
                    repeat = await asyncio.to_thread(backend.generate, GenerateRequest(**value), embedding, previous_for_repeat)
                    check(repeat["clip"]["frames"] == result["clip"]["frames"], "interleaving other character leaves exact per-revision RNG continuation reproducible")
                    check(torch.equal(global_cpu, torch.get_rng_state()) and torch.equal(global_cuda, torch.cuda.get_rng_state()),
                          "backend restores process CPU and CUDA RNG after isolated inference")
            duplicate = await client.post("/v1/motion/generate", json=body(chunk=2))
            check(duplicate.status_code == 200 and duplicate.json() == result, "last request replay is idempotent")
            seed_a = await client.post("/v1/motion/generate", json=body(character="http-seed-a", revision=1))
            seed_b = await client.post("/v1/motion/generate", json=body(character="http-seed-b", revision=9))
            check(seed_a.status_code == seed_b.status_code == 200, "cross-session explicit-seed pair generated")
            check(seed_a.json()["clip"]["frames"] == seed_b.json()["clip"]["frames"],
                  "same explicit seed and inputs produce exact motion across different character/revision identities")
            check(seed_a.json()["provenance"]["derivedSeed"] == seed_b.json()["provenance"]["derivedSeed"] == 42,
                  "effective seed equals the explicit requested seed")
            # The worker genuinely enters ARDY generation; cancel is handled by HTTP
            # while the uncancellable kernel finishes, and its result must be discarded.
            backend.entered.clear()
            pending = asyncio.create_task(client.post("/v1/motion/generate", json=body(character="http-cancel")))
            deadline = time.monotonic()+20
            while not backend.entered.is_set() and time.monotonic() < deadline:
                await asyncio.sleep(.001)
            check(backend.entered.is_set(), "cancellation fixture entered actual ARDY backend")
            cancel_start = time.perf_counter()
            cancelled = await client.post("/v1/motion/cancel", json=dict(characterId="http-cancel", turnId="validation-turn", revision=1, reason="validation"))
            report["cancelResponseMs"] = (time.perf_counter()-cancel_start)*1000
            rejected = await pending
            check(cancelled.status_code == 200 and rejected.status_code == 409, "running generation cancels promptly without publishing a chunk")
            retry_old = await client.post("/v1/motion/generate", json=body(character="http-cancel"))
            check(retry_old.status_code == 409, "cancelled revision cannot be resurrected")
            replacement = await client.post("/v1/motion/generate", json=body(character="http-cancel",revision=2))
            check(replacement.status_code == 200 and replacement.json()["historyFrames"] == 0,
                  "new revision does not inherit unplayed cancelled future motion")
            check(service.counts["staleResultsDiscarded"] >= 1, "completed cancelled GPU result was discarded")
            report["finalHealth"] = (await client.get("/health")).json()
            report["backendRuns"] = backend.results
            report["passed"] = True
    except Exception as error:
        report["passed"] = False
        report["error"] = repr(error)
        raise
    finally:
        server.should_exit = True
        await task
        report["assertionCount"] = len(report["assertions"])
        report["moduleSha256"] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in (ROOT/"Server/ARDY/motion_service").glob("*.py")}
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"passed":report["passed"],"report":str(args.report),"assertions":report["assertionCount"]}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--feature-url", default="http://127.0.0.1:8080")
    parser.add_argument("--port", type=int, default=8094)
    parser.add_argument("--report", type=Path, default=ROOT/"Tools/MotionAdapter/reports/ardy-motion-service-fixture.json")
    asyncio.run(run(parser.parse_args()))


if __name__ == "__main__":
    main()
