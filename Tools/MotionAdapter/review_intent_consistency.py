"""Bind an explicit human/agent review to one immutable public probe report.

No model inference and no automatic text-entailment score. Every judgment requires
a reason and source excerpt; real avatar outcome remains unavailable for these probes.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from validate_intent_consistency import exclusive_json, sha, utc


LABELS = {"pass", "fail", "unassessed"}


def combine(report: dict, review: dict, report_bytes: bytes) -> dict:
    if review.get("sourceReportSha256") != sha(report_bytes):
        raise ValueError("Review must reference the exact immutable report SHA256")
    results = {row["caseId"]: row for row in report["results"]}
    labels = review["results"]
    if len(labels) != len(results) or {row["caseId"] for row in labels} != set(results):
        raise ValueError("Every executed case needs exactly one review, including failures")
    rows = []
    for label in labels:
        source = results[label["caseId"]]
        for field in ("intentMatches", "narrationGrounded"):
            if label.get(field) not in LABELS:
                raise ValueError("Explicit review label missing: " + field)
        if not label.get("reason", "").strip():
            raise ValueError("Each review needs a concrete reason")
        excerpts = label.get("publicEvidenceExcerpts", [])
        visible = source["csharp"]["executableChannel"]
        if not excerpts or any(not excerpt or excerpt not in visible for excerpt in excerpts):
            raise ValueError("Review evidence must be an exact nonempty excerpt of this case's public executable output")
        rows.append({"caseId": label["caseId"], "planningChecks": source["planningChecks"],
                     "intentMatches": label["intentMatches"], "narrationGrounded": label["narrationGrounded"],
                     "reason": label["reason"], "publicEvidenceExcerpts": excerpts,
                     "actualMotionSuccess": None, "naturalness": "not-rated",
                     "sourceFactsWereSynthetic": True})
    counts = {field: {label: sum(row[field] == label for row in rows) for label in sorted(LABELS)}
              for field in ("intentMatches", "narrationGrounded")}
    return {"schemaVersion": 1, "reviewedAtUtc": utc(), "status": "review-complete", "sourceReportSha256": sha(report_bytes),
            "suiteSha256": report["suiteSha256"], "split": report["split"], "reviewer": review.get("reviewer", "unspecified"),
            "counts": counts, "results": rows, "semanticMotionSuccess": None,
            "scope": "Explicit review of public response planning/narration against synthetic supplied facts. Not proof of actual scene behavior, automatic repair, actual geometry or human naturalness.",
            "sourceReportWasNotModified": True}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--review", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    original = args.report.read_bytes()
    combined = combine(json.loads(original), json.loads(args.review.read_text(encoding="utf-8")), original)
    exclusive_json(args.output, combined)
    if args.report.read_bytes() != original:
        raise RuntimeError("Source report changed during review")
    print(json.dumps({"output": str(args.output), "counts": combined["counts"], "semanticMotionSuccess": None}))


if __name__ == "__main__":
    main()
