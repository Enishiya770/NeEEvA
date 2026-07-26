"""Kana-only score preparation for pluggable Japanese SVS renderers."""

from __future__ import annotations

import re
import unicodedata


_KANJI_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff々〆ヶ]")
_KATAKANA_RE = re.compile(r"[\u30a1-\u30f6]")
_SMALL_JOINING_KANA = frozenset("ゃゅょぁぃぅぇぉゎ")
_PUNCTUATION = frozenset(
    " \t\r\n、。・，．！？!?：:；;「」『』（）()［］[]【】…‥♪♫♬"
)


def _to_hiragana(text: str) -> str:
    return _KATAKANA_RE.sub(
        lambda match: chr(ord(match.group(0)) - 0x60),
        text,
    )


def _kana_reading(text: str) -> tuple[str, str]:
    text = unicodedata.normalize("NFKC", str(text or "")).strip()
    if not _KANJI_RE.search(text):
        return _to_hiragana(text), "score-kana"
    raise ValueError(
        "Japanese renderer accepts kana only; SenseVoice must provide "
        "lyrics_reading, and explicit lyric corrections must be written in kana"
    )


def _is_kana_only(text: str) -> bool:
    pronounced = [character for character in text if character not in _PUNCTUATION]
    return bool(pronounced) and all(
        "ぁ" <= character <= "ゖ" or character in {"ー", "ゝ", "ゞ"}
        for character in pronounced
    )


def _mora(kana: str) -> list[str]:
    units: list[str] = []
    for character in kana:
        if character in _PUNCTUATION:
            continue
        if character in _SMALL_JOINING_KANA and units:
            units[-1] += character
        elif "ぁ" <= character <= "ゖ" or character in {"ー", "ゝ", "ゞ"}:
            units.append(character)
    return units


def prepare_japanese_score(score: dict) -> dict:
    """Return a copy whose renderer input contains only hiragana/mora."""
    prepared = dict(score)
    supplied = str(prepared.get("lyrics_reading", "") or "").strip()
    supplied_complete = bool(prepared.get("lyrics_reading_complete"))
    source = str(prepared.get("lyrics_reading_source", "") or "")
    if supplied and supplied_complete and not _KANJI_RE.search(supplied):
        reading = _to_hiragana(unicodedata.normalize("NFKC", supplied))
        source = source or "sensevoice-kana"
    else:
        reading, source = _kana_reading(str(prepared.get("lyrics", "") or ""))
    reading = "".join(ch for ch in reading if ch not in _PUNCTUATION)
    if not _is_kana_only(reading):
        raise ValueError(
            "Japanese renderer lyrics must contain kana only; "
            "provide a kana lyric correction for Latin letters or symbols"
        )
    units = _mora(reading)
    if not reading or not units:
        raise ValueError("Japanese SingingScore contains no usable kana lyrics")

    prepared["language"] = "ja"
    prepared["lyrics_reading"] = reading
    prepared["lyrics_reading_source"] = source
    prepared["lyrics_reading_complete"] = True
    prepared["lyrics_mora"] = units
    # This is the only text field that a Japanese renderer is allowed to use.
    prepared["renderer_lyrics"] = reading
    prepared["renderer_lyric_units"] = units
    return prepared
