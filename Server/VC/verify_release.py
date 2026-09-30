import hashlib
import json
from pathlib import Path
root=Path(__file__).resolve().parent/"release"
manifest=json.loads((root/"manifest.json").read_text())
for entry in manifest["files"]:
    p=root/entry["path"]
    h=hashlib.sha256()
    with p.open("rb") as f:
        for block in iter(lambda:f.read(4*1024*1024),b""):h.update(block)
    assert p.stat().st_size==entry["bytes"] and h.hexdigest()==entry["sha256"],entry["path"]
print(json.dumps({"passed":True,"release_sha256":manifest["release_sha256"],"files":manifest["file_count"]}))
