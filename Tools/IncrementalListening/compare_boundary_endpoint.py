"""Compare one new direct-boundary request with recorded serial legacy /asr probes.

No legacy probes are repeated. The prior report must use exactly the same WAV hash.
Only the explicitly provided endpoint receives the supplied local audio.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("endpoint", "wav", "manifest", "baseline-report", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    data = Path(args.wav).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8-sig"))
    previous = json.loads(Path(args.baseline_report).read_text(encoding="utf-8-sig"))
    if digest != manifest.get("audio_sha256") or digest != previous.get("manifest", {}).get("audio_sha256"):
        raise ValueError("baseline, manifest and actual WAV hashes must match")
    for key in ("source_id", "source_revision", "source_offset_seconds", "current_start_seconds",
                "current_end_seconds", "edge", "exclude_text", "keep_text"):
        if manifest.get(key) != previous["manifest"].get(key):
            raise ValueError("baseline request changed: " + key)
    boundary = "neeeva-" + uuid.uuid4().hex
    body = bytearray((f"--{boundary}\r\nContent-Disposition: form-data; name=\"manifest\"\r\n\r\n" +
                      json.dumps(manifest, ensure_ascii=False) + "\r\n").encode("utf-8"))
    body.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"audio_file\"; filename=\"source.wav\"\r\nContent-Type: audio/wav\r\n\r\n").encode())
    body.extend(data)
    body.extend((f"\r\n--{boundary}--\r\n").encode())
    url = args.endpoint.rstrip("/") + "/singing/refine-boundary"
    request = urllib.request.Request(url, data=bytes(body),
            headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    started = time.perf_counter()
    try:
        with opener.open(request, timeout=40) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    elapsed = time.perf_counter() - started
    direct = json.loads(raw)
    baseline = previous.get("result", {})
    report = dict(schema=1, url=url, wav=str(Path(args.wav).resolve()), manifest=manifest,
                  baseline_report=str(Path(args.baseline_report).resolve()),
                  baseline_result=baseline, baseline_probe_elapsed_seconds=sum(p.get("elapsed_seconds", 0)
                    for p in previous.get("probes", [])),
                  direct_http_status=status, direct_elapsed_seconds=elapsed, direct_result=direct,
                  difference_note="legacy probes ran VAD/speaker/full-turn postprocessing; direct route uses generate_asr on exact local PCM",
                  exact_binding=all(direct.get(k) == manifest.get(k) for k in
                                    ("request_id", "source_id", "source_revision", "audio_sha256")),
                  same_resolved_range=bool(direct.get("ok") and baseline.get("ok") and
                    direct.get("range") == baseline.get("range")))
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("direct_http_status", "direct_elapsed_seconds",
                                            "exact_binding", "same_resolved_range")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
