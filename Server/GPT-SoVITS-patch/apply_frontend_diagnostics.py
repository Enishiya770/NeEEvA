"""Apply the reviewed mixed-script frontend and streaming diagnostics fix.

Requires an explicit installation and backup directory. Refuses unknown source;
does not start/stop services. Kept in Git because the installed runtime is ignored.
"""
from pathlib import Path
import argparse,ast,hashlib,json,shutil

OLD_LANGUAGE='''                            # 因无法区别中日韩文汉字,以用户输入为准
                            langlist.append(language)'''
NEW_LANGUAGE='''                            # NeEEvA: unambiguous kana/Hangul in a proper name
                            # must retain its frontend. Han-only text still uses
                            # the explicitly declared sentence language.
                            explicit_script = (
                                tmp["lang"] == "ja" and re.search(r"[\\u3040-\\u30ff\\uff66-\\uff9f]", tmp["text"])
                            ) or (tmp["lang"] == "ko" and re.search(r"[\\uac00-\\ud7af\\u1100-\\u11ff]", tmp["text"]))
                            langlist.append(tmp["lang"] if explicit_script else language)'''
OLD_EMPTY='''                    bert = self.get_bert_inf(phones, word2ph, norm_text, lang)
                    phones_list.append(phones)'''
NEW_EMPTY='''                    if not phones:
                        if any(c.isalnum() for c in textlist[i]):
                            raise ValueError("Text frontend produced no phonemes for a nonempty language span: " + lang)
                        continue  # punctuation-only segment; no BERT tensor exists
                    bert = self.get_bert_inf(phones, word2ph, norm_text, lang)
                    phones_list.append(phones)'''
OLD_CAT='''                bert = torch.cat(bert_list, dim=1)'''
NEW_CAT='''                if not bert_list:
                    raise ValueError("Text frontend produced no pronounceable segments")
                bert = torch.cat(bert_list, dim=1)'''
DIAGNOSTICS='''
# NeEEvA streaming receipt: preserve the HTTP failure when synthesis aborts
# after status 200. Never append a success terminator or fabricated silence.
def _neeeva_diagnosed_stream(iterator, request_id):
    import json, time, datetime
    started = time.monotonic()
    received = 0
    outcome = "cancelled"
    failure = None
    try:
        for chunk in iterator:
            received += len(chunk)
            yield chunk
        outcome = "completed"
    except Exception as error:
        outcome = "failed"
        failure = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        print("[TTS/ServerStreamReceipt] " + json.dumps({
            "requestId": request_id, "outcome": outcome, "bytesYielded": received,
            "seconds": time.monotonic() - started, "failure": failure,
            "utc": datetime.datetime.now(datetime.timezone.utc).isoformat()
        }, ensure_ascii=False), flush=True)

'''

LIFECYCLE='''
# NeEEvA: own both synchronous generators through ASGI cancellation. The
# default threadpool adapter can leave them open until cyclic GC much later.
from starlette.responses import StreamingResponse as _NeEEvABaseStream

class _NeEEvAStreamingResponse(_NeEEvABaseStream):
    def __init__(self, iterator, request_id, pipeline, **kwargs):
        self._neeeva_id = request_id
        super().__init__(self._owned_chunks(iterator, pipeline), **kwargs)

    async def stream_response(self, send):
        import anyio
        try:
            await super().stream_response(send)
        finally:
            # Cancellation may happen in ASGI send, while the async generator
            # is suspended at yield. async-for itself does not close it.
            with anyio.CancelScope(shield=True):
                await self.body_iterator.aclose()

    async def listen_for_disconnect(self, receive):
        import json, time
        while True:
            if (await receive())["type"] == "http.disconnect":
                print("[TTS/ServerCancellation] " + json.dumps({"requestId": self._neeeva_id,
                    "phase": "disconnect-observed", "monotonic": time.monotonic()}), flush=True)
                break

    async def _owned_chunks(self, iterator, pipeline):
        import anyio, json, time
        from starlette.concurrency import run_in_threadpool
        sentinel = object()
        def advance():
            return next(iterator, sentinel)
        try:
            while True:
                # Do not abandon a running native synthesis thread: concurrent
                # generator.close() is illegal and must not affect a newer job.
                chunk = await run_in_threadpool(advance)
                if chunk is sentinel:
                    break
                yield chunk
        finally:
            began = time.monotonic()
            def close_owned():
                for owned in (iterator, pipeline):
                    close = getattr(owned, "close", None)
                    if close is not None:
                        close()
            with anyio.CancelScope(shield=True):
                await run_in_threadpool(close_owned)
            print("[TTS/ServerCancellation] " + json.dumps({"requestId": self._neeeva_id,
                "phase": "generators-closed", "closeSeconds": time.monotonic()-began}), flush=True)

'''

def replace_known(text,old,new):
    if new in text:return text
    if text.count(old)!=1:raise ValueError('Unknown/ambiguous source anchor: '+old[:90])
    return text.replace(old,new,1)

def patched_frontend(text):
    for old,new in [(OLD_LANGUAGE,NEW_LANGUAGE),(OLD_EMPTY,NEW_EMPTY),(OLD_CAT,NEW_CAT)]:
        text=replace_known(text,old,new)
    ast.parse(text)
    return text

def patched_api(text):
    text=replace_known(text,'sys.path.append(now_dir)\nsys.path.append("%s/GPT_SoVITS" % (now_dir))',
        '# NeEEvA: bundled Python may have stale absolute paths in users.pth.\n'
        '# Load the explicitly launched installation before those old copies.\n'
        'sys.path.insert(0, now_dir)\nsys.path.insert(1, "%s/GPT_SoVITS" % (now_dir))')
    text=replace_known(text,'from fastapi import FastAPI, Response','from fastapi import FastAPI, Response, Request')
    text=replace_known(text,'async def tts_post_endpoint(request: TTS_Request):\n    req = request.dict()',
        'async def tts_post_endpoint(request: TTS_Request, http_request: Request):\n    req = request.dict()\n    req["_neeeva_request_id"] = http_request.headers.get("X-Request-ID", "")[:80]')
    text=replace_known(text,'        tts_generator = tts_pipeline.run(req)',
        '        import uuid\n        request_id = req.pop("_neeeva_request_id", "") or uuid.uuid4().hex\n        tts_generator = tts_pipeline.run(req)')
    old='''                streaming_generator(
                    tts_generator,
                    media_type,
                    stream_level,
                ),'''
    new='''                _neeeva_diagnosed_stream(streaming_generator(
                    tts_generator,
                    media_type,
                    stream_level,
                ), request_id),'''
    text=replace_known(text,old,new)
    if 'def _neeeva_diagnosed_stream(' not in text:
        text=replace_known(text,'async def tts_handle(req: dict):',DIAGNOSTICS+'async def tts_handle(req: dict):')
    if 'class _NeEEvAStreamingResponse(' not in text:
        text=replace_known(text,'async def tts_handle(req: dict):',LIFECYCLE+'async def tts_handle(req: dict):')
    text=replace_known(text,'return StreamingResponse(\n                _neeeva_diagnosed_stream(',
        'return _NeEEvAStreamingResponse(\n                _neeeva_diagnosed_stream(')
    text=replace_known(text, '                ), request_id),\n                media_type=',
        '                ), request_id), request_id, tts_generator,\n                media_type=')
    text=replace_known(text, '            return Response(audio_data, media_type=f"audio/{media_type}")',
        '            tts_generator.close()\n'
        '            import json\n'
        '            print("[TTS/ServerPreparedReceipt] " + json.dumps({"requestId": request_id, "outcome": "completed", "bytesPrepared": len(audio_data), "playbackConfirmed": False}), flush=True)\n'
        '            return Response(audio_data, media_type=f"audio/{media_type}")')
    ast.parse(text)
    return text

def main():
    p=argparse.ArgumentParser();p.add_argument('--root',required=True,type=Path);p.add_argument('--backup',required=True,type=Path)
    a=p.parse_args();root=a.root.resolve();a.backup.mkdir(parents=True,exist_ok=True)
    changes=[];prepared=[]
    for relative,patch in [('GPT_SoVITS/TTS_infer_pack/TextPreprocessor.py',patched_frontend),('api_v2.py',patched_api)]:
        target=(root/relative).resolve()
        if not target.is_relative_to(root):raise ValueError('Target escaped explicit installation')
        raw=target.read_bytes();old=raw.decode('utf-8-sig').replace('\r\n','\n');new=patch(old)
        prepared.append((relative,target,raw,old,new))
    # Validate every source before touching either installed module.
    for relative,target,raw,old,new in prepared:
        backup=a.backup/Path(relative).name
        if not backup.exists():shutil.copy2(target,backup)
        if new!=old:target.write_bytes(new.encode('utf-8'))
        changes.append(dict(path=str(target),changed=new!=old,backupSha256=hashlib.sha256(backup.read_bytes()).hexdigest(),beforeSha256=hashlib.sha256(raw).hexdigest(),afterSha256=hashlib.sha256(target.read_bytes()).hexdigest()))
    (a.backup/'patch-manifest.json').write_text(json.dumps(changes,indent=2),encoding='utf-8')
    print(json.dumps(changes,indent=2))

if __name__=='__main__':main()
