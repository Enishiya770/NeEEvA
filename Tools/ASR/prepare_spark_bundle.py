"""Inventory the configured Windows ASR, or create an explicit offline bundle.

Default operation only writes a small inventory. --bundle copies model/data bytes
into a new local archive; it never uploads or changes a running service.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
from datetime import datetime, timezone

MODELS = {
    "sensevoice": "iic--SenseVoiceSmall",
    "vad": "iic--speech_fsmn_vad_zh-cn-16k-common-pytorch",
    "speaker": "iic--speech_campplus_sv_zh-cn_16k-common",
}
PACKAGES = ["funasr", "torch", "torchaudio", "torchcrepe", "soundfile", "scipy",
            "numpy", "fastapi", "uvicorn", "python-multipart", "pyopenjtalk",
            "modelscope", "huggingface-hub", "websockets", "librosa", "numba", "llvmlite"]


def tree_files(root):
    return sorted(p for p in root.rglob("*") if p.is_file() and "__pycache__" not in p.parts)


def gather(project, cache):
    source = project / "Server/SenseVoice"
    runtime = json.loads((project / "Server/RuntimeLogs/asr_runtime.json").read_text(encoding="utf-8-sig"))
    python = Path(runtime["python"])
    if not python.is_absolute():
        python = project / python
    code = "import importlib.metadata as m,json; out={};\nfor n in " + repr(PACKAGES) + ":\n try:out[n]=m.version(n)\n except m.PackageNotFoundError:out[n]=None\nprint(json.dumps(out))"
    packages = json.loads(subprocess.check_output([str(python), "-c", code], text=True))
    mappings = [(p, "src/" + p.name) for p in source.glob("*.py")]
    mappings += [(p, "spark/" + p.name) for p in (source / "spark").glob("*") if p.is_file()]
    resources = []
    for role, name in MODELS.items():
        path = cache / name / "snapshots/master"
        if not path.is_dir():
            raise FileNotFoundError("Missing cached model: " + str(path))
        files = tree_files(path)
        resources.append({"kind": role, "source": str(path), "files": len(files), "bytes": sum(p.stat().st_size for p in files)})
        mappings += [(p, "models/" + role + "/" + p.relative_to(path).as_posix()) for p in files]
    profile = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "NeEEvA/speaker_profiles.json"
    if not profile.is_file():
        raise FileNotFoundError("Missing existing speaker identity database; do not silently bootstrap a new owner")
    identities = json.loads(profile.read_text(encoding="utf-8-sig"))
    resources.append({"kind": "speaker-profiles", "source": str(profile), "bytes": profile.stat().st_size,
                      "identities": len(identities.get("identities", [])),
                      "voiceprints": sum(len(x.get("voiceprints", [])) for x in identities.get("identities", []))})
    mappings.append((profile, "data/speaker_profiles.json"))
    catalog = source / "song_catalog.json"
    resources.append({"kind": "song-catalog", "source": str(catalog), "exists": catalog.is_file()})
    if catalog.is_file():
        mappings.append((catalog, "data/song_catalog.json"))
        library = source / "song_library"
        mappings += [(p, "data/song_library/" + p.relative_to(library).as_posix()) for p in tree_files(library)]
    ai_voice = project / "Assets/Model/41041.wav"
    if not ai_voice.is_file():
        raise FileNotFoundError(ai_voice)
    mappings.append((ai_voice, "assets/41041.wav"))
    dictionary = python.parent.parent / "Lib/site-packages/pyopenjtalk/open_jtalk_dic_utf_8-1.11"
    if not dictionary.is_dir():
        raise FileNotFoundError("Missing Japanese dictionary: " + str(dictionary))
    dictionaries = tree_files(dictionary)
    mappings += [(p, "assets/open_jtalk_dic_utf_8-1.11/" + p.relative_to(dictionary).as_posix()) for p in dictionaries]
    resources.append({"kind": "japanese-dictionary", "source": str(dictionary), "bytes": sum(p.stat().st_size for p in dictionaries)})
    inventory = {"schema": 1, "collectedAtUtc": datetime.now(timezone.utc).isoformat(),
                 "project": str(project), "python": str(python), "runtime": runtime,
                 "packages": packages, "resources": resources, "bundleFiles": len(mappings),
                 "bundleBytes": sum(p.stat().st_size for p, _ in mappings),
                 "notes": ["Current song catalog absence is preserved; no empty replacement overwrites an existing remote catalog.",
                           "Speaker names, embeddings, transcripts and audio are not embedded in this inventory.",
                           "Diagnostic audio dumps and Windows venv are not migration assets."]}
    return inventory, mappings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--cache", type=Path, default=Path.home() / ".cache/modelscope/models")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, help="Explicitly create a NEW local .tar archive including private voiceprints and model weights")
    args = parser.parse_args()
    report, mappings = gather(args.project.resolve(), args.cache.resolve())
    if args.bundle:
        args.bundle.parent.mkdir(parents=True, exist_ok=True)
        with tarfile.open(args.bundle, "x") as archive:
            manifest = []
            for path, relative in mappings:
                if relative.startswith("data/") and path.suffix.lower() == ".json":
                    # The running local ASR may atomically replace its database.
                    # Hash and archive the same single read, never two revisions.
                    snapshot = path.read_bytes()
                    info = tarfile.TarInfo(relative)
                    info.size = len(snapshot)
                    info.mode = 0o600
                    archive.addfile(info, io.BytesIO(snapshot))
                    manifest.append({"path": relative, "bytes": len(snapshot),
                                     "sha256": hashlib.sha256(snapshot).hexdigest()})
                    continue
                sha = hashlib.sha256()
                with path.open("rb") as stream:
                    for block in iter(lambda: stream.read(1024 * 1024), b""):
                        sha.update(block)
                archive.add(path, arcname=relative, recursive=False)
                manifest.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha.hexdigest()})
            data = json.dumps({"schema": 1, "files": manifest}, ensure_ascii=False, indent=2).encode("utf-8")
            info = tarfile.TarInfo("bundle-manifest.json")
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
        report["bundle"] = str(args.bundle.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"inventory": str(args.output), "bundleFiles": report["bundleFiles"], "bundleBytes": report["bundleBytes"], "bundleCreated": bool(args.bundle)}))


if __name__ == "__main__":
    main()
