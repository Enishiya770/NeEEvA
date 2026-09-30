"""Real server smoke: PCM, three languages, disconnect and queued successor."""
import argparse
import concurrent.futures
import json
import math
import pathlib
import struct
import time
import wave
import array
import requests

p = argparse.ArgumentParser()
p.add_argument('--url', default='http://192.168.114.163:9880')
p.add_argument('--output', default='Server/TTS/runtime/smoke.json')
p.add_argument('--reference', default='/home/spark_root_001/neeeva-voice/tts/release/refs/41041.wav')
args = p.parse_args()
out = pathlib.Path(args.output)
out.parent.mkdir(parents=True, exist_ok=True)
result = {'scope': 'Real network and CUDA synthesis; WAV validation only, not human listening or Unity playback', 'rows': []}


def client():
    s = requests.Session()
    s.trust_env = False
    return s


def health():
    with client() as s:
        r = s.get(args.url + '/health', timeout=5)
        r.raise_for_status()
        return r.json()


def synth(name, text, language, abort=False, mode=3):
    payload = dict(text=text, text_lang=language, ref_audio_path=args.reference,
                   prompt_text='夜が明けたら、私と一緒に、この辺りを散歩しましょう。',
                   prompt_lang='ja', streaming_mode=mode, media_type='wav',
                   min_chunk_length=12, seed=42, text_split_method='cut5')
    start = time.perf_counter()
    pcm = bytearray()
    first = None
    with client() as s, s.post(args.url + '/tts', json=payload, stream=True, timeout=(5, 180)) as r:
        r.raise_for_status()
        for chunk in r.iter_content(chunk_size=None):
            pcm.extend(chunk)
            if len(pcm) > 44 and first is None:
                first = time.perf_counter() - start
                if abort: break
    elapsed = time.perf_counter() - start
    if pcm[:4] != b'RIFF' or pcm[8:12] != b'WAVE' or len(pcm) <= 44:
        raise AssertionError(name + ': missing WAV PCM: ' + repr(pcm[:200]))
    channels, rate = struct.unpack_from('<HI', pcm, 22)
    bits = struct.unpack_from('<H', pcm, 34)[0]
    if bits != 16 or channels != 1: raise AssertionError('Expected mono PCM16')
    data = array.array('h', pcm[44:])
    rms = math.sqrt(sum(float(x) ** 2 for x in data) / len(data)) / 32768
    if not math.isfinite(rms) or rms < 1e-5: raise AssertionError(name + ': silent/nonfinite audio')
    audio = len(data) / rate
    path = out.parent / (name + '.wav')
    with wave.open(str(path), 'wb') as wav:
        wav.setnchannels(channels); wav.setsampwidth(2); wav.setframerate(rate); wav.writeframes(pcm[44:])
    row = {'name': name, 'language': language, 'first_pcm_seconds': first,
           'elapsed_seconds': elapsed, 'audio_seconds': audio, 'rtf': elapsed / audio,
           'rms': rms, 'rate': rate, 'cancelled_as_requested': abort, 'audio_file': str(path)}
    print(json.dumps(row), flush=True)
    return row


try:
    result['health_before'] = health()
    for name, text, language in [('warmup-ja', '準備ができました。', 'ja'),
                                 ('short-zh', '今天辛苦了，先休息一下吧。', 'zh'),
                                 ('short-en', 'I am ready to talk with you.', 'en'),
                                 ('short-ja', '今日も一日、よく頑張ったね。', 'ja')]:
        result['rows'].append(synth(name, text, language))
    result['rows'].append(synth('cancel-long', '这是一段用于中途取消的语音。' * 20, 'zh', abort=True))
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        queued = [pool.submit(synth, 'successor-zh', '已经停止上一段，现在播放新的句子。', 'zh'),
                  pool.submit(synth, 'successor-ja', '新しい会話を始めましょう。', 'ja')]
        result['rows'].extend(f.result(timeout=180) for f in queued)
    for _ in range(40):
        result['health_after'] = health()
        if not result['health_after']['busy']: break
        time.sleep(.25)
    if result['health_after']['busy']: raise AssertionError('Server remained busy after requests')
    if result['health_after']['release_sha256'] != result['health_before']['release_sha256']:
        raise AssertionError('Release changed during smoke')
    result['status'] = 'passed'
except BaseException as exc:
    result['status'] = 'failed'
    result['error'] = repr(exc)
    raise
finally:
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
