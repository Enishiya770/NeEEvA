"""Hash-pinned, serial ASR replay on user-owned local/Spark endpoints.

Without --execute this validates local files only. Final uploads explicitly turn
off speaker learning. Output excludes pitch arrays, embeddings and long text.
This is an acoustic replay, not a general accuracy or microphone benchmark.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


def request(url, payload=None, headers=None, timeout=120):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    req = urllib.request.Request(url, data=payload, headers=headers or {})
    try:
        with opener.open(req, timeout=timeout) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read().decode("utf-8"))


def upload(url, data, case):
    boundary = "asr-replay-" + uuid.uuid4().hex
    payload = bytearray()
    fields = {"language": case.get("language", "auto"), "learn_speaker": "false",
              "expect_singing": str(case.get("expect_singing", False)).lower(),
              "request_id": "saved-replay-" + uuid.uuid4().hex,
              "channel_id": "saved-evidence-replay"}
    for name, value in fields.items():
        payload.extend((f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n').encode())
    payload.extend((f'--{boundary}\r\nContent-Disposition: form-data; name="audio_file"; filename="saved.wav"\r\nContent-Type: audio/wav\r\n\r\n').encode())
    payload.extend(data)
    payload.extend((f"\r\n--{boundary}--\r\n").encode())
    return request(url + "/asr", payload, {"Content-Type": "multipart/form-data; boundary=" + boundary})


def evaluate(case, status, result):
    failures = []
    if status != 200:
        failures.append("http_error")
    if result.get("raw_vad_observed") is not True:
        failures.append("raw_vad_not_observed")
    if result.get("speaker_learning_allowed") is not False:
        failures.append("read_only_identity_not_confirmed")
    if case["kind"] == "weak_tonal":
        # Old Unity false/0 values came from absent JSON fields. Never use them
        # as the expected raw acoustic truth of this original recording.
        if result.get("raw_vad_has_speech") is False:
            if result.get("input_status") != "evidence_only" or result.get("text") != "":
                failures.append("weak_absent_vad_became_formal_input")
            verdict = "evidence_only_verified" if not failures else "failed"
        else:
            verdict = "raw_vad_speech_observed_requires_review"
    else:
        if result.get("input_status") != "accepted" or len(result.get("text", "")) < 10:
            failures.append("normal_speech_not_preserved")
        verdict = "normal_speech_verified" if not failures else "failed"
    return failures, verdict


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--url", default="http://127.0.0.1:9881")
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    target = urllib.parse.urlparse(args.url)
    if (target.scheme != "http" or target.hostname not in {"127.0.0.1", "localhost", "192.168.114.163"}
            or target.port != 9881 or target.username or target.password or target.query or target.fragment):
        raise ValueError("Only explicit local/Spark ASR port 9881 is permitted")
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    verified = []
    for case in manifest["cases"]:
        data = Path(case["wav"]).read_bytes()
        if hashlib.sha256(data).hexdigest() != case["sha256"]:
            raise ValueError("Audio hash mismatch: " + case["id"])
        verified.append((case, data))
    report = {"schema": 1, "executed": args.execute, "scope": "saved original WAVs and public synthetic speech; serial HTTP final ASR, speaker learning disabled", "rows": []}
    if args.execute:
        status, health = request(args.url + "/health", timeout=10)
        for key in ("input_status_schema", "speaker_failure_isolation_schema"):
            if status != 200 or health.get(key) != 1:
                raise RuntimeError("New ASR response contract unavailable: " + key)
        report["health"] = {k: health.get(k) for k in ("ok", "asr_device", "model_devices", "input_status_schema", "speaker_failure_isolation_schema", "speaker_window_policy")}
    for case, data in verified:
        row = {"id": case["id"], "sha256": case["sha256"], "bytes": len(data)}
        if args.execute:
            start = time.perf_counter()
            status, result = upload(args.url.rstrip("/"), data, case)
            row["wall_seconds"] = round(time.perf_counter() - start, 4)
            row["http_status"] = status
            keys = ("input_status", "input_error_code", "input_error_stage", "input_evidence_reason", "input_evidence_text",
                    "raw_vad_observed", "raw_vad_has_speech", "raw_vad_speech_ms", "speech_ms", "no_speech", "no_speech_reason",
                    "speaker_kind", "speaker_status", "speaker_error_code", "speaker_verification_available", "speaker_learning_allowed",
                    "is_singing", "singing_probability", "voiced_ratio", "pitch_stability", "sustained_ratio", "singing_expected_override", "timings")
            row.update({k: result.get(k) for k in keys})
            text = str(result.get("text", ""))
            row["text_characters"] = len(text)
            row["text_sha256"] = hashlib.sha256(text.encode()).hexdigest()
            row["text_excerpt"] = text[:120]  # no long transcript or acoustic array
            row["failures"], row["verdict"] = evaluate(case, status, result)
        report["rows"].append(row)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    report["passed"] = args.execute and all(not r["failures"] for r in report["rows"])
    weak_rows = [r for r, (c, _) in zip(report["rows"], verified) if c["kind"] == "weak_tonal"]
    report["weak_evidence_verified"] = (all(r["verdict"] == "evidence_only_verified" for r in weak_rows)
                                        if args.execute and weak_rows else None)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"executed": args.execute, "passed": report["passed"], "weak_evidence_verified": report["weak_evidence_verified"], "rows": [{k: r.get(k) for k in ("id", "verdict", "wall_seconds", "input_status", "raw_vad_observed", "raw_vad_has_speech", "raw_vad_speech_ms", "failures")} for r in report["rows"]]}, ensure_ascii=False))
    return 0 if not args.execute or report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
