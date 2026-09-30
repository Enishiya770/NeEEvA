import json,time,urllib.request,subprocess,sys
from pathlib import Path
root=Path(__file__).resolve().parents[3]
out=root/'work/intent-closure-repair-20260926';out.mkdir(parents=True,exist_ok=True)
protocol=json.loads((out/'actor-protocol.json').read_text(encoding='utf8'))
exe=out/'actor-semantic/Regression.dll'
cases=[json.loads(x) for x in Path(__file__).with_name('cases.jsonl').read_text(encoding='utf8').splitlines() if x]
def request(case,review):
    body={'model':'qwen3.6-35b-a3b','stream':False,'enable_thinking':False,'temperature':0,'max_tokens':384,
      'response_format':protocol['review_response_format' if review else 'response_format'],
      'messages':[{'role':'system','content':protocol['reviewSystem' if review else 'system']},{'role':'user','content':json.dumps({'context':protocol['context'],'draft':case['draft'],'current_user':case['user']},ensure_ascii=False)}]}
    started=time.monotonic()
    req=urllib.request.Request('http://192.168.114.175:8888/v1/chat/completions',data=json.dumps(body,ensure_ascii=False).encode(),headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(req,timeout=35) as response: envelope=json.loads(response.read())
    elapsed=time.monotonic()-started
    choice=envelope['choices'][0];raw=choice.get('message',{}).get('content','')
    rawpath=out/('actor-'+case['id']+('-review' if review else '-initial')+'-raw.json');draftpath=out/'actor-latest-draft.txt'
    rawpath.write_text(raw,encoding='utf8');draftpath.write_text(case['draft'],encoding='utf8')
    command=['dotnet',str(exe),'--classify',str(rawpath),str(draftpath)]+(['review'] if review else [])
    parsed=json.loads(subprocess.check_output(command,encoding='utf8'))
    return {'seconds':elapsed,'finish':choice.get('finish_reason'),'raw':raw,'parsed':parsed,'usage':envelope.get('usage')}
suffix='-final' if '--final' in sys.argv else ''
if suffix: cases=[c for c in cases if c['id'] in ('live01','live04','live09','live13','refusal-zh','perception-ja','head-act','head-claim','agency','hypothesis')]
results=[]
for case in cases:
    row=dict(case)
    try:
      row['initial']=request(case,False)
      initial=row['initial']['parsed']
      if initial['reviewRequired']: row['review']=request(case,True)
      final=row.get('review',row['initial'])['parsed'];decision=final.get('decision')
      status=final.get('status') or ('non_action' if decision and decision['Kind']=='none' else 'reported_action' if decision and decision['Kind']=='claim' else 'adopted_action' if decision and decision['Kind'] in ('act','wait','cancel','replan') else 'unresolved')
      row['actual']=status;row['passed']=final['valid'] and status==case['expected'] and all(row[k]['finish']=='stop' for k in ('initial','review') if k in row)
    except Exception as e:
      row['passed']=False;row['error']=str(e)
      (out/'actor-live-error.json').write_text(json.dumps(row,ensure_ascii=False,indent=2),encoding='utf8')
      raise
    results.append(row)
    (out/('actor-live'+suffix+'-results.json')).write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf8')
    print(case['id'],row['passed'],row.get('actual'),round(sum(row[k]['seconds'] for k in ('initial','review') if k in row),3),flush=True)
print('DONE',sum(x['passed'] for x in results),'/',len(results),'requests',sum(1+('review' in x) for x in results),flush=True)
