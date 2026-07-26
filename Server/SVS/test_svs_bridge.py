import io
import json
import os
import sys
import tempfile
import unittest
import wave
from pathlib import Path
from unittest import mock

from soulx_runner import (
    _clean_score_f0,
    _score_note_events,
    apply_singing_score_f0,
    build_direct_target_metadata,
    lyric_units,
    prepare_prompt_metadata,
)
from svs_server import (
    _backend_state,
    _language_name,
    _run_japanese_synthesis,
    _validate_score,
)
from svs_server import _language_code
from japanese_score import prepare_japanese_score
from japanese_phonemes import (
    japanese_g2p_transform,
    mora_to_soulx_phones,
    validate_against_phoneset,
)
from fastapi import HTTPException


class SVSBridgeTests(unittest.TestCase):
    @staticmethod
    def _identity_g2p(words, _language):
        return [f"phone_{word}" if word != "<SP>" else "<SP>" for word in words]

    def test_singing_score_f0_replaces_backend_contour_at_same_frame_count(self):
        metadata = [
            {
                "time": [0, 40],
                "f0": "1 1 1 1 1",
                "text": "la",
            }
        ]
        score = {
            "schema_version": 1,
            "frame_seconds": 0.01,
            "f0_hz": [220.0, 230.0, 240.0, 250.0, 260.0],
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "metadata.json"
            path.write_text(json.dumps(metadata), encoding="utf-8")
            replaced = apply_singing_score_f0(path, score)
            result = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(replaced, 5)
        self.assertEqual(
            result[0]["f0"],
            "220.00 230.00 240.00 250.00 260.00",
        )

    def test_health_is_honest_when_optional_model_is_not_installed(self):
        state = _backend_state()
        self.assertFalse(state["audio_to_audio_conversion"])
        self.assertEqual(state["score_schema"], 1)
        if not state["backend_available"]:
            self.assertTrue(state["missing"])

    def test_default_backend_rejects_japanese_before_model_inference(self):
        with self.assertRaises(HTTPException) as context:
            _language_name("ja")
        self.assertEqual(context.exception.status_code, 422)

    def test_router_accepts_japanese_without_claiming_model_is_installed(self):
        self.assertEqual(_language_code("Japanese"), "ja")
        state = _backend_state()
        self.assertIn("ja", state["backends"])
        if not state["backends"]["ja"]["backend_available"]:
            self.assertNotIn("ja", state["supported_languages"])

    def test_japanese_renderer_score_uses_kana_only(self):
        score = prepare_japanese_score(
            {
                "schema_version": 1,
                "language": "ja",
                "lyrics": "キミノコトガスキ",
                "lyrics_reading": "きみのことがすき",
                "lyrics_reading_source": "sensevoice-kana",
                "lyrics_reading_complete": True,
                "notes": [{"midi": 69}],
                "f0_hz": [440.0],
            }
        )
        self.assertEqual(score["renderer_lyrics"], "きみのことがすき")
        self.assertEqual(
            score["renderer_lyric_units"],
            ["き", "み", "の", "こ", "と", "が", "す", "き"],
        )
        self.assertNotIn("キ", score["renderer_lyrics"])

    def test_japanese_renderer_rejects_unresolved_latin_lyrics(self):
        with self.assertRaises(ValueError):
            prepare_japanese_score(
                {
                    "language": "ja",
                    "lyrics": "きみ Lemon",
                    "lyrics_reading_complete": False,
                }
            )

    def test_japanese_renderer_rejects_kanji_without_sensevoice_reading(self):
        with self.assertRaises(ValueError):
            prepare_japanese_score(
                {
                    "language": "ja",
                    "lyrics": "君のことが好き",
                    "lyrics_reading_complete": False,
                }
            )

    def test_japanese_phone_adapter_handles_contextual_mora(self):
        self.assertEqual(
            mora_to_soulx_phones(["が", "っ", "こ", "う"]),
            ["en_G-AA1", "en_K", "en_K-OW1", "en_UW1"],
        )
        self.assertEqual(
            mora_to_soulx_phones(["し", "ん", "ぶ", "ん"]),
            ["en_SH-IY1", "en_M", "en_B-UW1", "en_N"],
        )
        self.assertEqual(
            mora_to_soulx_phones(["きゃ", "り", "ー"]),
            ["en_K-Y-AA1", "en_R-IY1", "en_IY1"],
        )

    def test_japanese_phone_adapter_preserves_rests_and_inventory(self):
        phones = japanese_g2p_transform(
            ["<SP>", "き", "み", "<SP>"],
            "JapaneseAdapter",
        )
        self.assertEqual(
            phones,
            ["<SP>", "en_K-IY1", "en_M-IY1", "<SP>"],
        )
        validate_against_phoneset(
            phones,
            (
                Path(__file__).resolve().parent
                / "vendor"
                / "SoulX-Singer"
                / "soulxsinger"
                / "utils"
                / "phoneme"
                / "phone_set.json"
            ),
        )

    def test_direct_japanese_metadata_uses_kana_not_english_g2p(self):
        score = prepare_japanese_score(
            {
                "schema_version": 1,
                "language": "ja",
                "lyrics": "きみのことがすき",
                "duration_seconds": 1.6,
                "frame_seconds": 0.01,
                "notes": [
                    {
                        "midi": 64,
                        "start_seconds": 0.0,
                        "duration_seconds": 1.6,
                    }
                ],
                "f0_hz": [329.63] * 160,
            }
        )
        metadata, _ = build_direct_target_metadata(
            score,
            "JapaneseAdapter",
            japanese_g2p_transform,
        )
        segment = metadata[0]
        self.assertIn("と", segment["text"].split())
        self.assertIn("en_T-OW1", segment["phoneme"].split())
        self.assertEqual(segment["resolved_lyrics"], "きみのことがすき")

    def test_japanese_runner_receives_score_but_not_source_audio_path(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            runner = root / "fake_ja_runner.py"
            model = root / "model"
            model.mkdir()
            runner.write_text(
                "\n".join(
                    (
                        "import argparse, json, wave",
                        "p=argparse.ArgumentParser()",
                        "p.add_argument('--model')",
                        "p.add_argument('--score')",
                        "p.add_argument('--output')",
                        "p.add_argument('--seed')",
                        "p.add_argument('--request-id')",
                        "a=p.parse_args()",
                        "s=json.load(open(a.score, encoding='utf-8'))",
                        "assert s['renderer_lyrics']=='きみ'",
                        "with wave.open(a.output,'wb') as w:",
                        " w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)",
                        " w.writeframes(b'\\x00\\x00'*1600)",
                        "print(json.dumps({'backend':'fake-diffsinger-ja'}))",
                    )
                ),
                encoding="utf-8",
            )
            source = io.BytesIO()
            with wave.open(source, "wb") as wav:
                wav.setnchannels(1)
                wav.setsampwidth(2)
                wav.setframerate(16000)
                wav.writeframes(b"\x00\x00" * 1600)
            score = prepare_japanese_score(
                {
                    "schema_version": 1,
                    "language": "ja",
                    "lyrics": "きみ",
                    "lyrics_reading": "きみ",
                    "lyrics_reading_complete": True,
                    "notes": [{"midi": 69}],
                    "f0_hz": [440.0],
                }
            )
            environment = {
                "NEEEVA_JA_SVS_RUNNER": str(runner),
                "NEEEVA_JA_SVS_MODEL": str(model),
                "NEEEVA_JA_SVS_PYTHON": sys.executable,
                "NEEEVA_SVS_SAVE_CAPTURES": "0",
            }
            with mock.patch.dict(os.environ, environment, clear=False):
                wav_bytes, metadata = _run_japanese_synthesis(
                    source.getvalue(),
                    score,
                    prompt_path=root / "unused_prompt.wav",
                    prompt_language="Mandarin",
                    seed=7,
                    request_id="test-ja",
                    max_seconds=5.0,
                    inference_steps=12,
                )
        self.assertGreater(len(wav_bytes), 44)
        self.assertEqual(metadata["backend"], "fake-diffsinger-ja")
        self.assertFalse(metadata["audio_to_audio_conversion"])

    def test_score_validation_requires_notes_and_voiced_f0(self):
        valid = json.dumps(
            {
                "schema_version": 1,
                "notes": [{"midi": 69}],
                "f0_hz": [0.0, 440.0],
            }
        )
        self.assertEqual(_validate_score(valid)["schema_version"], 1)

    def test_direct_score_metadata_aligns_all_chinese_lyrics_without_asr(self):
        score = {
            "schema_version": 1,
            "lyrics": "时光流转",
            "duration_seconds": 1.0,
            "frame_seconds": 0.01,
            "notes": [
                {
                    "midi": 60,
                    "start_seconds": 0.1,
                    "duration_seconds": 0.8,
                }
            ],
            "f0_hz": [220.0] * 100,
        }
        metadata, frame_count = build_direct_target_metadata(
            score,
            "Mandarin",
            self._identity_g2p,
        )
        segment = metadata[0]
        token_fields = [
            segment[key].split()
            for key in ("duration", "text", "phoneme", "note_pitch", "note_type")
        ]
        self.assertEqual(len({len(values) for values in token_fields}), 1)
        self.assertTrue(set("时光流转").issubset(set(token_fields[1])))
        self.assertIn("<SP>", token_fields[1])
        self.assertEqual(frame_count, 50)
        self.assertEqual(len(segment["f0"].split()), 50)

    def test_direct_score_metadata_requires_lyrics_for_safe_fallback(self):
        score = {
            "lyrics": "",
            "duration_seconds": 1.0,
            "frame_seconds": 0.01,
            "notes": [
                {
                    "midi": 60,
                    "start_seconds": 0.0,
                    "duration_seconds": 1.0,
                }
            ],
            "f0_hz": [220.0] * 100,
        }
        with self.assertRaises(ValueError):
            build_direct_target_metadata(
                score,
                "Mandarin",
                self._identity_g2p,
            )

    def test_f0_cleaning_corrects_isolated_octave_spike(self):
        normal = 220.0
        score = {
            "frame_seconds": 0.02,
            "duration_seconds": 1.0,
            "notes": [
                {
                    "midi": 57,
                    "start_seconds": 0.0,
                    "duration_seconds": 1.0,
                }
            ],
            "f0_hz": [normal] * 20 + [normal * 4.0] + [normal] * 29,
        }
        cleaned, diagnostic = _clean_score_f0(score, 1.0)
        self.assertLess(max(cleaned), 250.0)
        self.assertGreater(diagnostic["corrected_octave_frames"], 0)

    def test_short_pitch_fragments_are_merged_into_stable_notes(self):
        score = {
            "frame_seconds": 0.02,
            "duration_seconds": 1.0,
            "notes": [
                {
                    "midi": 57,
                    "start_seconds": 0.0,
                    "duration_seconds": 1.0,
                }
            ],
            "f0_hz": [220.0] * 20 + [233.08] + [220.0] * 29,
        }
        cleaned, _ = _clean_score_f0(score, 1.0)
        events = _score_note_events(score, 1.0, cleaned)
        voiced = [event for event in events if event["midi"] > 0]
        self.assertEqual(len(voiced), 1)
        self.assertEqual(voiced[0]["midi"], 57)

    def test_direct_metadata_has_no_micro_voiced_notes(self):
        score = {
            "lyrics": "a b",
            "frame_seconds": 0.02,
            "duration_seconds": 0.6,
            "notes": [
                {
                    "midi": 57,
                    "start_seconds": 0.0,
                    "duration_seconds": 0.6,
                }
            ],
            "f0_hz": (
                [220.0] * 10
                + [233.08] * 3
                + [220.0] * 9
                + [246.94] * 8
            ),
        }
        metadata, _ = build_direct_target_metadata(
            score, "English", self._identity_g2p
        )
        segment = metadata[0]
        durations = [
            float(value) for value in segment["duration"].split()
        ]
        pitches = [
            int(value) for value in segment["note_pitch"].split()
        ]
        self.assertTrue(
            all(
                duration >= 0.08
                for duration, pitch in zip(durations, pitches)
                if pitch > 0
            )
        )

    def test_voiced_time_alignment_preserves_every_lyric_unit(self):
        score = {
            "lyrics": "abcd",
            "duration_seconds": 2.0,
            "frame_seconds": 0.02,
            "notes": [
                {
                    "midi": 57,
                    "start_seconds": 0.0,
                    "duration_seconds": 2.0,
                }
            ],
            "f0_hz": [220.0] * 100,
        }
        metadata, _ = build_direct_target_metadata(
            score,
            "English",
            self._identity_g2p,
        )
        segment = metadata[0]
        self.assertEqual(
            segment["alignment_method"], "voiced-time-note-boundaries"
        )
        # English tokenisation treats this as one word; use spaces to create
        # four independently aligned lyric units.
        score["lyrics"] = "a b c d"
        metadata, _ = build_direct_target_metadata(
            score,
            "English",
            self._identity_g2p,
        )
        text = metadata[0]["text"].split()
        for word in ("a", "b", "c", "d"):
            self.assertIn(word, text)
        durations = [
            float(value) for value in metadata[0]["duration"].split()
        ]
        self.assertTrue(all(value >= 0.49 for value in durations))

    def test_acoustic_word_timestamps_correct_singing_asr_lyrics(self):
        score = {
            "lyrics": "时光寸不断流转在从前刻骨的变迁不是遥远",
            "duration_seconds": 4.8,
            "frame_seconds": 0.02,
            "notes": [
                {
                    "midi": 57,
                    "start_seconds": 0.0,
                    "duration_seconds": 4.8,
                }
            ],
            "f0_hz": [220.0] * 240,
        }
        words = [
            "<SP>", "时", "光", "穿", "穿", "不", "断", "断",
            "流", "转", "在", "从", "前", "刻", "骨", "的", "的",
            "变", "迁", "不", "是", "遥", "远", "<SP>",
        ]
        metadata, _ = build_direct_target_metadata(
            score,
            "Mandarin",
            self._identity_g2p,
            {
                "words": words,
                "durations": [0.2] * len(words),
            },
        )
        segment = metadata[0]
        self.assertEqual(
            segment["alignment_method"], "acoustic-word-timestamps"
        )
        self.assertEqual(
            segment["resolved_lyrics"],
            "时光穿不断流转在从前刻骨的变迁不是遥远",
        )
        self.assertEqual(segment["lyrics_source"], "singing-asr")
        self.assertIn("穿 穿", segment["text"])

    def test_explicit_lyrics_override_wins_over_acoustic_transcript(self):
        score = {
            "lyrics": "你好",
            "lyrics_override": True,
            "duration_seconds": 1.0,
            "frame_seconds": 0.02,
            "notes": [
                {
                    "midi": 57,
                    "start_seconds": 0.0,
                    "duration_seconds": 1.0,
                }
            ],
            "f0_hz": [220.0] * 50,
        }
        metadata, _ = build_direct_target_metadata(
            score,
            "Mandarin",
            self._identity_g2p,
            {"words": ["你", "号"], "durations": [0.5, 0.5]},
        )
        self.assertEqual(metadata[0]["resolved_lyrics"], "你好")
        self.assertEqual(
            metadata[0]["lyrics_source"], "explicit-lyrics-override"
        )

    def test_english_lyrics_are_word_tokens(self):
        self.assertEqual(
            lyric_units("One last kiss, please!", "English"),
            ["One", "last", "kiss", "please"],
        )

    def test_existing_prompt_sidecar_is_imported_into_runtime_cache(self):
        metadata = [
            {
                "language": "English",
                "duration": "1.0",
                "phoneme": "en_T-EH1-S-T",
                "note_pitch": "60",
                "note_type": "2",
                "f0": "220",
            }
        ]
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            prompt = root / "voice.wav"
            prompt.write_bytes(b"RIFF test prompt")
            prompt.with_suffix(".json").write_text(
                json.dumps(metadata),
                encoding="utf-8",
            )

            def unexpected_pipeline(*_args):
                self.fail("valid prompt sidecar should avoid preprocessing")

            cached, status, pipeline = prepare_prompt_metadata(
                prompt,
                "English",
                root / "cache",
                root / "work",
                unexpected_pipeline,
            )

            self.assertEqual(status, "imported-sidecar")
            self.assertIsNone(pipeline)
            self.assertTrue(cached.is_file())


if __name__ == "__main__":
    unittest.main()
