import unittest
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sensevoice_server import (
    build_pitch_boundary_subsegments,
    merge_streaming_transcripts,
    summarize_boundary_subsegment_conflict,
)


class StreamingWindowMergeTests(unittest.TestCase):
    def test_deduplicates_cjk_overlap_ignoring_spacing_and_punctuation(self):
        committed = "あなたが焼きついたまま、私の心のプロジェクター"
        window = "私 の 心 の プロジェクター 寂しくないふりしてた。"
        merged = merge_streaming_transcripts(committed, window)
        self.assertEqual(merged.count("プロジェクター"), 1)
        self.assertIn("寂しくないふりしてた", merged)

    def test_keeps_all_text_when_boundary_has_no_reliable_overlap(self):
        merged = merge_streaming_transcripts("第一段歌词", "完全不同的第二段")
        self.assertIn("第一段歌词", merged)
        self.assertIn("完全不同的第二段", merged)

    def test_tolerates_one_character_revision_at_roll_boundary(self):
        committed = "誰かを求めることはすなわち傷つくことだった"
        window = "すなわち傷付くことだった燃えるようなキスをしよう"
        merged = merge_streaming_transcripts(committed, window)
        self.assertEqual(merged.count("すなわち"), 1)
        self.assertIn("燃えるようなキス", merged)

    def test_multiple_rolls_remain_cumulative(self):
        first = merge_streaming_transcripts("", "one two three four")
        second = merge_streaming_transcripts(first, "three four five six")
        third = merge_streaming_transcripts(second, "five six seven")
        self.assertEqual(third.count("three four"), 1)
        self.assertEqual(third.count("five six"), 1)
        self.assertTrue(third.endswith("seven"))


class SingingBoundarySubsegmentTests(unittest.TestCase):
    def test_long_mixed_head_exposes_internal_melodic_transition(self):
        # 0~2s has no reliable pitch; 2~5s is a stable melodic contour.
        analysis = {
            "pitch_timeline_midi": [0.0] * 20 + [60.0] * 30,
            "pitch_timeline_frame_seconds": 0.1,
            "pitch_timeline_start_seconds": 0.0,
        }
        segments = build_pitch_boundary_subsegments(analysis, 0.0, 5.0, 0.0)

        self.assertGreaterEqual(len(segments), 4)
        self.assertEqual(segments[0]["type"], "non_melodic")
        self.assertEqual(segments[-1]["type"], "melodic")
        self.assertEqual(segments[-1]["expanded_end_seconds"], 5.0)

    def test_offsets_are_relative_to_expanded_capture(self):
        analysis = {
            "pitch_timeline_midi": [0.0] * 10 + [64.0] * 20,
            "pitch_timeline_frame_seconds": 0.1,
            "pitch_timeline_start_seconds": 1.0,
        }
        segments = build_pitch_boundary_subsegments(analysis, 1.0, 3.0, 0.5)

        self.assertEqual(segments[0]["expanded_start_seconds"], 0.5)
        self.assertEqual(segments[-1]["expanded_end_seconds"], 2.5)

    def test_short_margin_does_not_manufacture_fine_windows(self):
        analysis = {
            "pitch_timeline_midi": [60.0] * 10,
            "pitch_timeline_frame_seconds": 0.1,
        }
        self.assertEqual(
            build_pitch_boundary_subsegments(analysis, 0.0, 0.8, 0.0), []
        )

    def test_long_melodic_run_inside_speech_margin_requires_role_review(self):
        segments = [
            {"start_seconds": 5.6, "end_seconds": 6.6, "type": "melodic"},
            {"start_seconds": 6.6, "end_seconds": 7.6, "type": "melodic"},
            {"start_seconds": 7.6, "end_seconds": 8.6, "type": "melodic"},
            {"start_seconds": 8.6, "end_seconds": 9.6, "type": "melodic"},
            {"start_seconds": 9.6, "end_seconds": 10.1, "type": "non_melodic"},
        ]
        summary = summarize_boundary_subsegment_conflict("speech", segments)

        self.assertTrue(summary["review_required"])
        self.assertAlmostEqual(summary["melodic_seconds"], 4.0)
        self.assertAlmostEqual(summary["longest_melodic_run_seconds"], 4.0)

    def test_short_melodic_blip_does_not_override_speech_margin(self):
        segments = [
            {"start_seconds": 0.0, "end_seconds": 1.0, "type": "non_melodic"},
            {"start_seconds": 1.0, "end_seconds": 2.0, "type": "melodic"},
            {"start_seconds": 2.0, "end_seconds": 3.0, "type": "non_melodic"},
        ]
        summary = summarize_boundary_subsegment_conflict("speech", segments)
        self.assertFalse(summary["review_required"])


if __name__ == "__main__":
    unittest.main()
