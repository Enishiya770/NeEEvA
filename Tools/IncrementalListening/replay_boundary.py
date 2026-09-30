"""Replay local boundary refinement on an explicitly available PCM prefix.

Default is dry-run acoustic candidates. --endpoint opts into bounded serial calls
to the existing /asr service (learn_speaker=false); it does not deploy anything.
This wrapper uses the old service's whole /asr path, not the new direct local-ASR
adapter, and records that difference. No audio after --prefix-seconds is read by
the candidate recognizer. Supply only text anchors known at that snapshot.
"""
import argparse
import asyncio
import hashlib
import io
import json
import math
from pathlib import Path
import sys
import time
import urllib.request
import uuid

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Server" / "SenseVoice"))
from singing_boundary_refinement import (RATE, BoundaryRequest, BoundaryError,
                                         acoustic_gaps, refine_boundary, text_key)


def encode_wav(samples):
    target = io.BytesIO()
    sf.write(target, samples, RATE, format="WAV", subtype="PCM_16")
    return target.getvalue()


def multipart_request(url, audio, request_id):
    boundary = "neeeva-" + uuid.uuid4().hex
    body = bytearray()
    fields = dict(language="auto", learn_speaker="false", expect_singing="false",
                  request_id=request_id, speculative="true")
    for key, value in fields.items():
        body.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"{key}\"\r\n\r\n{value}\r\n").encode())
    body.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"audio_file\"; filename=\"boundary.wav\"\r\nContent-Type: audio/wav\r\n\r\n").encode())
    body.extend(audio)
    body.extend((f"\r\n--{boundary}--\r\n").encode())
    request = urllib.request.Request(url.rstrip("/") + "/asr", data=bytes(body),
                headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=20) as response:
        return json.loads(response.read())


async def run(args):
    samples, rate = sf.read(args.wav, dtype="float32")
    if rate != RATE or samples.ndim != 1:
        raise ValueError("Replay requires existing mono 16k WAV, with explicit source offset in manifest")
    total = len(samples) / RATE
    prefix = total if args.prefix_seconds is None else args.prefix_seconds
    if not 0.5 <= prefix <= total + 1 / RATE:
        raise ValueError("prefix must be within existing audio")
    samples = samples[:int(round(prefix * RATE))].copy()
    raw_manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8-sig"))
    observed = raw_manifest.pop("observed_transcript", "")
    observed_at = raw_manifest.pop("observed_at_audio_seconds", None)
    if args.prefix_seconds is not None and (not isinstance(observed_at, (int, float)) or
            not math.isfinite(observed_at) or not 0 <= observed_at <= prefix + 1 / RATE):
        raise ValueError("prefix replay requires observed_at_audio_seconds from that prefix, not final text")
    if not all(text_key(raw_manifest[k]) in text_key(observed) for k in ("exclude_text", "keep_text")):
        raise ValueError("manifest observed_transcript must contain both exact anchors available at this prefix")
    raw_manifest["current_end_seconds"] = min(raw_manifest["current_end_seconds"],
                                               raw_manifest["source_offset_seconds"] + len(samples) / RATE)
    payload = encode_wav(samples)
    raw_manifest["audio_sha256"] = hashlib.sha256(payload).hexdigest()
    request = BoundaryRequest.parse(raw_manifest, raw_manifest["audio_sha256"], len(samples))
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    artifact_dir = output.parent / (output.stem + "-probes")
    artifact_dir.mkdir(exist_ok=True)
    report = dict(schema=1, wav=str(Path(args.wav).resolve()), original_audio_seconds=total,
                  available_audio_seconds=len(samples) / RATE, manifest=raw_manifest,
                  observed_transcript=observed, ground_truth=None,
                  observed_at_audio_seconds=observed_at,
                  backend="existing-http-asr" if args.endpoint else "dry-run-acoustics",
                  backend_note="HTTP /asr includes existing VAD/whole-turn processing; production adapter directly calls local recognizer",
                  candidates=acoustic_gaps(samples, request), probes=[])
    cache = {}
    if args.reuse_report:
        previous = json.loads(Path(args.reuse_report).read_text(encoding="utf-8"))
        if previous.get("manifest", {}).get("audio_sha256") != raw_manifest["audio_sha256"]:
            raise ValueError("cached report must be from the exact same prefix audio")
        cache = {p["audio_sha256"]: p for p in previous.get("probes", [])}
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    count = 0

    async def recognize(piece):
        nonlocal count
        audio = encode_wav(piece)
        digest = hashlib.sha256(audio).hexdigest()
        if digest in cache:
            report["probes"].append(dict(cache[digest], reused_from=args.reuse_report))
            return cache[digest]["response"]
        if count >= args.max_remote_probes:
            raise BoundaryError("analysis_budget_exhausted", "replay remote call limit reached")
        count += 1
        path = artifact_dir / (str(count).zfill(2) + ".wav")
        path.write_bytes(audio)
        started = time.perf_counter()
        response = await asyncio.to_thread(multipart_request, args.endpoint, audio,
                                            request.request_id + "/replay-" + str(count))
        record = dict(audio_file=str(path), audio_sha256=hashlib.sha256(audio).hexdigest(),
                      duration_seconds=len(piece) / RATE, elapsed_seconds=time.perf_counter() - started,
                      response=response)
        report["probes"].append(record)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return response

    if args.endpoint:
        report["result"] = await refine_boundary(samples, request, recognize)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(output=str(output), candidates=len(report["candidates"]), probes=count,
                         result=report.get("result", {}).get("reason", "dry-run")), ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wav", required=True)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--prefix-seconds", type=float)
    parser.add_argument("--endpoint")
    parser.add_argument("--max-remote-probes", type=int, default=6)
    parser.add_argument("--reuse-report", help="reuse exact PCM-hash probe responses; never use another prefix")
    args = parser.parse_args()
    if not 1 <= args.max_remote_probes <= 16:
        parser.error("max-remote-probes must be 1..16")
    asyncio.run(run(args))


if __name__ == "__main__":
    main()
