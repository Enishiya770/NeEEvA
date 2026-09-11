"""Replay public Unity-exported work-review requests against an idle local model.

No model tools execute. Requests are the production ChatQW payloads exported by the
Unity regression, with synthetic recordings and no private persona or conversation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request


def read_json(url: str):
    with urllib.request.urlopen(url, timeout=4) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    args = parser.parse_args()
    fixture = json.loads(args.cases.read_text(encoding="utf-8-sig"))
    report = {
        "source": str(args.cases.resolve()),
        "sourceSha256": hashlib.sha256(args.cases.read_bytes()).hexdigest(),
        "scope": "Production exported requests; public synthetic contexts; live local LLM semantic decisions only. No tool, TTS, microphone or private scene execution.",
        "cases": [], "status": "running",
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    try:
        for case in fixture["cases"]:
            slots = read_json(args.url + "/slots")
            if any(slot.get("is_processing") for slot in slots):
                raise RuntimeError("Local model is busy; no further request submitted.")
            payload = case["request"]
            if isinstance(payload, str):
                payload = json.loads(payload)
            started = time.perf_counter()
            request = urllib.request.Request(
                args.url + "/v1/chat/completions", json.dumps(payload).encode("utf-8"),
                {"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=55) as response:
                result = json.load(response)
            choice = result["choices"][0]
            # The dedicated schema is intentionally the only accepted output.
            # Do not store any separate reasoning channel from the server envelope.
            decision = json.loads(choice["message"].get("content") or "{}")
            expected = case["expected_singing_goal_status"]
            allowed = expected if isinstance(expected, list) else [expected]
            item = {"id": case["id"], "expected": allowed, "decision": decision,
                    "finishReason": choice.get("finish_reason"),
                    "passed": choice.get("finish_reason") == "stop" and decision.get("singing_goal_status") in allowed,
                    "elapsedSeconds": round(time.perf_counter() - started, 3)}
            report["cases"].append(item)
            print(json.dumps({"id": item["id"], "passed": item["passed"],
                              "goalStatus": decision.get("singing_goal_status")}, ensure_ascii=False), flush=True)
            args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        report["status"] = "completed"
        report["passed"] = all(item["passed"] for item in report["cases"])
        return report["passed"]
    except Exception as error:
        report["status"] = "failed"
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "report": str(args.out)}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    raise SystemExit(0 if main() else 1)
