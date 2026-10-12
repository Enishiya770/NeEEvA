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
            assert set(value) == {'intent', 'origin', 'evidence'}
            assert value['intent'] in ('stop-moving', 'none')
            assert isinstance(value['evidence'], str) and len(value['evidence']) <= 160
            if value['intent'] == 'stop-moving':
                assert value['origin'] == 'user' and value['evidence'] and value['evidence'] in row['input']['currentUserText']
            else:
                assert value['origin'] == 'none' and value['evidence'] == ''
            actual = value['intent']
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
