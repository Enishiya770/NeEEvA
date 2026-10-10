"""Replay each frozen Unity spatial-question routing wire once, keeping first responses."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request

from validate_scene_search import compile_probe, probe, write, request_json, all_slots_idle


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    compile_probe(output)
    contract = probe(output, "contracts")["roomContract"]
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    directory = args.manifest.resolve().parent
    report = dict(status="running", modelRequests=0, cases=[], scope=(
        "Single first responses to untouched production RoomTask wires captured in isolated Unity. "
        "Checks question-to-answer routing, not spoken-answer factual accuracy or physical search completion."))
    for case in manifest["cases"]:
        raw = (directory / case["requestFile"]).read_bytes()
        wire = json.loads(raw.decode("utf-8-sig"))
        source = json.loads((directory / case["inputFile"]).read_text(encoding="utf-8-sig"))
        if not wire["messages"][0]["content"].startswith(contract):
            raise ValueError("Production protocol changed after Unity export")
        if wire.get("stream") is not False:
            raise ValueError("Expected the production nonstreaming decision wire")
        row = dict(id=case["id"], status="prepared", requestSha256=hashlib.sha256(raw).hexdigest(), modelRequests=0)
        report["cases"].append(row)
        (output / (case["id"] + ".request.json")).write_bytes(raw)
        if args.execute_model:
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                row["status"] = "deferred-busy"
            else:
                row["modelRequests"] = 1
                report["modelRequests"] += 1
                start = time.perf_counter()
                try:
                    request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", raw,
                        {"Content-Type": "application/json"})
                    with urllib.request.urlopen(request, timeout=90) as response:
                        envelope_raw = response.read()
                    (output / (case["id"] + ".envelope.json")).write_bytes(envelope_raw)
                    envelope = json.loads(envelope_raw.decode("utf-8"))
                    choices = envelope.get("choices", [])
                    if len(choices) != 1 or choices[0].get("finish_reason") != "stop":
                        raise ValueError("Incomplete first completion")
                    content = choices[0]["message"]["content"]
                    (output / (case["id"] + ".response.json")).write_text(content, encoding="utf-8")
                    fixture = output / (case["id"] + ".parse-input.json")
                    write(fixture, [dict(id=case["id"], kind="room", json=content, user=source.get("currentUserText", ""))])
                    row["parser"] = probe(output, "parse", fixture)[0]
                    row["decision"] = json.loads(content)
                    expected = case["expected"]
                    row["questionAnsweredWithoutNewTaskRoute"] = (
                        row["decision"].get("intent") == expected["intent"] and
                        row["decision"].get("replyScope") == expected["replyScope"] and
                        "deviceGoal" not in row["decision"])
                    row["status"] = "passed" if row["parser"]["accepted"] and row["questionAnsweredWithoutNewTaskRoute"] else "failed"
                except Exception as error:
                    row.update(status="failed", error=str(error))
                row["elapsedSeconds"] = round(time.perf_counter() - start, 3)
        write(output / "report.json", report)
    report["status"] = "passed" if all(row["status"] in ("passed", "prepared") for row in report["cases"]) else "failed"
    write(output / "report.json", report)
    print(json.dumps(dict(status=report["status"], modelRequests=report["modelRequests"])))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
