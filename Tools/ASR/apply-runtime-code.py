"""Apply/rollback one pinned ASR source file after its owner stops the service.

Does not stop/start processes, install packages, or modify profiles/models/config.
Default is inspection; explicit --apply is required for a source replacement.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import tempfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def require_stopped(root):
    for item in Path("/proc").glob("[0-9]*/cmdline"):
        try:
            if str(root / "spark/run.py").encode() in item.read_bytes().split(b"\0"):
                raise RuntimeError("ASR owned process still running")
        except (FileNotFoundError, PermissionError):
            continue
    with socket.socket() as sock:
        sock.settimeout(.5)
        if sock.connect_ex(("127.0.0.1", 9881)) == 0:
            raise RuntimeError("Port 9881 is still accepting connections")


def atomic_write(path, data):
    handle, name = tempfile.mkstemp(prefix=".asr-code-", dir=path.parent)
    try:
        with os.fdopen(handle, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(name, path.stat().st_mode & 0o777)
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--root", type=Path, default=Path("/home/spark_root_001/neeeva-voice/asr"))
    p.add_argument("--backup", type=Path, required=True)
    p.add_argument("--staged", type=Path)
    p.add_argument("--before")
    p.add_argument("--after")
    p.add_argument("--rollback", action="store_true")
    p.add_argument("--apply", action="store_true")
    args = p.parse_args()
    root = args.root.resolve()
    if root.name != "asr" or root.parent.name != "neeeva-voice":
        raise ValueError("Unexpected ASR root")
    target = root / "src/sensevoice_server.py"
    if target.is_symlink() or not target.resolve().is_relative_to(root):
        raise ValueError("Source target must be a regular file in this ASR root")
    backup = args.backup.resolve()
    if not backup.is_relative_to(root / ".runtime/code-backups"):
        raise ValueError("Backup must remain within this ASR .runtime/code-backups")
    if args.rollback:
        record = json.loads((backup / "manifest.json").read_text())
        incoming = (backup / "sensevoice_server.py").read_bytes()
        before, after = record["after_sha256"], record["before_sha256"]
    else:
        if not args.staged or not args.before or not args.after:
            raise ValueError("Patch requires --staged/--before/--after")
        incoming = args.staged.read_bytes()
        before, after = args.before, args.after
    current = target.read_bytes()
    if digest(current) != before or digest(incoming) != after:
        raise ValueError("Pinned source hash mismatch; no files changed")
    compile(incoming, str(target), "exec")  # syntax only; no imports/model loading
    record = {"schema": 1, "file": "src/sensevoice_server.py", "before_sha256": before,
              "after_sha256": after, "backup": str(backup), "rollback": args.rollback,
              "applied": args.apply}
    if args.apply:
        require_stopped(root)
        if not args.rollback:
            backup.mkdir(parents=True, exist_ok=False)
            (backup / "sensevoice_server.py").write_bytes(current)
            (backup / "manifest.json").write_text(json.dumps(record, indent=2))
        atomic_write(target, incoming)
        if digest(target.read_bytes()) != after:
            raise RuntimeError("Post-write source hash mismatch")
    print(json.dumps(record))


if __name__ == "__main__":
    main()
