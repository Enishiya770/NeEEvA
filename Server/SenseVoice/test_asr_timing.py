import unittest
from unittest.mock import patch
import numpy as np
from asr_timing import AsrTiming
from singing_analysis import SingingAnalyzer, _MAX_ANALYSIS_SECONDS


class AsrTimingTests(unittest.TestCase):
    def test_stages_include_pitch_and_have_a_stable_total(self):
        clock = iter([10.0, 10.2, 11.7, 12.0])
        timer = AsrTiming(lambda: next(clock))
        timer.mark("recognizer")
        timer.mark("full_pitch")
        result = timer.finish()
        self.assertEqual(result["recognizer"], 0.2)
        self.assertEqual(result["full_pitch"], 1.5)
        self.assertEqual(result["total"], 2.0)
        self.assertEqual(result["other"], 0.3)
        self.assertEqual(timer.finish(), result)
        result["total"] = 0
        self.assertEqual(timer.finish()["total"], 2.0)

    def test_only_final_pitch_backend_builds_score(self):
        class FakeCrepe(SingingAnalyzer):
            def _track_crepe(self, signal):
                return self._track_fft(signal)

        signal = (0.1 * np.sin(np.arange(32000) * 2 * np.pi * 180 / 16000)).astype(np.float32)
        for analyzer in (SingingAnalyzer(enable_torchcrepe=False), FakeCrepe()):
            with patch("singing_analysis.build_singing_score", return_value={"test": True}) as build:
                result = analyzer.analyze(signal, thorough=True, force_score=True)
                self.assertEqual(build.call_count, 1)
                self.assertEqual(result["singing_score"], {"test": True})

    def test_probability_probe_keeps_evidence_without_building_unused_score(self):
        signal = (0.1 * np.sin(np.arange(32000) * 2 * np.pi * 180 / 16000)).astype(np.float32)
        analyzer = SingingAnalyzer(enable_torchcrepe=False)
        expected = analyzer.analyze(signal, thorough=True, force_score=True)
        with patch("singing_analysis.build_singing_score") as build:
            actual = analyzer.analyze(signal, thorough=True, force_score=True, build_score=False)
        self.assertEqual(build.call_count, 0)
        self.assertEqual(actual["singing_probability"], expected["singing_probability"])
        self.assertEqual(actual["singing_start_seconds"], expected["singing_start_seconds"])
        self.assertEqual(actual["singing_end_seconds"], expected["singing_end_seconds"])
        self.assertNotIn("singing_score", actual)

    def test_analysis_window_preserves_complete_sixty_second_take(self):
        seconds = 61.0
        samples = int(seconds * SingingAnalyzer.sample_rate)
        phase = np.arange(samples, dtype=np.float32)
        signal = (0.1 * np.sin(
            phase * 2 * np.pi * 180 / SingingAnalyzer.sample_rate
        )).astype(np.float32)

        result = SingingAnalyzer(enable_torchcrepe=False).analyze(
            signal, thorough=False
        )

        self.assertGreaterEqual(_MAX_ANALYSIS_SECONDS, 60.0)
        self.assertEqual(result["analysis_window_offset_seconds"], 0.0)
        self.assertAlmostEqual(result["duration"], seconds, places=3)
        self.assertLess(result["singing_start_seconds"], 0.5)
        self.assertAlmostEqual(result["singing_end_seconds"], seconds, places=3)
        self.assertGreaterEqual(
            len(result["pitch_timeline_midi"]),
            int(seconds / result["pitch_timeline_frame_seconds"]) - 2,
        )

    def test_timeline_cap_uses_same_analysis_window(self):
        frame_seconds = 0.10
        source_frames = int((_MAX_ANALYSIS_SECONDS + 5.0) / frame_seconds)
        timeline = SingingAnalyzer._build_timeline(
            np.full(source_frames, 60.0, dtype=np.float32),
            np.ones(source_frames, dtype=bool),
            frame_seconds,
            frame_seconds,
        )
        self.assertEqual(
            len(timeline),
            int(round(_MAX_ANALYSIS_SECONDS / frame_seconds)),
        )


if __name__ == "__main__":
    unittest.main()
