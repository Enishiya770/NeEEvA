"""Replay exported, unmodified production WorkReview payloads; never execute audio."""
import argparse
import datetime
import json
from pathlib import Path
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--cases', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--base-url', default='http://192.168.114.175:8888')
args = parser.parse_args()
cases = json.loads(args.cases.read_text(encoding='utf-8-sig'))
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
rows = []
args.output.parent.mkdir(parents=True, exist_ok=True)
for case in cases:
    row = {key: case[key] for key in ('id', 'expectedAllowed', 'currentClip', 'selectedClip', 'scope')}
    row.update(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), error=None)
    row['requestWire'] = case['wire']
    started = time.perf_counter()
    try:
        request = urllib.request.Request(
            args.base_url.rstrip('/') + '/v1/chat/completions',
            data=json.dumps(case['wire'], ensure_ascii=False).encode('utf-8'),
            headers={'Content-Type': 'application/json'})
        with opener.open(request, timeout=60) as response:
            envelope = json.loads(response.read().decode('utf-8'))
            choice = envelope['choices'][0]
            row.update(httpStatus=response.status, responseEnvelope=envelope, raw=choice['message']['content'],
                       finishReason=choice.get('finish_reason'), usage=envelope.get('usage'))
        decision = json.loads(row['raw'])
        expected = decision.get('singing_goal_expected', {})
        selected_relation = 'current_recording' if case['selectedClip'] == case['currentClip'] else 'retained_recording'
        row['decision'] = decision
        row['matchingSourceApproval'] = (
            row['finishReason'] == 'stop' and decision.get('singing_goal_status') == 'approved'
            and expected.get('refs') == case['selectedClip']
            and expected.get('source_relation') == selected_relation
            and expected.get('range') == 'clean')
        row['sourceSubsetPass'] = row['matchingSourceApproval'] == case['expectedAllowed']
        requested_clip = case.get('requestedClip', case['currentClip'])
        requested_relation = 'current_recording' if requested_clip == case['currentClip'] else 'retained_recording'
        row['independentExpectedMatchesRequested'] = (expected.get('refs') == requested_clip
            and expected.get('source_relation') == requested_relation and expected.get('range') == 'clean'
            and expected.get('start_seconds') is None and expected.get('end_seconds') is None)
        row['semanticPass'] = (row['finishReason'] == 'stop' and row['independentExpectedMatchesRequested']
            and decision.get('singing_goal_status') == ('approved' if case['expectedAllowed'] else 'revise'))
        row['validationScope'] = 'Source/ref/range subset of actual runtime acceptance; not a claim of executing the full Unity parser.'
    except Exception as error:
        row['error'] = str(error)
        row['sourceSubsetPass'] = False
        row['semanticPass'] = False
    row['elapsedSeconds'] = round(time.perf_counter() - started, 4)
    rows.append(row)
    args.output.write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding='utf-8')
    print(row['id'], row['elapsedSeconds'], 'semantic', row.get('semanticPass'),
          'source-subset', row.get('sourceSubsetPass'), row.get('decision', {}).get('singing_goal_status'), flush=True)
