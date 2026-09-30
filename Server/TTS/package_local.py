"""Package the exact active local GPT-SoVITS source and v2 inference assets."""
import argparse
import hashlib
import json
import tarfile
from pathlib import Path


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(4 * 1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--source', required=True)
    p.add_argument('--reference', required=True)
    p.add_argument('--output', required=True)
    args = p.parse_args()
    source, out = Path(args.source), Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    selected = {}
    for name in ('api_v2.py', 'config.py', 'requirements.txt', 'LICENSE'):
        selected[name] = source / name
    model_dirs = ('chinese-roberta-wwm-ext-large', 'chinese-hubert-base', 'fast_langdetect')
    model_files = ('s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt', 's2G2333k.pth')
    for parent in ('GPT_SoVITS', 'tools'):
        for f in (source / parent).rglob('*'):
            if not f.is_file() or any(x in ('__pycache__', '~_pycache__', '.git') for x in f.parts):
                continue
            rel = f.relative_to(source).as_posix()
            if f.suffix.lower() in ('.pyc', '.pyd', '.dll', '.exe'):
                continue
            if rel.startswith('GPT_SoVITS/pretrained_models/'):
                tail = f.relative_to(source / 'GPT_SoVITS/pretrained_models')
                if not (tail.parts[0] in model_dirs or (tail.parts[0] == 'gsv-v2final-pretrained' and f.name in model_files)):
                    continue
            if rel.startswith(('tools/asr/models/', 'tools/uvr5/uvr5_weights/')):
                continue
            if rel.startswith('tools/') and f.suffix.lower() in ('.pth', '.pt', '.ckpt', '.onnx', '.zip'):
                continue
            selected[rel] = f
    for f in (source / 'runtime/nltk_data').rglob('*'):
        if f.is_file(): selected['resources/nltk_data/' + f.relative_to(source / 'runtime/nltk_data').as_posix()] = f
    dictionary = source / 'runtime/Lib/site-packages/pyopenjtalk/open_jtalk_dic_utf_8-1.11'
    for f in dictionary.rglob('*'):
        if f.is_file(): selected['resources/open_jtalk_dic_utf_8-1.11/' + f.relative_to(dictionary).as_posix()] = f
    selected['refs/41041.wav'] = Path(args.reference)
    entries = [{'path': name, 'bytes': f.stat().st_size, 'sha256': digest(f)} for name, f in sorted(selected.items())]
    identity = hashlib.sha256(json.dumps(entries, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    manifest = {'version': 1, 'source_name': source.name, 'release_sha256': identity,
                'source_policy': 'Exact local files; no upstream checkout or model upgrade',
                'precision': 'float32', 'model_version': 'v2', 'reference': 'refs/41041.wav',
                'file_count': len(entries), 'total_bytes': sum(e['bytes'] for e in entries), 'files': entries}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: manifest[k] for k in ('release_sha256', 'file_count', 'total_bytes')}), flush=True)
    with tarfile.open(out / 'release.tar', 'w') as archive:
        for name, f in sorted(selected.items()): archive.add(f, arcname=name, recursive=False)
        archive.add(out / 'manifest.json', arcname='manifest.json')
    print(json.dumps({'archive': str(out / 'release.tar'), 'bytes': (out / 'release.tar').stat().st_size}), flush=True)


if __name__ == '__main__': main()
