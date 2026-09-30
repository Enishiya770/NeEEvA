"""Download one large Hugging Face file with resumable HTTP ranges."""
from __future__ import annotations

import argparse
import concurrent.futures
import os
import shutil
import urllib.request
from pathlib import Path


def get_range(url: str, start: int, end: int, path: Path, retries: int) -> None:
    expected = end - start + 1
    for attempt in range(retries):
        try:
            request = urllib.request.Request(url, headers={"Range": f"bytes={start}-{end}"})
            with urllib.request.urlopen(request, timeout=120) as response, path.open("wb") as stream:
                remaining = expected
                while remaining:
                    chunk = response.read(min(1024 * 1024, remaining))
                    if not chunk:
                        raise IOError("short ranged response")
                    stream.write(chunk)
                    remaining -= len(chunk)
            if path.stat().st_size == expected:
                return
        except Exception:
            if attempt + 1 == retries:
                raise
    raise RuntimeError(f"failed range {start}-{end}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--size", type=int, required=True)
    parser.add_argument("--workers", type=int, default=16)
    parser.add_argument("--chunk-mib", type=int, default=32)
    args = parser.parse_args()
    part_dir = args.output.with_name(args.output.name + ".parts")
    part_dir.mkdir(parents=True, exist_ok=True)
    chunk = args.chunk_mib * 1024 * 1024
    ranges = [(start, min(args.size - 1, start + chunk - 1)) for start in range(0, args.size, chunk)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = []
        for index, (start, end) in enumerate(ranges):
            part = part_dir / f"{index:04d}.part"
            if part.exists() and part.stat().st_size == end - start + 1:
                continue
            futures.append(pool.submit(get_range, args.url, start, end, part, 5))
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            future.result()
            print(f"completed {index}/{len(futures)}", flush=True)
    with args.output.open("wb") as stream:
        for index, (start, end) in enumerate(ranges):
            part = part_dir / f"{index:04d}.part"
            if part.stat().st_size != end - start + 1:
                raise RuntimeError(f"wrong part size: {part}")
            with part.open("rb") as source:
                shutil.copyfileobj(source, stream, length=1024 * 1024)
    if args.output.stat().st_size != args.size:
        raise RuntimeError("combined file size mismatch")
    print(f"downloaded {args.output} ({args.size} bytes)")


if __name__ == "__main__":
    main()
