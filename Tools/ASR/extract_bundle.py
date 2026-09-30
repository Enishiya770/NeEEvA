"""Install a verified bundle into a NEW ASR root; never overwrite existing state."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import tarfile


def install(bundle, root):
    if root.exists():
        raise FileExistsError("ASR root exists; refusing to overwrite code or speaker/song state")
    with tarfile.open(bundle, "r:") as archive:
        members = archive.getmembers()
        names = set()
        for member in members:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or "\\" in member.name or not member.isfile():
                raise ValueError("Unsafe archive member")
            if member.name in names:
                raise ValueError("Duplicate archive member")
            names.add(member.name)
        manifest = json.load(archive.extractfile("bundle-manifest.json"))
        if set(x["path"] for x in manifest["files"]) != names - {"bundle-manifest.json"}:
            raise ValueError("Manifest member mismatch")
        for row in manifest["files"]:
            sha = hashlib.sha256()
            count = 0
            with archive.extractfile(row["path"]) as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    sha.update(block)
                    count += len(block)
            if count != row["bytes"] or sha.hexdigest() != row["sha256"]:
                raise ValueError("Bundle checksum mismatch: " + row["path"])
        root.mkdir(parents=True, mode=0o700)
        # All members were checked as ordinary relative files; no links permitted.
        for member in members:
            target = root / member.name
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as source, target.open("xb") as output:
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    output.write(block)
    print(json.dumps({"installed": str(root), "files": len(members), "serviceStarted": False}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    install(args.bundle, args.root)
