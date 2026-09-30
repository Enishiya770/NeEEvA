"""Single-owner transport around the unmodified, frozen GPT-SoVITS API.

The lease covers generation, socket streaming and generator cleanup. A cancelled
client cannot release shared model ownership while a PCM worker still runs.
"""
import asyncio
import json
import os
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
RELEASE = ROOT / 'release'
os.chdir(RELEASE)
sys.path.insert(0, str(RELEASE))
runtime = ROOT / 'runtime'
runtime.mkdir(exist_ok=True)
# Upstream persists its config when loading weights; keep the frozen source
# read-only and give that behavior a private runtime copy.
active_config = runtime / 'tts_infer.yaml'
shutil.copyfile(RELEASE / 'GPT_SoVITS/configs/tts_infer.yaml', active_config)
sys.argv = ['api_v2.py', '-c', str(active_config)]
os.environ.setdefault('NLTK_DATA', str(RELEASE / 'resources/nltk_data'))
os.environ.setdefault('OPEN_JTALK_DICT_DIR', str(RELEASE / 'resources/open_jtalk_dic_utf_8-1.11'))

import anyio
import torch
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse
import api_v2 as upstream

MANIFEST = json.loads((RELEASE / 'manifest.json').read_text())
APP = FastAPI(title='NeEEvA frozen GPT-SoVITS')
LOCK = asyncio.Lock()
STATE = {'busy': False, 'completed': 0, 'disconnected': 0, 'failed': 0}
ACTIVE = None
ORIGINAL_RUN = upstream.tts_pipeline.run
MODEL_STATE = {}
for name in ('t2s_model', 'vits_model'):
    parameter = next(getattr(upstream.tts_pipeline, name).parameters())
    MODEL_STATE[name] = {'device': str(parameter.device), 'dtype': str(parameter.dtype)}
    if parameter.device.type != 'cuda' or parameter.dtype != torch.float32:
        raise RuntimeError('Expected CUDA FP32 model: ' + name + ' ' + str(MODEL_STATE[name]))


def tracked_run(req):
    global ACTIVE
    ACTIVE = ORIGINAL_RUN(req)
    return ACTIVE


upstream.tts_pipeline.run = tracked_run


async def release_owner():
    global ACTIVE
    generator, ACTIVE = ACTIVE, None
    try:
        if generator is not None:
            # Shield cleanup, including waiting for a currently executing chunk.
            with anyio.CancelScope(shield=True):
                await anyio.to_thread.run_sync(generator.close, abandon_on_cancel=False)
    finally:
        STATE['busy'] = False
        LOCK.release()


class OwnedStream(StreamingResponse):
    async def __call__(self, scope, receive, send):
        try:
            await super().__call__(scope, receive, send)
            STATE['completed'] += 1
        except BaseException:
            STATE['failed'] += 1
            raise
        finally:
            # StreamingResponse awaits each non-abandoned threadpool next().
            # Closing the retained model generator now is safe and deterministic.
            with anyio.CancelScope(shield=True):
                try:
                    await self.body_iterator.aclose()
                finally:
                    await release_owner()

    async def listen_for_disconnect(self, receive):
        await super().listen_for_disconnect(receive)
        STATE['disconnected'] += 1


@APP.get('/health')
async def health():
    return {'ok': True, 'ready': True, **STATE,
            'release_sha256': MANIFEST['release_sha256'],
            'source_files': MANIFEST['file_count'], 'model_version': upstream.tts_config.version,
            'models': MODEL_STATE,
            'model_hashes': {Path(f['path']).name: f['sha256'] for f in MANIFEST['files']
                             if '/gsv-v2final-pretrained/' in f['path']},
            'device': str(upstream.tts_config.device), 'cuda_available': torch.cuda.is_available(),
            'gpu': torch.cuda.get_device_name(0), 'torch': torch.__version__,
            'cuda': torch.version.cuda, 'precision': 'float16' if upstream.tts_config.is_half else 'float32',
            'cpu_threads': torch.get_num_threads(),
            'reference_path': str(RELEASE / 'refs/41041.wav')}


@APP.api_route('/tts', methods=['POST', 'GET'])
async def tts(request: Request):
    try:
        incoming = await request.json() if request.method == 'POST' else dict(request.query_params)
        payload = upstream.TTS_Request(**incoming).model_dump()
    except Exception as exc:
        return JSONResponse({'message': 'invalid tts request', 'detail': str(exc)}, status_code=422)
    # Body is consumed before awaiting ownership, so disconnect can be checked
    # without accidentally consuming a request-body message.
    try:
        await asyncio.wait_for(LOCK.acquire(), timeout=120)
    except TimeoutError:
        return JSONResponse({'message': 'TTS queue timed out'}, status_code=503)
    STATE['busy'] = True
    handed_off = False
    try:
        if await request.is_disconnected():
            return JSONResponse({'message': 'client disconnected'}, status_code=499)
        # Non-streaming mode also runs away from the ASGI event loop, allowing
        # /health to remain responsive. Never abandon a shared-model worker.
        response = await anyio.to_thread.run_sync(
            lambda: asyncio.run(upstream.tts_handle(payload)), abandon_on_cancel=False)
        if isinstance(response, StreamingResponse):
            owned = OwnedStream(response.body_iterator, status_code=response.status_code,
                                headers=dict(response.headers), media_type=response.media_type)
            handed_off = True
            return owned
        STATE['completed'] += 1
        return response
    finally:
        if not handed_off:
            with anyio.CancelScope(shield=True):
                await release_owner()


if __name__ == '__main__':
    import uvicorn
    uvicorn.run(APP, host='0.0.0.0', port=9880, log_level='info', workers=1)
