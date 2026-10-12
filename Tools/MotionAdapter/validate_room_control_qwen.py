"""Replay exact Unity-exported bounded control-review wires; performs no actions."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request

p = argparse.ArgumentParser()
p.add_argument('--cases', required=True)
p.add_argument('--output', required=True)
p.add_argument('--url', default='http://127.0.0.1:8080')
a = p.parse_args()
source = Path(a.cases)
out = Path(a.output)
out.mkdir(parents=True, exist_ok=True)
report = {'source': str(source.resolve()), 'sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
          'scope': 'Exact production control payloads, live model. No Unity/body/device execution.', 'cases': []}
for row in json.loads(source.read_text(encoding='utf-8-sig')):
    started = time.monotonic()
    result = {'name': row['name'], 'expected': row['expected'], 'passed': False}
    try:
        raw = json.dumps(row['request'], ensure_ascii=False).encode('utf-8')
        req = urllib.request.Request(a.url.rstrip('/') + '/v1/chat/completions', raw, {'Content-Type': 'application/json'})
        # The same bounded response window as Unity preparation (no retries).
        with urllib.request.urlopen(req, timeout=2) as response:
            envelope = json.load(response)
        result['seconds'] = round(time.monotonic() - started, 3)
        choice = envelope['choices'][0]
        value = json.loads(choice['message']['content'])
        result['result'] = value
        result['usage'] = envelope.get('usage')
        assert choice['finish_reason'] == 'stop'
        if row['input']['roomControlReview'] == 'body-stop':
            assert set(value) == {'explicitStopNow', 'bodyDirective', 'force', 'reason'}
            assert isinstance(value['explicitStopNow'], bool)
            assert all(isinstance(value[k], str) for k in ('bodyDirective', 'force', 'reason'))
            assert value['bodyDirective'] in ('continue-current', 'stop-current', 'other-task', 'none', 'unclear')
            assert value['force'] in ('command-now', 'conditional', 'reported', 'question', 'unclear')
            assert 0 < len(value['reason']) <= 160
            command = value['force'] == 'command-now' and value['bodyDirective'] in ('continue-current', 'stop-current', 'other-task')
            assert not value['explicitStopNow'] or command and value['bodyDirective'] != 'continue-current'
            assert not(command and value['bodyDirective'] == 'stop-current') or value['explicitStopNow']
            actual = 'stop-moving' if command and (value['explicitStopNow'] or
                value['bodyDirective'] == 'other-task' and row['input']['activeBody']['allowReplacement']) else 'none'
        elif row['input']['roomControlReview'] == 'sequence-body-continuation':
            assert set(value) == {'target', 'readiness', 'stepId', 'evidence', 'reason'}
            assert value['stepId'] == row['input']['selectedStep']['id']
            assert all(isinstance(v, str) for v in value.values()) and 0 < len(value['reason']) <= 160
            assert len(value['evidence']) <= 160
            assert value['target'] in ('current-user', 'other', 'unknown')
            assert value['readiness'] in ('satisfied', 'waiting-user', 'waiting-speech', 'waiting-other', 'rejected', 'failed', 'unclear')
            actual = 'execute' if value['target'] == 'current-user' and value['readiness'] == 'satisfied' else 'defer'
            if actual == 'execute':
                assert value['evidence'] == row['input']['selectedStep']['quote'] and value['evidence'] in row['input']['currentUserText']
            else:
                assert not value['evidence'] or value['evidence'] in row['input']['currentUserText']
        else:
            assert set(value) == {'verdict', 'reason'} and isinstance(value['reason'], str) and 0 < len(value['reason']) <= 160
            actual = value['verdict']
        result['passed'] = actual == row['expected'] and result['seconds'] <= 2
    except Exception as exc:
        result['seconds'] = round(time.monotonic() - started, 3)
        result['error'] = str(exc)
    report['cases'].append(result)
    print(row['name'], result['passed'], result['seconds'], flush=True)
    report['passed'] = all(c['passed'] for c in report['cases'])
    (out / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
raise SystemExit(0 if report['passed'] else 1)
