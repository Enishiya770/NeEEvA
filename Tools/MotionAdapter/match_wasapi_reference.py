"""Offline attribution of a WASAPI recording to an isolated AudioSource tap.

Normalized waveform cross-correlation identifies the source audio, not its words.
Onset bounds describe the measured signal/host clocks; they do not prove physical
speaker audibility. This script never opens an audio input or output device.
"""

from __future__ import annotations

import argparse
import json
import math
from datetime import datetime, timedelta
from pathlib import Path

import numpy as np
from scipy.signal import fftconvolve, resample_poly


def resample(data: np.ndarray, source_rate: int, target_rate: int) -> np.ndarray:
    if source_rate == target_rate:
        return data
    divisor = math.gcd(source_rate, target_rate)
    return resample_poly(data, target_rate // divisor, source_rate // divisor, axis=0)


def activity_onset(samples: np.ndarray, rate: int) -> dict:
    hop = max(1, round(rate * 0.01))
    rms = np.array([np.sqrt(np.mean(samples[i:i + hop] ** 2)) for i in range(0, len(samples), hop)])
    peak = float(np.max(rms)) if len(rms) else 0.0
    threshold = max(10 ** (-75 / 20), peak * 0.01)
    active = rms >= threshold
    start = next((i for i in range(len(active) - 1) if active[i] and active[i + 1]), None)
    return {"seconds": None if start is None else start * hop / rate,
            "thresholdDbfs": 20 * math.log10(threshold), "thresholdBelowPeakDb": 40,
            "windowSeconds": hop / rate, "minimumActiveWindows": 2}


def normalized_correlation(signal: np.ndarray, template: np.ndarray) -> np.ndarray:
    centered = template - np.mean(template)
    energy = float(np.dot(centered, centered))
    if len(signal) < len(template) or energy < 1e-16:
        return np.zeros(0)
    length = len(template)
    sums = np.concatenate(([0.0], np.cumsum(signal)))
    squares = np.concatenate(([0.0], np.cumsum(signal * signal)))
    window_sum = sums[length:] - sums[:-length]
    window_energy = np.maximum(0.0, squares[length:] - squares[:-length] - window_sum ** 2 / length)
    product = fftconvolve(signal, centered[::-1], mode="valid")
    denominator = np.sqrt(energy * window_energy)
    result = np.zeros_like(product)
    np.divide(product, denominator, out=result, where=denominator > 1e-12)
    return np.clip(result, -1, 1)


def coefficient(a: np.ndarray, b: np.ndarray) -> float:
    a, b = a - np.mean(a), b - np.mean(b)
    scale = np.linalg.norm(a) * np.linalg.norm(b)
    return float(np.dot(a, b) / scale) if scale > 1e-12 else 0.0


def channels_and_mix(data: np.ndarray) -> list[tuple[str, np.ndarray]]:
    if data.ndim == 1:
        data = data[:, None]
    choices = [(f"channel-{i}", data[:, i]) for i in range(min(2, data.shape[1]))]
    if data.shape[1] > 1:
        choices.append(("channel-mean", np.mean(data, axis=1)))
    return choices


def match(reference: np.ndarray, reference_rate: int, loopback: np.ndarray, loopback_rate: int) -> dict:
    rate = 16000
    reference = resample(np.asarray(reference, dtype=np.float64), reference_rate, rate)
    loopback = resample(np.asarray(loopback, dtype=np.float64), loopback_rate, rate)
    onset = activity_onset(reference, rate)
    result = {"status": "unmatched", "method": "demeaned normalized waveform cross-correlation",
              "analysisRate": rate, "referenceOnset": onset,
              "scope": "Waveform attribution to the tapped AudioSource, not transcription or proof of human audibility."}
    if onset["seconds"] is None:
        result["reason"] = "reference-has-no-sustained-signal"
        return result
    start = max(0, round((onset["seconds"] - 0.04) * rate))
    length = min(round(1.2 * rate), len(reference) - start)
    if length < round(0.35 * rate) or len(loopback) < length:
        result["reason"] = "insufficient-reference-or-loopback-length"
        return result
    attempts = []
    selected = None
    for reference_name, ref in channels_and_mix(reference):
        template = ref[start:start + length]
        for loopback_name, recorded in channels_and_mix(loopback):
            scores = normalized_correlation(recorded, template)
            if not len(scores):
                continue
            magnitudes = np.abs(scores)
            best = int(np.argmax(magnitudes))
            peak = float(magnitudes[best])
            separated = magnitudes.copy()
            guard = round(0.15 * rate)
            separated[max(0, best - guard):min(len(scores), best + guard + 1)] = 0
            second = float(np.max(separated))
            half = length // 2
            halves = [abs(coefficient(recorded[best:best + half], template[:half])),
                      abs(coefficient(recorded[best + half:best + length], template[half:]))]
            candidate = {"referenceChannel": reference_name, "loopbackChannel": loopback_name,
                         "normalizedPeak": peak, "signedPeak": float(scores[best]),
                         "secondPeakOutside150ms": second, "uniquePeakGap": peak - second,
                         "halfTemplateCorrelations": halves, "loopbackTemplateStartSeconds": best / rate}
            attempts.append(candidate)
            if selected is None or peak > selected["normalizedPeak"]:
                selected = candidate
                left = right = best
                cutoff = peak * 0.95
                while left > max(0, best - round(0.02 * rate)) and magnitudes[left - 1] >= cutoff:
                    left -= 1
                while right + 1 < min(len(scores), best + round(0.02 * rate)) and magnitudes[right + 1] >= cutoff:
                    right += 1
                selected["peakSupportSeconds"] = [left / rate, right / rate]
    result["channelAttempts"] = attempts
    if selected is None:
        result["reason"] = "no-energetic-template"
        return result
    result["selected"] = selected
    result["template"] = {"referenceStartSeconds": start / rate, "durationSeconds": length / rate}
    offset = selected["loopbackTemplateStartSeconds"] - start / rate
    output_onset = onset["seconds"] + offset
    width = max(abs(x - selected["loopbackTemplateStartSeconds"]) for x in selected["peakSupportSeconds"])
    uncertainty = onset["windowSeconds"] + width + 2 / rate
    result.update({"referenceToLoopbackOffsetSeconds": offset,
                   "loopbackOnsetSeconds": output_onset,
                   "sampleTimelineOnsetErrorSeconds": uncertainty,
                   "sampleTimelineOnsetIntervalSeconds": [output_onset - uncertainty, output_onset + uncertainty]})
    accepted = (selected["normalizedPeak"] >= 0.70 and selected["uniquePeakGap"] >= 0.05
                and min(selected["halfTemplateCorrelations"]) >= 0.60 and output_onset >= -uncertainty)
    result["status"] = "matched" if accepted else "unmatched"
    result["acceptance"] = {"minimumPeak": 0.70, "minimumUniquePeakGap": 0.05, "minimumHalfCorrelation": 0.60}
    result["reason"] = "source-waveform-found-in-system-output" if accepted else "weak-ambiguous-or-inconsistent-waveform-match"
    result["timingScope"] = "Signal-onset interval includes the 10 ms RMS window, correlation peak width and resampling quantization. It excludes unknown physical output latency."
    return result


def attach_clock_mapping(result: dict, capture: dict, blocks: list[dict]) -> None:
    if result["status"] != "matched":
        return
    onset = result["loopbackOnsetSeconds"]
    frame = max(0, round(onset * capture["sampleRate"]))
    block = next((x for x in blocks if x["sampleStart"] <= frame < x["sampleStart"] + x["frames"]), None)
    if not block:
        result["systemOutputClock"] = {"status": "unavailable", "reason": "matched-frame-has-no-callback-record"}
        return
    adc, current = block.get("inputBufferAdcTime"), block.get("portAudioCurrentTime")
    delay = current - adc if isinstance(adc, (int, float)) and isinstance(current, (int, float)) else -1
    clock = {"status": "estimated", "matchedCallback": block,
             "method": "callback UTC minus PortAudio current-minus-input-ADC time, plus within-buffer sample offset"}
    if adc is None or adc <= 0 or not 0 <= delay <= 2:
        clock.update({"status": "unavailable", "reason": "ADC/current mapping missing or invalid; sample-relative onset remains available"})
        result["systemOutputClock"] = clock
        return
    trigger = datetime.fromisoformat(capture["trigger"]["parsedUtc"].replace("Z", "+00:00"))
    callback = datetime.fromisoformat(block["callbackUtc"].replace("Z", "+00:00"))
    within = (frame - block["sampleStart"]) / capture["sampleRate"]
    center = callback - timedelta(seconds=delay) + timedelta(seconds=within)
    buffer_seconds = block["frames"] / capture["sampleRate"]
    # Include both the exposed queue latency and a full callback block as a
    # conservative engineering envelope; it is not a calibrated confidence CI.
    uncertainty = result["sampleTimelineOnsetErrorSeconds"] + max(buffer_seconds, delay, float(capture.get("inputLatencySeconds", 0)))
    after = (center - trigger).total_seconds()
    clock.update({"estimatedOnsetUtc": center.isoformat(), "estimatedAfterTriggerSeconds": after,
                  "engineeringUncertaintySeconds": uncertainty,
                  "estimatedIntervalAfterTriggerSeconds": [after - uncertainty, after + uncertainty],
                  "callbackOverflowOrStatusCount": capture.get("callbackStatusNonzeroCount"),
                  "limitations": "Host API clock mapping is not physically calibrated. The interval includes signal detection resolution and exposed capture buffering; unknown device/driver clock bias remains possible. Overflow or callback gaps can invalidate timing."})
    result["systemOutputClock"] = clock


def self_test() -> None:
    rng = np.random.default_rng(7329)
    rate = 16000
    t = np.arange(2 * rate) / rate
    noise = rng.normal(size=len(t))
    # A changing speech-like envelope and nonperiodic waveform keep the test
    # distinct from an arbitrary system tone at a different earlier onset.
    speech = np.convolve(noise, np.ones(5) / 5, mode="same") * (0.3 + 0.7 * np.sin(2 * np.pi * 3.1 * t) ** 2) * .15
    reference = np.concatenate([np.zeros(rate // 5), speech])
    delay = round(.37 * rate)
    output = rng.normal(scale=.0001, size=len(reference) + delay + rate // 2)
    output[:rate // 5] += .2 * np.sin(2 * np.pi * 800 * np.arange(rate // 5) / rate)
    output[delay:delay + len(reference)] += reference * .63
    result = match(reference[:, None], rate, np.column_stack([output, output * .8]), rate)
    assert result["status"] == "matched", result
    assert abs(result["referenceToLoopbackOffsetSeconds"] - .37) <= 2 / rate
    assert abs(result["loopbackOnsetSeconds"] - .57) <= .011
    unrelated = rng.normal(scale=.05, size=len(output))
    assert match(reference[:, None], rate, unrelated[:, None], rate)["status"] == "unmatched"
    assert match(np.zeros((rate, 1)), rate, output[:, None], rate)["status"] == "unmatched"
    # The same source after sample-rate conversion must retain the alignment.
    high = resample(output[:, None], rate, 48000)
    converted = match(reference[:, None], rate, high, 48000)
    assert converted["status"] == "matched" and abs(converted["referenceToLoopbackOffsetSeconds"] - .37) < .001
    print(json.dumps({"status": "passed", "checks": 6, "audioDeviceOpened": False,
                      "matchedKnownDelaySeconds": result["referenceToLoopbackOffsetSeconds"],
                      "earlyUnrelatedToneRejected": True}))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", help="reference.json produced by FirstAudioReferenceCapture.End")
    parser.add_argument("--loopback-report", help="report.json produced by capture_wasapi_case.py")
    parser.add_argument("--output", help="Destination match report JSON")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not all((args.reference, args.loopback_report, args.output)):
        parser.error("--reference, --loopback-report and --output are required")
    import soundfile as sf

    reference_path, capture_path = Path(args.reference).resolve(), Path(args.loopback_report).resolve()
    metadata = json.loads(reference_path.read_text(encoding="utf-8-sig"))
    capture = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    channels = metadata["channels"]
    if channels < 1:
        raise ValueError("Reference has no audio callback/channels")
    pcm_path = Path(metadata["pcmPath"])
    if not pcm_path.is_absolute():
        pcm_path = reference_path.parent / pcm_path
    reference = np.fromfile(pcm_path, dtype="<f4").reshape((-1, channels))
    loopback, loopback_rate = sf.read(capture["wavPath"], dtype="float64", always_2d=True)
    result = match(reference, metadata["sampleRate"], loopback, loopback_rate)
    timing_path = capture_path.parent / "callback-timing.json"
    blocks = json.loads(timing_path.read_text(encoding="utf-8-sig"))["blocks"] if timing_path.exists() else []
    attach_clock_mapping(result, capture, blocks)
    result.update({"referenceMetadata": str(reference_path), "loopbackReport": str(capture_path),
                   "referenceFirstCallbackUtc": metadata.get("firstCallbackUtc"),
                   "caseCorrelation": {"trigger": capture.get("trigger"), "done": capture.get("done")},
                   "audibilityClaim": False, "transcriptionClaim": False})
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "output": str(Path(args.output).resolve()),
                      "score": result.get("selected", {}).get("normalizedPeak"),
                      "onsetSeconds": result.get("loopbackOnsetSeconds")}, ensure_ascii=False))
    return 0 if result["status"] == "matched" else 2


if __name__ == "__main__":
    raise SystemExit(main())
