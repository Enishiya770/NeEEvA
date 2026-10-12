"""Replay Unity-exported initial classifiers through the production grounding parser."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request
from validate_first_reply_semantic_qwen import compile_probe, probe

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--cases', required=True, type=Path)
p.add_argument('--output', required=True, type=Path)
p.add_argument('--url', default='http://127.0.0.1:8080')
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=False)
report = {'source': str(a.cases.resolve()), 'sourceSha256': hashlib.sha256(a.cases.read_bytes()).hexdigest(),
          'parser': compile_probe(a.output), 'cases': [],
          'scope': 'Exact Unity initial-classifier wires, live Qwen, production grounded parser. At most one parser-triggered repair; no actions, TTS or expectation-driven retries.'}
for row in json.loads(a.cases.read_text(encoding='utf-8-sig')):
    name = row['id']; wire = row['request']
    current = a.output / (name + '.user.txt')
    current.write_bytes(row['user'].encode('utf-8'))
    item = {'id': name, 'expectedRoute': row['expectedRoute'], 'attempts': [], 'passed': False}
    try:
        for attempt in range(2):
            request_file = a.output / f'{name}.{attempt}.request.json'
            request_file.write_text(json.dumps(wire, ensure_ascii=False), encoding='utf-8')
            started = time.perf_counter()
            request = urllib.request.Request(a.url.rstrip('/') + '/v1/chat/completions',
                json.dumps(wire, ensure_ascii=False).encode('utf-8'), {'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=6) as response:
                envelope = json.load(response)
            seconds = time.perf_counter() - started
            choice = envelope['choices'][0]
            assert choice['finish_reason'] == 'stop'
            result_file = a.output / f'{name}.{attempt}.response.json'
            result_file.write_bytes(choice['message']['content'].encode('utf-8'))
            result = probe(a.output, 'preflight-review', str(result_file), str(current))
            item['attempts'].append({'seconds': seconds, 'usage': envelope.get('usage'), 'parsed': result,
                                     'output': choice['message']['content']})
            if result['valid'] or attempt != 0 or not result['repairableGrounding']:
                break
            wire = probe(a.output, 'repair-payload', str(request_file), str(result_file))
        kinds = {'latest-device-return': ['action'], 'generic-compound-action': ['action'],
                 'control-and-action': ['action', 'control'], 'mixed-answer-and-movement': ['mixed'],
                 'mixed-answer-and-device': ['mixed']}.get(name)
        item['planningFocus'] = json.loads(choice['message']['content']).get('planningFocus')
        item['expectedPlanningFocus'] = row.get('expectedPlanningFocus')
        expected_focus = row.get('expectedPlanningFocus')
        focus_ok = expected_focus is None or item['planningFocus'] == expected_focus or (
            isinstance(expected_focus, list) and item['planningFocus'] in expected_focus)
        item['passed'] = focus_ok and result['valid'] and result['route'] == row['expectedRoute'] and (
            kinds is None or result['requestKind'] in kinds) and all(t['seconds'] < 6 for t in item['attempts'])
    except Exception as exc:
        item['error'] = str(exc)
    report['cases'].append(item)
    report['passed'] = all(r['passed'] for r in report['cases'])
    (a.output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(name, item['passed'], len(item['attempts']), flush=True)
raise SystemExit(0 if report['passed'] else 1)
