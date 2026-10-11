"""Replay Unity-exported words-only publication requests. No scene or action execution."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import time
import urllib.request


def write(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="qwen36")
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise SystemExit("Use a fresh output directory.")
    args.output.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(args.url.rstrip("/") + "/slots", timeout=5) as response:
        assert all(not slot.get("is_processing") for slot in json.load(response)), "Model busy; do not interrupt user work."
    rows = json.loads(args.cases.read_text(encoding="utf-8-sig"))
    results = []
    report = {"source": str(args.cases.resolve()), "sourceSha256": hashlib.sha256(args.cases.read_bytes()).hexdigest(),
              "scope": __doc__, "model": args.model, "cases": results, "passed": False}
    for row in rows:
        entry = {"name": row["name"], "passed": False}
        results.append(entry)
        try:
            wire = copy.deepcopy(row["request"])
            wire["model"] = args.model
            assert json.loads(wire["messages"][-1]["content"]) == {"publicSpeech": row["speech"]}
            write(args.output / (row["name"] + ".request.json"), wire)
            started = time.monotonic()
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                json.dumps(wire, ensure_ascii=False).encode("utf-8"), {"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=8) as response:
                envelope = json.load(response)
            elapsed = time.monotonic() - started
            write(args.output / (row["name"] + ".envelope.json"), envelope)
            choice = envelope["choices"][0]
            assert choice["finish_reason"] == "stop", choice
            verdict = json.loads(choice["message"]["content"])
            entry.update(verdict=verdict, seconds=elapsed)
            assert set(verdict) == {"actualLanguage", "kind", "quote"}, verdict
            assert verdict["actualLanguage"] == row["expectedLanguage"], verdict
            assert verdict["kind"] == row["expectedKind"], verdict
            assert isinstance(verdict["quote"], str) and len(verdict["quote"]) <= 200 and verdict["quote"] in row["speech"], verdict
            assert verdict["kind"] == "conversation" or verdict["quote"].strip(), verdict
            assert elapsed < 8, elapsed
            entry["passed"] = True
        except Exception as error:
            entry["error"] = str(error)
        print(json.dumps(entry, ensure_ascii=False), flush=True)
        write(args.output / "report.json", report)
    report["passed"] = all(row["passed"] for row in results)
    report["passedCount"] = sum(row["passed"] for row in results)
    write(args.output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
