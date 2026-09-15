"""Replay public Unity-exported singing review requests against the existing model.

No generated actions are executed. Raw attempts and transport outcomes are retained;
production Unity validators separately check the returned decisions.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--kind', choices=['policy', 'goal', 'formal'], required=True)
    parser.add_argument('--url', default='http://127.0.0.1:8080')
    args = parser.parse_args()
    source = Path(args.source).read_bytes()
    fixtures = json.loads(source.decode('utf-8-sig'))
    cases = fixtures if isinstance(fixtures, list) else fixtures['cases']
    output = Path(args.out)
    if output.exists():
        raise RuntimeError('Preserve earlier attempts; use a new output path.')
    output.parent.mkdir(parents=True, exist_ok=True)
    records = []
    completed = False
    digest = hashlib.sha256(source).hexdigest()
    def save():
        content = records if args.kind == 'policy' else {
            'sourceSha256': digest, 'responses' if args.kind == 'formal' else 'cases': records, 'status': 'completed' if completed else 'incomplete',
            'scope': 'Exact exported public production review requests. No tools/audio executed.'}
        output.write_text(json.dumps(content, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    try:
        for case in cases:
            with urllib.request.urlopen(args.url+'/slots', timeout=5) as response:
                slots = json.load(response)
            if any(slot.get('is_processing') for slot in slots):
                raise RuntimeError('Existing model busy; no next request submitted.')
            request = case['request']
            row = {'id': case['id'], 'sourceSha256': digest}
            records.append(row)
            started = time.monotonic()
            try:
                wire = urllib.request.Request(args.url+'/v1/chat/completions',
                    json.dumps(request, ensure_ascii=False).encode('utf-8'),
                    {'Content-Type': 'application/json'})
                with urllib.request.urlopen(wire, timeout=55) as response:
                    envelope = response.read().decode('utf-8')
                if request.get('stream'):
                    wire_path = output.with_name(output.stem+'-'+case['id']+'.sse.txt')
                    wire_path.write_text(envelope, encoding='utf-8')
                    chunks = [json.loads(line[5:].strip()) for line in envelope.splitlines()
                              if line.startswith('data:') and line[5:].strip() != '[DONE]']
                    content, reasoning, finish = [], [], None
                    for chunk in chunks:
                        for choice in chunk.get('choices', []):
                            if choice.get('index', 0) != 0:
                                raise RuntimeError('Unexpected multiple response choices.')
                            delta = choice.get('delta', {})
                            content.append(delta.get('content') or '')
                            reasoning.append(delta.get('reasoning_content') or '')
                            finish = choice.get('finish_reason') or finish
                    parsed = {'choices': [{'index': 0, 'finish_reason': finish, 'message': {
                        'role': 'assistant', 'content': ''.join(content), 'reasoning_content': ''.join(reasoning)}}]}
                    row['sseFile'] = wire_path.name
                    row['envelopeSource'] = 'Concatenated actual SSE delta content; full original wire retained. No content repair.'
                else:
                    parsed = json.loads(envelope)
                row['envelope'] = parsed if args.kind == 'formal' else envelope
                choice = parsed['choices'][0]
                row['finishReason'] = choice.get('finish_reason')
                try:
                    if args.kind == 'formal':
                        row['raw'] = choice['message']['content']
                    else:
                        row['decision'] = json.loads(choice['message']['content'])
                except (ValueError, KeyError, TypeError) as error:
                    row['parseError'] = str(error)
            except Exception as error:
                row['error'] = type(error).__name__+': '+str(error)
            finally:
                row['elapsedSeconds'] = round(time.monotonic()-started, 3)
                save()
            print(json.dumps({'id': row['id'], 'finishReason': row.get('finishReason'),
                              'elapsedSeconds': row['elapsedSeconds'], 'error': row.get('error')}, ensure_ascii=False), flush=True)
        completed = True
    finally:
        save()


if __name__ == '__main__':
    main()
