"""One-shot SoulX-Singer transcription + SVS runner.

The user's target waveform is consumed only by SoulX's transcription pipeline.
The renderer receives the resulting metadata, not the waveform, so this is SVS
rather than audio-to-audio voice conversion.
"""

from __future__ import annotations

import argparse
import gc
import hashlib
import json
import math
import os
import random
import re
import shutil
import sys
import tempfile
import time
from pathlib import Path

from japanese_phonemes import (
    japanese_g2p_transform,
    validate_against_phoneset,
)


CACHE_VERSION = "prompt-metadata-v1"
SOULX_FRAME_SECONDS = 0.02
_CJK_RE = re.compile(r"[\u3400-\u9fff]")
_EN_WORD_RE = re.compile(r"[A-Za-z]+(?:'[A-Za-z]+)*")


def _valid_metadata(path: Path, expected_language: str = "") -> bool:
    try:
        metadata = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    if not isinstance(metadata, list) or not metadata:
        return False
    required = {"duration", "phoneme", "note_pitch", "note_type", "f0"}
    for segment in metadata:
        if not isinstance(segment, dict) or not required.issubset(segment):
            return False
        lengths = [
            len(str(segment[key]).split())
            for key in ("duration", "phoneme", "note_pitch", "note_type")
        ]
        if not lengths[0] or len(set(lengths)) != 1:
            return False
    if expected_language:
        languages = {
            str(segment.get("language", "")).strip().lower()
            for segment in metadata
        }
        if languages and expected_language.strip().lower() not in languages:
            return False
    return True


def _prompt_cache_path(prompt_path: Path, language: str, cache_dir: Path) -> Path:
    digest = hashlib.sha256()
    digest.update(CACHE_VERSION.encode("utf-8"))
    digest.update(language.strip().lower().encode("utf-8"))
    with prompt_path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return cache_dir / f"{digest.hexdigest()}.json"


def prepare_prompt_metadata(
    prompt_path: Path,
    language: str,
    cache_dir: Path,
    temp_dir: Path,
    pipeline_factory,
) -> tuple[Path, str, object | None]:
    """Return cached prompt metadata without writing sidecars into Unity Assets."""
    cache_dir.mkdir(parents=True, exist_ok=True)
    cached = _prompt_cache_path(prompt_path, language, cache_dir)
    if _valid_metadata(cached, language):
        return cached, "hit", None

    sidecar = prompt_path.with_suffix(".json")
    if _valid_metadata(sidecar, language):
        temporary = cached.with_suffix(".tmp")
        shutil.copy2(sidecar, temporary)
        os.replace(temporary, cached)
        return cached, "imported-sidecar", None

    prompt_dir = temp_dir / "prompt"
    prompt_copy = temp_dir / f"prompt_source{prompt_path.suffix.lower()}"
    shutil.copy2(prompt_path, prompt_copy)
    pipeline = pipeline_factory(language, prompt_dir)
    pipeline.run(
        audio_path=str(prompt_copy),
        vocal_sep=False,
        max_merge_duration=30000,
        language=language,
    )
    generated = prompt_dir / "metadata.json"
    if not _valid_metadata(generated, language):
        raise RuntimeError("SoulX prompt preprocessing did not create valid metadata")
    temporary = cached.with_suffix(".tmp")
    shutil.copy2(generated, temporary)
    os.replace(temporary, cached)
    return cached, "miss-generated", pipeline


def lyric_units(lyrics: str, language: str) -> list[str]:
    text = str(lyrics or "").strip()
    if language == "English":
        return _EN_WORD_RE.findall(text)
    units = []
    english_buffer = []
    for character in text:
        if character.isascii() and (character.isalpha() or character == "'"):
            english_buffer.append(character)
            continue
        if english_buffer:
            units.append("".join(english_buffer))
            english_buffer.clear()
        if _CJK_RE.fullmatch(character):
            units.append(character)
    if english_buffer:
        units.append("".join(english_buffer))
    return units


def fast_g2p_transform(words: list[str], language: str) -> list[str]:
    """SoulX-compatible G2P without eagerly loading all three languages."""
    transformed = ["<SP>"] * len(words)
    chinese = []
    english = []
    for index, word in enumerate(words):
        if word == "<SP>":
            continue
        if len(word) == 1 and _CJK_RE.fullmatch(word):
            chinese.append((index, word))
        elif _EN_WORD_RE.fullmatch(word):
            english.append((index, word))

    if chinese:
        sentence = "".join(word for _, word in chinese)
        if language == "Cantonese":
            import ToJyutping

            values = [
                f"yue_{item[1]}"
                for item in ToJyutping.get_jyutping_list(sentence)
            ]
        else:
            from g2pM import G2pM

            values = [
                f"zh_{value}"
                for value in G2pM()(sentence, tone=True, char_split=False)
            ]
        if len(values) != len(chinese):
            raise ValueError("Chinese G2P length does not match lyric units")
        for (index, _), value in zip(chinese, values):
            transformed[index] = value

    if english:
        from g2p_en import G2p

        converter = G2p()
        for index, word in english:
            phones = [
                str(phone)
                for phone in converter(word.lower())
                if str(phone).strip() and str(phone) != " "
            ]
            if phones:
                transformed[index] = "en_" + "-".join(phones)
    return transformed


def _median(values: list[float]) -> float:
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[middle]
    return (ordered[middle - 1] + ordered[middle]) * 0.5


def _raw_note_voiced_mask(score: dict, frame_count: int) -> list[bool] | None:
    notes = [note for note in score.get("notes", []) if isinstance(note, dict)]
    if not notes:
        return None
    mask = [False] * frame_count
    for note in notes:
        if float(note.get("midi", 0) or 0) <= 0:
            continue
        start = max(
            0,
            int(math.floor(float(note.get("start_seconds", 0.0)) /
                           SOULX_FRAME_SECONDS)),
        )
        end = min(
            frame_count,
            int(math.ceil(
                (float(note.get("start_seconds", 0.0)) +
                 float(note.get("duration_seconds", 0.0))) /
                SOULX_FRAME_SECONDS
            )),
        )
        for index in range(start, end):
            mask[index] = True
    return mask


def _clean_score_f0(score: dict, duration: float) -> tuple[list[float], dict]:
    source = [max(0.0, float(value or 0.0)) for value in score.get("f0_hz", [])]
    if not source:
        return [], {}
    source_step = max(0.001, float(score.get("frame_seconds", 0.01)))
    frame_count = max(1, int(round(duration / SOULX_FRAME_SECONDS)))
    raw = []
    for frame in range(frame_count):
        source_index = min(
            len(source) - 1,
            max(0, int(round(frame * SOULX_FRAME_SECONDS / source_step))),
        )
        raw.append(source[source_index])

    note_mask = _raw_note_voiced_mask(score, frame_count)
    midi = []
    for index, value in enumerate(raw):
        voiced = 55.0 <= value <= 900.0
        if note_mask is not None:
            voiced = voiced and note_mask[index]
        midi.append(
            69.0 + 12.0 * math.log2(value / 440.0) if voiced else 0.0
        )

    # Remove very short pitch detections, which are usually consonants,
    # harmonics or room-noise rather than sung notes.
    removed_short_frames = 0
    index = 0
    while index < frame_count:
        if midi[index] <= 0:
            index += 1
            continue
        end = index + 1
        while end < frame_count and midi[end] > 0:
            end += 1
        if end - index < 3:
            removed_short_frames += end - index
            for cursor in range(index, end):
                midi[cursor] = 0.0
        index = end

    # Bridge tiny unvoiced holes inside the same phrase.
    filled_gap_frames = 0
    index = 1
    while index < frame_count - 1:
        if midi[index] > 0:
            index += 1
            continue
        end = index + 1
        while end < frame_count and midi[end] <= 0:
            end += 1
        gap = end - index
        if (
            gap <= 3
            and midi[index - 1] > 0
            and end < frame_count
            and midi[end] > 0
            and abs(midi[index - 1] - midi[end]) <= 7.0
        ):
            for offset, cursor in enumerate(range(index, end), 1):
                fraction = offset / float(gap + 1)
                midi[cursor] = (
                    midi[index - 1] * (1.0 - fraction)
                    + midi[end] * fraction
                )
                filled_gap_frames += 1
        index = end

    # Correct isolated octave/harmonic errors against a half-second local
    # context while preserving genuine melodic intervals.
    corrected_octave_frames = 0
    corrected = list(midi)
    radius = 12
    for index, value in enumerate(midi):
        if value <= 0:
            continue
        local = [
            candidate
            for candidate in midi[max(0, index - radius) :
                                  min(frame_count, index + radius + 1)]
            if candidate > 0
        ]
        if len(local) < 3:
            continue
        center = _median(local)
        candidates = [value + octave * 12.0 for octave in range(-3, 4)]
        best = min(candidates, key=lambda candidate: abs(candidate - center))
        if abs(value - center) > 7.0 and abs(best - center) <= 4.5:
            corrected[index] = best
            corrected_octave_frames += 1

    global_octave_frames = 0
    voiced_values = [value for value in corrected if value > 0]
    if voiced_values:
        global_center = _median(voiced_values)
        index = 0
        while index < frame_count:
            value = corrected[index]
            if value <= 0 or abs(value - global_center) <= 12.0:
                index += 1
                continue
            direction = 1 if value > global_center else -1
            end = index + 1
            while (
                end < frame_count
                and corrected[end] > 0
                and (1 if corrected[end] > global_center else -1) == direction
                and abs(corrected[end] - global_center) > 12.0
            ):
                end += 1
            if end - index <= 25:
                for cursor in range(index, end):
                    candidates = [
                        corrected[cursor] + octave * 12.0
                        for octave in range(-3, 4)
                    ]
                    best = min(
                        candidates,
                        key=lambda candidate: abs(candidate - global_center),
                    )
                    if abs(best - global_center) <= 10.0:
                        corrected[cursor] = best
                        global_octave_frames += 1
            index = end

    # Five-frame median smoothing removes single-frame note-boundary jitter
    # without flattening normal vibrato.
    smoothed = list(corrected)
    for index, value in enumerate(corrected):
        if value <= 0:
            continue
        local = [
            candidate
            for candidate in corrected[max(0, index - 2) :
                                       min(frame_count, index + 3)]
            if candidate > 0
        ]
        if len(local) >= 3:
            smoothed[index] = _median(local)

    cleaned = [
        440.0 * (2.0 ** ((value - 69.0) / 12.0)) if value > 0 else 0.0
        for value in smoothed
    ]
    return cleaned, {
        "raw_frames": frame_count,
        "removed_short_frames": removed_short_frames,
        "filled_gap_frames": filled_gap_frames,
        "corrected_octave_frames": corrected_octave_frames,
        "global_octave_frames": global_octave_frames,
    }


def _smooth_short_label_runs(labels: list[int]) -> list[int]:
    result = list(labels)
    for _ in range(4):
        runs = []
        start = 0
        for index in range(1, len(result) + 1):
            if index < len(result) and result[index] == result[start]:
                continue
            runs.append((start, index, result[start]))
            start = index
        changed = False
        for run_index, (start, end, label) in enumerate(runs):
            if end - start >= 4:
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
            elif label <= 0 and previous and following and abs(previous - following) <= 2:
                replacement = previous
            elif label > 0:
                voiced_neighbours = [
                    candidate
                    for candidate in (previous, following)
                    if candidate is not None and candidate > 0
                ]
                if voiced_neighbours:
                    replacement = min(
                        voiced_neighbours,
                        key=lambda candidate: abs(candidate - label),
                    )
            if replacement is not None and replacement != label:
                for cursor in range(start, end):
                    result[cursor] = replacement
                changed = True
        if not changed:
            break
    return result


def _score_note_events(
    score: dict,
    duration: float,
    cleaned_f0: list[float] | None = None,
) -> list[dict]:
    f0 = cleaned_f0
    if f0 is None:
        f0, _ = _clean_score_f0(score, duration)
    labels = [
        int(round(69.0 + 12.0 * math.log2(value / 440.0)))
        if value >= 55.0
        else 0
        for value in f0
    ]
    labels = _smooth_short_label_runs(labels)
    events = []
    start = 0
    for index in range(1, len(labels) + 1):
        if index < len(labels) and labels[index] == labels[start]:
            continue
        event_start = start * SOULX_FRAME_SECONDS
        event_end = min(duration, index * SOULX_FRAME_SECONDS)
        if event_end > event_start:
            events.append(
                {
                    "start": event_start,
                    "end": event_end,
                    "midi": max(0, labels[start]),
                }
            )
        start = index
    if events and events[-1]["end"] < duration:
        events[-1]["end"] = duration
    return events


def _merge_micro_note_events(
    events: list[dict], minimum_seconds: float = 0.08
) -> list[dict]:
    """Absorb sub-80 ms voiced fragments into the nearest voiced neighbour."""
    result = [dict(event) for event in events]
    for _ in range(len(result)):
        candidate = next(
            (
                index
                for index, event in enumerate(result)
                if event["midi"] > 0
                and event["end"] - event["start"] < minimum_seconds - 1e-6
            ),
            None,
        )
        if candidate is None:
            break
        event = result[candidate]
        neighbours = [
            index
            for index in (candidate - 1, candidate + 1)
            if 0 <= index < len(result) and result[index]["midi"] > 0
        ]
        if not neighbours:
            break
        replacement = min(
            neighbours,
            key=lambda index: (
                abs(result[index]["midi"] - event["midi"]),
                -(result[index]["end"] - result[index]["start"]),
            ),
        )
        if replacement < candidate:
            result[replacement]["end"] = event["end"]
        else:
            result[replacement]["start"] = event["start"]
        result.pop(candidate)
    return result


def _split_voiced_events(events: list[dict], minimum_count: int) -> list[dict]:
    result = [dict(event) for event in events]
    while sum(event["midi"] > 0 for event in result) < minimum_count:
        candidates = [
            (event["end"] - event["start"], index)
            for index, event in enumerate(result)
            if event["midi"] > 0
            and event["end"] - event["start"] >= SOULX_FRAME_SECONDS * 2
        ]
        if not candidates:
            break
        _, index = max(candidates)
        event = result[index]
        midpoint = (event["start"] + event["end"]) * 0.5
        result[index : index + 1] = [
            {"start": event["start"], "end": midpoint, "midi": event["midi"]},
            {"start": midpoint, "end": event["end"], "midi": event["midi"]},
        ]
    return result


def _resample_score_f0(score: dict, duration: float) -> list[float]:
    values, _ = _clean_score_f0(score, duration)
    return values


def _assign_lyrics_to_events(
    events: list[dict], lyric_count: int
) -> dict[int, int]:
    """Partition existing voiced notes at their natural boundaries.

    Earlier code cut notes at mathematically uniform lyric boundaries.  That
    could leave 20-40 ms note fragments and make the singer repeat consonants.
    The score already contains at least one voiced event per lyric here, so
    choose the closest *existing* note boundary while reserving one event for
    every remaining lyric.
    """
    voiced = [
        (event_index, event["end"] - event["start"])
        for event_index, event in enumerate(events)
        if event["midi"] > 0
    ]
    if not voiced or lyric_count <= 0:
        return {}
    lyric_count = min(lyric_count, len(voiced))
    cumulative = []
    cursor = 0.0
    for _, duration in voiced:
        cursor += duration
        cumulative.append(cursor)
    total_voiced = cumulative[-1]

    boundaries = []
    previous = 0
    for lyric_index in range(1, lyric_count):
        minimum = previous + 1
        maximum = len(voiced) - (lyric_count - lyric_index)
        target = total_voiced * lyric_index / lyric_count
        boundary = min(
            range(minimum, maximum + 1),
            key=lambda candidate: abs(cumulative[candidate - 1] - target),
        )
        boundaries.append(boundary)
        previous = boundary

    assignment = {}
    start = 0
    for lyric_index, end in enumerate(boundaries + [len(voiced)]):
        for voiced_index in range(start, end):
            assignment[voiced[voiced_index][0]] = lyric_index
        start = end
    return assignment


def _sequence_index_mapping(
    source: list[str], target: list[str]
) -> dict[int, int]:
    """Map noisy acoustic lyric units onto an optional corrected transcript."""
    if not source or not target:
        return {}
    rows, columns = len(source) + 1, len(target) + 1
    cost = [[0] * columns for _ in range(rows)]
    move = [[""] * columns for _ in range(rows)]
    for row in range(1, rows):
        cost[row][0] = row
        move[row][0] = "delete"
    for column in range(1, columns):
        cost[0][column] = column
        move[0][column] = "insert"
    for row in range(1, rows):
        for column in range(1, columns):
            choices = [
                (
                    cost[row - 1][column - 1]
                    + (0 if source[row - 1] == target[column - 1] else 1),
                    "diagonal",
                ),
                (cost[row - 1][column] + 1, "delete"),
                (cost[row][column - 1] + 1, "insert"),
            ]
            cost[row][column], move[row][column] = min(
                choices, key=lambda item: item[0]
            )

    mapping = {}
    row, column = len(source), len(target)
    while row > 0 or column > 0:
        direction = move[row][column]
        if direction == "diagonal":
            mapping[row - 1] = column - 1
            row -= 1
            column -= 1
        elif direction == "delete":
            row -= 1
        else:
            column -= 1
    mapped = sorted(mapping)
    for source_index in range(len(source)):
        if source_index in mapping:
            continue
        if mapped:
            neighbour = min(mapped, key=lambda index: abs(index - source_index))
            mapping[source_index] = mapping[neighbour]
        else:
            mapping[source_index] = min(
                len(target) - 1,
                int(source_index * len(target) / max(1, len(source))),
            )
    return mapping


def _acoustic_target_metadata(
    score: dict,
    language: str,
    lyrics: list[str],
    cleaned_f0: list[float],
    cleaning: dict,
    alignment: dict,
    g2p_transformer,
) -> tuple[list[dict], int] | None:
    raw_words = alignment.get("words", [])
    raw_durations = alignment.get("durations", [])
    if (
        not isinstance(raw_words, list)
        or not isinstance(raw_durations, list)
        or len(raw_words) != len(raw_durations)
        or not raw_words
    ):
        return None

    words = []
    durations = []
    for raw_word, raw_duration in zip(raw_words, raw_durations):
        word = str(raw_word).strip()
        duration = max(SOULX_FRAME_SECONDS, float(raw_duration))
        if word == "<SP>":
            if words and words[-1] == "<SP>":
                durations[-1] += duration
            else:
                words.append("<SP>")
                durations.append(duration)
            continue
        units = lyric_units(word, language)
        if not units:
            continue
        share = duration / len(units)
        for unit in units:
            words.append(unit)
            durations.append(share)
    lexical_positions = [
        index for index, word in enumerate(words) if word != "<SP>"
    ]
    if not lexical_positions:
        return None

    # Collapse consecutive acoustic duplicates to recover the human-readable
    # transcript. Stable F0 transitions inside each timestamped syllable are
    # expanded back into note_type=3 melisma below.
    acoustic_units = []
    acoustic_group_by_position = {}
    previous_unit = None
    for position in lexical_positions:
        unit = words[position]
        if unit != previous_unit:
            acoustic_units.append(unit)
            previous_unit = unit
        acoustic_group_by_position[position] = len(acoustic_units) - 1

    explicit_override = bool(score.get("lyrics_override", False))
    if explicit_override and lyrics:
        resolved_units = lyrics
        transcript_source = "explicit-lyrics-override"
    elif acoustic_units and (
        not lyrics
        or 0.5 <= len(acoustic_units) / max(1, len(lyrics)) <= 1.8
    ):
        resolved_units = acoustic_units
        transcript_source = "singing-asr"
    else:
        resolved_units = lyrics
        transcript_source = "final-asr-fallback"
    if not resolved_units:
        return None

    group_mapping = _sequence_index_mapping(acoustic_units, resolved_units)
    total = sum(durations)
    score_duration = max(0.0, float(score.get("duration_seconds", 0.0)))
    if total <= 0 or score_duration <= 0:
        return None
    scale = score_duration / total
    durations = [duration * scale for duration in durations]

    pitch_events = _merge_micro_note_events(
        _score_note_events(score, score_duration, cleaned_f0)
    )
    pitch_boundaries = [
        (current["start"], abs(previous["midi"] - current["midi"]))
        for previous, current in zip(pitch_events, pitch_events[1:])
        if previous["midi"] > 0
        and current["midi"] > 0
        and abs(previous["midi"] - current["midi"]) >= 2
    ]

    aligned_durations = []
    text = []
    note_pitch = []
    note_type = []
    cursor = 0.0
    previous_lyric = -1
    for position, (word, duration) in enumerate(zip(words, durations)):
        word_start = cursor
        word_end = cursor + duration
        cursor += duration
        if word == "<SP>":
            aligned_durations.append(duration)
            text.append("<SP>")
            note_pitch.append(0)
            note_type.append(1)
            continue
        group = acoustic_group_by_position[position]
        lyric_index = group_mapping[group]
        boundaries = [word_start]
        candidates = [
            (boundary, pitch_change)
            for boundary, pitch_change in pitch_boundaries
            if boundary - word_start >= 0.08
            and word_end - boundary >= 0.08
        ]
        if candidates and duration >= 0.4:
            # One syllable may span two notes, but turning every vibrato or
            # ornamental bend into a new consonant makes the singer stutter.
            boundary, _ = max(candidates, key=lambda item: item[1])
            boundaries.append(boundary)
        boundaries.append(word_end)
        for part_start, part_end in zip(boundaries, boundaries[1:]):
            part_duration = part_end - part_start
            start_frame = max(
                0, int(math.floor(part_start / SOULX_FRAME_SECONDS))
            )
            end_frame = min(
                len(cleaned_f0),
                max(start_frame + 1, int(math.ceil(
                    part_end / SOULX_FRAME_SECONDS
                ))),
            )
            local_f0 = [
                value
                for value in cleaned_f0[start_frame:end_frame]
                if value >= 55
            ]
            text.append(resolved_units[lyric_index])
            aligned_durations.append(part_duration)
            if local_f0:
                pitch = int(round(_median([
                    69.0 + 12.0 * math.log2(value / 440.0)
                    for value in local_f0
                ])))
            else:
                pitch = (
                    note_pitch[-1]
                    if note_pitch and note_pitch[-1] > 0
                    else 60
                )
            note_pitch.append(max(1, pitch))
            note_type.append(3 if lyric_index == previous_lyric else 2)
            previous_lyric = lyric_index

    phonemes = list(g2p_transformer(text, language))
    if len(phonemes) != len(text) or any(not value for value in phonemes):
        return None
    metadata = [
        {
            "index": f"score_0_{int(round(score_duration * 1000))}",
            "language": language,
            "time": [0, int(round(score_duration * 1000))],
            "duration": " ".join(
                f"{value:.3f}" for value in aligned_durations
            ),
            "text": " ".join(text),
            "phoneme": " ".join(phonemes),
            "note_pitch": " ".join(str(value) for value in note_pitch),
            "note_type": " ".join(str(value) for value in note_type),
            "f0": " ".join(f"{value:.2f}" for value in cleaned_f0),
            "alignment_method": "acoustic-word-timestamps",
            "lyrics_source": transcript_source,
            "recognized_lyrics": "".join(acoustic_units),
            "resolved_lyrics": "".join(resolved_units),
            "cleaned_note_events": len(text),
            "f0_cleaning": cleaning,
        }
    ]
    return metadata, len(cleaned_f0)


def build_direct_target_metadata(
    score: dict,
    language: str,
    g2p_transformer,
    acoustic_alignment: dict | None = None,
) -> tuple[list[dict], int]:
    """Build SoulX metadata directly from NeEEvA's aligned SingingScore."""
    japanese_adapter = language == "JapaneseAdapter"
    if japanese_adapter:
        lyrics = [
            str(unit)
            for unit in score.get("renderer_lyric_units", [])
            if str(unit)
        ]
    else:
        lyrics = lyric_units(str(score.get("lyrics", "")), language)
    duration = max(0.0, float(score.get("duration_seconds", 0.0)))
    f0, clean_diagnostic = _clean_score_f0(score, duration)
    if acoustic_alignment and not japanese_adapter:
        acoustic_metadata = _acoustic_target_metadata(
            score,
            language,
            lyrics,
            f0,
            clean_diagnostic,
            acoustic_alignment,
            g2p_transformer,
        )
        if acoustic_metadata is not None:
            return acoustic_metadata
    events = _score_note_events(score, duration, cleaned_f0=f0)
    events = _merge_micro_note_events(events)
    voiced_count = sum(event["midi"] > 0 for event in events)
    if not lyrics or not f0 or voiced_count == 0:
        raise ValueError("SingingScore lacks aligned lyrics, F0, or voiced notes")

    events = _split_voiced_events(events, len(lyrics))
    lyric_assignment = _assign_lyrics_to_events(events, len(lyrics))
    voiced_total = sum(
        event["end"] - event["start"]
        for event in events
        if event["midi"] > 0
    )
    if voiced_total <= SOULX_FRAME_SECONDS:
        raise ValueError("not enough voiced duration to align SingingScore lyrics")

    durations = []
    text = []
    note_pitch = []
    note_type = []
    previous_lyric = -1
    for event_index, event in enumerate(events):
        durations.append(max(SOULX_FRAME_SECONDS, event["end"] - event["start"]))
        midi = event["midi"]
        note_pitch.append(midi)
        if midi <= 0:
            text.append("<SP>")
            note_type.append(1)
            continue
        lyric_index = lyric_assignment[event_index]
        text.append(lyrics[lyric_index])
        note_type.append(3 if lyric_index == previous_lyric else 2)
        previous_lyric = lyric_index

    phonemes = list(g2p_transformer(text, language))
    if len(phonemes) != len(text) or any(not value for value in phonemes):
        raise ValueError("G2P did not return one phoneme per aligned note")
    duration_total = sum(durations)
    metadata = [
        {
            "index": f"score_0_{int(round(duration_total * 1000))}",
            "language": language,
            "time": [0, int(round(duration_total * 1000))],
            "duration": " ".join(f"{value:.3f}" for value in durations),
            "text": " ".join(text),
            "phoneme": " ".join(phonemes),
            "note_pitch": " ".join(str(value) for value in note_pitch),
            "note_type": " ".join(str(value) for value in note_type),
            "f0": " ".join(f"{value:.2f}" for value in f0),
            "alignment_method": "voiced-time-note-boundaries",
            "lyrics_source": (
                str(score.get("lyrics_reading_source", "score-kana"))
                if japanese_adapter
                else "score"
            ),
            "resolved_lyrics": (
                str(score.get("lyrics_reading", ""))
                if japanese_adapter
                else str(score.get("lyrics", ""))
            ),
            "cleaned_note_events": len(events),
            "f0_cleaning": clean_diagnostic,
        }
    ]
    return metadata, len(f0)


def apply_singing_score_f0(metadata_path: Path, score: dict) -> int:
    """Replace backend F0 with NeEEvA's final-ASR-aligned continuous contour."""
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    f0 = [float(value or 0.0) for value in score.get("f0_hz", [])]
    frame_seconds = max(0.001, float(score.get("frame_seconds", 0.01)))
    if not isinstance(metadata, list) or not f0:
        return 0

    replaced = 0
    for segment in metadata:
        if not isinstance(segment, dict):
            continue
        existing = str(segment.get("f0", "")).split()
        if not existing:
            continue
        time_range = segment.get("time", [0, 0])
        if not isinstance(time_range, list) or len(time_range) < 2:
            continue
        start_seconds = max(0.0, float(time_range[0]) / 1000.0)
        end_seconds = max(start_seconds, float(time_range[1]) / 1000.0)
        if len(existing) == 1:
            sample_times = [start_seconds]
        else:
            step = (end_seconds - start_seconds) / max(1, len(existing) - 1)
            sample_times = [
                start_seconds + index * step for index in range(len(existing))
            ]
        values = []
        for sample_time in sample_times:
            frame = min(
                len(f0) - 1,
                max(0, int(round(sample_time / frame_seconds))),
            )
            values.append(f"{max(0.0, f0[frame]):.2f}")
        segment["f0"] = " ".join(values)
        replaced += len(values)

    metadata_path.write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return replaced


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--soulx-root", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--prompt", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--score", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--target-language", required=True)
    parser.add_argument("--prompt-language", default="English")
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--device", default="cuda:0")
    parser.add_argument("--preprocess-device", default="cpu")
    parser.add_argument("--cache-dir", default="")
    parser.add_argument("--n-steps", type=int, default=12)
    parser.add_argument("--diagnostic-prompt-metadata", default="")
    parser.add_argument("--diagnostic-target-metadata", default="")
    return parser.parse_args()


def build_low_vram_model(model_path, config, device, use_fp16):
    """Build on CPU and cast before moving to CUDA.

    SoulX's upstream helper constructs the full FP32 model on CUDA before
    converting it to FP16. That transient allocation exceeds a 6 GB card even
    though the final FP16 renderer fits.
    """
    import torch
    from soulxsinger.models.soulxsinger import SoulXSinger

    model = SoulXSinger(config)
    try:
        checkpoint = torch.load(
            model_path,
            weights_only=False,
            map_location="cpu",
            mmap=True,
        )
    except (TypeError, RuntimeError):
        checkpoint = torch.load(model_path, weights_only=False, map_location="cpu")
    if "state_dict" not in checkpoint:
        raise KeyError(f"checkpoint has no state_dict: {model_path}")
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    del checkpoint
    gc.collect()

    cuda_device = str(device).lower().startswith("cuda")
    if use_fp16 and cuda_device:
        model.half()
        model.mel.float()
    model.eval()
    model.to(device)
    return model


def main():
    total_started = time.perf_counter()
    args = parse_args()
    root = Path(args.soulx_root).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))

    import numpy as np
    import torch

    seed = int(args.seed) & 0xFFFFFFFF
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    import_seconds = time.perf_counter() - total_started
    cache_dir = (
        Path(args.cache_dir).resolve()
        if args.cache_dir
        else Path(__file__).resolve().parent / "runtime" / "prompt_cache"
    )

    with tempfile.TemporaryDirectory(prefix="soulx_transcription_") as temp_name:
        temp = Path(temp_name)
        target_dir = temp / "target"
        score = json.loads(Path(args.score).read_text(encoding="utf-8"))
        if int(score.get("schema_version", 0)) != 1:
            raise RuntimeError("unsupported SingingScore schema")

        pipeline = None

        def pipeline_factory(language, save_dir):
            nonlocal pipeline
            if pipeline is None:
                from preprocess.pipeline import PreprocessPipeline

                pipeline = PreprocessPipeline(
                    device=args.preprocess_device,
                    language=language,
                    save_dir=str(save_dir),
                    vocal_sep=False,
                    max_merge_duration=30000,
                )
            else:
                pipeline.save_dir = str(save_dir)
            return pipeline

        prompt_started = time.perf_counter()
        prompt_metadata, prompt_cache, created_pipeline = prepare_prompt_metadata(
            Path(args.prompt).resolve(),
            args.prompt_language,
            cache_dir,
            temp,
            pipeline_factory,
        )
        if created_pipeline is not None:
            pipeline = created_pipeline
        prompt_seconds = time.perf_counter() - prompt_started

        target_started = time.perf_counter()
        target_metadata = target_dir / "metadata.json"
        target_dir.mkdir(parents=True, exist_ok=True)
        target_mode = "singing-score-direct"
        try:
            g2p_transformer = (
                japanese_g2p_transform
                if args.target_language == "JapaneseAdapter"
                else fast_g2p_transform
            )
            metadata, score_f0_frames = build_direct_target_metadata(
                score,
                args.target_language,
                g2p_transformer,
            )
            if args.target_language == "JapaneseAdapter":
                validate_against_phoneset(
                    metadata[0]["phoneme"].split(),
                    (
                        root
                        / "soulxsinger"
                        / "utils"
                        / "phoneme"
                        / "phone_set.json"
                    ),
                )
            target_metadata.write_text(
                json.dumps(metadata, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
        except Exception as direct_error:
            if args.target_language == "JapaneseAdapter":
                raise RuntimeError(
                    "Japanese kana score could not be adapted safely: "
                    f"{direct_error}"
                ) from direct_error
            target_mode = "backend-transcription"
            print(
                f"[SoulX] direct SingingScore metadata unavailable: {direct_error}",
                file=sys.stderr,
                flush=True,
            )
            target_pipeline = pipeline_factory(args.target_language, target_dir)
            target_pipeline.run(
                audio_path=args.target,
                vocal_sep=False,
                max_merge_duration=120000,
                language=args.target_language,
            )
            if not _valid_metadata(target_metadata, args.target_language):
                raise RuntimeError(
                    "SoulX target preprocessing did not create valid metadata"
                )
            score_f0_frames = apply_singing_score_f0(target_metadata, score)
            if score_f0_frames <= 0:
                raise RuntimeError(
                    "could not align SingingScore F0 to SoulX metadata"
                )
        target_seconds = time.perf_counter() - target_started
        if args.diagnostic_prompt_metadata:
            diagnostic_prompt = Path(args.diagnostic_prompt_metadata).resolve()
            diagnostic_prompt.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(prompt_metadata, diagnostic_prompt)
        if args.diagnostic_target_metadata:
            diagnostic_target = Path(args.diagnostic_target_metadata).resolve()
            diagnostic_target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(target_metadata, diagnostic_target)

        del pipeline
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

        model_started = time.perf_counter()
        from cli.inference import process
        from soulxsinger.utils.file_utils import load_config

        config_path = root / "soulxsinger" / "config" / "soulxsinger.yaml"
        phoneset_path = (
            root / "soulxsinger" / "utils" / "phoneme" / "phone_set.json"
        )
        config = load_config(str(config_path))
        inference_steps = max(4, min(32, int(args.n_steps)))
        config.infer.n_steps = inference_steps
        use_fp16 = str(args.device).lower().startswith("cuda")
        model = build_low_vram_model(
            model_path=args.model,
            config=config,
            device=args.device,
            use_fp16=use_fp16,
        )
        model_seconds = time.perf_counter() - model_started

        generated_dir = temp / "generated"

        class InferenceArgs:
            pass

        inference = InferenceArgs()
        inference.device = args.device
        inference.model_path = args.model
        inference.config = str(config_path)
        inference.prompt_wav_path = args.prompt
        inference.prompt_metadata_path = str(prompt_metadata)
        inference.target_metadata_path = str(target_metadata)
        inference.phoneset_path = str(phoneset_path)
        inference.save_dir = str(generated_dir)
        # Preserve the user's original key. Automatic range matching caused a
        # stable three-semitone melody error with the character prompt.
        inference.auto_shift = False
        inference.pitch_shift = 0
        inference.control = "melody"
        inference.use_fp16 = use_fp16
        inference_started = time.perf_counter()
        process(inference, config, model)
        inference_seconds = time.perf_counter() - inference_started

        generated = generated_dir / "generated.wav"
        if not generated.is_file():
            raise RuntimeError("SoulX inference did not create generated.wav")
        destination = Path(args.output).resolve()
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(generated, destination)
        print(
            json.dumps(
                {
                    "backend": "soulx-singer-svs",
                    "control": "melody",
                    "score_schema": 1,
                    "score_f0_frames": score_f0_frames,
                    "target_language": args.target_language,
                    "prompt_cache": prompt_cache,
                    "target_metadata": target_mode,
                    "import_seconds": round(import_seconds, 3),
                    "prompt_seconds": round(prompt_seconds, 3),
                    "target_seconds": round(target_seconds, 3),
                    "preprocess_seconds": round(
                        prompt_seconds + target_seconds, 3
                    ),
                    "model_load_seconds": round(model_seconds, 3),
                    "inference_seconds": round(inference_seconds, 3),
                    "inference_steps": inference_steps,
                    "runner_seconds": round(
                        time.perf_counter() - total_started, 3
                    ),
                },
                ensure_ascii=False,
            )
        )


if __name__ == "__main__":
    main()
