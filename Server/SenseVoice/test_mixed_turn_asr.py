import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from types import SimpleNamespace

import numpy as np
from mixed_turn_asr import RATE, plan_segments, transcribe_segments, dump_mixed_sample


class MixedTurnTests(unittest.TestCase):
    def setUp(self):
        self.wav = np.arange(20 * RATE, dtype=np.float32) / RATE
        self.analysis = dict(analysis_available=True, is_singing=False,
                             singing_start_seconds=3, singing_end_seconds=14,
                             singing_recovery_start_seconds=1,
                             singing_recovery_end_seconds=18)

    def test_speech_verdict_cannot_suppress_island_or_full_tail(self):
        plan = plan_segments(self.wav, self.analysis)
        self.assertEqual(plan[0]["start_seconds"], 0)
        self.assertEqual(plan[-1]["end_seconds"], 20)
        for left, right in zip(plan, plan[1:]):
            self.assertEqual(left["end_seconds"], right["start_seconds"])
        self.assertTrue(any(s["region"] == "island" for s in plan))
        self.assertTrue(any(s["start_seconds"] == 14 for s in plan))
        self.assertFalse(any(s["start_seconds"] == 18 for s in plan))
        self.assertTrue(all(s["end_seconds"] - s["start_seconds"] <= 8 for s in plan))

    def test_each_segment_is_independent_and_whole_cannot_overwrite_tail(self):
        def recognize(piece):
            time = float(piece[0])
            return dict(text="请把这次唱出来" if time >= 14 else
                        "忘れたくないこと" if 3 <= time < 14 else "我继续唱",
                        language="ja" if 3 <= time < 14 else "zh")
        result = transcribe_segments(self.wav, self.analysis, recognize, "我继续唱轮")
        self.assertTrue(result["text"].endswith("请把这次唱出来"))
        self.assertIn("忘れたくないこと", result["text"])
        self.assertEqual(result["whole_text"], "我继续唱轮")
        self.assertEqual(result["transcript_source"], "segments")
        self.assertFalse(self.analysis["is_singing"])

    def test_failed_tail_retains_observable_failure_and_whole_fallback(self):
        def recognize(piece):
            if piece[0] >= 14:
                raise RuntimeError("decoder unavailable")
            return dict(text="歌词", language="zh")
        result = transcribe_segments(self.wav, self.analysis, recognize, "歌词 请再唱")
        self.assertFalse(result["turn_segments_complete"])
        self.assertEqual(result["turn_segments"][-1]["status"], "failed")
        self.assertEqual(result["whole_text"], "歌词 请再唱")
        self.assertNotIn("RuntimeError", result["text"])

    def test_overlap_is_alternative_not_duplicate_primary(self):
        count = 0
        def recognize(piece):
            nonlocal count
            count += 1
            return dict(text="主文本" if count % 2 else "边界备选", language="auto")
        result = transcribe_segments(self.wav, self.analysis, recognize)
        self.assertNotIn("边界备选", result["text"])
        self.assertTrue(result["turn_segments_review_required"])
        self.assertEqual(result["turn_segments"][0]["alternatives"][0]["text"], "边界备选")

    def test_pure_turn_uses_existing_path(self):
        self.analysis.update(singing_start_seconds=0, singing_end_seconds=4)
        self.assertEqual(plan_segments(self.wav[:4 * RATE], self.analysis), [])

    def test_missing_island_still_gets_bounded_unknown_windows(self):
        plan = plan_segments(self.wav, {})
        self.assertGreater(len(plan), 1)
        self.assertTrue(all(s["region"] == "unresolved" for s in plan))
        self.assertEqual(plan[-1]["end_seconds"], 20)

    def test_cancellation_does_not_become_missing_transcript(self):
        from asr_scheduler import AsrCancelled
        def cancelled(piece):
            raise AsrCancelled()
        with self.assertRaises(AsrCancelled):
            transcribe_segments(self.wav, self.analysis, cancelled)

    def test_short_spoken_tail_is_preserved(self):
        self.analysis.update(singing_end_seconds=19.4, singing_recovery_end_seconds=19.4)
        plan = plan_segments(self.wav, self.analysis)
        self.assertAlmostEqual(plan[-1]["end_seconds"] - plan[-1]["start_seconds"], .6)

    def test_internal_phrase_transition_is_not_merged_into_one_decode(self):
        self.analysis.update(singing_start_seconds=5.63, singing_end_seconds=13.07,
                             asr_boundary_candidates=[9.9])
        plan = plan_segments(self.wav, self.analysis)
        self.assertTrue(any(s["end_seconds"] == 9.9 for s in plan))
        self.assertTrue(any(s["start_seconds"] == 9.9 for s in plan))

    def test_corroborated_full_speech_keeps_boundary_words(self):
        plan = plan_segments(self.wav, self.analysis)
        texts = ["我是指我刚才唱的第三", "你有收到吗", "收到的话就唱给我听", "没有的话跟我说"]
        replies = iter([dict(text=text, language="zh") for text in texts for _ in range(2)])
        whole = "我是指我刚才唱的第三段，你有收到吗？收到的话就唱给我听，没有的话跟我说。"
        self.assertEqual(len(plan), len(texts))
        result = transcribe_segments(self.wav, self.analysis, lambda p: next(replies), whole)
        self.assertEqual(result["text"], whole)
        self.assertEqual(result["transcript_source"], "whole_corroborated_by_segments")

    def test_repeated_lyrics_are_not_erased_by_single_whole_occurrence(self):
        result = transcribe_segments(self.wav, self.analysis,
                                     lambda p: dict(text="再唱一次", language="zh"), "再唱一次")
        self.assertEqual(result["transcript_source"], "segments")
        self.assertGreater(result["text"].count("再唱一次"), 1)

    def test_minor_short_speech_spelling_error_does_not_replace_whole(self):
        analysis = dict(analysis_available=True, singing_start_seconds=0, singing_end_seconds=2)
        result = transcribe_segments(self.wav[:4 * RATE], analysis,
            lambda p: dict(text="你有路道" if p[0] < 2 else "吗", language="zh"), "你有录到吗")
        self.assertEqual(result["text"], "你有录到吗")
        self.assertEqual(result["segmented_text"], "你有路道 吗")

    def test_recovery_boundary_does_not_cut_japanese_phrase_in_half(self):
        analysis = dict(analysis_available=True, singing_start_seconds=2.93,
                        singing_end_seconds=14.01, singing_recovery_start_seconds=2.93,
                        singing_recovery_end_seconds=11.5, asr_boundary_candidates=[9.02, 11.5])
        plan = plan_segments(self.wav[:int(14.01 * RATE)], analysis)
        self.assertTrue(any(s["start_seconds"] == 9.02 and s["end_seconds"] == 14.01 for s in plan))
        self.assertFalse(any(s["start_seconds"] == 11.5 for s in plan))

    def test_recovery_keeps_full_english_phrase_before_japanese_clean(self):
        analysis = dict(analysis_available=True, singing_start_seconds=8.63,
                        singing_end_seconds=11.57, singing_recovery_start_seconds=4.23,
                        singing_recovery_end_seconds=13.77, asr_boundary_candidates=[8.4, 12.1])
        plan = plan_segments(self.wav[:int(14.6 * RATE)], analysis)
        self.assertTrue(any(s["start_seconds"] == 4.23 and s["end_seconds"] == 8.63 for s in plan))
        self.assertTrue(any(s["start_seconds"] == 11.57 and s["end_seconds"] == 14.6 for s in plan))

    def test_short_island_offers_context_and_separate_tail_without_changing_crop(self):
        analysis = dict(analysis_available=True, singing_start_seconds=11.93,
                        singing_end_seconds=14.57, singing_recovery_start_seconds=5.73,
                        singing_recovery_end_seconds=16.27)
        def recognize(piece):
            start = float(piece[0])
            duration = len(piece) / RATE
            return dict(text="完整日语歌词" if 11.92 < start < 11.94 and duration > 4 else
                        "好轮到你唱了" if start > 16 else "短核心", language="ja")
        result = transcribe_segments(self.wav[:int(19.01 * RATE)], analysis, recognize)
        alternatives = [a for s in result["turn_segments"] for a in s["alternatives"]]
        self.assertTrue(any(a["source"] == "recovery_context" and a["text"] == "完整日语歌词" for a in alternatives))
        self.assertTrue(any(a["source"] == "recovery_tail" and a["text"] == "好轮到你唱了" for a in alternatives))
        self.assertEqual(analysis["singing_end_seconds"], 14.57)

    def test_internal_boundaries_use_full_audio_time_base(self):
        from singing_analysis import SingingAnalyzer
        shifted = SingingAnalyzer._apply_analysis_window_offset(
            dict(asr_boundary_candidates=[4.2, 9.9]), 12)
        self.assertEqual(shifted["asr_boundary_candidates"], [16.2, 21.9])

    def test_raw_and_content_diagnostics_are_deduplicated(self):
        result = transcribe_segments(self.wav, self.analysis, lambda p: dict(text="测试"))
        with tempfile.TemporaryDirectory() as directory:
            dump_mixed_sample(self.wav, self.wav[RATE:], result, directory)
            dump_mixed_sample(self.wav, self.wav[RATE:], result, directory)
            self.assertEqual(len(list(Path(directory).glob("*.raw.wav"))), 1)
            self.assertEqual(len(list(Path(directory).glob("*.content.wav"))), 1)
            self.assertEqual(len(list(Path(directory).glob("*.json"))), 1)

    def test_final_api_promotes_segments_and_preserves_speech_verdict(self):
        import sensevoice_server as server
        analysis = dict(self.analysis, singing_probability=.42, pitch_stability=.7)
        def generate(piece, language):
            if len(piece) == len(self.wav):
                return [{"text": "<|zh|><|NEUTRAL|><|Speech|>我继续唱轮"}]
            self.assertEqual(language, "auto")
            return [{"text": "<|zh|><|NEUTRAL|><|Speech|>" +
                    ("请把这次唱出来" if piece[0] >= 14 else "分段歌词")}]
        with patch.object(server, "decode_wav", return_value=self.wav), \
             patch.object(server, "run_vad", return_value=(True, 19000, [[0, 20000]], 0)), \
             patch.object(server, "trim_to_speech_with_offset", return_value=(self.wav, .25)), \
             patch.object(server, "identify_speaker", return_value=({}, 0, None)), \
             patch.object(server, "commit_speaker_learning", return_value={}), \
             patch.object(server, "_singing_analyzer", SimpleNamespace(analyze=lambda *a, **k: dict(analysis))), \
             patch.object(server, "generate_asr", side_effect=generate), \
             patch.object(server, "analyze_singing_boundary_extras", return_value={}), \
             patch.object(server, "build_song_recall", return_value={}), \
             patch.object(server, "dump_band_sample"), \
             patch.object(server, "dump_mixed_sample") as dump:
            result = server.recognize_final_audio(b"wav", language="ja", learn_speaker=False)
        self.assertEqual(result["whole_text"], "我继续唱轮")
        self.assertTrue(result["text"].endswith("请把这次唱出来"))
        self.assertTrue(result["singing_tail_text"].endswith("请把这次唱出来"))
        self.assertFalse(result["is_singing"])
        self.assertEqual(result["audio_content_start_seconds"], .25)
        self.assertEqual(result["turn_segments_schema"], 1)
        self.assertIs(dump.call_args.args[0], self.wav)


if __name__ == "__main__":
    unittest.main()
