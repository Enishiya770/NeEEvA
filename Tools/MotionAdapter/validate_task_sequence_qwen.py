"""Evaluate Unity-exported production room-planning wires without executing actions.

This checks whole-request decomposition, prerequisite selection and literal
grounding on the real local model. It does not replace runtime ownership tests.
No expected-answer retries are used.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import sys
import urllib.request

from validate_speech_state_qwen import request, write


def validate(result, row):
    steps = result["taskPlan"]
    assert isinstance(steps, list) and len(steps) <= 6, result
    seen = set()
    for step in steps:
        assert set(step) == {"id", "quote", "kind", "desiredState", "after"}, step
        assert step["id"] not in seen and 0 < len(step["id"]) <= 16, step
        assert 0 < len(step["quote"]) <= 160 and step["quote"] in row["utterance"], step
        assert set(step["after"]) <= seen, step
        assert len(set(step["after"])) == len(step["after"]), step
        seen.add(step["id"])
    assert [s["kind"] for s in steps] == row["expectedKinds"], result
    if not steps:
        assert result["taskStep"] is None and result["intent"] == "none", result
        return
    for previous, following in zip(steps, steps[1:]):
        assert previous["id"] in following["after"], result
    first = steps[0]
    assert result["taskStep"] == first["id"] and first["after"] == [], result
    if first["kind"] == "device-state":
        assert result.get("deviceGoal", {}).get("operation") == "set", result
        assert result["deviceGoal"]["desiredState"] == first["desiredState"], result
        assert result["intent"] not in ("approach", "follow"), result
        assert result["deviceGoal"]["requestQuote"] in row["utterance"], result
    else:
        assert result["intent"] == first["kind"], result


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise SystemExit("Use a fresh output directory.")
    args.output.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(args.url + "/slots", timeout=5) as response:
        assert all(not x.get("is_processing") for x in json.load(response)), "Model slots are busy."
    report = {"scope": __doc__, "sourceSha256": hashlib.sha256(args.cases.read_bytes()).hexdigest(), "cases": [], "passed": False}
    for row in json.loads(args.cases.read_text(encoding="utf-8-sig")):
        entry = {"name": row["name"], "passed": False}
        report["cases"].append(entry)
        try:
            wire = copy.deepcopy(row["request"])
            wire["model"] = "qwen36"
            write(args.output / (row["name"] + ".request.json"), wire)
            result, envelope, elapsed = request(args.url, wire)
            write(args.output / (row["name"] + ".envelope.json"), envelope)
            entry.update(decision=result, seconds=elapsed)
            validate(result, row)
            entry["passed"] = True
        except Exception as error:
            entry["error"] = str(error)
        print(json.dumps({k: v for k, v in entry.items() if k != "decision"}, ensure_ascii=False), flush=True)
        write(args.output / "report.json", report)
    report["passed"] = all(x["passed"] for x in report["cases"])
    write(args.output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
