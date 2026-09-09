"""Short-tail measurements for turn timing, never a user/voice classification."""
import numpy as np


def recent_activity_fields(wav, analyzer, sample_rate=16000, vad_probe=None):
    tail = np.asarray(wav, dtype=np.float32).reshape(-1)[-int(sample_rate * 0.8):]
    result = {"activity_schema": 2, "activity_window_ms": int(len(tail) * 1000 / sample_rate),
              "activity_rms": 0.0, "activity_periodicity": 0.0, "activity_voiced_ratio": 0.0,
              "activity_vad_available": False, "activity_speech_ms": 0,
              "activity_speech_end_age_ms": -1}
    if len(tail) == 0 or not np.isfinite(tail).all():
        return result
    tail = tail - float(np.mean(tail))
    result["activity_rms"] = round(float(np.sqrt(np.mean(tail * tail))), 6)
    # Measure the audio tail itself. A correction of older ASR words is NOT new voice.
    # Preserve its audio-relative endpoint, rather than the response arrival time.
    if vad_probe is not None and len(tail) >= int(sample_rate * 0.2):
        try:
            _, speech_ms, segments, _ = vad_probe(tail, min_speech_ms=120)
            result["activity_vad_available"] = True
            result["activity_speech_ms"] = int(speech_ms)
            if segments:
                end = max(int(segment[1]) for segment in segments)
                result["activity_speech_end_age_ms"] = max(0, result["activity_window_ms"] - end)
        except Exception:
            # A failed probe is unavailable evidence, never positive voice evidence.
            pass
    if analyzer is None or len(tail) < int(sample_rate * 0.2) or result["activity_rms"] < 0.0002:
        return result
    pitch, periodicity, _ = analyzer._track_fft(tail)
    voiced = (pitch >= 75) & (pitch <= 700) & (periodicity >= 0.60)
    result["activity_voiced_ratio"] = round(float(np.mean(voiced)), 4)
    if np.any(voiced):
        result["activity_periodicity"] = round(float(np.median(periodicity[voiced])), 4)
    return result
