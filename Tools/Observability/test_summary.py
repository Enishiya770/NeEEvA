import json
from summarize_trace import summarize
def row(span,event,ms,schema=2,turn='turn'):
    return '[ConversationTrace] '+json.dumps(dict(turnId=turn,spanId=span,kind='test',event=event,elapsedMs=ms,schema=schema,utc='2026-09-18'))
lines=[row('root','capture.started',0),row('eot','eot.capture_closed',10000),
       row('r1','llm.send',10500),row('r1','llm.first_content_delta',11500),
       row('r2','llm.send',11000),row('r2','llm.first_content_delta',15000),
       row('root','audio.formal_playback_advanced',17000),row('old','eot.capture_closed',0,1,'old'),
       '[ConversationTrace] {', 'not a trace']
report=summarize(lines)
turn=next(t for t in report['turns'] if t['turnId']=='turn')
assert turn['eotToFormalPlaybackMs']==7000
assert turn['eotToFirstLlmSendMs']==500
assert [r['durations']['llmSendToContentMs'] for r in turn['spans']]==[1000,4000]
assert 'tts.first_pcm' in turn['missing']
assert report['malformedTraceLines']==1
assert next(t for t in report['turns'] if t['turnId']=='old')['eotToFormalPlaybackMs'] is None
for bad in ({'elapsedMs': 3, 'turnId': 'empty'}, [], {'elapsedMs': float('nan')}):
    report=summarize(['[ConversationTrace] '+json.dumps(bad)])
    assert report['malformedTraceLines']==1 and report['turns']==[]
print('PASS: 9 trace summary checks (mixed schema, nonzero EOT, overlapping requests, missing events, incomplete/malformed JSON)')

newlines=[row('llm','llm.send',100),row('llm','llm.first_public_candidate',300),
          row('review','goal.review_begin',500),row('review','goal.review_end',800)]
newlines[-1]=newlines[-1][:-1]+', "fields": {"outcome": "transport_cancelled"}}'
result=summarize(newlines)['turns'][0]
assert result['spans'][0]['durations']['llmSendToPublicCandidateMs']==200
assert result['spans'][1]['durations']['goalReviewMs']==300
assert result['spans'][1]['facts'][0]['fields']['outcome']=='transport_cancelled'
assert 'llm.first_public_candidate' not in result['missing']
legacy=summarize([row('llm','llm.send',100),row('llm','llm.first_valid_public_speech',300)])['turns'][0]
assert legacy['spans'][0]['durations']['llmSendToPublicCandidateMs']==200
print('PASS: 5 candidate-alias/review-outcome summary checks')


def fact(span,event,ms,fields):
    data=json.loads(row(span,event,ms).split('] ',1)[1]);data['fields']=fields
    return '[ConversationTrace] '+json.dumps(data)

closure=[row('h','review.handoff_begin',0),fact('h','review.handoff_end',400,{'outcome':'dispatched_fresh'}),
    row('e','eou.requested',100),fact('e','eou.request_end',300,{'outcome':'caller_cancelled'}),
    fact('c','eou.consumer_checked',500,{'sameText':False,'pendingText':True}),
    fact('c','eou.consumer_end',510,{'outcome':'input_changed'}),
    fact('d','speech.discarded.queue',600,{'reason':'motion_rejected','formalPlaybackCompleted':True})]
spans={r['spanId']:r for r in summarize(closure)['turns'][0]['spans']}
assert spans['h']['durations']['reviewHandoffMs']==400
assert spans['e']['durations']['eouRequestMs']==200
assert spans['e']['facts'][0]['fields']['outcome']=='caller_cancelled'
assert spans['c']['durations']['eouConsumerMs']==10
assert spans['c']['facts'][0]['fields']['pendingText'] is True
assert spans['d']['facts'][0]['fields']['formalPlaybackCompleted'] is True
print('PASS: 6 handoff/EOU-consumption/partial-playback summary checks')
