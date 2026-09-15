"""Validate six frozen production view-position requests; --execute sends each once.

Reads the original-room integration report, exact serialized requests, original
RGB and local opaque-entity masks. Default mode performs local preparation only.
No retry, service startup, alternative endpoint, Unity action or answer selection.
"""
from __future__ import annotations

import argparse
import base64
import copy
import hashlib
import json
import math
from pathlib import Path
import time
import urllib.error
import urllib.request

from validate_room_task_closure import run_harness, strict_json
from validate_scene_decision_repair import (
    NoRedirect, completion, context, embedded_object, images, reference_dialogue,
)


URL = "http://127.0.0.1:8080/v1/chat/completions"
MAX_BYTES = 5 * 1024 * 1024
GUIDE_HEADER = "\n[Native decision field guide]\n"
CASES = {
    "lamp-farther": ("reposition-view", ["farther"], "see-whole"),
    "lamp-closer": ("reposition-view", ["closer"], "read-detail"),
    "lamp-side": ("reposition-view", ["left", "right"], "avoid-occlusion"),
    "screen-farther": ("reposition-view", ["farther"], "see-whole"),
    "no-move-query": ("none", [], None),
    "stale-goal-repair": ("reposition-view", ["farther"], "see-whole"),
}


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def local_file(directory: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative.strip() or Path(relative).is_absolute():
        raise ValueError("Export must name a relative file inside its fixture directory.")
    result = (directory / relative).resolve()
    if not result.is_relative_to(directory.resolve()) or not result.is_file():
        raise ValueError("Export is missing or escapes its fixture directory: " + relative)
    return result


def read_json(path: Path) -> tuple[bytes, dict]:
    raw = path.read_bytes()
    if len(raw) > MAX_BYTES:
        raise ValueError("Bounded input file exceeded: " + path.name)
    value = strict_json(raw.decode("utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("Expected an object in " + path.name)
    return raw, value


def dynamic_schema(base: dict, source: dict) -> dict:
    """Mirror pure grammar narrowing; runtime C# remains the authoritative parser."""
    source_ids = source.get("decisionContractFacts", {}).get("allowedObservationGoalIds", [])
    if not isinstance(source_ids, list):
        raise ValueError("Fixture must explicitly provide its allowed observation-goal list.")
    ids = []
    for value in source_ids:
        if (isinstance(value, str) and value.strip()
                and len(value.encode("utf-16-le", errors="surrogatepass")) <= 256 and value not in ids):
            ids.append(value)
    result = copy.deepcopy(base)

    def narrow(branch: dict) -> None:
        properties = branch.get("properties", {})
        if "observationGoalId" in properties:
            if ids:
                properties["observationGoalId"]["enum"] = ids
            else:
                del properties["observationGoalId"]
        for child in branch.get("oneOf", []):
            narrow(child)

    narrow(result)
    return result


def schema_errors(value: object, schema: dict, path: str = "$") -> list[str]:
    """Exact supported vocabulary of the closed production decision schema.

    Fail on unfamiliar schema keywords rather than silently skip a new guard.
    Independent raw-schema checks complement (never replace) the compiled C# parser.
    """
    supported = {"type", "additionalProperties", "required", "properties", "enum", "oneOf",
                 "minLength", "maxLength", "minimum", "maximum"}
    if set(schema) - supported:
        raise ValueError("Unsupported production schema vocabulary: " + ",".join(sorted(set(schema) - supported)))
    expected = schema.get("type")
    correct = {"object": isinstance(value, dict), "string": isinstance(value, str),
               "number": type(value) in (int, float) and math.isfinite(value)}
    if expected not in correct or not correct[expected]:
        return [path + ":type"]
    errors = []
    if "enum" in schema and value not in schema["enum"]:
        errors.append(path + ":enum")
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        errors += [path + ":missing:" + key for key in schema.get("required", []) if key not in value]
        if schema.get("additionalProperties") is False:
            errors += [path + ":extra:" + key for key in value if key not in properties]
        for key, item in value.items():
            if key in properties:
                errors += schema_errors(item, properties[key], path + "." + key)
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0) or len(value) > schema.get("maxLength", math.inf):
            errors.append(path + ":length")
    if type(value) in (int, float):
        if value < schema.get("minimum", -math.inf) or value > schema.get("maximum", math.inf):
            errors.append(path + ":range")
    if "oneOf" in schema:
        count = sum(not schema_errors(value, branch, path) for branch in schema["oneOf"])
        if count != 1:
            errors.append(path + f":oneOf-matches:{count}")
    return errors


def verify_wire(wire: dict, source_export: dict, contracts: dict) -> dict:
    messages = wire.get("messages")
    if (not isinstance(messages, list) or len(messages) != 3
            or [item.get("role") for item in messages] != ["system", "user", "user"]):
        raise ValueError("Expected the exact three-message current production room decision wire.")
    source = context(wire)
    if source != source_export:
        raise ValueError("Frozen input export differs from the actual serialized current input.")
    if source.get("autonomous") is not False or not isinstance(source.get("currentUserText"), str):
        raise ValueError("These fixed probes require explicit fixture user requests.")
    schema = dynamic_schema(strict_json(contracts["decisionSchema"]), source)
    guide = copy.deepcopy(schema)
    guide.pop("oneOf")
    expected_prefix = contracts["decisionContract"] + GUIDE_HEADER
    system = messages[0].get("content")
    if (not isinstance(system, str) or not system.startswith(expected_prefix)
            or strict_json(system[len(expected_prefix):]) != guide):
        raise ValueError("Local field guide must be compiled production contract plus dynamic schema minus only top-level oneOf.")
    settings = wire.get("response_format", {})
    if (wire.get("max_tokens") != 640 or wire.get("stream") is not False
            or wire.get("enable_thinking") is not False or wire.get("temperature") != 0
            or settings.get("type") != "json_object" or settings.get("schema") != schema
            or wire.get("chat_template_kwargs", {}).get("enable_thinking") is not False):
        raise ValueError("Request no longer uses the actual 640-token nonthinking production grammar/settings.")
    observation = source.get("visualObservation", {})
    if (observation.get("source") != "character-scene-rgb" or observation.get("state") != "ready"
            or observation.get("hasImage") is not True or not observation.get("captureId")):
        raise ValueError("Only a real ready isolated character-view capture is allowed.")
    picture = images(wire)
    if len(picture) != 1 or not picture[0].startswith(("data:image/jpeg;base64,", "data:image/png;base64,")):
        raise ValueError("Exactly one actual frozen image is required.")
    if not isinstance(messages[2].get("content"), str):
        raise ValueError("Current-turn tail must be text only.")
    tail = embedded_object(messages[2]["content"], "sourceValid")[0]
    if (tail.get("sourceValid") is not True or tail.get("currentUserText") != source["currentUserText"]
            or tail.get("autonomous") is not False or tail.get("referenceDialogue") != reference_dialogue(source)
            or tail.get("referenceDialogueCanAuthorizeAction") is not False
            or not str(tail.get("referenceDialogueSource", "")).startswith("input.recentDialogue")):
        raise ValueError("Current-turn tail or its bounded source quotations do not match the actual input.")
    return source


def verify_mask(directory: Path, metadata: dict, source: dict) -> tuple[dict | None, bytes | None]:
    mask = metadata.get("visibleEntityMask")
    if metadata["expectedIntent"] == "none":
        if mask is not None:
            raise ValueError("Nonmovement fixture must not invent a selected object mask.")
        return None, None
    if not isinstance(mask, dict) or mask.get("encoding") != "row-major-top-left-u8-0-or-1":
        raise ValueError("Movement fixture needs its actual opaque-entity evaluation mask.")
    width, height = mask.get("width"), mask.get("height")
    observation = source["visualObservation"]
    if (type(width) is not int or type(height) is not int or not 3 <= width <= 1280 or not 3 <= height <= 960
            or width != observation.get("width") or height != observation.get("height")
            or mask.get("entityId") != metadata.get("actualEntityId")
            or mask.get("captureId") != observation["captureId"]):
        raise ValueError("Mask dimensions, entity or capture do not match the real frozen image.")
    raw = local_file(directory, mask["file"]).read_bytes()
    if len(raw) != width * height or any(pixel not in (0, 1) for pixel in raw) or not any(raw):
        raise ValueError("Invalid or empty bounded opaque-entity mask.")
    if sum(raw) != mask.get("markedPixels"):
        raise ValueError("Mask marked-pixel count differs from its fixture metadata.")
    sample = metadata.get("visibleSample") or {}
    if (sample.get("captureId") != mask["captureId"] or sample.get("entityId") != mask["entityId"]
            or sample.get("visibleOpaquePixels", 0) < sum(raw)):
        raise ValueError("Mask is inconsistent with the original same-frame visible sample.")
    return mask, raw


def prepare(args) -> tuple[dict, list[dict]]:
    report_path = args.fixture_report.resolve()
    report_raw, fixture = read_json(report_path)
    if fixture.get("status") != "passed" or fixture.get("nativeRequestsEnabled") is not False:
        raise ValueError("Use the completed non-native production integration report.")
    rows = fixture.get("frozenRequests")
    if (not isinstance(rows, list) or len(rows) != 6
            or [row.get("case") for row in rows] != list(CASES)):
        raise ValueError("Expected all six predeclared exports in fixed order, without selecting passing cases.")
    parser = args.parser_directory.resolve()
    if parser.name != "parser" or not (parser / "room-task-probe.exe").is_file():
        raise ValueError("--parser-directory must be the compiled parser/ directory.")
    contracts = run_harness(parser.parent, "contracts")
    fixed = []
    for row in rows:
        name = row["case"]
        if (row.get("expectedIntent"), row.get("allowedDirections"), row.get("expectedPurpose")) != CASES[name]:
            raise ValueError("Fixture expectations differ from the six fixed declarations: " + name)
        path = local_file(report_path.parent, row["request"])
        raw, wire = read_json(path)
        _, source_export = read_json(local_file(report_path.parent, row["input"]))
        source = verify_wire(wire, source_export, contracts)
        picture = base64.b64decode(images(wire)[0].split(",", 1)[1], validate=True)
        if picture != local_file(report_path.parent, row["image"]).read_bytes():
            raise ValueError("Original pixels changed between export and request: " + name)
        if row.get("captureId") != source["visualObservation"]["captureId"]:
            raise ValueError("Metadata capture must be the exact serialized current capture.")
        mask, mask_raw = verify_mask(report_path.parent, row, source)
        if name == "stale-goal-repair":
            repair = source.get("decisionRepair") or {}
            if (repair.get("attempt") != 1 or repair.get("maximumAttempts") != 1
                    or "observation-goal" not in str(repair.get("rejectionReason", ""))
                    or source["decisionContractFacts"]["allowedObservationGoalIds"] != []
                    or "observationGoalId" in wire["response_format"]["schema"]["properties"]):
                raise ValueError("Repair fixture must retain its rejected goal and remove unavailable grammar IDs.")
        elif source.get("decisionRepair"):
            raise ValueError("Only the fixed repair export may contain controlled rejected-decision context.")
        fixed.append({"name": name, "raw": raw, "wire": wire, "source": source,
                      "metadata": row, "mask": mask, "maskRaw": mask_raw,
                      "sourceFile": str(path), "imageSha256": sha(picture)})
    if fixed[3]["metadata"]["actualEntityId"] == fixed[0]["metadata"]["actualEntityId"]:
        raise ValueError("Actual screen transfer case cannot reuse the lamp entity.")
    if args.output.exists() and any(args.output.iterdir()):
        raise FileExistsError("Use an empty output directory; earlier answers are never reused or overwritten.")
    args.output.mkdir(parents=True, exist_ok=True)
    cases = []
    for item in fixed:
        name = item["name"]
        (args.output / (name + ".request.json")).write_bytes(item["raw"])
        if item["maskRaw"] is not None:
            (args.output / (name + ".visible-entity-mask.bin")).write_bytes(item["maskRaw"])
        cases.append({"name": name, "sourceFile": item["sourceFile"], "requestFile": name + ".request.json",
                      "requestSha256": sha(item["raw"]), "imageSha256": item["imageSha256"],
                      "captureId": item["source"]["visualObservation"]["captureId"],
                      "entityId": item["metadata"]["actualEntityId"], "expected": CASES[name],
                      "maskSha256": sha(item["maskRaw"]) if item["maskRaw"] is not None else None})
    manifest = {"status": "prepared", "url": URL, "maximumRequests": 6, "timeoutSeconds": 25,
                "fixtureReport": str(report_path), "fixtureReportSha256": sha(report_raw),
                "parserDirectory": str(parser), "parserSha256": sha((parser / "room-task-probe.exe").read_bytes()),
                "contractSha256": sha(json.dumps(contracts, ensure_ascii=False, sort_keys=True).encode("utf-8")),
                "scope": "Six exact production decision requests with their own real Unity head-view image. Only the existing local Qwen endpoint receives requests; predictions are parsed and compared, never dispatched or spoken. Evaluation expectations/masks are not inserted into requests.",
                "limitations": ["One fixed sample per case, no retries or response-conditioned selection.",
                                "Repair is a frozen controlled rejected-decision context, independent of live answers to other cases.",
                                "Opaque mask verifies historical image-pixel identity only; it cannot establish present visibility, walkability, readable text or full object framing.",
                                "Protocol and model checks are separate from the native runtime regression, not an end-to-end voice interaction."],
                "cases": cases}
    write_json(args.output / "manifest.json", manifest)
    return manifest, fixed


def decision_checks(item: dict, raw_decision: dict, parsed: dict) -> tuple[dict, list[str]]:
    schema = item["wire"]["response_format"]["schema"]
    errors = schema_errors(raw_decision, schema)
    name, source = item["name"], item["source"]
    intent, directions, purpose = CASES[name]
    checks = {"productionParserAccepted": parsed.get("valid") is True,
              "exactDynamicSchemaAccepted": not errors,
              "expectedIntent": raw_decision.get("intent") == intent,
              "noObservationGoalId": "observationGoalId" not in raw_decision}
    if intent == "none":
        checks["noActionFields"] = set(raw_decision) == {"intent", "origin", "evidence"}
        # Production deliberately canonicalizes a valid none/user/evidence triple.
        # Its declared no-action behavior, not extra stylistic normalization, is tested.
        canonical = parsed.get("decision") or {}
        checks["canonicalNoMove"] = (canonical.get("intent") == "none" and canonical.get("origin") == "none"
                                     and canonical.get("evidence") == "")
        return checks, errors
    view = raw_decision.get("viewPosition") or {}
    checks["semanticDirection"] = view.get("direction") in directions
    checks["semanticPurpose"] = view.get("purpose") == purpose
    checks["currentUserEvidence"] = (raw_decision.get("origin") == "user"
        and isinstance(raw_decision.get("evidence"), str) and bool(raw_decision["evidence"].strip())
        and raw_decision["evidence"] in source["currentUserText"])
    entity = raw_decision.get("entityTarget")
    pixel = raw_decision.get("visualTarget")
    capture = source["visualObservation"]["captureId"]
    if isinstance(entity, dict) and pixel is None:
        checks["exactCurrentCapture"] = entity.get("captureId") == capture
        checks["exactExpectedEntity"] = entity.get("entityId") == item["metadata"]["actualEntityId"]
        checks["entityWasKnownAndVisible"] = any(value.get("entityId") == entity.get("entityId") and value.get("captureId") == capture
            for value in source.get("visibleKnownObjects", []) if isinstance(value, dict))
    elif isinstance(pixel, dict) and entity is None:
        checks["exactCurrentCapture"] = pixel.get("captureId") == capture
        x, y = pixel.get("x"), pixel.get("y")
        valid = type(x) in (int, float) and type(y) in (int, float) and math.isfinite(x) and math.isfinite(y) and 0 <= x <= 1 and 0 <= y <= 1
        if valid:
            mask = item["mask"]
            index = min(math.floor(y * mask["height"]), mask["height"]-1) * mask["width"] + min(math.floor(x * mask["width"]), mask["width"]-1)
            valid = item["maskRaw"][index] == 1
        checks["pixelBelongsToExpectedOpaqueEntity"] = valid
    else:
        checks["oneActualTargetReference"] = False
    return checks, errors


def run(args) -> dict:
    manifest, fixed = prepare(args)
    report = {"status": "prepared", "networkRequests": 0, "scope": manifest["scope"], "cases": [],
              "manifestSha256": sha((args.output / "manifest.json").read_bytes())}
    write_json(args.output / "report.json", report)
    if not args.execute:
        return report
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    parser = Path(manifest["parserDirectory"])
    # All six immutable requests are prepared and recorded before the first send.
    for item in fixed:
        row = {"name": item["name"], "httpStatus": None, "checks": {}}
        started = time.monotonic()
        try:
            if sha((parser / "room-task-probe.exe").read_bytes()) != manifest["parserSha256"]:
                raise RuntimeError("Compiled parser changed after preparation; no request sent for this case.")
            report["networkRequests"] += 1
            request = urllib.request.Request(URL, data=item["raw"], method="POST", headers={"Content-Type": "application/json"})
            try:
                response = opener.open(request, timeout=25)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                row["httpStatus"] = response.code
                raw = response.read(MAX_BYTES + 1)
            row["elapsedSeconds"] = time.monotonic() - started
            if len(raw) > MAX_BYTES:
                raise ValueError("Response exceeded bounded size; response is not parsed.")
            text, envelope = completion(raw.decode("utf-8-sig"))
            row.update(envelope)
            # Persist public output and bounded timing/usage only; no private reasoning fields.
            write_json(args.output / (item["name"] + ".response.json"), {**envelope, "publicContent": text})
            if row["httpStatus"] != 200 or text is None:
                row["checks"]["completeHttpResponse"] = False
            else:
                decision_file = (args.output / (item["name"] + ".decision.json")).resolve()
                user_file = (args.output / (item["name"] + ".user.txt")).resolve()
                decision_file.write_text(text, encoding="utf-8")
                user_file.write_text(item["source"]["currentUserText"], encoding="utf-8")
                parsed = run_harness(parser.parent, "decision", str(decision_file), str(user_file), "false")
                write_json(args.output / (item["name"] + ".production-parser.json"), parsed)
                public = strict_json(text)
                if not isinstance(public, dict):
                    raise ValueError("Decision output must be an object.")
                checks, errors = decision_checks(item, public, parsed)
                row["checks"] = {"completeHttpResponse": True, **checks}
                row["schemaErrors"] = errors
                row["productionParser"] = parsed
            row["status"] = "passed" if row["checks"] and all(row["checks"].values()) else "failed"
        except Exception as error:
            row["status"] = "failed"
            row["error"] = type(error).__name__ + ": " + str(error)[:320]
        row.setdefault("elapsedSeconds", time.monotonic() - started)
        report["cases"].append(row)
        report["status"] = "running"
        write_json(args.output / "report.json", report)
    report["status"] = "passed" if len(report["cases"]) == 6 and all(row["status"] == "passed" for row in report["cases"]) else "failed"
    report["passedCases"] = sum(row["status"] == "passed" for row in report["cases"])
    write_json(args.output / "report.json", report)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-report", type=Path, required=True)
    parser.add_argument("--parser-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    report = run(args)
    print(json.dumps({"status": report["status"], "networkRequests": report["networkRequests"], "output": str(args.output)}, ensure_ascii=True))
    return 0 if report["status"] in ("prepared", "passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
