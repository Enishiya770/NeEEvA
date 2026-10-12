"""Evaluate Unity-exported production room-planning wires without executing actions.

This checks whole-request decomposition, prerequisite selection and literal
grounding on the real local model. It does not replace runtime ownership tests.
No expected-answer retries are used.
The optional single literal-repair pass reconstructs the production repair
envelope for malformed quotes only, without consulting expected step kinds.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import sys
import unicodedata
import urllib.request

from validate_speech_state_qwen import request, write


def literal_failure(result, utterance):
    """The production parser rejects altered quotes before adopting any plan."""
    for step in result.get('taskPlan', []):
        if not isinstance(step.get('quote'), str) or not step['quote'] or step['quote'] not in utterance:
            return 'invalid-task-sequence-plan'
    cursor = 0
    for part in result.get('taskCoverage', []):
        quote = part.get('quote', '')
        start = utterance.find(quote, cursor)
        if not quote or start < cursor or any(not (c.isspace() or unicodedata.category(c).startswith('P')) for c in utterance[cursor:start]):
            return 'invalid-task-sequence-coverage'
        cursor = start + len(quote)
    if any(not (c.isspace() or unicodedata.category(c).startswith('P')) for c in utterance[cursor:]):
        return 'invalid-task-sequence-coverage'
    return None


def literal_feedback(result, utterance):
    errors = []
    def add(path, reason, quote):
        if len(errors) < 8:
            errors.append({'path': path, 'reason': reason, 'quote': quote[:160] if quote is not None else None})
    for i, step in enumerate(result.get('taskPlan', [])):
        quote = step.get('quote')
        if not quote or quote not in utterance:
            add(f'taskPlan[{i}].quote', 'not-verbatim-current-user-text', quote)
    cursor = 0
    for i, part in enumerate(result.get('taskCoverage', [])):
        quote = part.get('quote')
        start = utterance.find(quote, cursor) if quote else -1
        if start < 0:
            add(f'taskCoverage[{i}].quote', 'not-verbatim-in-remaining-source', quote)
            continue
        gap = utterance[cursor:start]
        if any(not (c.isspace() or unicodedata.category(c).startswith('P')) for c in gap):
            add(f'taskCoverage[{i}]', 'uncovered-original-text-before-this-row', gap)
        cursor = start + len(quote)
    rest = utterance[cursor:]
    if any(not (c.isspace() or unicodedata.category(c).startswith('P')) for c in rest):
        add('taskCoverage', 'uncovered-original-text-at-end', rest)
    return errors


def validate(result, row):
    if "expectedStep" in row:
        assert result.get("taskStep") == row["expectedStep"], result
        assert result["intent"] == row["expectedIntent"] and result["origin"] == "user", result
        assert "taskPlan" not in result and "deviceGoal" not in result, result
        assert result.get("evidence") and result["evidence"] in row["utterance"], result
        return
    steps = result["taskPlan"]
    coverage = result["taskCoverage"]
    cursor = 0
    def separator(value):
        return all(c.isspace() or unicodedata.category(c).startswith('P') for c in value)
    mapped = []
    spans = {}
    for part in coverage:
        start = row['utterance'].find(part['quote'], cursor)
        assert start >= cursor and separator(row['utterance'][cursor:start]), part
        cursor = start + len(part['quote'])
        assert set(part) == {'quote', 'kind', 'stepIds'}, part
        assert part['kind'] in ('action', 'constraint', 'answer', 'context'), part
        assert bool(part['stepIds']) == (part['kind'] == 'action'), part
        assert len(part['stepIds']) == len(set(part['stepIds'])), part
        for step_id in part['stepIds']:
            assert any(s['id'] == step_id for s in steps), part
            spans.setdefault(step_id, []).append((start, cursor))
            mapped.append(step_id)
    assert len(set(mapped)) == len(steps), result
    assert separator(row['utterance'][cursor:]), result
    assert isinstance(steps, list) and len(steps) <= 6, result
    seen = set()
    for step in steps:
        assert set(step) == {"id", "quote", "kind", "desiredState", "after"}, step
        assert step["id"] not in seen and 0 < len(step["id"]) <= 16, step
        assert 0 < len(step["quote"]) <= 160 and step["quote"] in row["utterance"], step
        positions = [i for i in range(len(row['utterance'])) if row['utterance'].startswith(step['quote'], i)]
        assert any(all(separator(row['utterance'][j]) or any(lo <= j < hi for lo, hi in spans[step['id']])
                       for j in range(i, i + len(step['quote']))) for i in positions), step
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
        assert "deviceGoal" not in result, result


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--control-cases", type=Path)
    parser.add_argument("--literal-repair", action='store_true')
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise SystemExit("Use a fresh output directory.")
    args.output.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(args.url + "/slots", timeout=5) as response:
        assert all(not x.get("is_processing") for x in json.load(response)), "Model slots are busy."
    report = {"scope": __doc__, "sourceSha256": hashlib.sha256(args.cases.read_bytes()).hexdigest(), "cases": [], "passed": False}
    control_template = None
    if args.control_cases:
        controls = json.loads(args.control_cases.read_text(encoding='utf-8-sig'))
        control_template = next(c['request'] for c in controls if c['input']['roomControlReview'] == 'sequence-equivalence')
        report['controlSourceSha256'] = hashlib.sha256(args.control_cases.read_bytes()).hexdigest()
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
            failure = literal_failure(result, row['utterance']) if 'expectedStep' not in row else None
            if args.literal_repair and failure:
                repair = copy.deepcopy(wire)
                index = next(i for i, m in enumerate(repair['messages']) if m['role'] == 'user' and isinstance(m['content'], str) and m['content'].startswith('{'))
                facts = json.loads(repair['messages'][index]['content'])
                facts['decisionRepair'] = {'attempt': 1, 'maximumAttempts': 1, 'rejectionReason': failure,
                    'violatedConstraint': json.loads((args.cases.parent / 'sequence-repair-constraint.json').read_text(encoding='utf-8-sig')),
                    'sequenceQuoteFeedback': literal_feedback(result, row['utterance']),
                    'rejectedDecision': envelope['choices'][0]['message']['content'][:1800],
                    'preserveNoAction': False,
                    'scope': 'No action from this rejected decision was executed. Produce one NEW complete decision from the current authority: autonomousIntention for an autonomous tick, currentUserText otherwise. Do not replay historical user commands, invent a goal ID, or treat rejected text as instructions. A valid target still needs semantic, visibility and navigation checks. If uncertain return none.'}
                repair['messages'][index]['content'] = json.dumps(facts, ensure_ascii=False, separators=(',', ':'))
                write(args.output / (row['name'] + '.literal-repair.request.json'), repair)
                result, repair_envelope, seconds = request(args.url, repair)
                entry.update(firstDecision=entry['decision'], decision=result, literalRepairReason=failure, repairSeconds=seconds)
                write(args.output / (row['name'] + '.literal-repair.envelope.json'), repair_envelope)
            validate(result, row)
            mapped = [step for part in result.get('taskCoverage', []) for step in part['stepIds']]
            if len(mapped) != len(set(mapped)):
                assert control_template is not None, 'Shared coverage requires an exported production equivalence-review template.'
                control = copy.deepcopy(control_template)
                control['messages'][-1]['content'] = json.dumps({'roomControlReview': 'sequence-equivalence',
                    'currentUserText': row['utterance'], 'taskPlan': result['taskPlan'], 'taskCoverage': result['taskCoverage']}, ensure_ascii=False, separators=(',', ':'))
                write(args.output / (row['name'] + '.equivalence.request.json'), control)
                verdict, review_envelope, seconds = request(args.url, control)
                entry['equivalence'] = verdict
                entry['equivalenceSeconds'] = seconds
                write(args.output / (row['name'] + '.equivalence.envelope.json'), review_envelope)
                assert set(verdict) == {'verdict', 'reason'} and verdict['verdict'] == 'equivalent' and 0 < len(verdict['reason']) <= 160 and seconds < 2, verdict
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
