"""Replay production review wires; no character actions, TTS or device writes."""
import argparse
import json
from pathlib import Path
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("cases", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="qwen36")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    results = []
    for case in json.loads(args.cases.read_text(encoding="utf-8-sig")):
        request = case["request"]
        request["model"] = args.model
        started = time.monotonic()
        try:
            body = json.dumps(request, ensure_ascii=False).encode("utf-8")
            with urllib.request.urlopen(urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", body,
                    {"Content-Type": "application/json"}), timeout=55) as response:
                envelope = json.load(response)
            choice = envelope["choices"][0]
            decision = json.loads(choice["message"]["content"])
            passed = choice["finish_reason"] == "stop" and decision.get("work_status") == case["expectedStatus"]
            if decision.get("proceed") and case["expectedStatus"] == "closed":
                passed &= decision.get("next_intent_source") == "autonomous" and decision.get("next_step_id") == ""
            if case["expectedStatus"] == "continue":
                passed &= decision.get("next_intent_source") == "current-work" and decision.get("next_step_id") == ""
            if case["expectedStatus"] == "waiting_tool":
                passed &= not decision.get("proceed")
            if case["name"] == "completed-return-stay-restriction" and decision.get("proceed"):
                passed &= decision.get("planning_focus") != "current-user-body"
            row = dict(name=case["name"], passed=bool(passed), seconds=round(time.monotonic()-started, 3),
                       decision=decision, usage=envelope.get("usage"))
        except Exception as error:
            row = dict(name=case["name"], passed=False, error=str(error))
        results.append(row)
        print(f'{row["name"]}: passed={row["passed"]}', flush=True)
        (args.output / "report.json").write_text(json.dumps(dict(passed=all(r["passed"] for r in results),
            scope="Production serialized prompts with controlled task/execution evidence. Real model; no actual tools or character actions.",
            results=results), ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if all(row["passed"] for row in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
