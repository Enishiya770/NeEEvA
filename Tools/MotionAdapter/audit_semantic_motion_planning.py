"""Read-only supplemental source/tempo audit. Never rewrites a frozen planning report or scores model motion."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from validate_dialogue_motion import ROOT

# Authored after the first development run to expose omitted source coverage.
# These checks supplement, rather than replace, its frozen exact-parameter checks.
SOURCE_REQUIREMENTS = {
    "en-head-gentle-twice": [["joint"], ["axis"], ["tempo"], ["size"], ["repeat", "cycles"]],
    "zh-forward-greeting": [["left"], ["right"]],
    "ja-side-display": [["left"], ["leftPalm"], ["mode"]],
    "en-straight-down-hold": [["left"], ["right"], ["leftPalm"], ["rightPalm"], ["leftBend"], ["rightBend"], ["mode"]],
    "zh-retime-inherited-pose": [["left"], ["right"], ["leftPalm"], ["rightPalm"], ["joint"], ["tempo"], ["repeat", "cycles"]],
    "en-change-numeric-count": [["right"], ["rightPalm"], ["repeat", "cycles"]],
    "ja-resize-numeric-constraint": [["right"], ["rightPalm"], ["size"]],
    "zh-measured-current-other-arm": [["left"], ["right"], ["mode"]],
    "en-static-both-palms": [["left"], ["right"], ["leftPalm"], ["rightPalm"], ["mode"]],
    "zh-down-palm-farewell": [["left"], ["right"], ["leftPalm"], ["rightPalm"], ["joint"], ["size"]],
    "en-deliberate-head-disagree": [["joint"], ["axis"], ["tempo"], ["size"], ["repeat", "cycles"]],
    "zh-exact-duration": [["right"], ["rightPalm"], ["joint"], ["seconds"]],
    "ja-replace-goal-with-evidence": [["left"], ["right"], ["mode"]],
    "en-palm-goal-no-angle-invention": [["right"], ["rightPalm"]],
    # Frozen before the only held-out model run; do not tune these from its outputs.
    "held-zh-welcome-slow": [["left"], ["right"], ["leftPalm"], ["rightPalm"], ["joint"], ["tempo"]],
    "held-ja-front-still": [["left"], ["right"], ["leftPalm"], ["rightPalm"], ["mode"]],
    "held-en-side-right-display": [["right"], ["rightPalm"], ["mode"]],
    "held-zh-head-two-small": [["joint"], ["axis"], ["tempo"], ["size"], ["repeat", "cycles"]],
}


def audit_row(row: dict, case: dict) -> dict:
    parsed = row["csharp"]
    raw = parsed.get("actionPlan") or {}
    effective = parsed.get("effective") or raw
    locked = {field for field in effective.get("lockFields", "").split(",") if field}
    source_verified = parsed.get("ledgerValidated") is True
    required = SOURCE_REQUIREMENTS.get(row["caseId"], [])
    coverage = [{"oneOfFields": alternatives, "status": "verified_lock" if source_verified and any(field in locked for field in alternatives)
                 else "missing_or_unverified", "presentLocks": [field for field in alternatives if field in locked]}
                for alternatives in required]
    numeric = []
    for field in ("leftBend", "rightBend", "amplitude", "cycles", "seconds"):
        if raw.get(field):
            numeric.append({"field": field, "value": raw[field], "sourcedNumericVerified": source_verified and field in locked})
    tempo_check = None
    if row["caseId"] == "zh-forward-greeting":
        actual = effective.get("tempo") or "natural"
        tempo_check = {"actual": actual, "permittedForThisNaturalGreeting": ["natural", "brisk"],
                       "passed": actual in {"natural", "brisk"},
                       "reason": "Natural interaction did not ask for deliberate slow motion; gentle preserves the reported slow-wave issue."}
    timing_conflict = row["caseId"] == "en-deliberate-head-disagree"
    if required:
        source_passed = all(item["status"] == "verified_lock" for item in coverage)
    else:
        source_passed = None
    equivalent_notes = []
    if case.get("noNumericDefaults") and raw.get("cycles"):
        desired = {"once": "1", "twice": "2", "thrice": "3"}.get(case.get("expected", {}).get("repeat"))
        if raw.get("cycles") == desired and source_verified and "cycles" in locked:
            equivalent_notes.append("Explicit sourced cycles is a legitimate representation of the user's stated count; the frozen noNumericDefaults/exact-repeat mismatch alone does not establish a semantic error.")
    tag_attrs = None
    if len(row.get("motionTags", [])) == 1:
        try:
            tag_attrs = ET.fromstring(row["motionTags"][0]).attrib
        except ET.ParseError:
            pass
    if not row.get("motionTags"):
        response = row.get("rawResponse", "").lstrip()
        kind = "json_instead_of_xml" if response.startswith("{") else "function_instead_of_xml" if response.startswith("motion(") else "no_action_tag"
    elif tag_attrs is None or any("single trailing" in value for value in row.get("shapeErrors", [])):
        kind = "malformed_xml"
    elif tag_attrs.get("name") in {"left-wave", "right-wave", "nod", "shake-head", "none"} and set(tag_attrs) != {"name"}:
        kind = "basic_name_with_plan_attributes"
    elif not parsed.get("accepted"):
        kind = "plan_metadata_or_shape_rejected"
    elif parsed.get("compileError"):
        kind = "ledger_or_compiler_rejected"
    elif tag_attrs.get("name") in {"left-wave", "right-wave", "nod", "shake-head", "none"}:
        kind = "accepted_basic_action"
    else:
        kind = "compiled_plan"
    return {"caseId": row["caseId"], "frozenPlanningChecksPassed": row["planningChecksPassed"], "outputCategory": kind,
            "sourceAuditApplicable": bool(required), "requiredConstraintCoverage": coverage, "sourceAuditPassed": source_passed,
            "numericSources": numeric, "tempoAudit": tempo_check, "equivalentRepresentationNotes": equivalent_notes,
            "capabilityConflict": {"kind": "gentle_head_three_cycles_exceeds_six_seconds", "preferredSeconds": 3 / 0.45,
                "maximumSeconds": 6, "requiredTreatment": "Explain or report timing refusal; never silently accelerate, reduce count or claim success."} if timing_conflict else None,
            "motionSuccess": None,
            "interpretation": "A matched pose without sourced locks does not establish cross-turn constraint retention. Valid quotes verify provenance text, not semantic entailment."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-v1.json")
    parser.add_argument("--output", type=Path, default=ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-v1-source-audit.json")
    args = parser.parse_args()
    original = args.report.read_bytes()
    value = json.loads(original)
    cases = {case["id"]: case for case in value["cases"]}
    rows = [audit_row(row, cases[row["caseId"]]) for row in value["results"]]
    categories = {}
    for row in rows:
        categories[row["outputCategory"]] = categories.get(row["outputCategory"], 0) + 1
    audit = {"schema": 1, "sourceReport": str(args.report.resolve()), "sourceReportSha256": hashlib.sha256(original).hexdigest(),
             "policyTiming": "Development coverage checks were added transparently after v1. The four applicable held-out coverage policies were frozen before its sole model run; no model outputs were used to tune them.",
             "sourceRequirements": SOURCE_REQUIREMENTS, "results": rows, "outputCategories": categories,
             "sourceAuditsPassed": sum(row["sourceAuditPassed"] is True for row in rows),
             "sourceAuditsApplicable": sum(row["sourceAuditApplicable"] for row in rows),
             "limits": "Frozen geometry checks are retained verbatim. Source coverage and tempo are separate. Equivalent sourced count representations and the impossible gentle-three-head-cycle target are disclosed separately. No new model request or motion execution."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(audit, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    assert args.report.read_bytes() == original, "Frozen source report was altered"
    print(json.dumps({"output": str(args.output), "sourceAuditsPassed": audit["sourceAuditsPassed"],
                      "sourceAuditsApplicable": audit["sourceAuditsApplicable"], "categories": categories}))


if __name__ == "__main__":
    main()
