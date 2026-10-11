"""Replay Unity-exported production claim/review wires against the local model.

No character, movement, device, microphone or TTS action is performed. The suite
combines exact latest-Play candidate words with controlled engine facts; it does
not claim these are captured original HTTP requests.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import time
import urllib.request


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def request(base, payload, allow_invalid_json=False):
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    started = time.monotonic()
    with urllib.request.urlopen(urllib.request.Request(base.rstrip("/") + "/v1/chat/completions", data,
            {"Content-Type": "application/json"}), timeout=35) as response:
        envelope = json.load(response)
    choice = envelope["choices"][0]
    assert choice["finish_reason"] == "stop", choice
    try:
        parsed = json.loads(choice["message"]["content"])
    except json.JSONDecodeError:
        if not allow_invalid_json:
            raise
        parsed = None
    return parsed, envelope, time.monotonic() - started


def extraction_failure(claim, speech, conversation):
    """Validate the observed output before consulting any expected test answer."""
    fields = {"pendingSameResultQuote", "resultReport", "evidence", "actualLanguage", "targetQuote", "otherClaims"}
    if not isinstance(claim, dict) or set(claim) != fields:
        return "schema:unexpected-or-missing-fields"
    if any(not isinstance(claim[k], str) for k in fields - {"otherClaims"}):
        return "schema:expected-string"
    if claim["actualLanguage"] not in ("ja", "zh", "en", "mixed", "und"):
        return "actualLanguage:invalid-enum"
    states = {"on", "off", "arrived", "preparing", "moving", "waiting", "paused", "recovering", "replanning", "returning",
              "too-far", "target-unconfirmed", "selected-control-unavailable", "observation-unavailable", "incomplete", "cancelled",
              "clarification", "found", "survey-completed", "omitted"}
    if claim["resultReport"] not in states:
        return "resultReport:invalid-enum"
    if not isinstance(claim["otherClaims"], list) or len(claim["otherClaims"]) > 6:
        return "otherClaims:expected-array-at-most-six"
    quotes = [(k, claim[k], True) for k in ("pendingSameResultQuote", "targetQuote", "evidence")]
    quotes += [(f"otherClaims[{i}]", q, False) for i, q in enumerate(claim["otherClaims"])]
    for key, quote, empty in quotes:
        if not isinstance(quote, str):
            return key + ":expected-string"
        if not empty and not quote.strip():
            return key + ":empty-quote"
        if len(quote) > 160:
            return key + ":quote-too-long"
        if quote not in speech:
            return key + ":quote-not-in-speech"
    if claim["resultReport"] != "omitted" and not claim["evidence"].strip():
        return "evidence:required-for-reported-state"
    if conversation and (claim["resultReport"] != "omitted" or any(claim[k] for k in ("evidence", "targetQuote", "pendingSameResultQuote"))):
        return "conversation:unexpected-result-fields"
    return None


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
        assert all(not slot.get("is_processing") for slot in json.load(response)), "Model slots busy; do not interrupt user work."
    rows = json.loads(args.cases.read_text(encoding="utf-8-sig"))
    results = []
    report = {"source": str(args.cases.resolve()), "sourceSha256": hashlib.sha256(args.cases.read_bytes()).hexdigest(),
              "scope": __doc__, "model": args.model, "cases": results, "passed": False}
    for row in rows:
        entry = {"name": row["name"], "passed": False}
        results.append(entry)
        try:
            wire = copy.deepcopy(row["claimRequest"]); wire["model"] = args.model
            write(args.output / (row["name"] + ".claim.request.json"), wire)
            claim, envelope, elapsed = request(args.url, wire, allow_invalid_json=True)
            write(args.output / (row["name"] + ".claim.envelope.json"), envelope)
            entry.update(claim=claim, claimSeconds=elapsed)
            words = json.loads(wire["messages"][1]["content"])
            failure = extraction_failure(claim, row["input"]["publicSpeech"], words.get("reportDomain") == "conversation")
            ambiguous = not failure and claim["actualLanguage"] in ("mixed", "und")
            # Exactly the production allowance: retry only a structural/quote
            # failure or ambiguous language, never a mismatch with test answers.
            if (failure or ambiguous) and "languageRecheckRequest" in row:
                entry["initialClaim"] = copy.deepcopy(claim)
                entry["recoveryReason"] = failure or "actualLanguage:recheck-substantive-clause-grammar"
                recovery = copy.deepcopy(row["languageRecheckRequest" if ambiguous else "extractionRecoveryRequest"])
                recovery["model"] = args.model
                if not ambiguous:
                    data = json.loads(recovery["messages"][1]["content"])
                    data["extractionRepair"].update(failureDetail=failure, rejectedOutput=envelope["choices"][0]["message"]["content"][:1600])
                    recovery["messages"][1]["content"] = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
                write(args.output / (row["name"] + ".recovery.request.json"), recovery)
                recovered, recovery_envelope, recovery_seconds = request(args.url, recovery, allow_invalid_json=True)
                write(args.output / (row["name"] + ".recovery.envelope.json"), recovery_envelope)
                entry["recoverySeconds"] = recovery_seconds
                if ambiguous:
                    assert isinstance(recovered, dict) and set(recovered) == {"actualLanguage"}, recovered
                    assert recovered["actualLanguage"] in ("ja", "zh", "en", "mixed", "und"), recovered
                    claim["actualLanguage"] = recovered["actualLanguage"]
                else:
                    claim = recovered
                entry["claim"] = claim
                failure = extraction_failure(claim, row["input"]["publicSpeech"], words.get("reportDomain") == "conversation")
            assert failure is None, failure
            assert set(claim) == {"pendingSameResultQuote", "resultReport", "evidence", "actualLanguage", "targetQuote", "otherClaims"}, claim
            assert claim["resultReport"] in row.get("acceptableSpokenStates", [row["expectedSpokenState"]]), claim
            assert claim["actualLanguage"] == row["expectedLanguage"], claim
            assert claim["evidence"] in row["input"]["publicSpeech"], claim
            assert claim["targetQuote"] in row["input"]["publicSpeech"], claim
            assert claim["pendingSameResultQuote"] in row["input"]["publicSpeech"], claim
            assert len(claim["otherClaims"]) <= 6 and all(q and q in row["input"]["publicSpeech"] for q in claim["otherClaims"]), claim
            assert claim["resultReport"] == "omitted" or claim["evidence"].strip(), claim
            for required in row.get("requiredQuotedWords", []):
                assert any(required in quote for quote in claim["otherClaims"]), ("missing claim coverage", required, claim)
            state_guard_rejected = row.get("allowStateGuardRejection", False) and claim["resultReport"] == "omitted"
            temporal_conflict = bool(claim["pendingSameResultQuote"].strip()) and claim["resultReport"] in ("arrived", "on", "off", "found", "survey-completed")
            if temporal_conflict:
                assert not row.get("expectedFactualApproval", True), claim
                state_guard_rejected = True
            entry["temporalConflict"] = temporal_conflict
            if row["expectedAdmission"] and not state_guard_rejected and not row.get("claimOnly", False):
                wire = copy.deepcopy(row["reviewRequest"]); wire["model"] = args.model
                # Replace only the already-exported grounded classification. Full
                # facts, contract and schema are from the production serializer.
                input_data = json.loads(wire["messages"][-1]["content"])
                input_data["independentSpeechClaim"] = claim
                wire["messages"][-1]["content"] = json.dumps(input_data, ensure_ascii=False, separators=(",", ":"))
                write(args.output / (row["name"] + ".review.request.json"), wire)
                verdict, envelope, elapsed = request(args.url, wire)
                write(args.output / (row["name"] + ".review.envelope.json"), envelope)
                entry.update(review=verdict, reviewSeconds=elapsed)
                assert isinstance(verdict.get("targetComparison"), str) and 0 < len(verdict["targetComparison"]) <= 160, verdict
                assert isinstance(verdict.get("otherClaimsComparison"), str) and 0 < len(verdict["otherClaimsComparison"]) <= 200, verdict
                if row.get("expectedFactualApproval", True):
                    assert verdict["verdict"] == "consistent" and verdict["conflict"] == "none", verdict
                    assert verdict["targetConsistency"] in ("matched", "unspecified", "not-applicable"), verdict
                    assert verdict["otherClaimsSupport"] in ("none", "supported"), verdict
                else:
                    assert verdict["verdict"] == "inconsistent" and verdict["conflict"] not in ("none", "uncertain"), verdict
                    assert verdict["conflictQuote"] and verdict["conflictQuote"] in row["input"]["publicSpeech"], verdict
                assert verdict["resultReport"] == claim["resultReport"] and verdict["actualLanguage"] == claim["actualLanguage"], verdict
                if row.get("expectedFactScope"):
                    assert verdict["factScope"] == row["expectedFactScope"], verdict
                # The grounded quote list can include requests or courtesy.
                # The factual review compares actor/modality before classifying support.
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
