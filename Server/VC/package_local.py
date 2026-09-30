"""Freeze current RVC implementation, trained voice and exact frontend assets."""
import hashlib
import json
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent / "runtime/package"


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for part in iter(lambda: f.read(4*1024*1024), b""): h.update(part)
    return h.hexdigest()


def main():
    selected = {}
    for name in ("seedvc_server.py", "seedvc_runner.py", "model_assets.py"):
        selected["SeedVC/"+name] = ROOT/"Server/SeedVC"/name
    for name in ("rvc_convert.py", "hubert_compat.py"):
        selected["RVC/"+name] = ROOT/"Server/RVC"/name
    for sub in ("models", "vendor/rvc"):
        for p in (ROOT/"Server/RVC"/sub).rglob("*"):
            if not p.is_file() or p.suffix.lower() in {".exe", ".dll", ".pyd", ".pyc"}: continue
            if any(x in {"__pycache__", ".git"} for x in p.parts): continue
            selected["RVC/"+p.relative_to(ROOT/"Server/RVC").as_posix()] = p
    selected["refs/41041.wav"] = ROOT/"Assets/Model/41041.wav"
    entries = [{"path": name, "bytes": path.stat().st_size, "sha256": sha(path)} for name,path in sorted(selected.items())]
    identity=hashlib.sha256(json.dumps(entries,sort_keys=True,separators=(",",":")).encode()).hexdigest()
    manifest={"version":1,"release_sha256":identity,"file_count":len(entries),"total_bytes":sum(e["bytes"] for e in entries),
              "backend":"rvc-character-v2","source_policy":"Current local vendor and exact trained model; no upstream upgrade", "files":entries}
    OUT.mkdir(parents=True,exist_ok=True)
    (OUT/"manifest.json").write_text(json.dumps(manifest,indent=2),encoding="utf-8")
    with tarfile.open(OUT/"release.tar","w") as tar:
        for name,p in sorted(selected.items()):tar.add(p,arcname=name,recursive=False)
        tar.add(OUT/"manifest.json",arcname="manifest.json")
    print(json.dumps({k:v for k,v in manifest.items() if k!="files"}))


if __name__=="__main__": main()
