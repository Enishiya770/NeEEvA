"""Check production progress-review prompts against the local model, without actions.

Unity exports controlled per-step execution receipts through the real work prompt
and serializer. This verifies task reasoning; it is not an end-to-end Play test.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import urllib.request

from validate_speech_state_qwen import request, write


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cases', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--url', default='http://127.0.0.1:8080')
    parser.add_argument('--model', default='qwen36')
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise SystemExit('Use a fresh output directory.')
    args.output.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(args.url.rstrip('/') + '/slots', timeout=5) as response:
        assert all(not slot.get('is_processing') for slot in json.load(response)), 'Model slots busy.'
    rows = json.loads(args.cases.read_text(encoding='utf-8-sig'))
    report = {'source': str(args.cases.resolve()), 'scope': __doc__, 'passed': False,
              'sourceSha256': hashlib.sha256(args.cases.read_bytes()).hexdigest(), 'cases': []}
    for row in rows:
        entry = {'name': row['name'], 'passed': False}
        report['cases'].append(entry)
        try:
            wire = copy.deepcopy(row['request'])
            wire['model'] = args.model
            write(args.output / (row['name'] + '.request.json'), wire)
            result, envelope, elapsed = request(args.url, wire)
            write(args.output / (row['name'] + '.envelope.json'), envelope)
            entry.update(decision=result, seconds=elapsed, usage=envelope.get('usage'))
            assert result['work_status'] == row['expectedStatus'], result
            assert result['proceed'] == row['expectedProceed'], result
            assert result['work_evidence'].strip(), result
            assert not result['proceed'] or result['intent'].strip(), result
            entry['passed'] = True
        except Exception as error:
            entry['error'] = str(error)
        print(json.dumps(entry, ensure_ascii=False), flush=True)
        write(args.output / 'report.json', report)
    report['passed'] = all(row['passed'] for row in report['cases'])
    write(args.output / 'report.json', report)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
