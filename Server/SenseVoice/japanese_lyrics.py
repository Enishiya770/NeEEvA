"""Japanese lyric normalisation for renderer-neutral singing scores.

SenseVoice remains the lyric recogniser.  This module only turns its Japanese
transcript into the kana/mora representation required by a score-driven
singing synthesiser; it does not run a second ASR model.
"""

from __future__ import annotations

import re
import unicodedata
from functools import lru_cache
from typing import Dict, List


_KANJI_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff々〆ヶ]")
_KATAKANA_RE = re.compile(r"[\u30a1-\u30f6]")
_SMALL_JOINING_KANA = frozenset("ゃゅょぁぃぅぇぉゎ")
_LYRIC_PUNCTUATION = frozenset(
    " \t\r\n、。・，．！？!?：:；;「」『』（）()［］[]【】…‥♪♫♬"
)


def _katakana_to_hiragana(text: str) -> str:
    def convert(match: re.Match[str]) -> str:
        return chr(ord(match.group(0)) - 0x60)

    return _KATAKANA_RE.sub(convert, text)


@lru_cache(maxsize=1)
def _openjtalk_module():
    try:
        import pyopenjtalk
    except ImportError:
        return None
    return pyopenjtalk


def _reading_from_transcript(text: str) -> tuple[str, str, bool]:
    """Return hiragana, provenance, and whether every kanji was resolved."""
    if not _KANJI_RE.search(text):
        return _katakana_to_hiragana(text), "sensevoice-kana", True

    openjtalk = _openjtalk_module()
    if openjtalk is None:
        # Do not silently send kanji into a Japanese singing phonemizer.  The
        # original transcript is still preserved in SingingScore.lyrics.
        return "", "contextual-reading-unavailable", False
    try:
        converted = "".join(
            str(item.get("read") or "")
            for item in openjtalk.run_frontend(text)
        )
    except Exception:
        return "", "contextual-reading-failed", False
    converted = _katakana_to_hiragana(converted)
    complete = not _KANJI_RE.search(converted)
    return converted if complete else "", "sensevoice+pyopenjtalk", complete


def _is_kana_only(text: str) -> bool:
    pronounced = [
        character for character in text if character not in _LYRIC_PUNCTUATION
    ]
    return bool(pronounced) and all(
        "ぁ" <= character <= "ゖ" or character in {"ー", "ゝ", "ゞ"}
        for character in pronounced
    )


def split_japanese_mora(kana: str) -> List[str]:
    """Split kana into singing timing units.

    Contracted sounds such as ``きゃ`` form one mora.  Sokuon ``っ``, moraic
    nasal ``ん`` and the long-vowel mark ``ー`` remain explicit timing units so
    the renderer can assign their duration from the score.
    """
    mora: List[str] = []
    for character in kana:
        if character in _LYRIC_PUNCTUATION:
            continue
        if character in _SMALL_JOINING_KANA and mora:
            mora[-1] += character
        elif "ぁ" <= character <= "ゖ" or character in {"ー", "ゝ", "ゞ"}:
            mora.append(character)
    return mora


def normalise_japanese_lyrics(text: str) -> Dict:
    """Create the kana-only pronunciation view of a SenseVoice transcript."""
    original = unicodedata.normalize("NFKC", str(text or "")).strip()
    reading, source, complete = _reading_from_transcript(original)
    complete = bool(complete and _is_kana_only(reading))
    if not complete:
        reading = ""
    reading = "".join(
        character
        for character in reading
        if character not in _LYRIC_PUNCTUATION
    )
    return {
        "lyrics_reading": reading,
        "lyrics_reading_source": source,
        "lyrics_reading_complete": bool(complete),
        "lyrics_mora": split_japanese_mora(reading),
    }
