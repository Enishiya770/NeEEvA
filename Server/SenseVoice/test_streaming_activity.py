import unittest

import numpy as np

from singing_analysis import SingingAnalyzer
from streaming_activity import recent_activity_fields


class StreamingActivityTests(unittest.TestCase):
    def setUp(self):
        self.analyzer = SingingAnalyzer(enable_torchcrepe=False)

    def tone(self, seconds=2.0):
        t = np.arange(int(16000 * seconds), dtype=np.float32) / 16000
        return .012 * np.sin(2 * np.pi * 220 * t)

    def test_soft_sustained_note_retains_recent_acoustic_evidence(self):
        result = recent_activity_fields(self.tone(), self.analyzer)
        self.assertEqual(result["activity_schema"], 2)
        self.assertEqual(result["activity_window_ms"], 800)
        self.assertGreater(result["activity_rms"], .008)
        self.assertLess(result["activity_rms"], .01)
        self.assertGreater(result["activity_periodicity"], .7)
        self.assertGreater(result["activity_voiced_ratio"], .8)

    def test_old_melody_does_not_mask_current_silence(self):
        wav = np.concatenate((self.tone(7), np.zeros(16000, dtype=np.float32)))
        result = recent_activity_fields(wav, self.analyzer)
        self.assertEqual(result["activity_rms"], 0)
        self.assertEqual(result["activity_voiced_ratio"], 0)

    def test_noise_is_not_a_sustained_note(self):
        wav = np.random.default_rng(123).normal(0, .005, 16000).astype(np.float32)
        result = recent_activity_fields(wav, self.analyzer)
        self.assertLess(result["activity_voiced_ratio"], .6)

    def test_vad_endpoint_uses_audio_time_not_asr_revision_time(self):
        def probe(tail, min_speech_ms):
            self.assertEqual(len(tail), 12800)
            self.assertEqual(min_speech_ms, 120)
            return True, 500, [[100, 600]], .01
        result = recent_activity_fields(self.tone(8), self.analyzer, vad_probe=probe)
        self.assertTrue(result["activity_vad_available"])
        self.assertEqual(result["activity_speech_ms"], 500)
        self.assertEqual(result["activity_speech_end_age_ms"], 200)

    def test_room_noise_and_unavailable_vad_do_not_claim_voice(self):
        noise = np.random.default_rng(2).normal(0, .008, 16000).astype(np.float32)
        result = recent_activity_fields(noise, self.analyzer,
                                        vad_probe=lambda *a, **k: (False, 0, [], .01))
        self.assertTrue(result["activity_vad_available"])
        self.assertEqual(result["activity_speech_ms"], 0)
        self.assertEqual(result["activity_speech_end_age_ms"], -1)
        def fail(*a, **k):
            raise RuntimeError("test probe unavailable")
        result = recent_activity_fields(noise, self.analyzer, vad_probe=fail)
        self.assertFalse(result["activity_vad_available"])
        self.assertEqual(result["activity_speech_ms"], 0)

    def test_short_empty_invalid_and_missing_analyzer_are_safe(self):
        for wav in ([], [np.nan], [np.inf], np.zeros(100), np.ones(16000) * .004):
            result = recent_activity_fields(wav, self.analyzer)
            self.assertTrue(all(np.isfinite(value) for value in result.values()))
            self.assertEqual(result["activity_voiced_ratio"], 0)
        result = recent_activity_fields(self.tone(), None)
        self.assertGreater(result["activity_rms"], 0)
        self.assertEqual(result["activity_voiced_ratio"], 0)


if __name__ == "__main__":
    unittest.main()
