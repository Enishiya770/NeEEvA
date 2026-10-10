"""Replay one untouched Unity-exported object-match request against the existing model.

Expected labels stay in a separate export manifest. Default mode only verifies
the export and compiles the actual protocol parser; --execute-model sends the
original request bytes once. This does not execute a Unity action or establish
current scene visibility after the captured frame.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

from validate_autonomous_dialogue_motion import visible_content
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import MONO, NEWTONSOFT, PROTOCOL, ROOT, strict_json

HARNESS = r'''using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public static class SceneGoalReferentProbe {
    public static int Main(string[] args) {
        if (args.Length != 2) return 2;
        var metadata=JObject.Parse(File.ReadAllText(args[1]));
        var ids=metadata["candidates"].Values<JObject>();
        var offered=new System.Collections.Generic.List<string>();
        foreach(var item in ids) offered.Add((string)item["candidateId"]);
        RoomTaskProtocol.ObjectMatch match;
        bool valid=RoomTaskProtocol.TryObjectMatch(File.ReadAllText(args[0]),offered,out match);
        Console.Write(JsonConvert.SerializeObject(new {valid, match})); return 0;
    }
}'''


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def compile_parser(output: Path) -> None:
    build = output / "parser"
    build.mkdir()
    (build / "Probe.cs").write_text(HARNESS, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
               "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
               str(ROOT / PROTOCOL), str(build / "Probe.cs")]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production protocol compile failed; inspect parser/compile.log")


def parse_match(output: Path, response: Path, metadata: Path) -> dict:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), str(response), str(metadata)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Production object-match parser failed: " + str(result.returncode))
    return strict_json(result.stdout.strip().lstrip("\ufeff"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--upstream-validation-note", default="Upstream suite status is not asserted by this replay.")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    exported, output = args.export.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest = strict_json((exported / "manifest.json").read_text(encoding="utf-8-sig"))
    raw = (exported / manifest["requestFile"]).read_bytes()
    wire = strict_json(raw.decode("utf-8-sig"))
    metadata_path = exported / "candidates.metadata.json"
    metadata = strict_json(metadata_path.read_text(encoding="utf-8-sig"))
    candidates = metadata["candidates"]
    expected = [row for row in candidates if row["candidateId"] == manifest["expectedCandidateId"]]
    if len(expected) != 1 or expected[0]["entityId"] != manifest["expectedEntityId"]:
        raise ValueError("Expected label is not the exported current-frame candidate/entity pair")
    if metadata["captureId"] != manifest["sourceCaptureId"]:
        raise ValueError("Candidate image capture ID differs from its exported source")
    if wire.get("stream") is True or not isinstance(wire.get("messages"), list):
        raise ValueError("Require an untouched non-streaming object-match request")
    images = [part["image_url"]["url"] for message in wire["messages"]
              if isinstance(message.get("content"), list) for part in message["content"]
              if part.get("type") == "image_url"]
    candidate_bytes = (exported / manifest["candidateImage"]).read_bytes()
    if len(images) != 1 or base64.b64decode(images[0].split(",", 1)[1], validate=True) != candidate_bytes:
        raise ValueError("Wire must contain exactly the exported candidate image")
    compile_parser(output)
    # Preserve the exact request bytes rather than rebuilding JSON or changing settings.
    (output / "request.json").write_bytes(raw)
    files = [manifest["requestFile"], manifest["inputFile"], "manifest.json", "candidates.metadata.json",
             manifest["candidateImage"], manifest["sourceImage"], "source.metadata.json"]
    report = {"status": "prepared", "exportDirectory": str(exported), "model": wire.get("model"),
              "upstreamValidationNote": args.upstream_validation_note,
              "scope": "One actual model response to untouched production object-match wire and saved current-frame RGB/candidates with actual prior-device history. No Unity/device execution and no current-world freshness claim.",
              "policy": "One request only; no retries, answer selection, payload edits, alternate endpoint or model startup. Expected labels are never added to the request.",
              "sources": {name: sha((ROOT / name).read_bytes()) for name in (PROTOCOL, NEWTONSOFT,
                          "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs",
                          "Assets/AIChatTookit/Scripts/Chat/ChatSample.SceneObjectMatching.cs")},
              "scriptSha256": sha(Path(__file__).read_bytes()),
              "exportHashes": {name: sha((exported / name).read_bytes()) for name in files},
              "expected": {key: manifest[key] for key in ("expectedVerdict", "expectedCandidateId", "expectedEntityId", "sourceCaptureId")},
              "modelRequests": 0, "errors": []}
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "output": str(output), "modelRequests": 0}, ensure_ascii=False))
        return 0
    try:
        if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
            report["status"] = "deferred"
            report["errors"].append("Shared slots are busy or their idle state is unknown; no request sent")
            return 1
        started = time.perf_counter()
        request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", raw,
                                         {"Content-Type": "application/json"})
        report["modelRequests"] = 1
        with urllib.request.urlopen(request, timeout=90) as response:
            envelope = strict_json(response.read().decode("utf-8"))
            status = response.status
        choices = envelope.get("choices", [])
        choice = choices[0] if len(choices) == 1 else {}
        message = choice.get("message", {})
        content = message.get("content", "")
        if status != 200 or choice.get("finish_reason") != "stop" or message.get("role") != "assistant":
            report["errors"].append("invalid-or-incomplete-envelope")
        if not isinstance(content, str) or not content.strip():
            report["errors"].append("empty-public-json")
            content = ""
        public = visible_content(content)
        if public != content.strip():
            report["errors"].append("hidden-text-in-public-content")
        # Hidden blocks are neither persisted nor accepted as a repaired answer.
        response_path = output / "response.json"
        response_path.write_text(public, encoding="utf-8")
        parsed = parse_match(output, response_path, metadata_path)
        report["parsed"] = parsed
        if not parsed["valid"]:
            report["errors"].append("production-parser-rejected")
        else:
            match = parsed["match"]
            if match["verdict"] != manifest["expectedVerdict"] or match["candidateId"] != manifest["expectedCandidateId"]:
                report["errors"].append("actual-model-did-not-match-the-proven-current-candidate")
        report["requestSeconds"] = round(time.perf_counter() - started, 6)
        report["status"] = "passed" if not report["errors"] else "failed"
    except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
        report["status"] = "failed"
        report["errors"].append(type(error).__name__ + ":" + str(error))
    finally:
        write(output / "report.json", report)
        print(json.dumps(report, ensure_ascii=False), flush=True)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
