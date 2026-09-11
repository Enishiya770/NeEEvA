"""Run exported public formal requests, including the actual parameter-repair turn.

Only transport options change (non-streaming for capture); messages are exactly the
production Unity export. No private role, microphone, renderer or tools execute.
"""
import argparse
import copy
import hashlib
import json
import re
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    args = parser.parse_args()
    with open(args.report, "rb") as handle:
        source_bytes = handle.read()
    source = json.loads(source_bytes.decode("utf-8-sig"))
    selected = [
        ("complete_speech_song_survives_wrong_partial", "heard_without_request"),
        ("approved_prose_cannot_override_a_structural_goal_mismatch", "repair_actual_goal"),
    ]
    report = {"source": args.report, "sourceSha256": hashlib.sha256(source_bytes).hexdigest(),
              "scope": "Unchanged exported public formal messages; live model text/actions only. No execution, speaker or microphone claim.",
              "cases": [], "passed": False}
    try:
        for scenario_name, case_id in selected:
            scenario = next(s for s in source["scenarios"] if s["name"] == scenario_name)
            original = scenario["finalRequests"][0]
            request = copy.deepcopy(original)
            request.update(stream=False, temperature=0.2, max_tokens=1024)
            request.pop("stream_options", None)
            with urllib.request.urlopen(args.url + "/slots", timeout=5) as response:
                if any(s.get("is_processing") for s in json.load(response)):
                    raise RuntimeError("Model busy; no request submitted")
            started = time.monotonic()
            wire = urllib.request.Request(args.url + "/v1/chat/completions",
                                          json.dumps(request).encode(), {"Content-Type": "application/json"})
            with urllib.request.urlopen(wire, timeout=55) as response:
                choice = json.load(response)["choices"][0]
            content = choice["message"].get("content") or ""
            tags = re.findall(r"<sing_goal\b([^>]*)/>", content, re.I)
            proposals = [dict(re.findall(r'(\w+)\s*=\s*"([^"]*)"', tag)) for tag in tags]
            if case_id == "heard_without_request":
                passed = bool(content.strip()) and not re.search(r"<(?:sing|hum_back|song_sing)\b", content, re.I)
                passed &= all(p.get("intent") == "observe" for p in proposals)
                passed &= any(word in content for word in ("青い空", "静かな夜", "歌", "唱"))
                expected = {"role_performance_requested": False, "lyrics": ["青い空", "静かな夜"], "melody": "A3-B3-C4"}
            else:
                match = re.search(r"expected_refs=(clip:[\w-]+) expected_range=expanded", json.dumps(original, ensure_ascii=False))
                if match is None:
                    raise RuntimeError("Export omitted the independently reviewed target")
                expected = {"refs": match.group(1), "range": "expanded"}
                passed = any(p.get("refs") == expected["refs"] and p.get("range") == "expanded" for p in proposals)
            passed = bool(passed and choice.get("finish_reason") == "stop")
            report["cases"].append({"id": case_id, "passed": passed, "expected": expected,
                                    "sourceScenario": scenario_name, "response": content,
                                    "finishReason": choice.get("finish_reason"), "proposals": proposals,
                                    "elapsedSeconds": round(time.monotonic() - started, 3)})
            print(json.dumps({"id": case_id, "passed": passed}, ensure_ascii=False), flush=True)
        report["passed"] = all(c["passed"] for c in report["cases"])
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        with open(args.out, "w", encoding="utf-8") as handle:
            json.dump(report, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
    return report["passed"]


if __name__ == "__main__":
    raise SystemExit(0 if main() else 1)
