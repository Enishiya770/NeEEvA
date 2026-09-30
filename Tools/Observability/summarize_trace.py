"""Summarize [ConversationTrace] JSON lines (schema 1/2). Read-only input; no transcript export."""
import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

def summarize(lines):
    turns = {}; malformed = 0
    for line in lines:
        marker = '[ConversationTrace] '
        if not line.startswith(marker):
            continue
        try:
            event = json.loads(line[len(marker):])
            if not isinstance(event, dict) or not isinstance(event.get('elapsedMs'), (int, float)) or not math.isfinite(event['elapsedMs']):
                raise ValueError('missing timestamp')
            if not all(isinstance(event.get(k), str) for k in ('turnId', 'spanId', 'kind', 'event')):
                raise ValueError('missing identifiers')
            if not isinstance(event.get('schema', 1), int):
                raise ValueError('invalid schema')
            turns.setdefault(event['turnId'], []).append(event)
        except (ValueError, KeyError, TypeError):
            malformed += 1
    result = []
    def first(events, name):
        return next((e['elapsedMs'] for e in events if (e['event'] == name or (name == 'llm.first_public_candidate' and e['event'] == 'llm.first_valid_public_speech'))), None)
    def difference(start, end):
        return round(end-start, 3) if start is not None and end is not None and end >= start else None
    for turn_id, events in sorted(turns.items(), key=lambda pair: pair[1][0].get('utc', '')):
        events.sort(key=lambda e: e['elapsedMs'])
        eot = first(events, 'eot.capture_closed')
        spans = defaultdict(list)
        for e in events:
            spans[e['spanId']].append(e)
        rows = []
        pairs = {
            'llmSendToContentMs': ('llm.send', 'llm.first_content_delta'),
            'llmSendToPublicCandidateMs': ('llm.send', 'llm.first_public_candidate'),
            'firstThoughtBlockMs': ('role.thought_opened', 'role.thought_closed'),
            'roomPreparationMs': ('room.preparation_send', 'room.preparation_complete'),
            'roomHttpMs': ('room.http_send', 'room.http_complete'),
            'asrHttpMs': ('asr.send', 'asr.transport_complete'),
            'heldUntilGenerationCompleteMs': ('speech.first_buffered', 'speech.generation_complete'),
            'speechReviewMs': ('speech.review_begin', 'speech.review_complete'),
            'ttsSendToPcmMs': ('tts.send', 'tts.first_pcm'),
            'ttsPcmToPlayCallMs': ('tts.first_pcm', 'audio.source_play_called'),
            'svcHttpMs': ('svc.send', 'svc.transport_complete'),
            'goalReviewMs': ('goal.review_begin', 'goal.review_end'),
            'reviewHttpMs': ('review.http_send', 'review.http_complete'),
            'reviewHandoffMs': ('review.handoff_begin', 'review.handoff_end'),
            'eouRequestMs': ('eou.requested', 'eou.request_end'),
            'eouConsumerMs': ('eou.consumer_checked', 'eou.consumer_end')
        }
        for span_id, group in spans.items():
            durations = {name: difference(first(group, a), first(group, b)) for name, (a,b) in pairs.items()}
            durations = {k:v for k,v in durations.items() if v is not None}
            fields = [{'event': e['event'], 'fields': e.get('fields')} for e in group if e.get('fields') and
                                     e['event'] in ('room.preparation_complete', 'room.preparation_reused', 'room.preparation_discarded',
                                     'room.http_complete', 'room.decision_resolved', 'speech.gate_selected', 'speech.review_complete',
                                     'svc.transport_complete', 'svc.input', 'singing.material_resolved', 'llm.send', 'asr.result',
                                     'delivery.round_closed', 'delivery.wait_ended', 'goal.review_end',
                                     'review.http_complete', 'review.http_cancelled', 'eou.result', 'eou.decision_rejected',
                                     'review.handoff_end', 'goal.execution_handoff', 'goal.execution_request', 'goal.cancellation_reply',
                                     'eou.request_end', 'eou.decision', 'eou.consumer_checked', 'eou.consumer_end',
                                     'eou.coverage_wait', 'eou.apply_rejected')
                      or (e.get('fields') and e['event'].startswith(('speech.withheld.', 'speech.discarded.', 'speech.queued.')))]
            if durations or fields:
                rows.append({'spanId': span_id, 'kind': group[0]['kind'], 'durations': durations, 'facts': fields})
        names = {e['event'] for e in events}
        if 'llm.first_valid_public_speech' in names: names.add('llm.first_public_candidate')
        result.append({'turnId': turn_id, 'utc': events[0].get('utc'), 'schema': max(e.get('schema',1) for e in events),
                       'eventCount': len(events), 'eotElapsedMs': eot,
                       'eotToFormalPlaybackMs': difference(eot,first(events,'audio.formal_playback_advanced')),
                       'eotToFirstLlmSendMs': difference(eot,first(events,'llm.send')),
                       'missing': [n for n in ('eot.capture_closed','asr.final_callback','llm.send','llm.first_content_delta',
                                               'llm.first_public_candidate','tts.send','tts.first_pcm','audio.formal_playback_advanced') if n not in names],
                       'spans': rows})
    return {'scope': 'Observed events only. Missing is unknown, first content is a provider chunk, EOT is capture close. Schema 2 begins at capture; subtract EOT explicitly. Thought is intentional role behavior. No counterfactual speedup estimate.',
            'malformedTraceLines': malformed, 'turns': result}

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log',type=Path);parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args()
    if args.log.resolve()==args.out.resolve():parser.error('Output must differ from input log')
    with args.log.open(encoding='utf-8-sig',errors='replace') as stream: report=summarize(stream)
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(f"{len(report['turns'])} turns; {report['malformedTraceLines']} malformed Trace lines; saved {args.out}")
if __name__=='__main__': main()
