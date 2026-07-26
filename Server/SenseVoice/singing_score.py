"""Renderer-neutral score extraction for sung performances.

The score is independent from SVC/RVC audio.  A singing voice synthesizer can
consume notes, continuous F0 and dynamics without using the user's waveform as
its carrier.
"""

from __future__ import annotations

import math
from typing import Dict, List, Optional

import numpy as np

from japanese_lyrics import normalise_japanese_lyrics


NOTE_NAMES = ("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")


def _round(value: float, digits: int = 4) -> float:
    return round(float(value), digits)


def _note_name(midi: int) -> str:
    if midi <= 0:
        return ""
    return f"{NOTE_NAMES[midi % 12]}{midi // 12 - 1}"


def _normalise_language(value: str) -> str:
    language = (value or "").strip().lower()
    aliases = {
        "chinese": "zh",
        "mandarin": "zh",
        "cmn": "zh",
        "english": "en",
        "japanese": "ja",
        "jp": "ja",
        "cantonese": "yue",
        "zh-yue": "yue",
    }
    return aliases.get(language, language)


def _energy_envelope(
    signal: Optional[np.ndarray],
    frame_count: int,
    hop_seconds: float,
    sample_rate: int,
) -> List[float]:
    if signal is None or frame_count <= 0:
        return [0.0] * max(0, frame_count)
    audio = np.asarray(signal, dtype=np.float32).reshape(-1)
    if audio.size == 0:
        return [0.0] * frame_count

    hop_samples = max(1, int(round(hop_seconds * sample_rate)))
    window_samples = max(hop_samples, int(round(0.04 * sample_rate)))
    envelope = np.zeros(frame_count, dtype=np.float32)
    for index in range(frame_count):
        start = index * hop_samples
        end = min(audio.size, start + window_samples)
        if end > start:
            frame = audio[start:end]
            envelope[index] = float(np.sqrt(np.mean(frame * frame) + 1e-12))
    nonzero = envelope[envelope > 1e-6]
    reference = float(np.percentile(nonzero, 95)) if nonzero.size else 1.0
    envelope = np.clip(envelope / max(reference, 1e-6), 0.0, 1.0)
    return [_round(value, 4) for value in envelope]


def _stable_note_labels(midi: np.ndarray, voiced: np.ndarray) -> np.ndarray:
    """Quantise pitch while suppressing one-frame note-boundary jitter."""
    labels = np.full(midi.size, -1, dtype=np.int16)
    labels[voiced] = np.rint(midi[voiced]).astype(np.int16)
    if labels.size < 3:
        return labels

    output = labels.copy()
    index = 0
    while index < labels.size:
        if labels[index] < 0:
            index += 1
            continue
        end = index + 1
        while end < labels.size and labels[end] >= 0:
            end += 1
        run = labels[index:end]
        if run.size >= 3:
            padded = np.pad(run, (1, 1), mode="edge")
            windows = np.lib.stride_tricks.sliding_window_view(padded, 3)
            output[index:end] = np.median(windows, axis=1).astype(np.int16)
        index = end
    # Absorb sub-80 ms fragments into their surrounding stable note. These
    # fragments are usually octave/harmonic mistakes at consonant boundaries.
    for _ in range(4):
        runs = []
        start = 0
        for index in range(1, output.size + 1):
            if index < output.size and output[index] == output[start]:
                continue
            runs.append((start, index, int(output[start])))
            start = index
        changed = False
        for run_index, (start, end, label) in enumerate(runs):
            if end - start >= 8:
                continue
            previous = runs[run_index - 1][2] if run_index > 0 else None
            following = (
                runs[run_index + 1][2]
                if run_index + 1 < len(runs)
                else None
            )
            replacement = None
            if previous is not None and previous == following:
                replacement = previous
            elif label < 0 and previous is not None and following is not None:
                if previous >= 0 and following >= 0 and abs(previous - following) <= 2:
                    replacement = previous
            elif label >= 0:
                neighbours = [
                    candidate
                    for candidate in (previous, following)
                    if candidate is not None and candidate >= 0
                ]
                if neighbours:
                    replacement = min(
                        neighbours, key=lambda candidate: abs(candidate - label)
                    )
            if replacement is not None and replacement != label:
                output[start:end] = replacement
                changed = True
        if not changed:
            break
    return output


def _clean_pitch_track(
    pitch: np.ndarray,
    periodicity: np.ndarray,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, Dict]:
    """Suppress consonant spikes, short dropouts and local octave mistakes."""
    voiced = (pitch >= 55.0) & (pitch <= 900.0) & (periodicity >= 0.42)
    midi = np.zeros(pitch.size, dtype=np.float32)
    midi[voiced] = 69.0 + 12.0 * np.log2(
        np.maximum(pitch[voiced], 1e-6) / 440.0
    )

    removed_short = 0
    index = 0
    while index < midi.size:
        if midi[index] <= 0:
            index += 1
            continue
        end = index + 1
        while end < midi.size and midi[end] > 0:
            end += 1
        if end - index < 6:
            removed_short += end - index
            midi[index:end] = 0.0
        index = end

    filled_gaps = 0
    index = 1
    while index < midi.size - 1:
        if midi[index] > 0:
            index += 1
            continue
        end = index + 1
        while end < midi.size and midi[end] <= 0:
            end += 1
        gap = end - index
        if (
            gap <= 6
            and midi[index - 1] > 0
            and end < midi.size
            and midi[end] > 0
            and abs(float(midi[index - 1] - midi[end])) <= 7.0
        ):
            midi[index:end] = np.linspace(
                midi[index - 1], midi[end], gap + 2, dtype=np.float32
            )[1:-1]
            filled_gaps += gap
        index = end

    corrected_octaves = 0
    corrected = midi.copy()
    radius = 24
    for index in np.flatnonzero(midi > 0):
        local = midi[max(0, index - radius) : min(midi.size, index + radius + 1)]
        local = local[local > 0]
        if local.size < 5:
            continue
        center = float(np.median(local))
        value = float(midi[index])
        candidates = value + np.arange(-3, 4, dtype=np.float32) * 12.0
        best = float(candidates[np.argmin(np.abs(candidates - center))])
        if abs(value - center) > 7.0 and abs(best - center) <= 4.5:
            corrected[index] = best
            corrected_octaves += 1

    global_octaves = 0
    voiced_values = corrected[corrected > 0]
    if voiced_values.size:
        global_center = float(np.median(voiced_values))
        index = 0
        while index < corrected.size:
            value = float(corrected[index])
            if value <= 0 or abs(value - global_center) <= 12.0:
                index += 1
                continue
            direction = 1 if value > global_center else -1
            end = index + 1
            while (
                end < corrected.size
                and corrected[end] > 0
                and (1 if corrected[end] > global_center else -1) == direction
                and abs(float(corrected[end]) - global_center) > 12.0
            ):
                end += 1
            if end - index <= 50:
                for cursor in range(index, end):
                    candidates = (
                        float(corrected[cursor])
                        + np.arange(-3, 4, dtype=np.float32) * 12.0
                    )
                    best = float(
                        candidates[np.argmin(np.abs(candidates - global_center))]
                    )
                    if abs(best - global_center) <= 10.0:
                        corrected[cursor] = best
                        global_octaves += 1
            index = end

    smoothed = corrected.copy()
    for index in np.flatnonzero(corrected > 0):
        local = corrected[max(0, index - 2) : min(corrected.size, index + 3)]
        local = local[local > 0]
        if local.size >= 3:
            smoothed[index] = float(np.median(local))

    cleaned_voiced = smoothed > 0
    cleaned_pitch = np.zeros_like(pitch)
    cleaned_pitch[cleaned_voiced] = (
        440.0 * np.power(2.0, (smoothed[cleaned_voiced] - 69.0) / 12.0)
    )
    return cleaned_pitch, cleaned_voiced, smoothed, {
        "removed_short_frames": int(removed_short),
        "filled_gap_frames": int(filled_gaps),
        "corrected_octave_frames": int(corrected_octaves),
        "global_octave_frames": int(global_octaves),
    }


def _note_events(
    midi: np.ndarray,
    voiced: np.ndarray,
    periodicity: np.ndarray,
    hop_seconds: float,
    duration: float,
) -> List[Dict]:
    labels = _stable_note_labels(midi, voiced)
    if labels.size == 0:
        return []

    events: List[Dict] = []
    start = 0
    for index in range(1, labels.size + 1):
        if index < labels.size and labels[index] == labels[start]:
            continue
        label = int(labels[start])
        event_start = start * hop_seconds
        event_end = min(duration, index * hop_seconds)
        if event_end > event_start + 1e-4:
            local_periodicity = periodicity[start:index]
            confidence = (
                float(np.mean(local_periodicity))
                if label >= 0 and local_periodicity.size
                else 1.0
            )
            events.append(
                {
                    "midi": label if label >= 0 else 0,
                    "note_name": _note_name(label),
                    "start_seconds": _round(event_start, 3),
                    "duration_seconds": _round(event_end - event_start, 3),
                    "note_type": "note" if label >= 0 else "rest",
                    "confidence": _round(max(0.0, min(1.0, confidence)), 3),
                }
            )
        start = index

    covered = labels.size * hop_seconds
    if duration > covered + hop_seconds * 0.5:
        events.append(
            {
                "midi": 0,
                "note_name": "",
                "start_seconds": _round(covered, 3),
                "duration_seconds": _round(duration - covered, 3),
                "note_type": "rest",
                "confidence": 1.0,
            }
        )
    return events


def _breath_positions(events: List[Dict]) -> List[float]:
    positions: List[float] = []
    for index, event in enumerate(events):
        if event.get("note_type") != "rest":
            continue
        duration = float(event.get("duration_seconds", 0.0))
        if duration < 0.12 or index == 0 or index == len(events) - 1:
            continue
        positions.append(
            _round(float(event.get("start_seconds", 0.0)) + duration * 0.5, 3)
        )
    return positions


def _vibrato_regions(
    midi: np.ndarray,
    voiced: np.ndarray,
    hop_seconds: float,
) -> List[Dict]:
    """Estimate sustained periodic pitch modulation for future SVS controls."""
    regions: List[Dict] = []
    window = max(16, int(round(0.65 / max(hop_seconds, 1e-3))))
    stride = max(4, window // 3)
    for start in range(0, max(0, midi.size - window + 1), stride):
        end = start + window
        local_voiced = voiced[start:end]
        if float(np.mean(local_voiced)) < 0.85:
            continue
        values = midi[start:end]
        centered = values - np.mean(values)
        depth = float(np.std(centered) * math.sqrt(2.0))
        if depth < 0.08 or depth > 1.4:
            continue
        spectrum = np.abs(np.fft.rfft(centered * np.hanning(centered.size)))
        frequencies = np.fft.rfftfreq(centered.size, d=hop_seconds)
        mask = (frequencies >= 3.5) & (frequencies <= 8.5)
        if not np.any(mask):
            continue
        local_index = int(np.argmax(spectrum[mask]))
        rate = float(frequencies[mask][local_index])
        strength = float(
            spectrum[mask][local_index] / max(np.sum(spectrum[mask]), 1e-6)
        )
        if strength < 0.18:
            continue
        regions.append(
            {
                "start_seconds": _round(start * hop_seconds, 3),
                "duration_seconds": _round(window * hop_seconds, 3),
                "rate_hz": _round(rate, 2),
                "depth_semitones": _round(depth, 3),
                "confidence": _round(min(1.0, strength * 3.0), 3),
            }
        )
    return regions[:32]


def build_singing_score(
    pitch_hz: np.ndarray,
    periodicity: np.ndarray,
    hop_seconds: float,
    duration: float,
    lyrics: str = "",
    language: str = "",
    signal: Optional[np.ndarray] = None,
    sample_rate: int = 16000,
    extractor_backend: str = "",
    confidence: float = 0.0,
) -> Dict:
    pitch = np.asarray(pitch_hz, dtype=np.float32).reshape(-1)
    periodicity = np.asarray(periodicity, dtype=np.float32).reshape(-1)
    count = min(pitch.size, periodicity.size)
    pitch = pitch[:count]
    periodicity = periodicity[:count]
    pitch, voiced, midi, cleaning = _clean_pitch_track(pitch, periodicity)
    f0 = np.where(voiced, pitch, 0.0)

    events = _note_events(midi, voiced, periodicity, hop_seconds, duration)
    normalised_language = _normalise_language(language)
    score = {
        "schema_version": 1,
        "source": "recognized_performance",
        "extractor_backend": extractor_backend or "builtin",
        "language": normalised_language,
        "lyrics": (lyrics or "").strip(),
        "lyrics_alignment": "acoustic_voiced_time",
        "duration_seconds": _round(duration, 3),
        "frame_seconds": _round(hop_seconds, 4),
        "confidence": _round(max(0.0, min(1.0, confidence)), 3),
        "notes": events,
        "f0_hz": [_round(value, 2) for value in f0],
        "energy": _energy_envelope(signal, count, hop_seconds, sample_rate),
        "breath_positions_seconds": _breath_positions(events),
        "vibrato": _vibrato_regions(midi, voiced, hop_seconds),
        "pitch_cleaning": cleaning,
    }
    if normalised_language == "ja":
        score.update(normalise_japanese_lyrics(lyrics))
    return score
