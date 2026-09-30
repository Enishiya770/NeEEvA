"""Inspect already copied diagnostic WAVs without inference or transcript scoring.

python Tools/ASR/inspect-dump-samples.py path/to/asr-sample-inventory.json
Accepts PCM16 and IEEE float WAVs. Does not change the original sample files.
"""
import argparse
import hashlib
import json
import math
import struct
from pathlib import Path


def read_wav(path):
    data = path.read_bytes()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError(f"Not RIFF WAVE: {path.name}")
    position, fmt, pcm = 12, None, None
    while position + 8 <= len(data):
        kind, size = struct.unpack_from("<4sI", data, position)
        chunk = data[position + 8:position + 8 + size]
        if len(chunk) != size:
            raise ValueError(f"Truncated chunk: {path.name}")
        if kind == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", chunk)
        elif kind == b"data":
            pcm = chunk
        position += 8 + size + size % 2
    if fmt is None or pcm is None:
        raise ValueError(f"Missing WAV chunks: {path.name}")
    codec, channels, rate, _, _, bits = fmt
    if codec == 1 and bits == 16:
        values = [sample[0] / 32768.0 for sample in struct.iter_unpack("<h", pcm)]
    elif codec == 3 and bits == 32:
        values = [sample[0] for sample in struct.iter_unpack("<f", pcm)]
    else:
        raise ValueError(f"Unsupported WAV format {codec}/{bits}: {path.name}")
    return data, pcm, values, channels, rate, codec, bits


def rms(samples):
    return math.sqrt(sum(x * x for x in samples) / len(samples)) if samples else 0.0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("inventory", type=Path)
    args = parser.parse_args()
    inventory = json.loads(args.inventory.read_text(encoding="utf-8-sig"))
    rows, pairs, labels = [], [], []
    for sample in inventory["samples"]:
        by_kind = {}
        for audio in sample["files"]:
            path = Path(audio["localPath"])
            data, pcm, values, channels, rate, codec, bits = read_wav(path)
            digest = hashlib.sha256(data).hexdigest()
            if digest != audio["sha256"]:
                raise ValueError(f"SHA mismatch: {path.name}")
            finite = [x for x in values if math.isfinite(x)]
            window = max(1, round(.12 * rate * channels))
            rows.append(dict(
                file=str(path), sha256=digest, seconds=len(values) / (rate * channels),
                sampleRate=rate, channels=channels, format=codec, bits=bits,
                nonFiniteSamples=len(values)-len(finite),
                peakAbsolute=max((abs(x) for x in finite), default=0),
                rms=rms(finite), first120msRms=rms(finite[:window]),
                last120msRms=rms(finite[-window:]),
                nearFullScaleFraction=sum(abs(x) >= .999 for x in finite) / max(1, len(finite)),
                exactZeroFraction=finite.count(0.0) / max(1, len(finite))))
            if path.name.endswith(".raw.wav"):
                by_kind["raw"] = (pcm, values, channels, rate, bits)
            elif path.name.endswith(".content.wav"):
                by_kind["content"] = (pcm, values, channels, rate, bits)
        if len(by_kind) == 2:
            raw, content = by_kind["raw"], by_kind["content"]
            if raw[2:] != content[2:]:
                raise ValueError("Raw/content formats differ")
            offset_bytes = raw[0].find(content[0])
            bytes_per_frame = raw[2] * raw[4] // 8
            aligned = offset_bytes >= 0 and offset_bytes % bytes_per_frame == 0
            if not aligned:
                raise ValueError("Content is not a byte-identical aligned slice of raw audio")
            start_frame = offset_bytes // bytes_per_frame
            head = start_frame / raw[3]
            content_seconds = len(content[1]) / (raw[2] * raw[3])
            raw_seconds = len(raw[1]) / (raw[2] * raw[3])
            tail_start = start_frame * raw[2] + len(content[1])
            removed_tail = raw[1][tail_start:]
            pairs.append(dict(
                metadata=sample["metadataPath"], rawSeconds=raw_seconds,
                contentSeconds=content_seconds, contentIsExactRawSlice=True,
                removedHeadSeconds=head,
                removedTailSeconds=raw_seconds-head-content_seconds,
                removedFraction=1-content_seconds/raw_seconds,
                removedTailRms=rms(removed_tail),
                removedTailPeak=max((abs(x) for x in removed_tail), default=0),
                interpretation="Numerical crop/audio statistics only; silence or missing speech needs human listening/reference."))
        labels.append(dict(metadata=sample["metadataPath"],
                           audio=[x["localPath"] for x in sample["files"]],
                           humanTranscript=None, speechStartSeconds=None,
                           speechEndSeconds=None, referenceSegments=[],
                           wordOrCharacterErrorRate=None,
                           note="Human transcription and speech/singing boundary labels required; ASR output is not ground truth."))
    report = dict(scope="Existing actual-session diagnostic samples, read-only PCM inspection; no inference, ASR text correction, or WER/CER computation.",
                  sampleGroups=len(inventory["samples"]), wavCount=len(rows),
                  humanReferenceCount=0, rows=rows, trimPairs=pairs)
    output = args.inventory.with_name("asr-sample-audio-stats.json")
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    labels_path = args.inventory.with_name("asr-sample-labels.template.json")
    if not labels_path.exists():
        labels_path.write_text(json.dumps(labels, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(wavCount=len(rows), pairs=pairs), ensure_ascii=True))


if __name__ == "__main__":
    main()
