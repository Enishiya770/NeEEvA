"""Real 9882 audio and cancellation checks; saves WAV, never plays it."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import io
import json
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid

import numpy as np
import soundfile as sf

HTTP=urllib.request.build_opener(urllib.request.ProxyHandler({}))


def request(base,path,data=None,headers=None,timeout=180):
    req=urllib.request.Request(base+path,data=data,headers=headers or {})
    with HTTP.open(req,timeout=timeout) as response:
        return response.read(),dict(response.headers)


def json_request(base,path,post=False):
    return json.loads(request(base,path,b"" if post else None)[0])


def wav(audio,rate):
    b=io.BytesIO();sf.write(b,audio,rate,format="WAV",subtype="PCM_16");return b.getvalue()


def convert(base,source,reference,identity):
    boundary="neeeva-"+uuid.uuid4().hex
    parts=[]
    fields={"target_path":reference,"request_id":identity,"performance_seed":"1234",
            "index_rate":"0.75","auto_f0_adjust":"true","max_seconds":"120"}
    for key,value in fields.items():
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n').encode())
    parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="source_audio"; filename="source.wav"\r\nContent-Type: audio/wav\r\n\r\n').encode()+source+b"\r\n")
    parts.append((f'--{boundary}--\r\n').encode())
    started=time.perf_counter()
    try:
        data,headers=request(base,"/convert",b"".join(parts),{"Content-Type":"multipart/form-data; boundary="+boundary},600)
        audio,rate=sf.read(io.BytesIO(data),always_2d=True,dtype="float32")
        assert np.isfinite(audio).all() and np.any(audio),"invalid or silent audio"
        headers={key.lower():value for key,value in headers.items()}
        assert headers.get("x-svc-complete")=="true",headers
        return {"status":200,"seconds":time.perf_counter()-started,"audio_seconds":len(audio)/rate,
                "rms":float(np.sqrt(np.mean(audio**2))),"sample_rate":rate,"headers":headers},data
    except urllib.error.HTTPError as error:
        return {"status":error.code,"seconds":time.perf_counter()-started,"error":error.read().decode("utf-8",errors="replace")},b""


def main():
    p=argparse.ArgumentParser();p.add_argument("--base",required=True);p.add_argument("--reference",required=True)
    p.add_argument("--source",required=True);p.add_argument("--output",required=True);p.add_argument("--cancel",action="store_true")
    args=p.parse_args();out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    audio,rate=sf.read(args.source,dtype="float32",always_2d=True)
    report={"scope":"Real HTTP conversion and numeric WAV validation; no audio playback or human listening", "base":args.base,"rows":[]}
    try:
        report["warmup"]=json_request(args.base,"/warmup",True)
        report["health_before"]=json_request(args.base,"/health")
        if args.cancel:
            models=report["health_before"].get("models",{})
            assert set(models)=={"rvc","hubert","rmvpe"},models
            assert all(v["device"].startswith("cuda") for v in models.values()),models
        for name,samples in (("short",audio[:rate*4]),("full",audio)):
            row,data=convert(args.base,wav(samples,rate),args.reference,"migration-"+uuid.uuid4().hex)
            row["name"]=name;row["source_seconds"]=len(samples)/rate
            assert row["status"]==200,row
            assert abs(row["audio_seconds"]-row["source_seconds"])<.3,row
            assert row["headers"].get("x-svc-backend")=="rvc-character-v2",row
            (out/(name+".wav")).write_bytes(data);report["rows"].append(row)
        if args.cancel:
            identity="cancel-"+uuid.uuid4().hex
            long_audio=np.tile(audio,(max(1,int(60*rate/len(audio))+1),1))[:60*rate]
            with ThreadPoolExecutor(max_workers=1) as executor:
                future=executor.submit(convert,args.base,wav(long_audio,rate),args.reference,identity)
                deadline=time.monotonic()+20
                while time.monotonic()<deadline:
                    if json_request(args.base,"/health").get("busy"):break
                    time.sleep(.05)
                else:raise AssertionError("request did not become busy")
                wrong=json_request(args.base,"/cancel/wrong-"+identity,True)
                assert not wrong["cancelled"],wrong
                correct=json_request(args.base,"/cancel/"+identity,True)
                assert correct["cancelled"],correct
                row,_=future.result(timeout=60)
                assert row["status"]==499,row
                report["cancellation"]={"wrong_id":wrong,"correct_id":correct,"request":row}
            row,data=convert(args.base,wav(audio[:rate*4],rate),args.reference,"successor-"+uuid.uuid4().hex)
            assert row["status"]==200,row
            row["name"]="successor";report["rows"].append(row);(out/"successor.wav").write_bytes(data)
        report["health_after"]=json_request(args.base,"/health")
        assert not report["health_after"]["busy"]
        report["passed"]=True
    except Exception as error:
        report.update(passed=False,error=str(error));raise
    finally:
        (out/"report.json").write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
        print(json.dumps({"passed":report.get("passed"),"error":report.get("error"),"rows":len(report["rows"])}))


if __name__=="__main__":main()
