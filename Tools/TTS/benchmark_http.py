"""Explicit local service benchmark; no playback, service mutation or Unity dependency."""
import argparse
import json
import pathlib
import struct
import time
import requests
import wave
import math
import array
from urllib.parse import urlparse

parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True)
parser.add_argument('--chunk', type=int, default=12)
parser.add_argument('--mode', type=int, default=3)
parser.add_argument('--seed', type=int, default=42)
parser.add_argument('--split', default='cut5')
parser.add_argument('--url', default='http://127.0.0.1:9880/tts')
parser.add_argument('--reference', default=str(pathlib.Path(__file__).resolve().parents[2] / 'Assets/Model/41041.wav'))
parser.add_argument('--extended', action='store_true')
parser.add_argument('--audio-directory', type=pathlib.Path)
args = parser.parse_args()
endpoint = urlparse(args.url)
if endpoint.scheme not in ('http', 'https') or not endpoint.hostname or endpoint.username or endpoint.password or endpoint.query or endpoint.fragment:
    parser.error('--url must be an HTTP(S) endpoint without credentials, query or fragment')
session = requests.Session()
session.trust_env = False
cases = [('warmup', '準備ができました。', 'ja'),
         ('ja-1', '今日も一日、よく頑張ったね。', 'ja'),
         ('zh-1', '今天辛苦了，先休息一下吧。', 'zh'),
         ('ja-2', '今日も一日、よく頑張ったね。', 'ja'),
         ('zh-2', '今天辛苦了，先休息一下吧。', 'zh')]
if args.extended:
    cases[1:1] = [('warmup-zh', '准备好了。', 'zh'), ('warmup-en', 'I am ready.', 'en')]
    cases += [('long-zh', '今天的事情已经忙完了，我们可以慢慢聊一会儿。你不用急着把所有事情都说清楚，我会认真听着，也会在你需要的时候陪你一起想办法。', 'zh'),
              ('long-ja', '今日はもう仕事が終わったんだね。少し休んでから、ゆっくり話そう。急いですべてを説明しなくても大丈夫。あなたの話を聞きながら、一緒に考えていくよ。', 'ja')]
result = {'scope': 'Direct HTTP stream without Unity or audio playback; extended cases warm all three languages.',
          'endpoint': args.url, 'reference': args.reference, 'extended': args.extended,
          'mode': args.mode, 'minChunkLength': args.chunk, 'seed': args.seed, 'split': args.split, 'rows': []}
for name, text, language in cases:
    payload = dict(ref_audio_path=args.reference.replace('\\', '/'),
                   prompt_text='夜が明けたら、私と一緒に、この辺りを散歩しましょう。',
                   prompt_lang='ja', text=text, text_lang=language, streaming_mode=args.mode,
                   media_type='wav', min_chunk_length=args.chunk, seed=args.seed, text_split_method=args.split)
    started = time.perf_counter()
    chunks = []
    received = bytearray()
    with session.post(args.url, json=payload, stream=True, timeout=(5, 90)) as response:
        response.raise_for_status()
        headers_at = time.perf_counter() - started
        for data in response.iter_content(chunk_size=None):
            if not data:
                continue
            received.extend(data)
            chunks.append({'seconds': time.perf_counter() - started, 'bytes': len(data), 'totalBytes': len(received)})
    complete = time.perf_counter() - started
    if received[:4] != b'RIFF' or received[8:12] != b'WAVE' or len(received) <= 44:
        raise RuntimeError('Expected streaming WAV with PCM, received ' + repr(received[:80]))
    channels, rate = struct.unpack_from('<HI', received, 22)
    bits = struct.unpack_from('<H', received, 34)[0]
    duration = (len(received) - 44) / (channels * rate * bits / 8)
    first_pcm = next(c['seconds'] for c in chunks if c['totalBytes'] > 44)
    pcm = array.array('h', received[44:])
    rms = math.sqrt(sum(float(v)*v for v in pcm)/len(pcm))/32768
    if rms < 0.00001:
        raise RuntimeError('Received effectively silent audio: ' + name)
    row = dict(name=name, text=text, language=language, headerSeconds=headers_at,
               firstPcmSeconds=first_pcm, completeSeconds=complete, audioSeconds=duration,
               realtimeFactor=complete/duration, rms=rms, peak=max(abs(v) for v in pcm)/32768,
               rate=rate, channels=channels, bits=bits, chunks=chunks)
    if args.audio_directory:
        args.audio_directory.mkdir(parents=True, exist_ok=True)
        audio_path=args.audio_directory/(name+'.wav')
        with wave.open(str(audio_path), 'wb') as wav:
            wav.setnchannels(channels);wav.setsampwidth(bits//8);wav.setframerate(rate);wav.writeframes(received[44:])
        row['audioFile']=str(audio_path)
    result['rows'].append(row)
    print(json.dumps({k:v for k,v in row.items() if k not in ('chunks', 'text')}), flush=True)
    output = pathlib.Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
