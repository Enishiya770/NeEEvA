"""Experimental kana-mora to SoulX English-phone adapter.

SoulX-Singer does not ship a Japanese frontend.  This module deliberately
limits itself to kana pronunciation and maps each Japanese mora to phonemes
that already exist in SoulX's English inventory.  It is an approximation, but
it is deterministic, score-only, and does not use the user's waveform as a
voice-conversion carrier.
"""

from __future__ import annotations

import json
from pathlib import Path


_VOWELS = {
    "あ": "AA1",
    "い": "IY1",
    "う": "UW1",
    "え": "EH1",
    "お": "OW1",
    "ぁ": "AA1",
    "ぃ": "IY1",
    "ぅ": "UW1",
    "ぇ": "EH1",
    "ぉ": "OW1",
}

_ROWS = {
    "か": ("K", "AA1"),
    "き": ("K", "IY1"),
    "く": ("K", "UW1"),
    "け": ("K", "EH1"),
    "こ": ("K", "OW1"),
    "が": ("G", "AA1"),
    "ぎ": ("G", "IY1"),
    "ぐ": ("G", "UW1"),
    "げ": ("G", "EH1"),
    "ご": ("G", "OW1"),
    "さ": ("S", "AA1"),
    "し": ("SH", "IY1"),
    "す": ("S", "UW1"),
    "せ": ("S", "EH1"),
    "そ": ("S", "OW1"),
    "ざ": ("Z", "AA1"),
    "じ": ("JH", "IY1"),
    "ず": ("Z", "UW1"),
    "ぜ": ("Z", "EH1"),
    "ぞ": ("Z", "OW1"),
    "た": ("T", "AA1"),
    "ち": ("CH", "IY1"),
    "つ": ("T", "S", "UW1"),
    "て": ("T", "EH1"),
    "と": ("T", "OW1"),
    "だ": ("D", "AA1"),
    "ぢ": ("JH", "IY1"),
    "づ": ("D", "Z", "UW1"),
    "で": ("D", "EH1"),
    "ど": ("D", "OW1"),
    "な": ("N", "AA1"),
    "に": ("N", "IY1"),
    "ぬ": ("N", "UW1"),
    "ね": ("N", "EH1"),
    "の": ("N", "OW1"),
    "は": ("HH", "AA1"),
    "ひ": ("HH", "IY1"),
    "ふ": ("F", "UW1"),
    "へ": ("HH", "EH1"),
    "ほ": ("HH", "OW1"),
    "ば": ("B", "AA1"),
    "び": ("B", "IY1"),
    "ぶ": ("B", "UW1"),
    "べ": ("B", "EH1"),
    "ぼ": ("B", "OW1"),
    "ぱ": ("P", "AA1"),
    "ぴ": ("P", "IY1"),
    "ぷ": ("P", "UW1"),
    "ぺ": ("P", "EH1"),
    "ぽ": ("P", "OW1"),
    "ま": ("M", "AA1"),
    "み": ("M", "IY1"),
    "む": ("M", "UW1"),
    "め": ("M", "EH1"),
    "も": ("M", "OW1"),
    "や": ("Y", "AA1"),
    "ゆ": ("Y", "UW1"),
    "よ": ("Y", "OW1"),
    "ゃ": ("Y", "AA1"),
    "ゅ": ("Y", "UW1"),
    "ょ": ("Y", "OW1"),
    "ら": ("R", "AA1"),
    "り": ("R", "IY1"),
    "る": ("R", "UW1"),
    "れ": ("R", "EH1"),
    "ろ": ("R", "OW1"),
    "わ": ("W", "AA1"),
    "ゎ": ("W", "AA1"),
    "ゐ": ("W", "IY1"),
    "ゑ": ("W", "EH1"),
    "を": ("OW1",),
    "ゔ": ("V", "UW1"),
}

_PALATAL_BASE = {
    "き": ("K", "Y"),
    "ぎ": ("G", "Y"),
    "し": ("SH",),
    "じ": ("JH",),
    "ち": ("CH",),
    "ぢ": ("JH",),
    "に": ("N", "Y"),
    "ひ": ("HH", "Y"),
    "び": ("B", "Y"),
    "ぴ": ("P", "Y"),
    "み": ("M", "Y"),
    "り": ("R", "Y"),
}

_FOREIGN_MORA = {
    "いぇ": ("Y", "EH1"),
    "うぃ": ("W", "IY1"),
    "うぇ": ("W", "EH1"),
    "うぉ": ("W", "OW1"),
    "きぇ": ("K", "Y", "EH1"),
    "ぎぇ": ("G", "Y", "EH1"),
    "しぇ": ("SH", "EH1"),
    "じぇ": ("JH", "EH1"),
    "ちぇ": ("CH", "EH1"),
    "にぇ": ("N", "Y", "EH1"),
    "ひぇ": ("HH", "Y", "EH1"),
    "びぇ": ("B", "Y", "EH1"),
    "ぴぇ": ("P", "Y", "EH1"),
    "みぇ": ("M", "Y", "EH1"),
    "りぇ": ("R", "Y", "EH1"),
    "てぃ": ("T", "IY1"),
    "でぃ": ("D", "IY1"),
    "とぅ": ("T", "UW1"),
    "どぅ": ("D", "UW1"),
    "つぁ": ("T", "S", "AA1"),
    "つぃ": ("T", "S", "IY1"),
    "つぇ": ("T", "S", "EH1"),
    "つぉ": ("T", "S", "OW1"),
    "ふぁ": ("F", "AA1"),
    "ふぃ": ("F", "IY1"),
    "ふぇ": ("F", "EH1"),
    "ふぉ": ("F", "OW1"),
    "ゔぁ": ("V", "AA1"),
    "ゔぃ": ("V", "IY1"),
    "ゔぇ": ("V", "EH1"),
    "ゔぉ": ("V", "OW1"),
}

_SMALL_TO_VOWEL = {"ゃ": "AA1", "ゅ": "UW1", "ょ": "OW1"}
_LABIAL_ONSETS = {"B", "M", "P"}
_VELAR_ONSETS = {"G", "K"}


def _phones_for_regular_mora(mora: str) -> tuple[str, ...]:
    if mora in _FOREIGN_MORA:
        return _FOREIGN_MORA[mora]
    if mora in _VOWELS:
        return (_VOWELS[mora],)
    if mora in _ROWS:
        return _ROWS[mora]
    if len(mora) == 2 and mora[0] in _PALATAL_BASE:
        vowel = _SMALL_TO_VOWEL.get(mora[1])
        if vowel:
            return (*_PALATAL_BASE[mora[0]], vowel)
    raise ValueError(f"unsupported Japanese mora: {mora}")


def _onset(phones: tuple[str, ...]) -> str:
    for phone in phones:
        if not any(marker in phone for marker in ("AA", "AE", "AH", "AO", "AW",
                                                   "AY", "EH", "ER", "EY", "IH",
                                                   "IY", "OW", "OY", "UH", "UW")):
            return phone
    return ""


def _vowel(phones: tuple[str, ...]) -> str:
    for phone in reversed(phones):
        if any(marker in phone for marker in ("AA", "AE", "AH", "AO", "AW",
                                               "AY", "EH", "ER", "EY", "IH",
                                               "IY", "OW", "OY", "UH", "UW")):
            return phone
    return "UW1"


def mora_to_soulx_phones(morae: list[str]) -> list[str]:
    """Return one SoulX metadata phone group for every Japanese mora."""
    result: list[str] = []
    regular: list[tuple[str, ...] | None] = []
    for mora in morae:
        if mora in {"っ", "ん", "ー"}:
            regular.append(None)
        else:
            regular.append(_phones_for_regular_mora(mora))

    for index, mora in enumerate(morae):
        phones = regular[index]
        if mora == "っ":
            next_phones = next(
                (value for value in regular[index + 1:] if value),
                None,
            )
            onset = _onset(next_phones or ())
            phones = (onset or "T",)
        elif mora == "ん":
            next_phones = next(
                (value for value in regular[index + 1:] if value),
                None,
            )
            onset = _onset(next_phones or ())
            phones = (
                "M" if onset in _LABIAL_ONSETS
                else "NG" if onset in _VELAR_ONSETS
                else "N",
            )
        elif mora == "ー":
            previous = next(
                (value for value in reversed(regular[:index]) if value),
                None,
            )
            phones = (_vowel(previous or ()),)
        if not phones:
            raise ValueError(f"could not resolve Japanese mora: {mora}")
        result.append("en_" + "-".join(phones))
    return result


def japanese_g2p_transform(words: list[str], _language: str) -> list[str]:
    """SoulX G2P callback preserving rests and one group per score event."""
    pronounced = [word for word in words if word != "<SP>"]
    converted = iter(mora_to_soulx_phones(pronounced))
    return ["<SP>" if word == "<SP>" else next(converted) for word in words]


def validate_against_phoneset(
    phone_groups: list[str],
    phoneset_path: str | Path,
) -> None:
    """Fail early if an adapter atom is absent from the installed SoulX model."""
    inventory = set(json.loads(Path(phoneset_path).read_text(encoding="utf-8")))
    missing: set[str] = set()
    for group in phone_groups:
        if group == "<SP>":
            continue
        if not group.startswith("en_"):
            missing.add(group)
            continue
        for atom in group[3:].split("-"):
            token = "en_" + atom
            if token not in inventory:
                missing.add(token)
    if missing:
        raise ValueError(
            "Japanese adapter produced phones outside SoulX inventory: "
            + ", ".join(sorted(missing))
        )
