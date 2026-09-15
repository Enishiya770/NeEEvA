"""Explicit isolated scheduler probe: native cold step, then shared-Qwen speech plus warm step.

Uses one saved Unity stream request and one saved isolated-scene formal Qwen wire.
It never drives Unity, plays speech, restarts services, or creates another model.
Without --execute it only validates and exports inputs; no network is contacted.
"""
from __future__ import annotations

import argparse
import asyncio
import copy
import hashlib
import json
import math
from pathlib import Path
import sys
import time
import uuid
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
import httpx
from Server.ARDY.motion_service.locomotion_stream_protocol import StreamStep


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def read(path):
    data = path.read_bytes()
    if len(data) > 4 * 1024 * 1024:
        raise ValueError("Use a bounded saved request smaller than 4 MiB.")
    return json.loads(data.decode("utf-8-sig")), hashlib.sha256(data).hexdigest()


def prepare(args):
    original, stream_hash = read(args.stream_request)
    first = StreamStep(**original)
    if first.stepIndex != 0:
        raise ValueError("A saved first stream step with actual initialHistory is required.")
    formal, formal_hash = read(args.formal_wire)
    if not isinstance(formal.get("messages"), list) or not any(m.get("role") == "system" for m in formal["messages"]):
        raise ValueError("Formal wire must retain the existing character system prompt.")
    images = [part["image_url"]["url"] for msg in formal["messages"] if isinstance(msg.get("content"), list)
              for part in msg["content"] if part.get("type") == "image_url"]
    if len(images) != 1 or not images[0].startswith(("data:image/png;base64,", "data:image/jpeg;base64,")):
        raise ValueError("Use exactly one embedded actual isolated-scene image, with no external image URL.")
    run_id = "follow-schedule-" + uuid.uuid4().hex
    first = first.model_dump()
    first.update(characterId=run_id, sessionId=run_id, revision=1, requestId=run_id+"-0", timeoutMs=20000)
    formal = copy.deepcopy(formal)
    formal["max_tokens"] = min(320, int(formal.get("max_tokens", 320)))
    formal["messages"].insert(0, {"role": "system", "content":
        "This is an isolated offline replay for scheduler validation. The following character prompt, facts and image are saved fixture inputs, not current events. Produce a bounded reply only; no Unity actions, device changes, speech playback or user messages are executed from this response."})
    return first, formal, {"streamRequestSha256": stream_hash, "formalWireSha256": formal_hash,
        "formalWireChanges": "Only prepended explicit archived-fixture scope and capped max_tokens at 320; original character prompt, message facts and scene image retained.",
        "streamChanges": "Only isolated character/session/request IDs, revision and explicit 20 s cold deadline; captured history, description, seed and first path unchanged."}


def continuation(first, response):
    """A diagnostic path from the actually returned prefix, never claimed as Unity playback."""
    commit = first["commitFrames"]
    point = response["frames"][commit-1]["rootPosition"]
    q = response["rotationClip"]["frames"][commit-1]["globalRotations"][0]
    x, z = 2*(q["x"]*q["z"]+q["w"]*q["y"]), 1-2*(q["x"]**2+q["y"]**2)
    heading = math.atan2(x, z)
    value = {key: first[key] for key in ("schema", "characterId", "sessionId", "revision", "seed", "commitFrames")}
    value.update(requestId=first["sessionId"]+"-1", stepIndex=1, startFrame=commit, controlRevision=1,
        previousRequestId=first["requestId"], timeoutMs=5000,
        targets=[{"rootPosition": {"x": point["x"]+.02*i*math.sin(heading), "y": 0,
                                  "z": point["z"]+.02*i*math.cos(heading)},
                  "headingDegrees": math.degrees(heading)} for i in range(40)])
    return StreamStep(**value).model_dump()


async def run(args):
    if args.output.exists() and any(args.output.iterdir()):
        raise FileExistsError("Use an empty output directory.")
    first, formal, metadata = prepare(args)
    args.output.mkdir(parents=True, exist_ok=True)
    write(args.output/"step0.request.json", first)
    write(args.output/"formal.request.json", formal)
    report = {"status": "prepared", **metadata, "requests": [], "checks": {},
        "scope": "One cold ARDY step completes before one shared-Qwen formal request. A second step overlaps that formal request using the acknowledged generated prefix. No Unity playback or terrain audit is claimed; the second path is a bounded .4 m/s diagnostic continuation from the returned seam.",
        "limitations": ["Does not prove the Unity handoff timing; use its integration regression separately.",
                        "No reversed-order failure is deliberately induced. Remote queue wait may include unrelated already-running work.",
                        "Model text is recorded only, never executed or spoken."], "networkRequests": 0}
    write(args.output/"report.json", report)
    if not args.execute:
        return report
    for endpoint in (args.ardy_url, args.qwen_url):
        parsed = urlparse(endpoint)
        if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost") or parsed.username:
            raise ValueError("Use the existing local loopback endpoints only.")
    formal_task = None
    started = time.monotonic()
    async with httpx.AsyncClient(timeout=45, follow_redirects=False) as client:
        async def post(name, url, payload, timeout):
            sent = time.monotonic()-started
            report["networkRequests"] += 1
            response = await client.post(url, json=payload, timeout=timeout)
            elapsed = time.monotonic()-started
            (args.output/(name+".response.txt")).write_text(response.text, encoding="utf-8")
            item = {"name": name, "sentSeconds": sent, "doneSeconds": elapsed,
                    "elapsedSeconds": elapsed-sent, "httpStatus": response.status_code}
            report["requests"].append(item)
            try: result = response.json()
            except ValueError: result = None  # The original production formal wire may use SSE.
            if name.startswith("step") and isinstance(result, dict):
                item["timings"] = result.get("timings")
                item["source"] = {k: result.get("source", {}).get(k) for k in
                    ("featureSource", "featureRequestId", "conditionReusedWithinSession")}
                item["error"] = result.get("error")
            write(args.output/"report.json", report)
            return response.status_code, result
        try:
            status, first_result = await post("step0", args.ardy_url+"/v1/locomotion/stream/step", first, 22)
            if status != 200:
                raise RuntimeError("Cold step did not succeed; formal request was not dispatched.")
            if first_result.get("requestId") != first["requestId"] or first_result.get("stepIndex") != 0:
                raise ValueError("Cold response identity mismatch.")
            second = continuation(first, first_result)
            write(args.output/"step1.request.json", second)
            formal_task = asyncio.create_task(post("formal", args.qwen_url+"/v1/chat/completions", formal, 45))
            await asyncio.sleep(.1)
            report["checks"]["formalInFlightWhenContinuationSent"] = not formal_task.done()
            status, result = await post("step1", args.ardy_url+"/v1/locomotion/stream/step", second, 7)
            formal_status, _ = await formal_task
            report["checks"].update(coldBeforeFormal=True, coldSucceeded=True, formalSucceeded=formal_status == 200,
                continuationSucceeded=status == 200,
                warmFeatureReused=status == 200 and result.get("source", {}).get("conditionReusedWithinSession") is True,
                exactColdFeatureProvenance=status == 200 and result.get("source", {}).get("featureRequestId") == first["requestId"])
            report["status"] = "passed" if all(report["checks"].values()) else "failed"
        except Exception as error:
            report["status"] = "failed"
            report["failure"] = type(error).__name__ + ": " + str(error)[:240]
        finally:
            if formal_task is not None and not formal_task.done():
                formal_task.cancel()
                await asyncio.gather(formal_task, return_exceptions=True)
            try:
                cleanup_status, _ = await post("cancel", args.ardy_url+"/v1/locomotion/stream/cancel",
                    {k: first[k] for k in ("characterId", "revision", "sessionId")}, 3)
                if cleanup_status != 200:
                    report["cleanupFailure"] = "Isolated stream cancellation returned HTTP " + str(cleanup_status)
                    report["status"] = "failed"
            except Exception as error:
                report["cleanupFailure"] = type(error).__name__ + ": " + str(error)[:240]
                report["status"] = "failed"
            write(args.output/"report.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stream-request", type=Path, required=True)
    parser.add_argument("--formal-wire", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ardy-url", default="http://127.0.0.1:8093")
    parser.add_argument("--qwen-url", default="http://127.0.0.1:8080")
    parser.add_argument("--execute", action="store_true")
    result = asyncio.run(run(parser.parse_args()))
    print(json.dumps({"status": result["status"], "networkRequests": result["networkRequests"], "checks": result["checks"]}))
    return 1 if result["status"] == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main())
