import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parent / 'release'
manifest = json.loads((root / 'manifest.json').read_text())
for item in manifest['files']:
    f = root / item['path']
    h = hashlib.sha256()
    with f.open('rb') as stream:
        for data in iter(lambda: stream.read(4 * 1024 * 1024), b''): h.update(data)
    if f.stat().st_size != item['bytes'] or h.hexdigest() != item['sha256']:
        raise SystemExit('Release verification failed: ' + item['path'])
print(json.dumps({'verified': True, 'files': manifest['file_count'], 'release': manifest['release_sha256']}))
