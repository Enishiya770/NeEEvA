"""Serial decision-only replay of exported production prompt/schema. No audio actions."""
import argparse
import datetime
import json
from pathlib import Path
import time
import urllib.request

p=argparse.ArgumentParser()
p.add_argument('--cases',type=Path,required=True)
p.add_argument('--output',type=Path,required=True)
p.add_argument('--base-url',default='http://192.168.114.175:8888')
p.add_argument('--model',default='qwen3.6-35b-a3b')
args=p.parse_args()
cases=json.loads(args.cases.read_text(encoding='utf-8-sig'))
results=[]
args.output.parent.mkdir(parents=True,exist_ok=True)
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
for case in cases:
    wire=case.get('wire') or {'model':args.model,'messages':case['messages'],'response_format':case['response_format'],
          'temperature':0,'stream':False,'max_tokens':384,'enable_thinking':False,
          'chat_template_kwargs':{'enable_thinking':False}}
    start=time.perf_counter()
    row={'id':case['id'],'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
         'raw':'','error':None,'scope':case.get('scope','Production recording prompt/schema, synthetic counterexample facts, decision only; no playback.'),
         'requestWire':wire}
    try:
        request=urllib.request.Request(args.base_url.rstrip('/')+'/v1/chat/completions',
            data=json.dumps(wire,ensure_ascii=False).encode(),headers={'Content-Type':'application/json'})
        with opener.open(request,timeout=40) as response:
            result=json.loads(response.read().decode('utf-8'))
            row.update(httpStatus=response.status,responseEnvelope=result,raw=result['choices'][0]['message']['content'],
                finishReason=result['choices'][0].get('finish_reason'),usage=result.get('usage'))
    except Exception as error:
        row['error']=str(error)
    row['elapsedSeconds']=round(time.perf_counter()-start,4)
    results.append(row)
    args.output.write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
    print(row['id'],row['elapsedSeconds'],'error' if row['error'] else row.get('finishReason'),flush=True)
