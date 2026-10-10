"""Prepare or single-pass replay frozen real-image planner wires without rewriting them.

The manifest is sidecar-only: cases contain id, inputPath, requestPath, provenance,
optional expected (dotted parsed-decision paths -> allowed values), and optional
forbiddenPresent paths. Paths are relative to the manifest. Exact wire bytes are
copied before any request; no expected labels or provenance enter model input.
Default is offline compilation/preparation. --execute-model requires the existing
local Qwen to be idle, then sends each frozen wire once. No Unity, service startup,
retry, mutation, response repair, or action dispatch occurs.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import MONO, NEWTONSOFT, PROTOCOL, ROOT, strict_json


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write(path: Path, value) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read(path: Path):
    return strict_json(path.read_bytes().decode("utf-8-sig"))


def compile_parser(output: Path) -> dict:
    build = output / "parser"
    build.mkdir()
    harness = r'''using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public static class DecisionParserProbe {
    public static int Main(string[] args) {
        var input=JObject.Parse(File.ReadAllText(args[1]));
        bool valid=RoomTaskProtocol.TryDecision(File.ReadAllText(args[0]),
            (string)input["currentUserText"]??"", (bool?)input["autonomous"]==true,
            out var decision,out string reason);
        Console.Write(JsonConvert.SerializeObject(new {valid,decision,reason}));
        return 0;
    }
}
'''
    (build / "DecisionParserProbe.cs").write_text(harness, encoding="utf-8")
    shutil.copyfile(ROOT / PROTOCOL, build / "RoomTaskProtocol.cs")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"),
        "/nologo", "/langversion:9", "/r:" + str(build / "Newtonsoft.Json.dll"),
        "/out:" + str(build / "probe.exe"), str(build / "RoomTaskProtocol.cs"), str(build / "DecisionParserProbe.cs")],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production parser compilation failed; inspect parser/compile.log")
    return {"source": PROTOCOL, "sha256": digest((ROOT / PROTOCOL).read_bytes()),
            "scope": "Current production RoomTaskProtocol compiled unchanged; no Unity lifecycle or action execution."}


def inspect_wire(raw: bytes) -> dict:
    wire = strict_json(raw.decode("utf-8-sig"))
    if wire.get("stream") is True:
        raise ValueError("This planner wire runner accepts non-streaming production requests only")
    if "expected" in wire or "provenance" in wire:
        raise ValueError("Evaluation sidecars must not enter the model request")
    images = []
    text_bytes = 0
    for message in wire["messages"]:
        content = message.get("content", "")
        if isinstance(content, str):
            text_bytes += len(content.encode("utf-8"))
        else:
            for part in content:
                if part.get("type") == "text":
                    text_bytes += len(part.get("text", "").encode("utf-8"))
                elif part.get("type") == "image_url":
                    url = part["image_url"]["url"]
                    if not url.startswith("data:image/") or ";base64," not in url:
                        raise ValueError("Require frozen inline image bytes, not a remote mutable URL")
                    image = base64.b64decode(url.split(",", 1)[1], validate=True)
                    images.append({"mime": url.split(";", 1)[0][5:], "bytes": len(image), "sha256": digest(image)})
    if len(images) != 1:
        raise ValueError("This regression requires exactly one real captured image")
    return {"sha256": digest(raw), "wireBytes": len(raw), "textUtf8Bytes": text_bytes,
            "imageCount": len(images), "images": images, "model": wire.get("model"),
            "maxTokens": wire.get("max_tokens"), "slot": wire.get("id_slot")}


def lookup(value, path: str):
    for key in path.split("."):
        if not isinstance(value, dict) or key not in value:
            return None
        value = value[key]
    return value


def parse_decision(output: Path, content_path: Path, input_path: Path):
    parsed = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"),
        str(content_path), str(input_path)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if parsed.returncode:
        raise RuntimeError("Production decision parser probe failed")
    return strict_json(parsed.stdout.lstrip("\ufeff"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--timeout", type=float, default=90.0)
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    endpoint = urllib.parse.urlsplit(args.url)
    if endpoint.scheme != "http" or endpoint.hostname not in {"127.0.0.1", "localhost", "::1"} or endpoint.username:
        raise ValueError("Only the existing local Qwen endpoint is supported")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest_path = args.manifest.resolve()
    manifest_raw = manifest_path.read_bytes()
    manifest = strict_json(manifest_raw.decode("utf-8-sig"))
    (output / "source-manifest.json").write_bytes(manifest_raw)
    rows = []
    seen = set()
    for source in manifest["cases"]:
        name = source["id"]
        if not re.fullmatch(r"[A-Za-z0-9_-]+", name) or name in seen:
            raise ValueError("Case IDs must be unique safe file names")
        seen.add(name)
        if not source.get("provenance", {}).get("kind"):
            raise ValueError("Each case must identify exact capture, reconstruction, or synthetic provenance")
        request_path = (manifest_path.parent / source["requestPath"]).resolve()
        input_path = (manifest_path.parent / source["inputPath"]).resolve()
        request_raw, input_raw = request_path.read_bytes(), input_path.read_bytes()
        measurements = inspect_wire(request_raw)
        inp = strict_json(input_raw.decode("utf-8-sig"))
        if not isinstance(inp.get("currentUserText"), str) or not isinstance(inp.get("autonomous"), bool):
            raise ValueError("Require full producer input with explicit currentUserText and autonomous")
        frozen_request, frozen_input = output / (name + ".request.json"), output / (name + ".input.json")
        frozen_request.write_bytes(request_raw)
        frozen_input.write_bytes(input_raw)
        row = dict(source, inputSha256=digest(input_raw), wire=measurements,
            frozenRequest=str(frozen_request), frozenInput=str(frozen_input))
        rows.append(row)
    if not rows:
        raise ValueError("No cases supplied")
    report = {"status": "prepared", "executed": False, "manifestSha256": digest(manifest_raw),
        "productionParser": compile_parser(output), "cases": rows, "results": [],
        "scope": "Frozen one-image wires sent byte-for-byte once. Evaluator labels are sidecar-only. Any reconstructed context remains reconstructed; this cannot establish live Unity correctness."}
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(rows), "output": str(output)}, ensure_ascii=False))
        return 0
    report["status"] = "running"
    report["executed"] = True
    try:
        for row in rows:
            if digest((ROOT / PROTOCOL).read_bytes()) != report["productionParser"]["sha256"]:
                raise RuntimeError("Production parser changed after this run was frozen")
            slots = request_json(args.url.rstrip("/") + "/slots")
            if not all_slots_idle(slots):
                raise RuntimeError("Existing Qwen slots are not all idle; no request sent for next case")
            name = row["id"]
            raw = Path(row["frozenRequest"]).read_bytes()
            if digest(raw) != row["wire"]["sha256"]:
                raise RuntimeError("Frozen request changed after preparation")
            errors, parsed, envelope = [], None, None
            started = time.perf_counter()
            status = None
            try:
                request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", raw,
                    {"Content-Type": "application/json"})
                with urllib.request.urlopen(request, timeout=args.timeout) as response:
                    status = response.status
                    response_raw = response.read()
                (output / (name + ".envelope.json")).write_bytes(response_raw)
                envelope = strict_json(response_raw.decode("utf-8-sig"))
                choice = envelope["choices"][0]
                content = choice.get("message", {}).get("content", "")
                if choice.get("finish_reason") != "stop" or not isinstance(content, str):
                    errors.append("incomplete-or-nontext-response")
                if isinstance(content, str):
                    response_path = output / (name + ".response.json")
                    response_path.write_text(content, encoding="utf-8")
                    parsed = parse_decision(output, response_path, Path(row["frozenInput"]))
                    if not parsed["valid"]:
                        errors.append("production-parser:" + str(parsed["reason"]))
                    else:
                        decision = parsed["decision"]
                        for path, allowed in row.get("expected", {}).items():
                            if lookup(decision, path) not in (allowed if isinstance(allowed, list) else [allowed]):
                                errors.append("unexpected-decision-field:" + path)
                        for path in row.get("forbiddenPresent", []):
                            if lookup(decision, path) is not None:
                                errors.append("forbidden-decision-field:" + path)
            except urllib.error.HTTPError as error:
                status = error.code
                (output / (name + ".http-error.txt")).write_bytes(error.read())
                errors.append("http-status:" + str(error.code))
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            result = {"id": name, "passed": not errors, "errors": errors, "httpStatus": status,
                "requestSeconds": round(time.perf_counter() - started, 6), "productionParser": parsed,
                "usage": envelope.get("usage") if isinstance(envelope, dict) else None,
                "timings": envelope.get("timings") if isinstance(envelope, dict) else None}
            report["results"].append(result)
            write(output / "report.json", report)
            print(json.dumps({key: result[key] for key in ("id", "passed", "httpStatus", "requestSeconds", "errors", "usage")}, ensure_ascii=False), flush=True)
        report["status"] = "passed" if all(row["passed"] for row in report["results"]) else "failed"
    except Exception as error:
        report["status"] = "blocked"
        report["blockingError"] = type(error).__name__ + ":" + str(error)
        raise
    finally:
        write(output / "report.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
