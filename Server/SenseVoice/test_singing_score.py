import unittest

import numpy as np

from singing_analysis import SingingAnalyzer
from singing_score import build_singing_score
from japanese_lyrics import normalise_japanese_lyrics, split_japanese_mora


class SingingScoreTests(unittest.TestCase):
    def test_identical_analysis_is_stable_and_cache_isolation_is_deep(self):
        class CountingAnalyzer(SingingAnalyzer):
            def __init__(self):
                super().__init__(enable_torchcrepe=False)
                self.calls = 0

            def _analyze(
                self, wav, lyrics, audio_event, thorough, language,
                force_score, build_score,
            ):
                self.calls += 1
                return {"call": self.calls, "nested": {"values": [1, 2]}}

        analyzer = CountingAnalyzer()
        signal = np.linspace(-0.1, 0.1, 3200, dtype=np.float32)
        first = analyzer.analyze(signal, lyrics="same", thorough=True)
        first["nested"]["values"].append(99)
        second = analyzer.analyze(signal.copy(), lyrics="same", thorough=True)

        self.assertEqual(analyzer.calls, 1)
        self.assertEqual(second["nested"]["values"], [1, 2])
        analyzer.analyze(signal, lyrics="different", thorough=True)
        self.assertEqual(analyzer.calls, 2)

    def test_japanese_kana_is_normalised_without_second_asr(self):
        result = normalise_japanese_lyrics("キミノコトガ、スキ。")
        self.assertEqual(result["lyrics_reading"], "きみのことがすき")
        self.assertEqual(
            result["lyrics_mora"],
            ["き", "み", "の", "こ", "と", "が", "す", "き"],
        )
        self.assertEqual(result["lyrics_reading_source"], "sensevoice-kana")
        self.assertTrue(result["lyrics_reading_complete"])

    def test_japanese_mora_rules_keep_timing_information(self):
        self.assertEqual(
            split_japanese_mora("きょうって"),
            ["きょ", "う", "っ", "て"],
        )

    def test_sensevoice_kanji_transcript_gets_kana_pronunciation(self):
        result = normalise_japanese_lyrics("君のことが好き")
        self.assertEqual(result["lyrics_reading"], "きみのことがすき")
        self.assertEqual(result["lyrics_reading_source"], "sensevoice+pyopenjtalk")
        self.assertTrue(result["lyrics_reading_complete"])

    def test_non_kana_tokens_are_not_silently_sent_to_renderer(self):
        result = normalise_japanese_lyrics("きみ Lemon")
        self.assertFalse(result["lyrics_reading_complete"])
        self.assertEqual(result["lyrics_reading"], "")

    def test_japanese_score_preserves_semantic_lyrics_and_adds_kana_view(self):
        score = build_singing_score(
            np.full(20, 220.0, dtype=np.float32),
            np.full(20, 0.9, dtype=np.float32),
            0.01,
            0.2,
            lyrics="キミガスキ",
            language="ja",
        )
        self.assertEqual(score["lyrics"], "キミガスキ")
        self.assertEqual(score["lyrics_reading"], "きみがすき")
        self.assertEqual(score["lyrics_mora"], ["き", "み", "が", "す", "き"])

    def test_score_contains_timed_notes_rests_and_expression(self):
        hop = 0.01
        pitch = np.concatenate(
            (
                np.full(50, 440.0, dtype=np.float32),
                np.zeros(20, dtype=np.float32),
                np.full(60, 493.88, dtype=np.float32),
            )
        )
        periodicity = np.where(pitch > 0, 0.92, 0.0).astype(np.float32)
        samples = np.arange(int(1.3 * 16000), dtype=np.float32)
        signal = 0.2 * np.sin(2.0 * np.pi * 440.0 * samples / 16000.0)

        score = build_singing_score(
            pitch,
            periodicity,
            hop,
            1.3,
            lyrics="la la",
            language="en",
            signal=signal,
            extractor_backend="test",
            confidence=0.9,
        )

        self.assertEqual(score["schema_version"], 1)
        self.assertEqual(score["language"], "en")
        self.assertEqual(len(score["f0_hz"]), len(pitch))
        self.assertEqual(len(score["energy"]), len(pitch))
        self.assertTrue(any(note["note_type"] == "rest" for note in score["notes"]))
        sung = [note for note in score["notes"] if note["note_type"] == "note"]
        self.assertEqual(sung[0]["midi"], 69)
        self.assertEqual(sung[-1]["midi"], 71)
        self.assertTrue(score["breath_positions_seconds"])

    def test_thorough_analysis_attaches_score_but_fast_probe_does_not(self):
        sample_rate = 16000
        time = np.arange(sample_rate * 2, dtype=np.float32) / sample_rate
        signal = (0.18 * np.sin(2.0 * np.pi * 220.0 * time)).astype(np.float32)
        analyzer = SingingAnalyzer(enable_torchcrepe=False)

        fast = analyzer.analyze(signal, thorough=False)
        thorough = analyzer.analyze(
            signal,
            lyrics="啊",
            language="zh",
            thorough=True,
        )

        self.assertNotIn("singing_score", fast)
        self.assertEqual(thorough["singing_score"]["schema_version"], 1)
        self.assertEqual(thorough["singing_score"]["language"], "zh")
        self.assertGreater(len(thorough["singing_score"]["notes"]), 0)

    def test_recovery_envelope_keeps_adjacent_low_confidence_melodic_island(self):
        hop = 0.01
        duration = 12.0
        frames = int(duration / hop)
        smoothed_midi = np.full(frames, 60.0, dtype=np.float32)
        voiced = np.zeros(frames, dtype=bool)
        periodicity = np.zeros(frames, dtype=np.float32)
        # 两座相邻旋律岛：第一座单独复核时低于 clean 门槛，第二座是主锚。
        # 默认边界应只采用主锚；可选恢复边界应保留前一座，避免素材永久丢失。
        for begin, end in ((50, 300), (390, 760)):
            voiced[begin:end] = True
            periodicity[begin:end] = 0.9

        def island_probability(_start, end):
            return 0.4 if end < 4.0 else 0.8

        clean_start, clean_end, recovery_start, recovery_end = (
            SingingAnalyzer._estimate_singing_start(
                smoothed_midi,
                voiced,
                periodicity,
                hop,
                duration,
                island_probability,
            )
        )

        self.assertGreater(clean_start, 3.0)
        self.assertLess(recovery_start, 0.5)
        self.assertAlmostEqual(recovery_end, clean_end, places=2)

    def test_one_minute_take_keeps_its_opening_in_analysis_and_timeline(self):
        sample_rate = 16000
        time = np.arange(sample_rate * 60, dtype=np.float32) / sample_rate
        signal = (0.12 * np.sin(2.0 * np.pi * 220.0 * time)).astype(np.float32)
        analyzer = SingingAnalyzer(enable_torchcrepe=False)

        result = analyzer.analyze(signal, thorough=False)

        self.assertEqual(result["analysis_window_offset_seconds"], 0.0)
        self.assertLess(result["singing_start_seconds"], 1.0)
        # 0.10-second playable frames: a complete minute must not regress to
        # the old 45-second/450-frame tail-only timeline.
        self.assertGreaterEqual(len(result["pitch_timeline_midi"]), 590)


if __name__ == "__main__":
    unittest.main()
