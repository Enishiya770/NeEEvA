"""Song lookup providers used by the autonomous NeEEvA agent.

Providers are intentionally layered:
  * local catalog: query text plus transposition-invariant melody DTW;
  * MusicBrainz: public metadata search (no raw audio is uploaded);

Raw microphone audio never leaves the local SenseVoice service.
"""

from __future__ import annotations

import json
import math
import os
import re
import threading
import time
import uuid
from copy import deepcopy
from difflib import SequenceMatcher
from typing import Dict, Iterable, List, Optional, Tuple
from urllib.parse import quote
from urllib.request import Request, urlopen

import numpy as np


PROJECT_URL = "https://github.com/Enishiya770/NeEEvA"
USER_AGENT = f"NeEEvA/1.0 ({PROJECT_URL})"


def _normalize_text(value: str) -> str:
    return "".join(ch.lower() for ch in (value or "") if ch.isalnum())


def _safe_float(value, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


_INVALID_FILENAME_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_WINDOWS_RESERVED_NAMES = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{index}" for index in range(1, 10)),
    *(f"LPT{index}" for index in range(1, 10)),
}


def _safe_filename_component(value: str, fallback: str, max_length: int = 60) -> str:
    cleaned = _INVALID_FILENAME_CHARS.sub("_", (value or "").strip())
    cleaned = re.sub(r"\s+", " ", cleaned).strip(" .")
    if not cleaned:
        cleaned = fallback
    if cleaned.upper() in _WINDOWS_RESERVED_NAMES:
        cleaned = "_" + cleaned
    return cleaned[:max_length].rstrip(" .") or fallback


def _resample_sequence(values: Iterable[float], max_points: int = 220) -> np.ndarray:
    arr = np.asarray(list(values or []), dtype=np.float32)
    arr = arr[np.isfinite(arr)]
    if arr.size <= max_points:
        return arr
    x_old = np.linspace(0.0, 1.0, arr.size)
    x_new = np.linspace(0.0, 1.0, max_points)
    return np.interp(x_new, x_old, arr).astype(np.float32)


#音名序列：给 LLM 看的旋律文本形式。18 音是实测的够用长度——同一批四选一题上
#18/36/72/不截断分别 66%/70%/66%/66%，差异全在噪声内，而字符数从 62 涨到 187。
NOTE_SEQUENCE_MAX_NOTES = 18


def notes_from_contour(contour: Iterable[float], limit: int = NOTE_SEQUENCE_MAX_NOTES) -> str:
    """与 SingingAnalyzer._note_sequence 同一算法，独立实现一份供曲库补算用。"""
    names: List[str] = []
    for value in contour or []:
        try:
            f = float(value)
        except (TypeError, ValueError):
            continue
        if not math.isfinite(f):
            continue
        n = _midi_to_note_name(f)
        if n and (not names or names[-1] != n):
            names.append(n)
    if limit and len(names) > limit:
        names = names[:limit] + ["…"]
    return "-".join(names)


_NOTE_NAMES = ("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")


def _midi_to_note_name(midi: float) -> str:
    if not math.isfinite(midi) or midi <= 0:
        return ""
    index = int(round(midi))
    if index < 12 or index > 127:
        return ""
    return f"{_NOTE_NAMES[index % 12]}{index // 12 - 1}"


def semitone_tokens(contour: Iterable[float]) -> List[int]:
    """把轮廓量化成半音、合并相邻重复——音名序列的数值等价物。

    与 SingingAnalyzer._note_sequence 同一口径(那边输出音名字符串给 LLM 看)，
    只是留成整数便于移调归一。
    """
    out: List[int] = []
    for value in contour or []:
        if not math.isfinite(float(value)):
            continue
        s = int(round(float(value)))
        if not out or out[-1] != s:
            out.append(s)
    return out


def token_alignment_similarity(query: Iterable[float], reference: Iterable[float]) -> float:
    """音名 token 对齐 + 移调归一，用于**候选排序**。

    8/17 拿曲库已有的重复组量过(63 条参考、515 同歌对 vs 1438 异歌对)，
    按 AUC(随机取一对同歌、一对异歌，同歌得分更高的概率)比较：

        现役 melody_similarity      全部 0.531   长度悬殊 0.613   长度接近 0.511
        本函数(对称 token + 移调)    全部 0.648   长度悬殊 0.666   长度接近 0.753
        含有率(移调归一)             全部 0.633   长度悬殊 0.661   长度接近 0.661

    melody_similarity 把轮廓重采样到 220 点再取差分，**时长和调高都被抹掉**，
    只剩起伏形状——而人声旋律的起伏形状天生相似，所以它在长度接近时 AUC 0.511，
    等于抛硬币。改成按量化音名逐 token 对齐、并在 ±6 半音内取最好的移调，
    三档都不输，是唯一没有明显短板的。

    注意：这里只用于**排序取前几名**，不设阈值。0.65 的 AUC 撑不起判定——
    实测任何阈值要么只认出两成，要么带进一堆错。谁是谁最终由 LLM 看歌词判断。
    """
    q = semitone_tokens(query)
    r = semitone_tokens(reference)
    if len(q) < 4 or len(r) < 4:
        return 0.0
    best = 0.0
    for shift in range(-6, 7):
        shifted = [v + shift for v in q]
        best = max(best, SequenceMatcher(None, shifted, r).ratio())
    return float(max(0.0, min(1.0, best)))


def melody_containment(query: Iterable[float], reference: Iterable[float]) -> float:
    """较短那条里有多少音在另一条里找到了（移调归一）。

    与上面的对称相似度是**不同的问题**：对称回答"这两次录音是不是同一段"，
    含有率回答"我唱的这一小段是不是那首歌的一部分"。用户唱 6 个音、曲库存着
    50 个音时，完美前缀的对称分只有 2×6/56=0.21，而含有率是 1.00。
    排序用对称(AUC 更高)，含有率作为一条**有直观含义**的线索展示给她。
    """
    q = semitone_tokens(query)
    r = semitone_tokens(reference)
    if not q or not r:
        return 0.0
    shorter = min(len(q), len(r))
    best = 0
    for shift in range(-6, 7):
        shifted = [v + shift for v in q]
        hit = sum(b.size for b in SequenceMatcher(None, shifted, r).get_matching_blocks())
        best = max(best, hit)
    return float(max(0.0, min(1.0, best / float(shorter))))


def melody_similarity(query: Iterable[float], reference: Iterable[float]) -> float:
    """Subsequence DTW over melodic intervals, invariant to vocal key."""
    q = _resample_sequence(query)
    r = _resample_sequence(reference)
    if q.size < 6 or r.size < 8:
        return 0.0
    q = np.clip(np.diff(q), -5.0, 5.0)
    r = np.clip(np.diff(r), -5.0, 5.0)
    if q.size < 5 or r.size < 7:
        return 0.0

    # dp[0, :] = 0 permits matching the sung phrase anywhere in the full
    # reference melody. Horizontal/vertical steps absorb moderate tempo drift.
    previous = np.zeros(r.size + 1, dtype=np.float64)
    for i in range(1, q.size + 1):
        current = np.full(r.size + 1, np.inf, dtype=np.float64)
        for j in range(1, r.size + 1):
            cost = min(6.0, abs(float(q[i - 1] - r[j - 1])))
            current[j] = cost + min(previous[j - 1], previous[j], current[j - 1])
        previous = current
    distance = float(np.min(previous[1:])) / max(1, q.size)
    return float(max(0.0, min(1.0, math.exp(-distance / 1.8))))


def melody_subsequence_alignment(
    query: Iterable[float], reference: Iterable[float]
) -> Tuple[float, int, int]:
    """Return (score, start_note, end_note) for query inside reference.

    The indices address the voiced-note contour, not wall-clock audio frames.  The
    caller can map them back through the non-zero entries of pitch_timeline_midi.
    Like melody_similarity, interval DTW makes the match independent of key.
    """
    q_notes = _resample_sequence(query)
    r_notes = _resample_sequence(reference)
    if q_notes.size < 6 or r_notes.size < 8:
        return 0.0, 0, 0
    q = np.clip(np.diff(q_notes), -5.0, 5.0)
    r = np.clip(np.diff(r_notes), -5.0, 5.0)
    if q.size < 5 or r.size < 7:
        return 0.0, 0, 0

    rows, cols = q.size + 1, r.size + 1
    dp = np.full((rows, cols), np.inf, dtype=np.float64)
    parent = np.zeros((rows, cols), dtype=np.uint8)
    dp[0, :] = 0.0
    for i in range(1, rows):
        for j in range(1, cols):
            cost = min(6.0, abs(float(q[i - 1] - r[j - 1])))
            candidates = (dp[i - 1, j - 1], dp[i - 1, j], dp[i, j - 1])
            choice = int(np.argmin(candidates))
            dp[i, j] = cost + candidates[choice]
            parent[i, j] = choice + 1

    end_j = int(np.argmin(dp[-1, 1:])) + 1
    distance = float(dp[-1, end_j]) / max(1, q.size)
    score = float(max(0.0, min(1.0, math.exp(-distance / 1.8))))
    i, j = q.size, end_j
    while i > 0 and j > 0:
        choice = int(parent[i, j])
        if choice == 1:
            i -= 1
            j -= 1
        elif choice == 2:
            i -= 1
        elif choice == 3:
            j -= 1
        else:
            break
    # Interval j maps to note j; end_j is exclusive in the interval grid.
    return score, max(0, j), min(int(r_notes.size - 1), end_j)


def performance_similarity(first: Iterable[float], second: Iterable[float]) -> float:
    """Symmetric full-phrase similarity used to group repeated takes."""
    a = list(first or [])
    b = list(second or [])
    if len(a) < 8 or len(b) < 8:
        return 0.0
    length_ratio = min(len(a), len(b)) / max(len(a), len(b))
    if length_ratio < 0.58:
        return 0.0
    score = 0.5 * melody_similarity(a, b) + 0.5 * melody_similarity(b, a)
    return float(score * (0.75 + 0.25 * length_ratio))


class SongSearchEngine:
    def __init__(self, catalog_path: str, analyzer=None):
        self.catalog_path = os.path.abspath(catalog_path)
        self.catalog_root = os.path.dirname(self.catalog_path)
        self.audio_dir = os.path.abspath(os.path.join(self.catalog_root, "song_library"))
        self.analyzer = analyzer
        self._lock = threading.Lock()
        self._catalog: List[Dict] = []
        self._load_catalog()

    @property
    def catalog_count(self) -> int:
        with self._lock:
            return len(self._catalog)

    def _load_catalog(self):
        migrated = False
        try:
            if not os.path.isfile(self.catalog_path):
                return
            with open(self.catalog_path, "r", encoding="utf-8") as handle:
                payload = json.load(handle)
            songs = payload.get("songs", payload if isinstance(payload, list) else [])
            if isinstance(songs, list):
                self._catalog = [item for item in songs if isinstance(item, dict)]
                for item in self._catalog:
                    if not str(item.get("id", "")).strip():
                        item["id"] = uuid.uuid4().hex[:12]
                        migrated = True
                    if not isinstance(item.get("references"), list):
                        item["references"] = []
                        migrated = True
                    if not isinstance(item.get("aliases"), list):
                        item["aliases"] = []
                        migrated = True
                    if self._migrate_reference_metadata_locked(item):
                        migrated = True
                if migrated:
                    self._save_catalog_locked()
        except Exception as exc:
            print(f"[SongSearch] catalog load failed: {exc}")

    def catalog_summaries(self) -> List[Dict]:
        with self._lock:
            return [self._entry_summary(item) for item in self._catalog]

    def remember_clip(
        self,
        title: str,
        artist: str,
        wav: np.ndarray,
        wav_bytes: bytes,
        lyrics: str = "",
        aliases: Optional[List[str]] = None,
        reason: str = "",
        song_id: str = "",
    ) -> Dict:
        if self.analyzer is None:
            raise RuntimeError("singing analyzer unavailable")
        if not wav_bytes or len(wav_bytes) <= 44:
            raise ValueError("reference audio is empty")
        analysis = self.analyzer.analyze(
            wav, lyrics=lyrics, thorough=True, force_score=True
        )
        contour = analysis.get("pitch_contour_midi") or []
        if len(contour) < 8:
            raise ValueError("reference audio has no reliable melody contour")

        clean_title = (title or "").strip()
        clean_artist = (artist or "").strip()
        clean_aliases = list(dict.fromkeys(value.strip() for value in (aliases or []) if value.strip()))
        now = int(time.time())
        with self._lock:
            entry = None
            requested_song_id = (song_id or "").strip()
            if requested_song_id:
                entry = self._find_entry_locked(requested_song_id)
                if entry is None:
                    raise KeyError(f"song not found: {requested_song_id}")
            elif clean_title:
                key = (_normalize_text(clean_title), _normalize_text(clean_artist))
                for item in self._catalog:
                    item_key = (
                        _normalize_text(item.get("title", "")),
                        _normalize_text(item.get("artist", "")),
                    )
                    if item_key == key:
                        entry = item
                        break
                if entry is None and not clean_artist:
                    title_matches = [
                        item for item in self._catalog
                        if _normalize_text(item.get("title", "")) == key[0]
                    ]
                    # A unique local title is enough to append another learned take.
                    # Ambiguous duplicate titles still require id/artist and never guess.
                    if len(title_matches) == 1:
                        entry = title_matches[0]
            if entry is None:
                entry = {
                    "id": uuid.uuid4().hex[:12],
                    "title": clean_title,
                    "artist": clean_artist,
                    "aliases": clean_aliases,
                    "lyrics": (lyrics or "").strip(),
                    "references": [],
                    "created_at": now,
                    "updated_at": now,
                }
                self._catalog.append(entry)
            else:
                entry["title"] = clean_title or str(entry.get("title", ""))
                entry["artist"] = clean_artist or str(entry.get("artist", ""))
                if clean_aliases:
                    entry["aliases"] = list(dict.fromkeys(list(entry.get("aliases", [])) + clean_aliases))
                if (lyrics or "").strip():
                    existing_lyrics = str(entry.get("lyrics", "")).strip()
                    new_lyrics = (lyrics or "").strip()
                    entry["lyrics"] = new_lyrics if not existing_lyrics else existing_lyrics + "\n" + new_lyrics

            song_id = str(entry["id"])
            clip_id = uuid.uuid4().hex[:12]
            filename = self._build_audio_filename(song_id, clip_id, entry.get("title", ""), entry.get("artist", ""))
            absolute_path = os.path.join(self.audio_dir, filename)
            os.makedirs(self.audio_dir, exist_ok=True)
            temp_path = absolute_path + ".tmp"
            with open(temp_path, "wb") as handle:
                handle.write(wav_bytes)
            os.replace(temp_path, absolute_path)

            reference = {
                "id": clip_id,
                "wav_file": self._relative_audio_path(absolute_path),
                "pitch_contour_midi": contour,
                #旋律的文本形式。分析结果里本来就有(note_sequence)，以前算完就丢，
                #于是回忆候选拿不到旋律、她只能靠歌词认歌。存一份即可，不额外算。
                "note_sequence": (str(analysis.get("note_sequence", "")).strip()
                                  or notes_from_contour(contour)),
                "pitch_timeline_midi": analysis.get("pitch_timeline_midi", []) or [],
                "pitch_timeline_frame_seconds": float(
                    analysis.get("pitch_timeline_frame_seconds", 0.10)
                ),
                "singing_score": analysis.get("singing_score"),
                "duration_seconds": round(float(len(wav)) / 16000.0, 3),
                "lyrics": (lyrics or "").strip(),
                "created_at": now,
                "reason": (reason or "").strip(),
            }
            segment_status = self._classify_reference_locked(entry, reference)
            entry.setdefault("references", []).append(reference)
            # Retain this field for backward compatibility with existing search
            # data and older tools that inspect the JSON directly.
            entry["pitch_contour_midi"] = contour
            entry["updated_at"] = now
            self._save_catalog_locked()
            result = self._entry_summary(entry)
            result.update({
                "clip_id": clip_id,
                "wav_file": reference["wav_file"],
                "contour_points": len(contour),
                "segment_group_id": reference["segment_group_id"],
                "sequence_index": int(reference["sequence_index"]),
                "segment_status": segment_status,
                "score_available": bool(reference.get("singing_score")),
            })
            return result

    def _classify_reference_locked(self, entry: Dict, reference: Dict) -> str:
        """Put another take of the same phrase in the same segment group.

        A repeated take remains a useful voice/performance variant, but it must not
        advance the remembered song sequence.  A genuinely different contour is the
        next learned segment unless a future full-song source gives us an absolute
        position.
        """
        references = entry.get("references", []) if isinstance(entry.get("references"), list) else []
        contour = reference.get("pitch_contour_midi", []) or []
        lyrics = _normalize_text(str(reference.get("lyrics", "")))
        best = None
        best_score = 0.0
        for previous in references:
            score = performance_similarity(contour, previous.get("pitch_contour_midi", []) or [])
            previous_lyrics = _normalize_text(str(previous.get("lyrics", "")))
            if lyrics and previous_lyrics:
                lyric_score = SequenceMatcher(None, lyrics, previous_lyrics).ratio()
                score = 0.82 * score + 0.18 * lyric_score
            if score > best_score:
                best_score = score
                best = previous

        duplicate_threshold = 0.90 if lyrics else 0.93
        if best is not None and best_score >= duplicate_threshold:
            group_id = str(best.get("segment_group_id", "") or best.get("id", ""))
            reference["segment_group_id"] = group_id
            reference["sequence_index"] = int(best.get("sequence_index", 0))
            reference["duplicate_of"] = str(best.get("id", ""))
            reference["melody_similarity"] = round(best_score, 4)
            return "duplicate_variant"

        used_indices = [int(item.get("sequence_index", -1)) for item in references]
        reference["segment_group_id"] = str(reference.get("id", ""))
        reference["sequence_index"] = max(used_indices, default=-1) + 1
        reference["duplicate_of"] = ""
        reference["melody_similarity"] = round(best_score, 4)
        return "new_segment"

    def _migrate_reference_metadata_locked(self, entry: Dict) -> bool:
        references = entry.get("references", []) if isinstance(entry.get("references"), list) else []
        changed = False
        # Rebuild only legacy entries.  Once every take is classified, preserve the
        # explicit ordering even if thresholds change in a later release.
        if references and all(
            str(item.get("segment_group_id", "")) and "sequence_index" in item
            for item in references if isinstance(item, dict)
        ):
            return False

        classified: List[Dict] = []
        fallback_lyrics = str(entry.get("lyrics", ""))
        for reference in references:
            if not isinstance(reference, dict):
                continue
            if not str(reference.get("id", "")):
                reference["id"] = uuid.uuid4().hex[:12]
                changed = True
            if "lyrics" not in reference:
                reference["lyrics"] = fallback_lyrics
                changed = True
            timeline = reference.get("pitch_timeline_midi", []) or []
            frame_seconds = _safe_float(reference.get("pitch_timeline_frame_seconds"), 0.10)
            if "duration_seconds" not in reference and timeline:
                reference["duration_seconds"] = round(len(timeline) * frame_seconds, 3)
                changed = True
            shadow_entry = {"references": classified}
            self._classify_reference_locked(shadow_entry, reference)
            classified.append(reference)
            changed = True
        return changed

    def rename_song(
        self,
        song_id: str,
        title: str,
        artist: str = "",
        aliases: Optional[List[str]] = None,
    ) -> Dict:
        clean_id = (song_id or "").strip()
        clean_title = (title or "").strip()
        if not clean_id:
            raise ValueError("song id is required")
        if not clean_title:
            raise ValueError("new title is required")

        with self._lock:
            entry = self._find_entry_locked(clean_id)
            if entry is None:
                raise KeyError(f"song not found: {clean_id}")
            clean_artist = (artist or "").strip()
            final_artist = clean_artist or str(entry.get("artist", ""))
            moved = []
            original_references = []
            try:
                for reference in entry.get("references", []):
                    old_relative = str(reference.get("wav_file", ""))
                    if not old_relative:
                        continue
                    original_references.append((reference, old_relative))
                    old_path = self._absolute_audio_path(old_relative)
                    clip_id = str(reference.get("id", "")) or uuid.uuid4().hex[:12]
                    reference["id"] = clip_id
                    new_name = self._build_audio_filename(clean_id, clip_id, clean_title, final_artist)
                    new_path = os.path.join(self.audio_dir, new_name)
                    if os.path.normcase(old_path) != os.path.normcase(new_path) and os.path.isfile(old_path):
                        os.replace(old_path, new_path)
                        moved.append((new_path, old_path))
                    reference["wav_file"] = self._relative_audio_path(new_path)
            except Exception:
                for new_path, old_path in reversed(moved):
                    if os.path.isfile(new_path):
                        os.replace(new_path, old_path)
                for reference, old_relative in original_references:
                    reference["wav_file"] = old_relative
                raise

            entry["title"] = clean_title
            entry["artist"] = final_artist
            if aliases is not None:
                entry["aliases"] = list(dict.fromkeys(value.strip() for value in aliases if value.strip()))
            entry["updated_at"] = int(time.time())
            self._save_catalog_locked()
            return self._entry_summary(entry)

    def forget_song(self, song_id: str) -> Dict:
        clean_id = (song_id or "").strip()
        if not clean_id:
            raise ValueError("song id is required")
        with self._lock:
            entry = self._find_entry_locked(clean_id)
            if entry is None:
                raise KeyError(f"song not found: {clean_id}")
            summary = self._entry_summary(entry)
            wav_paths = [
                self._absolute_audio_path(str(reference.get("wav_file", "")))
                for reference in entry.get("references", [])
                if str(reference.get("wav_file", ""))
            ]
            self._catalog.remove(entry)
            self._save_catalog_locked()
        for wav_path in wav_paths:
            try:
                if os.path.isfile(wav_path):
                    os.remove(wav_path)
            except OSError as exc:
                print(f"[SongMemory] failed to remove {wav_path}: {exc}")
        return summary

    def resolve_performance(
        self,
        song_id: str = "",
        title: str = "",
        mode: str = "memory",
        query_contour: Optional[List[float]] = None,
        query_lyrics: str = "",
        max_seconds: float = 60.0,
        seed: int = 1234,
    ) -> Dict:
        """Resolve managed WAV material for autonomous remembered-song singing.

        memory: sing each unique learned segment once, choosing one take per group.
        continue: align the user's newest melody, then return only material after it.
        Repeated takes are variants of one segment and are never concatenated.
        """
        clean_mode = (mode or "memory").strip().lower()
        if clean_mode == "auto":
            clean_mode = "continue" if query_contour else "memory"
        if clean_mode not in ("memory", "continue"):
            raise ValueError("song singing mode must be memory, continue, or auto")
        max_seconds = max(3.0, min(180.0, float(max_seconds)))

        with self._lock:
            entry = self._find_performance_entry_locked(song_id, title, query_contour or [])
            if entry is None:
                selector = (title or song_id or "当前旋律").strip()
                raise KeyError(f"remembered song not found: {selector}")
            entry_copy = deepcopy(entry)

        references = [
            item for item in entry_copy.get("references", [])
            if isinstance(item, dict) and str(item.get("wav_file", ""))
        ]
        groups = self._group_references(references)
        if not groups:
            raise ValueError("remembered song has no managed audio references")

        selected: List[Dict] = []
        continuation_basis = "remembered_unique_segments"
        matched_sequence = -1
        match_confidence = 0.0
        lyrics_confidence = 0.0
        if clean_mode == "memory":
            for group_offset, group in enumerate(groups):
                selected.append(self._pick_group_variant(group, seed, group_offset))
        else:
            contour = list(query_contour or [])
            if len(contour) < 6:
                raise ValueError("续唱需要最近一次真实歌声，当前没有足够的旋律用于定位")
            matched_group_index = -1
            normalized_query_lyrics = _normalize_text(query_lyrics)
            for index, group in enumerate(groups):
                melody_score = max(
                    performance_similarity(contour, item.get("pitch_contour_midi", []) or [])
                    for item in group
                )
                group_lyrics_score = 0.0
                if normalized_query_lyrics:
                    for item in group:
                        stored_lyrics = _normalize_text(str(item.get("lyrics", "")))
                        if not stored_lyrics:
                            continue
                        if normalized_query_lyrics in stored_lyrics or stored_lyrics in normalized_query_lyrics:
                            group_lyrics_score = max(group_lyrics_score, 1.0)
                        else:
                            group_lyrics_score = max(
                                group_lyrics_score,
                                SequenceMatcher(None, normalized_query_lyrics, stored_lyrics).ratio(),
                            )
                group_score = (
                    0.84 * melody_score + 0.16 * group_lyrics_score
                    if group_lyrics_score > 0.0 else melody_score
                )
                if group_score > match_confidence:
                    match_confidence = group_score
                    lyrics_confidence = group_lyrics_score
                    matched_group_index = index
            if matched_group_index < 0 or match_confidence < 0.68:
                raise ValueError(
                    f"最近旋律与这首歌的本地记忆匹配不足（{match_confidence:.2f}），未冒险续唱"
                )
            matched_sequence = int(
                groups[matched_group_index][0].get("sequence_index", matched_group_index)
            )
            later_groups = groups[matched_group_index + 1:]
            if later_groups:
                for group_offset, group in enumerate(later_groups):
                    selected.append(self._pick_group_variant(group, seed, group_offset))
                continuation_basis = "learned_segment_sequence"
            else:
                # A long/full reference may contain the query plus material after it.
                # Locate the end note and slice only the unseen tail.
                best_tail = None
                best_tail_score = 0.0
                for item in groups[matched_group_index]:
                    # Only an explicitly imported/marked full reference may supply an
                    # unseen tail.  A longer user take can simply contain the same line
                    # twice, so duration alone is not evidence of a real continuation.
                    if not bool(item.get("is_full_source")):
                        continue
                    timeline = item.get("pitch_timeline_midi", []) or []
                    reference_contour = item.get("pitch_contour_midi", []) or []
                    score, _, end_note = melody_subsequence_alignment(contour, reference_contour)
                    voiced_frames = [i for i, value in enumerate(timeline) if _safe_float(value) > 0.0]
                    if not voiced_frames or end_note >= len(voiced_frames):
                        continue
                    frame_seconds = max(
                        0.02, _safe_float(item.get("pitch_timeline_frame_seconds"), 0.10)
                    )
                    start_frame = voiced_frames[end_note] + 1
                    remaining_seconds = max(0.0, (len(timeline) - start_frame) * frame_seconds)
                    if score >= 0.68 and remaining_seconds >= 1.2 and score > best_tail_score:
                        best_tail = deepcopy(item)
                        best_tail["slice_start_seconds"] = round(start_frame * frame_seconds, 3)
                        best_tail["slice_end_seconds"] = round(
                            min(len(timeline) * frame_seconds,
                                start_frame * frame_seconds + max_seconds), 3
                        )
                        best_tail_score = score
                if best_tail is not None:
                    selected = [best_tail]
                    match_confidence = max(match_confidence, best_tail_score)
                    continuation_basis = "long_reference_subsequence_alignment"
                else:
                    variant_count = len(groups[matched_group_index])
                    raise ValueError(
                        "这首歌目前只有当前这一独立段；"
                        f"{variant_count} 条相似录音属于重复演唱样本，不是后续段落。"
                        "需要再学习下一段或导入包含后续的本地原曲后才能可靠续唱"
                    )

        total_seconds = 0.0
        for item in selected:
            relative = str(item.get("wav_file", ""))
            absolute = self._absolute_audio_path(relative)
            if not os.path.isfile(absolute):
                raise FileNotFoundError(f"remembered song WAV is missing: {relative}")
            item["absolute_wav_path"] = absolute
            start = max(0.0, _safe_float(item.get("slice_start_seconds"), 0.0))
            end = _safe_float(item.get("slice_end_seconds"), 0.0)
            duration = _safe_float(item.get("duration_seconds"), 0.0)
            if end > start:
                duration = end - start
            total_seconds += max(0.0, duration - start if end <= start else duration)

        if total_seconds > max_seconds + 0.25:
            raise ValueError(
                f"已记住的独立段合计约 {total_seconds:.1f} 秒，超过当前一次演唱上限 "
                f"{max_seconds:.1f} 秒；为避免裁掉开头，本次没有播放"
            )

        summary = self._entry_summary(entry_copy)
        summary.update({
            "mode": clean_mode,
            "selected_references": selected,
            "selected_segment_count": len(selected),
            "matched_sequence_index": matched_sequence,
            "match_confidence": round(match_confidence, 4),
            "lyrics_confidence": round(lyrics_confidence, 4),
            "continuation": clean_mode == "continue",
            "continuation_basis": continuation_basis,
            "predicted_from_local_timeline": clean_mode == "continue",
        })
        return summary

    def _find_performance_entry_locked(
        self, song_id: str, title: str, query_contour: List[float]
    ) -> Optional[Dict]:
        clean_id = (song_id or "").strip()
        if clean_id:
            return self._find_entry_locked(clean_id)

        normalized_title = _normalize_text(title)
        if normalized_title:
            exact = None
            best = None
            best_score = 0.0
            for item in self._catalog:
                fields = [str(item.get("title", ""))]
                fields.extend(str(alias) for alias in item.get("aliases", []) if alias)
                for field in fields:
                    normalized_field = _normalize_text(field)
                    if normalized_field == normalized_title:
                        exact = item
                        break
                    score = SequenceMatcher(None, normalized_title, normalized_field).ratio()
                    if score > best_score:
                        best_score = score
                        best = item
                if exact is not None:
                    break
            if exact is not None:
                return exact
            if best is not None and best_score >= 0.72:
                return best

        if query_contour:
            best = None
            best_score = 0.0
            for item in self._catalog:
                for reference in item.get("references", []):
                    score = performance_similarity(
                        query_contour, reference.get("pitch_contour_midi", []) or []
                    )
                    if score > best_score:
                        best_score = score
                        best = item
            if best is not None and best_score >= 0.68:
                return best
        return None

    @staticmethod
    def _group_references(references: List[Dict]) -> List[List[Dict]]:
        grouped: Dict[str, List[Dict]] = {}
        for reference in references:
            group_id = str(reference.get("segment_group_id", "") or reference.get("id", ""))
            grouped.setdefault(group_id, []).append(reference)
        groups = list(grouped.values())
        groups.sort(key=lambda group: (
            int(group[0].get("sequence_index", 0)),
            int(group[0].get("created_at", 0) or 0),
        ))
        return groups

    @staticmethod
    def _pick_group_variant(group: List[Dict], seed: int, offset: int) -> Dict:
        ordered = sorted(group, key=lambda item: int(item.get("created_at", 0) or 0))
        index = abs(int(seed) + offset * 7919) % len(ordered)
        return deepcopy(ordered[index])

    def _find_entry_locked(self, song_id: str) -> Optional[Dict]:
        return next((item for item in self._catalog if str(item.get("id", "")) == song_id), None)

    def _build_audio_filename(self, song_id: str, clip_id: str, title: str, artist: str) -> str:
        safe_title = _safe_filename_component(title, "unknown")
        safe_artist = _safe_filename_component(artist, "", 40) if artist else ""
        middle = f"{safe_title}_{safe_artist}" if safe_artist else safe_title
        return f"{song_id}_{middle}_{clip_id}.wav"

    def _relative_audio_path(self, absolute_path: str) -> str:
        return os.path.relpath(absolute_path, self.catalog_root).replace("\\", "/")

    def _absolute_audio_path(self, relative_path: str) -> str:
        candidate = os.path.abspath(os.path.join(self.catalog_root, relative_path))
        if os.path.commonpath((self.audio_dir, candidate)) != self.audio_dir:
            raise ValueError("catalog audio path escapes the local song library")
        return candidate

    @staticmethod
    def _entry_summary(entry: Dict) -> Dict:
        song_id = str(entry.get("id", ""))
        title = str(entry.get("title", ""))
        artist = str(entry.get("artist", ""))
        references = entry.get("references", []) if isinstance(entry.get("references"), list) else []
        groups = SongSearchEngine._group_references(
            [item for item in references if isinstance(item, dict)]
        )
        return {
            "song_id": song_id,
            "title": title,
            "artist": artist,
            "display_name": title or f"未命名旋律 {song_id[:6]}",
            "named": bool(title),
            "reference_count": len(references) or (1 if entry.get("pitch_contour_midi") else 0),
            "unique_segment_count": len(groups) or (1 if entry.get("pitch_contour_midi") else 0),
            "duplicate_variant_count": max(0, len(references) - len(groups)),
            "can_continue": len(groups) >= 2 or any(
                bool(item.get("is_full_source")) for item in references if isinstance(item, dict)
            ),
            "score_available": any(
                bool(item.get("singing_score"))
                for item in references
                if isinstance(item, dict)
            ),
            "updated_at": int(entry.get("updated_at", 0) or 0),
        }

    def _save_catalog_locked(self):
        os.makedirs(self.catalog_root, exist_ok=True)
        temp_path = self.catalog_path + ".tmp"
        with open(temp_path, "w", encoding="utf-8") as handle:
            json.dump({"version": 4, "songs": self._catalog}, handle, ensure_ascii=False, indent=2)
        os.replace(temp_path, self.catalog_path)

    def search(
        self,
        query: str,
        mode: str = "auto",
        melody_contour: Optional[List[float]] = None,
        max_results: int = 5,
    ) -> Dict:
        mode = (mode or "auto").strip().lower()
        if mode not in ("auto", "hum", "catalog"):
            mode = "auto"
        max_results = max(1, min(10, int(max_results)))
        providers_used: List[str] = []
        providers_skipped: List[str] = []
        matches: List[Dict] = []

        if mode in ("auto", "hum", "catalog"):
            local = self._search_local(query, melody_contour or [], max_results)
            if local:
                matches.extend(local)
                providers_used.append("local_catalog")
            elif self.catalog_count == 0:
                providers_skipped.append("local_catalog_empty")

        # MusicBrainz is a metadata catalog, not a lyrics or humming engine.
        # Use it only when the agent has a useful text clue.
        if mode in ("auto", "catalog") and len(_normalize_text(query)) >= 2:
            try:
                matches.extend(self._search_musicbrainz(query, max_results))
                providers_used.append("musicbrainz")
            except Exception as exc:
                providers_skipped.append("musicbrainz_error:" + str(exc)[:100])

        matches = self._deduplicate(matches)
        matches.sort(key=lambda item: _safe_float(item.get("confidence")), reverse=True)
        matches = matches[:max_results]
        best = matches[0] if matches else None
        # MusicBrainz exposes metadata-search relevance, not evidence that the
        # user's lyrics or melody identify that recording.  Only a strong match
        # against the user's private local reference catalog can confirm a song.
        reliable = bool(
            best
            and best.get("source") == "local_catalog"
            and _safe_float(best.get("confidence")) >= 0.70
        )
        summary = self._build_summary(query, matches, providers_used, providers_skipped, reliable)
        return {
            "ok": True,
            "query": query or "",
            "mode": mode,
            "reliable": reliable,
            "providers_used": providers_used,
            "providers_skipped": providers_skipped,
            "matches": matches,
            "best_match": best,
            "summary": summary,
            "privacy": "raw_audio_kept_local",
        }

    def recall(self, query: str, contour: List[float], limit: int = 3) -> List[Dict]:
        """听到一段歌声时"想起了什么"——只查本机，不打外部目录。

        **刻意不设置信度门槛。** 8/12 拿曲库现有的重复组量过：同一首歌的不同录音
        与不同歌之间，melody_similarity 的分布几乎完全重合(组内中位 0.701 /
        跨组中位 0.675，213 对 vs 490 对)，0.80 以上才干净但只认得出 9/213。
        原因在实现本身：轮廓重采样到 220 点再取差分，时长和调性都被抹掉，
        只剩起伏形状——而人声旋律的起伏形状天生相似。

        所以这里只按分数取前几名，把歌词和上次听到的时间一并带出去，判断留给上层：
        能分辨这几条的是歌词语义，不是旋律数字。
        """
        ranked = self._search_local(query or "", list(contour or []), max(1, int(limit)))
        with self._lock:
            index = {str(item.get("id", "")): item for item in self._catalog}
        out: List[Dict] = []
        for match in ranked:
            entry = index.get(str(match.get("source_id", "")))
            if entry is None:
                continue
            references = [
                item for item in (entry.get("references") or [])
                if isinstance(item, dict)
            ]
            stamps = [int(_safe_float(item.get("created_at"), 0.0)) for item in references]
            stamps.append(int(_safe_float(entry.get("updated_at"), 0.0)))
            #旋律的文本形式一并带出去。8/17 实测：候选里只有歌词时她四选一 50%，
            #只有旋律文本 58%，两个都给 66%——它们是互补的而不是冗余。
            #歌词来自唱歌 ASR、错字很多(「几十长旋天边」)，音高提取相对稳，
            #所以旋律文本单独反而比歌词强。
            note_seq = ""
            for item in references:
                cand = str(item.get("note_sequence", "")).strip()
                if cand:
                    note_seq = cand
                    break
            if not note_seq:
                note_seq = notes_from_contour(entry.get("pitch_contour_midi") or [])
            out.append({
                "song_id": str(entry.get("id", "")),
                "display_name": str(match.get("title", "")),
                "named": bool(match.get("named")),
                "lyrics": " ".join(str(entry.get("lyrics", "")).split()),
                "note_sequence": note_seq,
                "confidence": _safe_float(match.get("confidence"), 0.0),
                "melody_score": _safe_float(match.get("melody_score"), 0.0),
                "containment": _safe_float(match.get("containment"), 0.0),
                "match_reason": str(match.get("match_reason", "")),
                "last_heard": max(stamps) if stamps else 0,
                "take_count": len(references),
            })
        return out

    def _search_local(self, query: str, contour: List[float], limit: int) -> List[Dict]:
        normalized_query = _normalize_text(query)
        with self._lock:
            catalog = [dict(item) for item in self._catalog]

        output = []
        for item in catalog:
            song_id = str(item.get("id", ""))
            stored_title = str(item.get("title", ""))
            title = stored_title or f"未命名旋律 {song_id[:6]}"
            artist = str(item.get("artist", ""))
            fields = [stored_title, artist, str(item.get("lyrics", ""))]
            fields.extend(str(v) for v in item.get("aliases", []) if v)
            text_score = 0.0
            if normalized_query:
                for field in fields:
                    normalized_field = _normalize_text(field)
                    if not normalized_field:
                        continue
                    if normalized_query in normalized_field or normalized_field in normalized_query:
                        text_score = max(text_score, 0.94)
                    else:
                        text_score = max(text_score, SequenceMatcher(None, normalized_query, normalized_field).ratio())

            reference_contours = []
            if item.get("pitch_contour_midi"):
                reference_contours.append(item.get("pitch_contour_midi", []))
            for reference in item.get("references", []):
                if isinstance(reference, dict) and reference.get("pitch_contour_midi"):
                    reference_contours.append(reference.get("pitch_contour_midi", []))
            #排序判据换成 token 对齐(见 token_alignment_similarity 的注释：
            #AUC 0.531 → 0.648，长度接近时 0.511 → 0.753)。
            #去重与 mode=continue 仍用旧的 performance_similarity /
            #melody_subsequence_alignment——那两处的语义不同，不在这次改动范围内。
            melody_score = max(
                (token_alignment_similarity(contour, reference)
                 for reference in reference_contours),
                default=0.0,
            )
            containment = max(
                (melody_containment(contour, reference)
                 for reference in reference_contours),
                default=0.0,
            )
            if text_score > 0 and melody_score > 0:
                confidence = 0.55 * melody_score + 0.45 * text_score
            else:
                confidence = max(text_score * 0.88, melody_score * 0.93)
            if confidence < 0.35:
                continue
            output.append(
                {
                    "title": title,
                    "artist": artist,
                    "album": str(item.get("album", "")),
                    "source": "local_catalog",
                    "source_id": song_id,
                    "named": bool(stored_title),
                    "confidence": round(float(confidence), 3),
                    "match_reason": (
                        f"旋律{melody_score:.2f}，文字{text_score:.2f}"
                        if melody_score > 0 and text_score > 0
                        else (f"旋律{melody_score:.2f}" if melody_score > 0 else f"文字{text_score:.2f}")
                    ),
                    "url": str(item.get("url", "")),
                    "melody_score": round(float(melody_score), 3),
                    "containment": round(float(containment), 3),
                }
            )
        output.sort(key=lambda item: item["confidence"], reverse=True)
        return output[:limit]

    def _search_musicbrainz(self, query: str, limit: int) -> List[Dict]:
        url = (
            "https://musicbrainz.org/ws/2/recording/"
            f"?query={quote(query)}&fmt=json&limit={max(1, min(limit, 10))}"
        )
        request = Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
        with urlopen(request, timeout=5.0) as response:
            payload = json.loads(response.read().decode("utf-8"))
        output = []
        for recording in payload.get("recordings", []):
            credits = recording.get("artist-credit") or []
            artist = "".join(str(part.get("name", "")) + str(part.get("joinphrase", "")) for part in credits)
            releases = recording.get("releases") or []
            album = str(releases[0].get("title", "")) if releases else ""
            raw_score = _safe_float(recording.get("score"), 0.0) / 100.0
            output.append(
                {
                    "title": str(recording.get("title", "")),
                    "artist": artist,
                    "album": album,
                    "source": "musicbrainz",
                    "source_id": str(recording.get("id", "")),
                    # Metadata search alone is a candidate, not proof that the
                    # sung melody or a lyric fragment identifies this recording.
                    # Keep this below the global reliable threshold even when
                    # MusicBrainz reports a perfect search-relevance score.
                    "confidence": round(min(0.55, raw_score * 0.55), 3),
                    "match_reason": "公开元数据目录文字候选（不代表歌词或旋律识别）",
                    "evidence_level": "metadata_candidate_only",
                    "url": f"https://musicbrainz.org/recording/{recording.get('id', '')}",
                }
            )
        return output

    @staticmethod
    def _deduplicate(matches: List[Dict]) -> List[Dict]:
        output: Dict[Tuple[str, str], Dict] = {}
        for item in matches:
            key = (_normalize_text(item.get("title", "")), _normalize_text(item.get("artist", "")))
            if not key[0]:
                continue
            previous = output.get(key)
            if previous is None or _safe_float(item.get("confidence")) > _safe_float(previous.get("confidence")):
                output[key] = item
        return list(output.values())

    @staticmethod
    def _build_summary(query, matches, used, skipped, reliable) -> str:
        if not matches:
            providers = "、".join(used) if used else "没有可用提供方"
            return f"没有找到可靠匹配。查询线索：{query or '仅旋律'}；已尝试：{providers}。"
        lines = ["本地参考库确认到可靠候选：" if reliable else "只找到一些目录候选，目前不能确认歌名："]
        for index, item in enumerate(matches[:3], 1):
            artist = f" - {item.get('artist')}" if item.get("artist") else ""
            lines.append(
                f"{index}. {item.get('title', '未知')}{artist}"
                f"（置信度{_safe_float(item.get('confidence')):.2f}，{item.get('source')}）"
            )
        if skipped:
            lines.append("未启用项：" + "、".join(skipped[:3]))
        if not reliable and any(item.get("source") == "musicbrainz" for item in matches):
            lines.append("注意：MusicBrainz 只匹配标题/歌手等元数据，不识别歌词或旋律；不得把这些候选说成确定答案。")
        return " ".join(lines)
