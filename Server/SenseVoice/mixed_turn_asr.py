"""Independent, time-ordered ASR observations for mixed speech/song recordings.

Acoustic labels are hypotheses, never permission to discard audio. Core windows
cover the entire content WAV; padded alternatives preserve uncertain boundaries.
No language, lyrics, or user intent is inferred by this module.
"""
import hashlib
from difflib import SequenceMatcher
import json
import math
import os
import time
from pathlib import Path

import numpy as np
import soundfile as sf

RATE = 16000
SCHEMA = 1


def _number(value, default=0.0):
    try:
        value = float(value)
        return value if math.isfinite(value) else default
    except (TypeError, ValueError):
        return default


def plan_segments(wav, analysis):
    """Split at acoustic hypotheses, then quiet points in long intervals.

    A speech verdict does not disable transcription of a detected island. Tiny
    uncertain margins are retained rather than rejected as 'too short to speak'.
    All timestamps use the post-VAD content time base, not the playback crop.
    """
    analysis = analysis or {}
    total = len(wav) / RATE
    start = max(0.0, min(total, _number(analysis.get("singing_start_seconds"))))
    end = max(start, min(total, _number(analysis.get("singing_end_seconds"), total)))
    has_boundary = bool(analysis.get("analysis_available")) and (
        end - start >= 1.2 and start + total - end >= .45)
    if not has_boundary:
        if total <= 8.0:
            return []
        # A missed island must not return a long heterogeneous clip to one ASR
        # window. Bound the decoding context even when segmentation is unsure.
        start, end = 0.0, total
    # Recovery boundaries can reveal a separate phrase missed by clean (e.g.
    # English before Japanese). Use them only with enough decoding context;
    # boundary-extra ASR retains the exact playback evidence independently.
    cuts = sorted(set([0.0, start, end, total])) if has_boundary else [0.0, total]
    if has_boundary:
        for value in (max(0.0, min(start, _number(analysis.get("singing_recovery_start_seconds"), start))),
                      max(end, min(total, _number(analysis.get("singing_recovery_end_seconds"), end)))):
            if all(abs(value - cut) >= 2.8 for cut in cuts):
                cuts.append(value)
    phrase_cuts = [_number(value, -1) for value in analysis.get("asr_boundary_candidates", [])]
    for value in sorted(set(phrase_cuts)):
        # Pitch instability is often inside a word, not a language transition.
        # Keep linguistic context on both sides of optional inner cuts. Real
        # clean start/end boundaries (including a short spoken tail) stay intact.
        if all(abs(value - cut) >= 2.8 for cut in cuts) and 0 < value < total:
            cuts.append(value)
    cuts.sort()
    # Avoid decoding isolated 50-ms fragments while retaining complete coverage.
    internal = []
    for cut in cuts[1:-1]:
        if cut - (internal[-1] if internal else 0.0) >= .45 and total - cut >= .45:
            internal.append(cut)
    cuts = [0.0] + internal + [total]
    spans = []
    for lo, hi in zip(cuts, cuts[1:]):
        cursor = lo
        while hi - cursor > 8.0:
            target = cursor + 6.0
            # Search a local quiet point, without using volume to reject speech.
            choices = np.arange(target - .75, min(hi - 1.0, target + .75), .02)
            cut = min(choices, key=lambda t: float(np.mean(np.square(
                wav[max(0, int((t - .06) * RATE)):int((t + .06) * RATE)]))))
            spans.append((cursor, float(cut)))
            cursor = float(cut)
        spans.append((cursor, hi))
    result = []
    for i, (lo, hi) in enumerate(spans):
        middle = (lo + hi) / 2
        region = ("head" if middle < start else "tail" if middle >= end else "island") if has_boundary else "unresolved"
        kind = "singing_candidate" if region == "island" else "uncertain"
        result.append(dict(id=i + 1, start_seconds=round(lo, 4),
                           end_seconds=round(hi, 4), region=region,
                           type=kind, type_source="acoustic_boundary_hypothesis"))
    return result


def transcribe_segments(wav, analysis, recognize, whole_text=""):
    """recognize(piece) returns parsed text/language/event with a fresh cache.

    Core text is primary; overlapping text is an explicitly separate hypothesis.
    ASR models here do not expose calibrated confidence: never invent a score.
    Exceptions/empty hypotheses remain visible and don't erase the other spans.
    """
    plan = plan_segments(wav, analysis)
    if not plan:
        return {}
    total = len(wav) / RATE
    for segment in plan:
        lo, hi = segment["start_seconds"], segment["end_seconds"]
        segment.update(text="", language="", status="empty", error="",
                       alternatives=[], confidence_available=False)
        windows = [("core", lo, hi), ("boundary_overlap", max(0, lo - .4), min(total, hi + .4))]
        clean_start = _number((analysis or {}).get("singing_start_seconds"))
        clean_end = _number((analysis or {}).get("singing_end_seconds"), total)
        recovery_end = min(total, _number((analysis or {}).get("singing_recovery_end_seconds"), clean_end))
        if 0 < clean_end - clean_start < 3 and recovery_end > clean_end + .4:
            # Context observations never change playback boundaries or erase core text.
            if segment["region"] == "island" and recovery_end - clean_start <= 8:
                windows.append(("recovery_context", clean_start, recovery_end))
            elif segment["region"] == "tail" and lo < recovery_end < hi - .5:
                windows.append(("recovery_tail", recovery_end, hi))
        for source, a, b in windows:
            if source != "core" and a == lo and b == hi:
                continue
            try:
                parsed = recognize(wav[int(round(a * RATE)):int(round(b * RATE))])
                observation = dict(source=source, start_seconds=round(a, 4),
                                   end_seconds=round(b, 4), text=(parsed.get("text") or "").strip(),
                                   language=parsed.get("language") or "",
                                   audio_event=parsed.get("audio_event") or "")
                if source == "core":
                    segment.update(text=observation["text"], language=observation["language"],
                                   status="ok" if observation["text"] else "empty")
                elif observation["text"] and observation["text"] != segment["text"]:
                    segment["alternatives"].append(observation)
            except Exception as exc:
                if source == "core":
                    segment.update(status="failed", error=type(exc).__name__)
                else:
                    segment["alternatives"].append(dict(source=source, text="", error=type(exc).__name__))
        segment["review_required"] = segment["status"] != "ok" or bool(segment["alternatives"])
    primary = " ".join(s["text"] for s in plan if s["text"])
    # Partial failure must not silently delete whole-turn-only information.
    complete = all(s["status"] == "ok" for s in plan)
    # Preserve a fluent whole transcript only when it contains EVERY independent
    # core span in order and in the same language. This is literal corroboration,
    # not an LLM intent decision or a preference for the longer hypothesis.
    normalize = lambda text: "".join(c.lower() for c in text if c.isalnum())
    whole_key = normalize(whole_text)
    cursor = 0
    whole_corroborated = complete and bool(whole_key) and len(set(s["language"] for s in plan)) == 1
    for segment in plan:
        key = normalize(segment["text"])
        found = whole_key.find(key, cursor) if key else -1
        if found < 0:
            whole_corroborated = False
            break
        cursor = found + len(key)
    primary_key = normalize(primary)
    alignment = SequenceMatcher(None, whole_key, primary_key, autojunk=False)
    # Small spelling/boundary differences are not evidence that the fluent whole
    # pass lost a phrase. Preserve it only with ordered coverage; a substantial
    # segment-only insertion (e.g. the spoken request after a song) vetoes this.
    missing_span = max((j2 - j1 for tag, i1, i2, j1, j2 in alignment.get_opcodes()
                        if tag == "insert"), default=0)
    short_spelling_difference = (len(whole_key) <= 12 and len(whole_key) == len(primary_key) and
                                len(set(s["language"] for s in plan)) == 1 and
                                all(tag == "equal" or (tag == "replace" and i2 - i1 <= 2 and j2 - j1 <= 2)
                                    for tag, i1, i2, j1, j2 in alignment.get_opcodes()))
    near_equivalent = (complete and bool(whole_key) and
                       len(whole_key) >= .85 * len(primary_key) and
                       alignment.ratio() >= (.6 if short_spelling_difference else .72) and missing_span < 6)
    keep_whole = whole_corroborated or near_equivalent
    chosen = whole_text if keep_whole else primary if primary else whole_text
    return dict(turn_segments_schema=SCHEMA, turn_segments=plan,
                whole_text=whole_text, segmented_text=primary,
                text=chosen,
                transcript_source="whole_corroborated_by_segments" if whole_corroborated else
                                  "whole_with_minor_segment_disagreement" if near_equivalent else
                                  "segments" if primary else "whole_fallback",
                turn_segments_complete=complete,
                turn_segments_review_required=not complete or near_equivalent or any(s["review_required"] for s in plan))


def dump_mixed_sample(raw_wav, content_wav, result, directory):
    """Local diagnostic snapshots. Never remove existing recordings to make room."""
    if os.environ.get("NEEEVA_MIXED_DUMP", "1") == "0" or not result.get("turn_segments"):
        return
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(np.asarray(raw_wav, dtype=np.float32).tobytes()).hexdigest()[:20]
    if list(directory.glob(f"*_{digest}.json")):
        return
    if len(list(directory.glob("*.raw.wav"))) >= 80:
        print("[ASR/MixedDump] capacity=80; snapshot skipped (existing audio retained)", flush=True)
        return
    stem = directory / (time.strftime("%Y%m%d_%H%M%S") + "_" + digest)
    sf.write(str(stem) + ".raw.wav", raw_wav, RATE, subtype="FLOAT")
    sf.write(str(stem) + ".content.wav", content_wav, RATE, subtype="FLOAT")
    metadata = dict(result)
    metadata["reference_segments"] = []  # Human annotation; ASR is not ground truth.
    Path(str(stem) + ".json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[ASR/MixedDump] {stem.name}", flush=True)
