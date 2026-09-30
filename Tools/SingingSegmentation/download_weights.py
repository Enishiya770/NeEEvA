"""Download the official PANNs SED checkpoint to an explicit isolated path."""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import time
import urllib.request

URL = 'https://zenodo.org/api/records/3987831/files/Cnn14_DecisionLevelMax_mAP=0.385.pth/content'
SIZE = 327428481
MD5 = '70539c43c18b6a289b3199c503a82c5a'  # Official Zenodo record 3987831 API.


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    path = args.output.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and hashlib.md5(path.read_bytes()).hexdigest() == MD5:
        print('Official checksum already verified', flush=True)
        return
    parts = path.parent / (path.name + '.parts')
    parts.mkdir(exist_ok=True)
    chunk = 8 * 1024 * 1024

    def get(start):
        end = min(SIZE, start + chunk) - 1
        part = parts / str(start)
        if part.exists() and part.stat().st_size == end - start + 1:
            return part
        subparts=[]
        # Some WAN transfers stalled mid-body at 8 MiB. Preserve successful
        # original chunks and resume missing ones with bounded 1 MiB requests.
        for lo in range(start,end+1,1024*1024):
            hi=min(end,lo+1024*1024-1)
            sub=parts/f'{start}.{lo}.partial'
            if not sub.exists() or sub.stat().st_size!=hi-lo+1:
                for attempt in range(4):
                    try:
                        req=urllib.request.Request(URL,headers={'Range':f'bytes={lo}-{hi}'})
                        with urllib.request.urlopen(req,timeout=20) as response:
                            if response.status!=206 or response.headers.get('Content-Range')!=f'bytes {lo}-{hi}/{SIZE}':
                                raise ValueError('Server did not return exact byte range')
                            data=response.read()
                        if len(data)!=hi-lo+1:raise ValueError('Incomplete range')
                        sub.write_bytes(data)
                        break
                    except Exception as error:
                        print(f'retry {lo} attempt {attempt+1}: {type(error).__name__}',flush=True)
                        if attempt==3:raise
                        time.sleep(2+attempt*2)
            subparts.append(sub)
        with part.open('wb') as output:
            for sub in subparts:output.write(sub.read_bytes())
        print(f'part {start} completed',flush=True)
        return part

    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        ordered = list(executor.map(get, range(0, SIZE, chunk)))
    pending = path.with_suffix(path.suffix + '.verified-download')
    with pending.open('wb') as output:
        for part in ordered:
            output.write(part.read_bytes())
    data = pending.read_bytes()
    if len(data) != SIZE or hashlib.md5(data).hexdigest() != MD5:
        raise ValueError('Official checkpoint checksum mismatch; refusing to install')
    pending.replace(path)
    manifest = {'url': URL, 'bytes': SIZE, 'md5': MD5,
                'sha256': hashlib.sha256(data).hexdigest(),
                'elapsed_seconds': time.perf_counter()-started}
    path.with_suffix('.manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(manifest), flush=True)


if __name__ == '__main__':
    main()
