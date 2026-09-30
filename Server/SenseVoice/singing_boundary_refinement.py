"""Conservative edge edits in a fixed recording, using acoustic gaps and local ASR.

No word timestamps are inferred from text length, coarse singing scores, or model
arrival time. Recognition is injected so the HTTP adapter can yield the shared
inference worker to final ASR between probes. Unsupported evidence stays unresolved.
"""
from dataclasses import dataclass
import math
import re
import time
import unicodedata

import numpy as np

RATE = 16000
ALGORITHM = "local-gap-asr-v1"
MAX_AUDIO_SECONDS = 180
LOCAL_SECONDS = 8.0
MAX_CANDIDATES = 8
MAX_PROBES = 16
MAX_TRANSITION_SPREAD = .65


class BoundaryError(ValueError):
    def __init__(self, reason, message):
        super().__init__(message)
        self.reason = reason


class BoundaryCancelled(Exception):
    pass


def text_key(text):
    return "".join(c for c in unicodedata.normalize("NFKC", str(text)).casefold()
                   if c.isalnum())


def _number(manifest, name):
    value = manifest.get(name)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise BoundaryError("invalid_manifest", name + " must be finite numeric seconds")
    return float(value)


def _string(manifest, name, maximum=256, optional=False):
    value = manifest.get(name, "")
    if not isinstance(value, str) or len(value) > maximum or (not optional and not value.strip()):
        raise BoundaryError("invalid_manifest", name + " is missing or too long")
    return value.strip()


@dataclass(frozen=True)
class BoundaryRequest:
    request_id: str
    source_id: str
    source_revision: str
    audio_sha256: str
    source_offset_seconds: float
    current_start_seconds: float
    current_end_seconds: float
    edge: str
    exclude_text: str
    keep_text: str
    language: str = "auto"
    played_revision: str = ""
    pcm_lineage_hash: str = ""

    @classmethod
    def parse(cls, manifest, actual_sha256, sample_count):
        if not isinstance(manifest, dict) or type(manifest.get("schema")) is not int or manifest["schema"] != 1:
            raise BoundaryError("invalid_manifest", "schema must be 1")
        values = {name: _string(manifest, name) for name in
                  ("request_id", "source_id", "source_revision", "audio_sha256", "edge")}
        values["audio_sha256"] = values["audio_sha256"].lower()
        if not re.fullmatch(r"[0-9a-f]{64}", values["audio_sha256"]):
            raise BoundaryError("invalid_manifest", "audio_sha256 must hash the uploaded WAV bytes")
        if values["audio_sha256"] != actual_sha256.lower():
            raise BoundaryError("audio_hash_mismatch", "uploaded WAV does not match audio_sha256")
        if values["edge"] not in {"lead", "tail"}:
            raise BoundaryError("invalid_manifest", "edge must be lead or tail")
        for name in ("source_offset_seconds", "current_start_seconds", "current_end_seconds"):
            values[name] = _number(manifest, name)
        for name in ("exclude_text", "keep_text"):
            values[name] = _string(manifest, name, 384)
        excluded, kept = text_key(values["exclude_text"]), text_key(values["keep_text"])
        if len(excluded) < 2 or len(kept) < 3:
            raise BoundaryError("invalid_manifest", "exact excluded phrase and adjacent kept lyric anchor are required")
        if excluded in kept or kept in excluded:
            raise BoundaryError("invalid_manifest", "excluded and kept anchors overlap")
        values["language"] = _string(manifest, "language", 24, True) or "auto"
        for name in ("played_revision", "pcm_lineage_hash"):
            values[name] = _string(manifest, name, 256, True)
        duration = sample_count / RATE
        start, end, offset = (values[k] for k in
                              ("current_start_seconds", "current_end_seconds", "source_offset_seconds"))
        if sample_count < RATE // 2 or duration > MAX_AUDIO_SECONDS:
            raise BoundaryError("invalid_manifest", "audio duration must be 0.5 to 180 seconds")
        if offset < 0 or start < offset - 1 / RATE or end > offset + duration + 1 / RATE or end - start < .5:
            raise BoundaryError("range_outside_audio", "current range must be inside this exact uploaded recording")
        return cls(**values)

    def envelope(self):
        return dict(schema=1, algorithm_version=ALGORITHM, request_id=self.request_id,
                    source_id=self.source_id, source_revision=self.source_revision,
                    audio_sha256=self.audio_sha256, source_offset_seconds=self.source_offset_seconds,
                    played_revision=self.played_revision, pcm_lineage_hash=self.pcm_lineage_hash,
                    edge=self.edge, current_start_seconds=self.current_start_seconds,
                    current_end_seconds=self.current_end_seconds, word_timestamps=False,
                    range=None, quality="unresolved", ok=False, status="boundary_unresolved")


def acoustic_gaps(wav, request):
    """Quiet intervals in at most eight source seconds; never text-proportional cuts."""
    start = request.current_start_seconds - request.source_offset_seconds
    end = request.current_end_seconds - request.source_offset_seconds
    lo = start if request.edge == "lead" else max(start, end - LOCAL_SECONDS)
    hi = min(end, start + LOCAL_SECONDS) if request.edge == "lead" else end
    first, last = int(round(lo * RATE)), int(round(hi * RATE))
    samples = np.asarray(wav[first:last], dtype=np.float64)
    window, hop = 320, 160
    if samples.size < window:
        return []
    offsets = np.arange(0, samples.size - window + 1, hop)
    summed = np.r_[0.0, np.cumsum(samples * samples)]
    rms = np.sqrt(np.maximum(0, (summed[offsets + window] - summed[offsets]) / window))
    reference = float(np.percentile(rms, 80))
    # An all-silent/near-silent file does not establish any speech/song boundary.
    if reference < 1e-4:
        return []
    threshold = max(2e-5, reference * .14)
    quiet = rms < threshold
    runs, opened = [], None
    for index, low in enumerate(np.r_[quiet, False]):
        if low and opened is None:
            opened = index
        elif not low and opened is not None:
            # Frame centres avoid claiming the entire overlapping analysis frame is quiet.
            gap_lo = (first + offsets[opened] + window / 2) / RATE
            gap_hi = (first + offsets[index - 1] + window / 2) / RATE
            if gap_hi - gap_lo >= .06 and opened > 0 and index < len(quiet):
                split = (gap_lo + gap_hi) / 2
                if split - start >= .12 and end - split >= .12:
                    runs.append(dict(start_seconds=gap_lo + request.source_offset_seconds,
                                     end_seconds=gap_hi + request.source_offset_seconds,
                                     split_seconds=split + request.source_offset_seconds,
                                     rms_threshold=round(threshold, 8)))
            opened = None
    # Examine the least destructive edge edits first; final ambiguity is still checked.
    return runs if request.edge == "lead" else list(reversed(runs))


def same_quiet_transition(gaps, wav, request):
    """Several breathing/noise notches may be equivalent safe cuts in one pause.

    This does not merge arbitrary ASR-equivalent boundaries. Both the maximum
    time spread and every 20ms bridge's energy must support one quiet transition.
    Choose its least destructive cut, never the last restored-word hypothesis.
    """
    if len(gaps) < 2:
        return True
    low = min(g["split_seconds"] for g in gaps)
    high = max(g["split_seconds"] for g in gaps)
    if high - low > MAX_TRANSITION_SPREAD:
        return False
    first = int(round((low - request.source_offset_seconds) * RATE))
    last = int(round((high - request.source_offset_seconds) * RATE))
    samples = wav[first:last].astype(np.float64)
    window = 320
    if samples.size < window:
        return True
    offsets = np.arange(0, samples.size - window + 1, 160)
    summed = np.r_[0.0, np.cumsum(samples * samples)]
    rms = np.sqrt(np.maximum(0, (summed[offsets + window] - summed[offsets]) / window))
    return bool(np.max(rms) <= min(g["rms_threshold"] for g in gaps) * 1.8)


async def refine_boundary(wav, request, recognize, is_cancelled=lambda: False):
    """recognize(float32 mono16k) is async and must not learn speakers/store songs."""
    result = request.envelope()
    result.update(reason="anchor_not_supported", evidence=[], asr_probes=0)
    started = time.perf_counter()
    try:
        if wav.ndim != 1 or not np.isfinite(wav).all():
            raise BoundaryError("invalid_audio", "mono finite PCM is required")
        if is_cancelled():
            raise BoundaryCancelled()
        gaps = acoustic_gaps(wav, request)
        result["candidate_count"] = len(gaps)
        if not gaps:
            result["reason"] = "no_acoustic_gap"
            return result
        excluded, kept = text_key(request.exclude_text), text_key(request.keep_text)
        accepted = []
        for gap in gaps[:MAX_CANDIDATES]:
            if is_cancelled():
                raise BoundaryCancelled()
            split = gap["split_seconds"]
            if request.edge == "lead":
                removed = (request.current_start_seconds, split)
                retained = (split, min(request.current_end_seconds, split + LOCAL_SECONDS))
            else:
                removed = (split, request.current_end_seconds)
                retained = (max(request.current_start_seconds, split - LOCAL_SECONDS), split)
            evidence = dict(gap=gap, removed_range=dict(start_seconds=removed[0], end_seconds=removed[1]),
                            retained_probe_range=dict(start_seconds=retained[0], end_seconds=retained[1]),
                            status="unsupported", removed_text="", retained_text="")
            result["evidence"].append(evidence)

            async def probe(bounds):
                if is_cancelled():
                    raise BoundaryCancelled()
                if result["asr_probes"] >= MAX_PROBES:
                    raise BoundaryError("analysis_budget_exhausted", "local ASR probe budget exhausted")
                lo, hi = [int(round((s - request.source_offset_seconds) * RATE)) for s in bounds]
                result["asr_probes"] += 1
                output = await recognize(wav[max(0, lo):min(len(wav), hi)].copy())
                if is_cancelled() or (isinstance(output, dict) and output.get("cancelled")):
                    raise BoundaryCancelled()
                if not isinstance(output, dict) or not isinstance(output.get("text"), str):
                    raise BoundaryError("analysis_failed", "local ASR returned no transcript evidence")
                return output["text"].strip()

            evidence["removed_text"] = await probe(removed)
            # Exact normalized evidence is intentionally conservative: extra removed
            # words may be lyrics, and approximate matches cannot authorize cutting.
            if text_key(evidence["removed_text"]) != excluded:
                evidence["status"] = "excluded_phrase_not_exact"
                continue
            evidence["retained_text"] = await probe(retained)
            retained_key = text_key(evidence["retained_text"])
            anchor_at_edge = (retained_key.startswith(kept) if request.edge == "lead"
                              else retained_key.endswith(kept))
            if not anchor_at_edge or excluded in retained_key:
                evidence["status"] = "kept_anchor_not_intact"
                continue
            evidence["status"] = "supported"
            accepted.append(gap)
        if len(gaps) > MAX_CANDIDATES:
            result["reason"] = "analysis_budget_exhausted"
        elif len(accepted) > 1 and not same_quiet_transition(accepted, wav, request):
            result["reason"] = "ambiguous_boundary"
        elif accepted:
            split = accepted[0]["split_seconds"]
            result.update(ok=True, status="resolved", reason="resolved_local_asr_gap",
                          quality="local-asr-acoustic-gap",
                          range=dict(start_seconds=split if request.edge == "lead" else request.current_start_seconds,
                                     end_seconds=split if request.edge == "tail" else request.current_end_seconds),
                          boundary_uncertainty=dict(start_seconds=min(g["start_seconds"] for g in accepted),
                                                    end_seconds=max(g["end_seconds"] for g in accepted)),
                          supported_gap_count=len(accepted),
                          verified_claim="excluded phrase on removed side; adjacent kept anchor intact in local ASR",
                          mode_verified=False)
        if is_cancelled():
            raise BoundaryCancelled()
    except BoundaryCancelled:
        result.update(ok=False, status="cancelled", reason="cancelled", range=None, quality="unresolved")
    except BoundaryError as exc:
        result.update(ok=False, reason=exc.reason, detail=str(exc), range=None, quality="unresolved")
    except Exception as exc:
        result.update(ok=False, reason="analysis_failed", detail=type(exc).__name__, range=None, quality="unresolved")
    finally:
        result["elapsed_seconds"] = round(time.perf_counter() - started, 4)
    return result
