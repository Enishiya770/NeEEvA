"""Replay Unity-exported production claim/review wires against the local model.

No character, movement, device, microphone or TTS action is performed. The suite
combines exact latest-Play candidate words with controlled engine facts; it does
not claim these are captured original HTTP requests.
"""
import argparse
import copy
import hashlib
import json
import re
import sys
from pathlib import Path
import time
import urllib.request


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def speech_fragments(data):
    """Mirror the deterministic C# span layout, not the semantic ownership decision."""
    speech = data['publicSpeech']
    claim = data.get('independentSpeechClaim', {})
    evidence = claim.get('evidence', '')
    quotes = [evidence, claim.get('pendingSameResultQuote', ''), *claim.get('otherClaims', [])]
    spans = [(speech.index(q), speech.index(q) + len(q)) for q in quotes if q and q in speech]
    protected = speech.find(evidence) if evidence else -1
    ends = {len(speech)}
    for i, character in enumerate(speech):
        boundary = character in '。！？!?；;，,' or character == '.' and not (
            i > 0 and i + 1 < len(speech) and speech[i-1].isdigit() and speech[i+1].isdigit())
        if boundary and not any(start < i + 1 < end for start, end in spans):
            ends.add(i + 1)
    ends.update(end for _, end in spans)
    adjusted = set()
    for end in ends:
        while end < len(speech) and (speech[end].isspace() or speech[end] in '。！？!?.,，;；'):
            end += 1
        adjusted.add(end)
    result, start = [], 0
    for end in sorted(adjusted):
        if end <= start:
            continue
        result.append(dict(id=f'p{len(result)}', text=speech[start:end],
                           protectedResult=protected >= 0 and start < protected+len(evidence) and end > protected))
        start = end
    return result


def independent_speech(parts, language):
    """Same punctuation-only join as RoomSpeechPartsProtocol.TryRead."""
    kept, removed = '', False
    def end_clause(text):
        trimmed = text.rstrip()
        if not trimmed:
            return text
        if trimmed[-1] in ',，;；':
            return trimmed.rstrip(',，;；') + ('. ' if language == 'en' else '。')
        if trimmed[-1] not in '.。！？!?':
            return trimmed + ('. ' if language == 'en' else '。')
        return text
    for part in parts:
        if part['owner'] == 'independent':
            if removed and kept:
                kept = end_clause(kept)
            kept += part['text']
            removed = False
        else:
            removed = True
    return (end_clause(kept) if removed and kept else kept).strip()


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
    sys.stdout.reconfigure(encoding="utf-8")
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
                content = wire["messages"][-1]["content"]
                text_part = next((part for part in content if part.get("type") == "text"), None) if isinstance(content, list) else None
                input_text = text_part["text"] if text_part is not None else content
                # Multimodal production requests prefix their JSON with the
                # image provenance instruction. Keep that instruction intact.
                json_start = input_text.index("{")
                input_data = json.loads(input_text[json_start:])
                input_data["independentSpeechClaim"] = claim
                schema = wire["response_format"]["schema"]
                if input_data.get("speechProgressOwner"):
                    fragments = speech_fragments(input_data)
                    input_data["progressSpeechParts"] = fragments
                    eligible = [p["id"] for p in fragments if not p["protectedResult"]]
                    schema["properties"]["bodyProgressParts"]["items"]["enum"] = eligible or ["none"]
                    schema["properties"]["bodyProgressParts"]["maxItems"] = len(eligible)
                for key in ("resultReport", "actualLanguage"):
                    schema["properties"][key]["enum"] = [claim[key]]
                speech = input_data["publicSpeech"]
                if "enum" in schema["properties"]["conflictQuote"]:
                    quotes = ["", speech, claim["evidence"], claim["targetQuote"], claim["pendingSameResultQuote"], *claim["otherClaims"]]
                    schema["properties"]["conflictQuote"]["enum"] = list(dict.fromkeys(q for q in quotes if
                        len(q.encode('utf-16-le')) // 2 <= 160 and q in speech))
                wire["messages"][0]["content"] = wire["messages"][0]["content"].rsplit('\n', 1)[0] + '\n' + json.dumps(schema, ensure_ascii=False, separators=(',', ':'))
                updated_input = input_text[:json_start] + json.dumps(input_data, ensure_ascii=False, separators=(",", ":"))
                if text_part is not None:
                    text_part["text"] = updated_input
                else:
                    wire["messages"][-1]["content"] = updated_input
                write(args.output / (row["name"] + ".review.request.json"), wire)
                verdict, envelope, elapsed = request(args.url, wire)
                write(args.output / (row["name"] + ".review.envelope.json"), envelope)
                entry.update(review=verdict, reviewSeconds=elapsed)
                if row["input"].get("speechProgressOwner"):
                    selected = verdict.get("bodyProgressParts")
                    assert isinstance(selected, list) and len(set(selected)) == len(selected), verdict
                    assert all(key in eligible for key in selected), verdict
                    assert not (verdict.get('verdict') == 'consistent' and verdict.get('otherClaimsSupport') == 'none' and selected), verdict
                    parts = [dict(owner="current-body" if p["id"] in selected else "independent", text=p["text"]) for p in fragments]
                    entry["resolvedSpeechParts"] = parts
                    assert "".join(p["text"] for p in parts) == speech, parts
                    retained_literal = "".join(p["text"] for p in parts if p["owner"] == "independent")
                    assert not claim["evidence"] or claim["evidence"] in retained_literal, (claim, parts)
                    stable = independent_speech(parts, claim['actualLanguage'])
                    if row.get("expectedFactualApproval", True):
                        assert all(q in stable for q in row.get("retainedQuotes", [])), stable
                        assert all(q not in stable for q in row.get("replacedQuotes", [])), stable
                    entry["independentSpeech"] = stable
                assert isinstance(verdict.get("targetComparison"), str) and 0 < len(verdict["targetComparison"]) <= 160, verdict
                assert isinstance(verdict.get("otherClaimsComparison"), str) and 0 < len(verdict["otherClaimsComparison"]) <= 200, verdict
                assert isinstance(verdict.get("taskRelationComparison"), str) and 0 < len(verdict["taskRelationComparison"]) <= 160, verdict
                relation_pattern = wire["response_format"]["schema"]["properties"]["taskRelationComparison"].get("pattern")
                assert not relation_pattern or re.fullmatch(relation_pattern, verdict["taskRelationComparison"]), verdict
                if row.get("expectedFactualApproval", True):
                    assert verdict["verdict"] == "consistent" and verdict["conflict"] == "none", verdict
                    assert verdict["targetConsistency"] in ("matched", "unspecified", "not-applicable"), verdict
                    assert verdict["otherClaimsSupport"] in ("none", "supported"), verdict
                    assert verdict["taskRelation"] in ("aligned", "not-applicable"), verdict
                else:
                    assert verdict["verdict"] == "inconsistent" and verdict["conflict"] not in ("none", "uncertain"), verdict
                    assert verdict["conflictQuote"] and verdict["conflictQuote"] in row["input"]["publicSpeech"], verdict
                assert verdict["resultReport"] == claim["resultReport"] and verdict["actualLanguage"] == claim["actualLanguage"], verdict
                if row.get("expectedTaskRelationConflict"):
                    assert verdict["conflict"] == "task-relation" and verdict["taskRelation"] == "contradicted", verdict
                if row.get("expectedFactScope"):
                    assert verdict["factScope"] == row["expectedFactScope"], verdict
                # The grounded quote list can include requests or courtesy.
                # The factual review compares actor/modality before classifying support.
            entry["passed"] = True
        except Exception as error:
            entry["error"] = str(error)
        print(json.dumps({k: v for k, v in entry.items() if k not in ('claim', 'review', 'error')}, ensure_ascii=False), flush=True)
        write(args.output / "report.json", report)
    report["passed"] = all(row["passed"] for row in results)
    report["passedCount"] = sum(row["passed"] for row in results)
    write(args.output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
