"""Known weak-text acoustic summaries and nearby speech/humming controls.

Historical final-success responses omitted raw VAD fields. Thus historical
false/0 client logs are not ground-truth VAD; false-VAD variants below are
explicit controlled fixtures, not claimed acoustic replay of the original WAV.
"""
import unittest
import sensevoice_server as server
import test_speaker_failure_isolation as pipeline_fixture


def analysis(seconds=3.07, probability=.489, voiced=.398, stability=.501, sustained=0.0):
    return dict(analysis_available=True, is_singing=True, duration=seconds,
                singing_probability=probability, voiced_ratio=voiced,
                pitch_stability=stability, sustained_ratio=sustained,
                periodicity_mean=.7, pitch_median_hz=220.0,
                singing_start_seconds=0.0, singing_end_seconds=seconds,
                pitch_timeline_frame_seconds=.1, pitch_timeline_midi=[60] * round(seconds * 10))


class WeakTonalEvidenceTests(unittest.TestCase):
    def fixture(self):
        f = pipeline_fixture.SpeakerFailureIsolationTests(); f.setUp()
        f.store.identify_only.return_value = dict(speaker_id="fixture-guest", speaker_kind="guest", speaker_status="candidate")
        return f

    def test_c11_summary_with_controlled_absent_vad_is_evidence_not_new_words(self):
        f = self.fixture(); a = analysis()
        self.assertFalse(server.is_expected_singing_performance(a))
        result, _, wav = f.final(3.07, text="我.", emotion="EMO_UNKNOWN", speech=False, analysis=a, expect_singing=True)
        self.assertEqual(result["input_status"], "evidence_only")
        self.assertFalse(result["no_speech"], "Uncertain acoustic evidence is not proof of silence")
        self.assertEqual(result["text"], "")
        self.assertEqual(result["input_evidence_text"], "我.")
        self.assertEqual(result["input_evidence_reason"], "weak-tonal-without-speech")
        self.assertTrue(result["raw_vad_observed"])
        self.assertFalse(result["raw_vad_has_speech"])
        self.assertEqual(result["pitch_timeline_midi"], a["pitch_timeline_midi"])
        self.assertEqual(result["singing_end_seconds"], 3.07)
        self.assertEqual(wav.size, 49120)
        self.assertFalse(result["speaker_learning_allowed"])
        f.store.identify_and_learn.assert_not_called()

    def test_c15_short_summary_is_retained_with_or_without_expectation(self):
        for expected in [False, True]:
            f = self.fixture(); a = analysis(2.02, .491, .447, .55)
            self.assertFalse(server.is_expected_singing_performance(a))
            result, _, _ = f.final(2.02, text=".", emotion="EMO_UNKNOWN", speech=False, analysis=a, expect_singing=expected)
            self.assertEqual(result["input_status"], "evidence_only")
            self.assertEqual(result["text"], "")
            self.assertEqual(result["input_evidence_text"], ".")
            f.store.identify_and_learn.assert_not_called()

    def test_short_neutral_real_speech_is_not_a_length_keyword_filter(self):
        f = self.fixture()
        result, _, _ = f.final(text="はい。", emotion="NEUTRAL", speech=True, analysis=analysis(), expect_singing=True)
        self.assertEqual(result["input_status"], "accepted")
        self.assertEqual(result["text"], "はい。")
        self.assertTrue(result["raw_vad_has_speech"])

    def test_strong_wordless_humming_retains_existing_accepted_path(self):
        f = self.fixture(); a = analysis(5.0, .65, .75, .8, .5)
        self.assertTrue(server.is_expected_singing_performance(a))
        result, _, _ = f.final(5.0, text="", emotion="EMO_UNKNOWN", speech=False, analysis=a, expect_singing=True)
        self.assertEqual(result["input_status"], "accepted")
        self.assertTrue(result["is_singing"])
        self.assertFalse(result["raw_vad_has_speech"])
        self.assertNotIn("input_evidence_reason", result)

    def test_unknown_short_speech_with_real_vad_does_not_enter_new_tonal_rule(self):
        f = self.fixture()
        result, _, _ = f.final(text="我.", emotion="EMO_UNKNOWN", speech=True, analysis=analysis(), expect_singing=True)
        self.assertEqual(result["input_status"], "accepted")
        self.assertTrue(result["raw_vad_has_speech"])

    def test_full_linguistic_request_and_tail_are_not_truncated(self):
        f = self.fixture(); text = "前面是刚才的歌声，请把刚才的录音回唱给我听。"
        result, _, _ = f.final(7.0, text=text, emotion="NEUTRAL", speech=False, analysis=analysis(7), expect_singing=True)
        self.assertEqual(result["input_status"], "accepted")
        self.assertEqual(result["text"], text)

    def test_clearly_rejected_non_tonal_noise_remains_no_speech(self):
        f = self.fixture()
        result, _, _ = f.final(text=".", emotion="EMO_UNKNOWN", speech=False)
        self.assertEqual(result["input_status"], "no_speech")
        self.assertEqual(result["no_speech_reason"], "vad-insufficient-speech")


if __name__ == "__main__":
    unittest.main()
