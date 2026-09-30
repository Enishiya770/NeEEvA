"""Bounded final-speech context; no model calls and no streaming policy changes."""
import math

SAMPLE_RATE = 16000
MAX_CONTEXT_SECONDS = 18.0
TARGET_CONTEXT_SECONDS = 15.0
LEGACY_WINDOW_SECONDS = 9.0
OVERLAP_SECONDS = 0.8
MIN_PAUSE_SECONDS = 0.18
POLICY = "speech-context-v1"


def speech_context_reason(analysis, raw_duration, *, vad_has_speech,
                          expect_singing=False, singing_vad_override=False):
    """A conservative acoustic eligibility gate, not a new singing verdict.

    The final singing classifier needs the transcript, so cannot select the first
    decode. Reuse only the existing quick probe and reject missing/ambiguous
    evidence. Pitch frequency alone is deliberately not used: quick FFT peaks
    have known octave errors. Never extrapolate a tail probe to the whole turn.
    """
    if expect_singing or singing_vad_override:
        return "singing-request-or-override"
    if not vad_has_speech:
        return "no-speech-vad"
    if not isinstance(analysis, dict) or analysis.get("analysis_available") is not True:
        return "analysis-unavailable"
    if analysis.get("is_singing") is not False:
        return "singing-or-unknown"
    try:
        values = {key: float(analysis[key]) for key in (
            "duration", "analysis_window_offset_seconds", "singing_probability",
            "sustained_ratio", "pitch_stability", "voiced_ratio", "periodicity_mean")}
        duration = float(raw_duration)
    except (KeyError, TypeError, ValueError, OverflowError):
        return "invalid-analysis"
    if not all(math.isfinite(v) for v in [duration, *values.values()]):
        return "invalid-analysis"
    if duration <= 0 or values["analysis_window_offset_seconds"] != 0 or abs(values["duration"] - duration) > .05:
        return "partial-analysis-coverage"
    if any(not 0 <= values[key] <= 1 for key in (
            "singing_probability", "sustained_ratio", "pitch_stability",
            "voiced_ratio", "periodicity_mean")):
        return "invalid-analysis"
    if (values["singing_probability"] >= .45 or values["sustained_ratio"] >= .08
            or values["pitch_stability"] >= .50 or values["voiced_ratio"] < .30
            or values["periodicity_mean"] < .30):
        return "melodic-or-uncertain"
    return "ordinary-speech-evidence"


def plan_speech_windows(total_samples, vad_segments_ms, content_start_seconds=0.0):
    """Return sample bounds, or None for invalid VAD evidence.

    Retain up to 18 seconds in one call. For longer speech prefer an existing
    >=180ms VAD pause near 15 seconds. If none exists in the bounded search range,
    fall back to a 9-second cut with the existing 800ms overlap. VAD timestamps
    refer to the upload, so subtract the outer-silence trim offset first. No audio
    is discarded, and each window has a strict length limit.
    """
    if not isinstance(total_samples, int) or total_samples <= 0:
        return None
    try:
        offset = float(content_start_seconds)
        if not math.isfinite(offset) or offset < 0 or not vad_segments_ms:
            return None
        duration = total_samples / SAMPLE_RATE
        segments = []
        for item in vad_segments_ms:
            begin, end = float(item[0]) / 1000, float(item[1]) / 1000
            if not math.isfinite(begin) or not math.isfinite(end) or begin < 0 or end <= begin:
                return None
            begin, end = max(0.0, begin - offset), min(duration, end - offset)
            if end > begin:
                segments.append((begin, end))
    except (TypeError, ValueError, IndexError, OverflowError):
        return None
    if not segments:
        return None
    # Merge overlapping VAD regions; they must not manufacture false pauses.
    regions = []
    for begin, end in sorted(segments):
        if regions and begin <= regions[-1][1]:
            regions[-1] = (regions[-1][0], max(regions[-1][1], end))
        else:
            regions.append((begin, end))
    pauses = [int(round((left[1] + right[0]) * .5 * SAMPLE_RATE))
              for left, right in zip(regions, regions[1:])
              if right[0] - left[1] >= MIN_PAUSE_SECONDS]
    maximum = int(MAX_CONTEXT_SECONDS * SAMPLE_RATE)
    target = int(TARGET_CONTEXT_SECONDS * SAMPLE_RATE)
    legacy = int(LEGACY_WINDOW_SECONDS * SAMPLE_RATE)
    overlap = int(OVERLAP_SECONDS * SAMPLE_RATE)
    windows, start = [], 0
    while start < total_samples:
        if total_samples - start <= maximum:
            windows.append((start, total_samples))
            break
        candidates = [cut for cut in pauses if start + legacy <= cut <= start + maximum]
        end = min(candidates, key=lambda cut: (abs(cut - start - target), cut)) if candidates else start + legacy
        windows.append((start, end))
        start = end - overlap
    return windows
