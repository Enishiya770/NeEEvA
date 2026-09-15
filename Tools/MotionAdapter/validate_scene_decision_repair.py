"""Prepare four fixed scene decisions; --execute sends at most four local Qwen requests.

Uses production wires exported by the isolated Unity fixture, including its real
character-view image. It does not drive Unity, execute decisions, retry failures,
start another model, or select cases based on model answers.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import time
import urllib.error
import urllib.request

from validate_room_task_closure import run_harness, strict_json


URL = "http://127.0.0.1:8080/v1/chat/completions"
MAX_BYTES = 5 * 1024 * 1024
REPAIR_REASON = "observation-goal-reference-not-pending"
PREFERRED_USER = "请你靠近他。"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def read_wire(path: Path) -> tuple[bytes, dict]:
    raw = path.read_bytes()
    if len(raw) > MAX_BYTES:
        raise ValueError("Fixture request exceeds the bounded file size: " + path.name)
    value = strict_json(raw.decode("utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("Expected a production request object: " + path.name)
    return raw, value


def embedded_object(text: str, required: str) -> tuple[dict, int, int]:
    """Locate the quoted JSON after the production image provenance preamble."""
    decoder = json.JSONDecoder()
    for match in re.finditer(r"\{", text):
        try:
            _, length = decoder.raw_decode(text[match.start():])
            value = strict_json(text[match.start():match.start() + length])
        except (TypeError, ValueError):
            continue
        if isinstance(value, dict) and required in value:
            return value, match.start(), match.start() + length
    raise ValueError("Production message has no unambiguous " + required + " object.")


def context_text(wire: dict) -> tuple[str, int | None]:
    content = wire["messages"][1]["content"]
    if isinstance(content, str):
        return content, None
    text_parts = [(index, part["text"]) for index, part in enumerate(content)
                  if part.get("type") == "text" and isinstance(part.get("text"), str)]
    if len(text_parts) != 1:
        raise ValueError("Expected one production context text beside the current image.")
    return text_parts[0][1], text_parts[0][0]


def context(wire: dict) -> dict:
    return embedded_object(context_text(wire)[0], "currentUserText")[0]


def images(wire: dict) -> list[str]:
    return [part["image_url"]["url"] for msg in wire["messages"] if isinstance(msg.get("content"), list)
            for part in msg["content"] if part.get("type") == "image_url"]


def reference_dialogue(source: dict) -> list[dict]:
    """Mirror only the production bounded quotation format, never infer a referent."""
    if not isinstance(source.get("currentUserText"), str) or type(source.get("autonomous")) is not bool:
        return []
    history = source.get("recentDialogue")
    if not isinstance(history, list):
        return []
    result = []
    for message in history:
        if (not isinstance(message, dict) or message.get("role") not in ("user", "assistant")
                or not isinstance(message.get("quotedText"), str)):
            continue
        text = message["quotedText"]
        units = text.encode("utf-16-le", errors="surrogatepass")
        if len(units) > 800:
            start = len(units) - 800
            first, previous = int.from_bytes(units[start:start + 2], "little"), int.from_bytes(units[start - 2:start], "little")
            if 0xDC00 <= first <= 0xDFFF and 0xD800 <= previous <= 0xDBFF:
                start += 2
            text = units[start:].decode("utf-16-le", errors="surrogatepass")
        result.append({"role": message["role"], "quotedText": text})
    return result[-4:]


def verify_wire(wire: dict, contracts: dict) -> dict:
    messages = wire.get("messages")
    if not isinstance(messages, list) or len(messages) != 3 or [m.get("role") for m in messages] != ["system", "user", "user"]:
        raise ValueError("Use this batch's three-message production decision wire.")
    expected_system = contracts["decisionContract"] + "\n" + contracts["decisionSchema"]
    if messages[0].get("content") != expected_system:
        raise ValueError("Wire system contract differs from the supplied compiled production parser.")
    if (wire.get("max_tokens") != 640 or wire.get("stream") is not False or wire.get("enable_thinking") is not False
            or wire.get("temperature") != 0 or wire.get("response_format", {}).get("schema") != strict_json(contracts["decisionSchema"])):
        raise ValueError("Use the unchanged 640-token, nonthinking, local production decision schema.")
    image = images(wire)
    if len(image) != 1 or not image[0].startswith(("data:image/png;base64,", "data:image/jpeg;base64,")):
        raise ValueError("Exactly one embedded authorized isolated-scene image is required.")
    source = context(wire)
    observation = source.get("visualObservation") or {}
    if (observation.get("source") != "character-scene-rgb" or observation.get("state") != "ready"
            or not observation.get("captureId") or observation.get("hasImage") is not True):
        raise ValueError("Fixture is not an actual ready character-scene observation.")
    if not isinstance(messages[2].get("content"), str):
        raise ValueError("The trailing current-turn message must contain no second image.")
    tail = embedded_object(messages[2]["content"], "sourceValid")[0]
    if (tail.get("sourceValid") is not True or tail.get("autonomous") != source.get("autonomous")
            or tail.get("currentUserText") != ("" if source.get("autonomous") is True else source.get("currentUserText"))):
        raise ValueError("Production current-turn suffix and structured current input disagree.")
    if (tail.get("referenceDialogue") != reference_dialogue(source)
            or tail.get("referenceDialogueCanAuthorizeAction") is not False
            or not str(tail.get("referenceDialogueSource", "")).startswith("input.recentDialogue")):
        raise ValueError("Trailing reference-only quotes do not match the exact bounded source dialogue.")
    return source


def select_pair(directory: Path, contracts: dict) -> tuple[tuple, tuple, bool]:
    items = []
    for path in sorted(directory.glob("wire-*-room.json"), key=lambda p: int(p.name.split("-")[1])):
        raw, wire = read_wire(path)
        if len(wire.get("messages", [])) != 3 or wire.get("max_tokens") != 640:
            continue
        # The guard suite also exports text-only negative controls. Select by the
        # declared case input first, then fully verify the chosen image pair.
        try:
            source = context(wire)
        except ValueError:
            continue
        items.append((path, raw, wire, source))
    pairs = []
    for index, item in enumerate(items):
        repair = item[3].get("decisionRepair") or {}
        if repair.get("rejectionReason") != REPAIR_REASON:
            continue
        candidates = [old for old in items[:index] if not old[3].get("decisionRepair")
                      and old[3].get("currentUserText") == item[3].get("currentUserText")
                      and old[3].get("decisionContractFacts", {}).get("currentUserTurn") is not None
                      and old[3].get("decisionContractFacts", {}).get("currentUserTurn") == item[3].get("decisionContractFacts", {}).get("currentUserTurn")]
        if candidates:
            pairs.append((candidates[-1], item))
    if not pairs:
        raise ValueError("No exported initial/repair pair for the declared observation-goal rejection exists yet.")
    preferred = [pair for pair in pairs if pair[0][3].get("currentUserText") == PREFERRED_USER]
    initial, repair = (preferred or pairs)[0]
    verify_wire(initial[2], contracts)
    verify_wire(repair[2], contracts)
    if initial[3].get("autonomous") is not False:
        raise ValueError("The fixed approach case must be a current user request.")
    authored = [item for item in initial[3].get("visibleKnownObjects", [])
                if item.get("identitySource") == "scene-authored"
                and item.get("captureId") == initial[3]["visualObservation"]["captureId"]]
    if not authored:
        raise ValueError("The isolated fixture must contain a previously observed, currently visible authored object.")
    # Production re-observes before a repair; retain its distinct real image and
    # capture identity, rather than replacing it with the earlier snapshot.
    if initial[3]["visualObservation"].get("loadedSceneFingerprint") != repair[3]["visualObservation"].get("loadedSceneFingerprint"):
        raise ValueError("Initial and repair are not from the same isolated scene binding.")
    return initial, repair, bool(preferred)


def derived_case(wire: dict, user_text: str) -> dict:
    result = copy.deepcopy(wire)
    original, part_index = context_text(result)
    source, start, end = embedded_object(original, "currentUserText")
    source["currentUserText"] = user_text
    source["autonomous"] = False
    # These two independent controls do not continue the lamp conversation or a
    # pending observation goal. Actual engine observations and the image remain.
    source["recentDialogue"] = []
    source["objectTargetReview"] = None
    source["pendingObservationGoal"] = None
    source["autonomousIntention"] = ""
    source["autonomousContext"] = ""
    source.pop("decisionRepair", None)
    replacement = original[:start] + json.dumps(source, ensure_ascii=False, separators=(",", ":")) + original[end:]
    if part_index is None:
        result["messages"][1]["content"] = replacement
    else:
        result["messages"][1]["content"][part_index]["text"] = replacement
    tail = result["messages"][2]["content"]
    data, start, end = embedded_object(tail, "sourceValid")
    data.update(sourceValid=True, autonomous=False, currentUserText=user_text,
                referenceDialogue=reference_dialogue(source), referenceDialogueCanAuthorizeAction=False)
    result["messages"][2]["content"] = tail[:start] + json.dumps(data, ensure_ascii=False, separators=(",", ":")) + tail[end:]
    return result


def prepare(args) -> tuple[dict, list[dict]]:
    parser = args.parser_directory.resolve()
    if parser.name != "parser" or not (parser / "room-task-probe.exe").is_file():
        raise ValueError("--parser-directory must be the compiled parser/ directory, not its parent.")
    contracts = run_harness(parser.parent, "contracts")
    initial, repair, preferred = select_pair(args.wire_directory, contracts)
    if args.output.exists() and any(args.output.iterdir()):
        raise FileExistsError("Use an empty output directory; previous answers are never reused.")
    args.output.mkdir(parents=True, exist_ok=True)
    fixed = [
        {"name": "initial-object-approach", "raw": initial[1], "wire": initial[2], "sourceFile": str(initial[0]),
         "changes": "None; original production request file bytes.", "expected": {"intent": "approach-object", "noObservationGoalId": True}},
        {"name": "repair-object-approach", "raw": repair[1], "wire": repair[2], "sourceFile": str(repair[0]),
         "changes": "None; fixed production repair input, independent of the live initial answer.", "expected": {"intent": "approach-object", "noObservationGoalId": True}},
    ]
    for name, text, expectation in [
        ("turn-around", "转身看看身后。", {"intent": "turn-in-place", "absoluteYaw": 180, "pitch": 0, "noObservationGoalId": True}),
        ("explain-without-movement", "刚才为什么没通过？只解释，先不要移动。", {"allowedIntents": ["none", "inspect"], "noObservationGoalId": True}),
    ]:
        wire = derived_case(initial[2], text)
        raw = (json.dumps(wire, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        fixed.append({"name": name, "raw": raw, "wire": wire, "sourceFile": str(initial[0]), "expected": expectation,
                      "changes": "Only currentUserText in both production message locations, autonomous=false, cleared recentDialogue and its trailing referenceDialogue, objectTargetReview/pendingObservationGoal and autonomous intention/context. Actual engine observations, the one image, system contract, schema and generation parameters retained."})
    cases = []
    for case in fixed:
        source = verify_wire(case["wire"], contracts)
        if images(case["wire"]) != images(read_wire(Path(case["sourceFile"]))[1]):
            raise ValueError("Each request must retain its own exact authorized fixture pixels.")
        request_file = case["name"] + ".request.json"
        (args.output / request_file).write_bytes(case["raw"])
        cases.append({key: value for key, value in case.items() if key not in ("raw", "wire")} | {
            "requestFile": request_file, "requestSha256": sha(case["raw"]),
            "currentUserText": source["currentUserText"], "autonomous": source["autonomous"],
            "captureId": source["visualObservation"]["captureId"], "imageSha256": sha(images(case["wire"])[0].encode("ascii")),
            "sourceFileSha256": sha(Path(case["sourceFile"]).read_bytes())})
    manifest = {"status": "prepared", "url": URL, "maximumRequests": 4, "timeoutSeconds": 20,
                "selection": {"rejectionReason": REPAIR_REASON, "preferredCurrentUserText": PREFERRED_USER,
                              "preferredFound": preferred, "rule": "First numeric-wire-order matching pair; exact preferred text first if present. No model-output filtering."},
                "parserDirectory": str(parser), "parserSha256": sha((parser / "room-task-probe.exe").read_bytes()),
                "contractSha256": sha(json.dumps(contracts, ensure_ascii=False, sort_keys=True).encode("utf-8")),
                "scope": "Four frozen decision-only probes from the authorized isolated Unity character-view fixture. Predictions are recorded and parsed, never dispatched to Unity, spoken, or sent to a device. Expectations are only in this manifest, never added to the model wire.",
                "limitations": ["The repair wire contains a controlled prior rejection and is fixed before the live initial answer; it does not claim a live failed-initial/repaired-end-to-end sequence.",
                                "Production C# parsing proves protocol validity, not semantic grounding, image freshness in a running session, navigation or physical execution.",
                                "One sample per declared case, no failure retries, no altered answer selection, no deliberate extra preflight model requests."],
                "cases": cases}
    write_json(args.output / "manifest.json", manifest)
    return manifest, fixed


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def completion(envelope: str) -> tuple[str | None, dict]:
    metadata = {"envelopeAccepted": False}
    try:
        body = strict_json(envelope)
        choices = body.get("choices") if isinstance(body, dict) else None
        if not isinstance(choices, list) or len(choices) != 1 or not isinstance(choices[0], dict):
            return None, metadata | {"envelopeError": "invalid-choices"}
        choice = choices[0]
        metadata["finishReason"] = choice.get("finish_reason")
        usage = body.get("usage")
        metadata["usage"] = {key: value for key, value in usage.items() if key in ("prompt_tokens", "completion_tokens", "total_tokens")
                             and type(value) is int and value >= 0} if isinstance(usage, dict) else None
        message = choice.get("message")
        if choice.get("finish_reason") != "stop":
            return None, metadata | {"envelopeError": "incomplete-output-not-parsed"}
        if (not isinstance(message, dict) or message.get("role") != "assistant" or not isinstance(message.get("content"), str)
                or not message["content"].strip() or message.get("tool_calls") is not None
                or message.get("function_call") is not None or message.get("refusal") not in (None, "")):
            return None, metadata | {"envelopeError": "invalid-assistant-content"}
        metadata["envelopeAccepted"] = True
        return message["content"], metadata
    except (ValueError, TypeError, AttributeError) as error:
        return None, metadata | {"envelopeError": type(error).__name__}


def expectations(case: dict, result: dict) -> dict:
    decision = result.get("decision") or {}
    expected = case["expected"]
    checks = {"productionParserAccepted": result.get("valid") is True}
    if "intent" in expected:
        checks["expectedIntent"] = decision.get("intent") == expected["intent"]
    if "allowedIntents" in expected:
        checks["noMovementRequested"] = decision.get("intent") in expected["allowedIntents"]
    if expected.get("noObservationGoalId"):
        checks["noInventedObservationGoalId"] = not decision.get("observationGoalId")
    if "absoluteYaw" in expected:
        view = decision.get("viewChange") or {}
        yaw, pitch = view.get("yaw"), view.get("pitch")
        checks["actualHalfTurn"] = type(yaw) in (int, float) and abs(abs(yaw) - expected["absoluteYaw"]) <= 0.001
        checks["noHeadPitch"] = type(pitch) in (int, float) and abs(pitch - expected["pitch"]) <= 0.001
        checks["sameCaptureReference"] = view.get("captureId") == case["captureId"]
    return checks


def run(args) -> dict:
    manifest, fixed = prepare(args)
    report = {"status": "prepared", "networkRequests": 0, "scope": manifest["scope"], "cases": [],
              "manifestSha256": sha((args.output / "manifest.json").read_bytes())}
    write_json(args.output / "report.json", report)
    if not args.execute:
        return report
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    parser = Path(manifest["parserDirectory"])
    for case, prepared in zip(manifest["cases"], fixed):
        record = {"name": case["name"], "httpStatus": None, "checks": {}}
        started = time.monotonic()
        try:
            if sha((parser / "room-task-probe.exe").read_bytes()) != manifest["parserSha256"]:
                raise RuntimeError("Compiled production parser changed after preparation.")
            report["networkRequests"] += 1
            request = urllib.request.Request(URL, data=prepared["raw"], headers={"Content-Type": "application/json"}, method="POST")
            try:
                response = opener.open(request, timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                record["httpStatus"] = response.code
                raw = response.read(MAX_BYTES + 1)
            record["elapsedSeconds"] = time.monotonic() - started
            (args.output / (case["name"] + ".response.txt")).write_bytes(raw)
            if len(raw) > MAX_BYTES:
                raise ValueError("Response exceeded bounded storage size; saved prefix is not parsed.")
            text, envelope = completion(raw.decode("utf-8-sig"))
            record.update(envelope)
            if record["httpStatus"] != 200 or text is None:
                record["checks"]["completeHttpResponse"] = False
            else:
                public = (args.output / (case["name"] + ".decision.json")).resolve()
                user = (args.output / (case["name"] + ".user.txt")).resolve()
                public.write_text(text, encoding="utf-8")
                user.write_text(case["currentUserText"], encoding="utf-8")
                result = run_harness(parser.parent, "decision", str(public), str(user), "true" if case["autonomous"] else "false")
                write_json(args.output / (case["name"] + ".production-parser.json"), result)
                record["productionParser"] = result
                record["checks"] = {"completeHttpResponse": True, **expectations(case, result)}
            record["status"] = "passed" if record["checks"] and all(record["checks"].values()) else "failed"
        except Exception as error:
            record["status"] = "failed"
            record["error"] = type(error).__name__ + ": " + str(error)[:320]
        record.setdefault("elapsedSeconds", time.monotonic() - started)
        report["cases"].append(record)
        report["status"] = "running"
        write_json(args.output / "report.json", report)
    report["status"] = "passed" if len(report["cases"]) == 4 and all(item["status"] == "passed" for item in report["cases"]) else "failed"
    write_json(args.output / "report.json", report)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wire-directory", type=Path, required=True)
    parser.add_argument("--parser-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    report = run(args)
    print(json.dumps({"status": report["status"], "networkRequests": report["networkRequests"], "output": str(args.output)}, ensure_ascii=True))
    return 0 if report["status"] in ("prepared", "passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
