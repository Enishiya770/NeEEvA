"""Measure bounded control calls beside a real exported large planning request.

No Unity actions. Main payloads are production exports with an isolated slot and
unique system suffix to measure cold prefill, not a synthetic busy loop.
"""
import argparse
import concurrent.futures
import copy
import json
from pathlib import Path
import time
import urllib.request
import uuid

p = argparse.ArgumentParser()
p.add_argument('--controls', required=True)
p.add_argument('--load', required=True)
p.add_argument('--output', required=True)
p.add_argument('--repeats', type=int, default=3)
a = p.parse_args()
controls = json.loads(Path(a.controls).read_text(encoding='utf-8-sig'))
heavy = json.loads(Path(a.load).read_text(encoding='utf-8-sig'))[0]['request']
out = Path(a.output); out.mkdir(parents=True, exist_ok=True)
report = {'scope': 'Live requests only; no actions. Load is exported production planning, not exact recorded first-reply bytes.', 'cases': []}

def call(wire, timeout):
    rid = uuid.uuid4().hex
    start = time.monotonic()
    result = {'requestId': rid, 'slot': wire.get('id_slot'), 'timeout': timeout}
    try:
        req = urllib.request.Request('http://127.0.0.1:8080/v1/chat/completions',
            json.dumps(wire, ensure_ascii=False).encode('utf-8'),
            {'Content-Type': 'application/json', 'X-Request-ID': rid})
        with urllib.request.urlopen(req, timeout=timeout) as response:
            value = json.load(response)
        result.update(ok=True, usage=value.get('usage'), timings=value.get('timings'),
            content=value['choices'][0]['message']['content'])
    except Exception as error:
        result.update(ok=False, error=str(error))
    result['seconds'] = time.monotonic() - start
    return result

for mode in ['control-alone', 'overlap-control-first', 'overlap-main-first', 'control-before-main']:
    for i in range(a.repeats):
        row = next(r for r in controls if r['name'] == ('quick-channel-explicit-only' if i % 2 else 'latest-repeated-stop'))
        control = copy.deepcopy(row['request'])
        load = copy.deepcopy(heavy)
        load['id_slot'] = 0
        # Different leading system content prevents cross-case warm-cache bias.
        load['messages'][0]['content'] = 'Benchmark identity ' + uuid.uuid4().hex + '\n' + load['messages'][0]['content']
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            if mode == 'control-alone': c, h = call(control, 2), None
            elif mode == 'control-before-main':
                c = call(control, 2); h = call(load, 30)
            elif mode == 'overlap-control-first':
                cf = pool.submit(call, control, 2); time.sleep(.05)
                hf = pool.submit(call, load, 30); c, h = cf.result(), hf.result()
            else:
                hf = pool.submit(call, load, 30); time.sleep(.05)
                cf = pool.submit(call, control, 2); c, h = cf.result(), hf.result()
        if c.get('ok'):
            try: c['correct'] = json.loads(c['content']).get('intent') == row['expected']
            except (ValueError, TypeError): c['correct'] = False
        report['cases'].append({'mode': mode, 'case': row['name'], 'control': c, 'load': h})
        (out/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(mode, row['name'], c['ok'], round(c['seconds'], 3), flush=True)
report['priorityPassed'] = all(c['control'].get('correct') and c['control']['seconds'] < 2
    for c in report['cases'] if c['mode'] == 'control-before-main')
(out/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
raise SystemExit(0 if report['priorityPassed'] else 1)
