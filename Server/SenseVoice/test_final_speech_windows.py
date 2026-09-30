"""Guard final speech context without changing music or streaming decode paths."""
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from final_speech_windows import plan_speech_windows, speech_context_reason


def ordinary_analysis(duration=15.79):
    return dict(analysis_available=True, is_singing=False, duration=duration,
                analysis_window_offset_seconds=0.0, singing_probability=.419,
                sustained_ratio=.010, pitch_stability=.435,
                voiced_ratio=.652, periodicity_mean=.632)


class SpeechWindowTests(unittest.TestCase):
    def test_observed_speech_is_eligible_but_melodic_clip_is_not(self):
        self.assertEqual(speech_context_reason(ordinary_analysis(), 15.79,
                         vad_has_speech=True), "ordinary-speech-evidence")
        song = dict(ordinary_analysis(16.78), singing_probability=.553,
                    sustained_ratio=.165, pitch_stability=.589)
        self.assertEqual(speech_context_reason(song, 16.78, vad_has_speech=True),
                         "melodic-or-uncertain")

    def test_missing_invalid_or_tail_only_analysis_is_not_speech_evidence(self):
        for analysis in (None, {}, dict(ordinary_analysis(), singing_probability=float('nan')),
                         dict(ordinary_analysis(), pitch_stability=-1),
                         dict(ordinary_analysis(), analysis_window_offset_seconds=2),
                         dict(ordinary_analysis(), duration=10),
                         dict(ordinary_analysis(), is_singing=True)):
            with self.subTest(analysis=analysis):
                self.assertNotEqual(speech_context_reason(analysis, 15.79, vad_has_speech=True),
                                    "ordinary-speech-evidence")

    def test_singing_intent_or_no_real_speech_vad_keeps_original_policy(self):
        for flags in (dict(vad_has_speech=False),
                      dict(vad_has_speech=True, expect_singing=True),
                      dict(vad_has_speech=True, singing_vad_override=True)):
            self.assertNotEqual(speech_context_reason(ordinary_analysis(), 15.79, **flags),
                                "ordinary-speech-evidence")

    def test_idiom_retains_context_in_one_bounded_pass(self):
        samples = round(15.79 * 16000)
        self.assertEqual(plan_speech_windows(samples, [[0, 15790]]), [(0, samples)])

    def test_pause_coordinates_account_for_outer_silence_trim(self):
        # In original upload the pause is 17..17.4s. After a 2s trim it is 15..15.4s.
        plan = plan_speech_windows(30 * 16000, [[2100, 17000], [17400, 32000]], 2)
        self.assertEqual(plan[0], (0, int(15.2 * 16000)))
        self.assertEqual(plan[1][0], int(14.4 * 16000))
        self.assertEqual(plan[-1][1], 30 * 16000)

    def test_overlapping_vad_does_not_create_a_false_pause(self):
        plan = plan_speech_windows(30 * 16000, [[0, 16000], [15000, 17000], [16900, 30000]])
        self.assertEqual(plan[0], (0, 9 * 16000))

    def test_invalid_vad_falls_back(self):
        for segments in (None, [], [[1000, 0]], [[0, float('nan')]], [[0]], [[0, -3]]):
            self.assertIsNone(plan_speech_windows(20 * 16000, segments))
        self.assertIsNone(plan_speech_windows(20 * 16000, [[0, 20000]], float('nan')))

    def test_long_no_pause_audio_is_covered_without_gaps_or_unbounded_windows(self):
        for seconds in (18.001, 19, 30, 65, 300):
            total = round(seconds * 16000)
            plan = plan_speech_windows(total, [[0, seconds * 1000]])
            self.assertEqual(plan[0][0], 0)
            self.assertEqual(plan[-1][1], total)
            for index, (start, end) in enumerate(plan):
                self.assertGreater(end, start)
                self.assertLessEqual(end - start, 18 * 16000)
                if index:
                    self.assertEqual(start, plan[index - 1][1] - int(.8 * 16000))


class FinalRoutingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import numpy as np
        import sensevoice_server as server
        cls.np, cls.server = np, server

    def test_final_uses_one_decode_not_a_correction_second_pass(self):
        wav = self.np.zeros(round(15.79 * 16000), self.np.float32)
        with patch.object(self.server, '_generate_asr_once', return_value=[{'text': '识时务者为俊杰'}]) as decode:
            result = self.server.generate_final_asr(wav, quick_analysis=ordinary_analysis(),
                raw_duration=15.79, vad_has_speech=True, vad_segments=[[0, 15790]])
            self.assertEqual(decode.call_count, 1)
            self.assertEqual(decode.call_args[0][0].size, wav.size)
            self.assertEqual(result[0]['text'], '识时务者为俊杰')

    def test_http_final_passes_raw_probe_and_trimmed_audio_to_one_decode(self):
        server = self.server
        raw = self.np.ones(18 * 16000, self.np.float32) * .05
        analysis = ordinary_analysis(18)
        with patch.object(server, 'decode_wav', return_value=raw), \
             patch.object(server, 'run_vad', return_value=(True, 14000, [[2000, 16000]], 0)), \
             patch.object(server, 'identify_speaker', return_value=({}, 0, None)), \
             patch.object(server, 'commit_speaker_learning', return_value={}), \
             patch.object(server, '_singing_analyzer', SimpleNamespace(analyze=lambda *a, **k: dict(analysis))), \
             patch.object(server, '_generate_asr_once', return_value=[{
                 'text': '<|zh|><|NEUTRAL|><|Speech|>识时务者为俊杰。'}]) as decode, \
             patch.object(server, 'dump_band_sample'), patch.object(server, 'dump_mixed_sample'):
            result = server.recognize_final_audio(b'fixture', language='auto', learn_speaker=False)
        self.assertEqual(result['text'], '识时务者为俊杰。')
        self.assertEqual(result['audio_content_start_seconds'], 1.88)
        self.assertEqual(decode.call_count, 1)
        self.assertEqual(decode.call_args[0][0].size, int(14.24 * 16000))
        self.assertEqual(result['singing_text'], '')

    def test_default_and_singing_callers_keep_legacy_window_bounds(self):
        wav = self.np.zeros(round(15.79 * 16000), self.np.float32)
        for call in (lambda: self.server.generate_asr(wav),
                     lambda: self.server.generate_final_asr(wav, quick_analysis=ordinary_analysis(),
                        raw_duration=15.79, vad_has_speech=True, vad_segments=[[0, 15790]],
                        expect_singing=True),
                     lambda: self.server.generate_final_asr(wav)):
            sizes = []
            def decode(piece, language):
                sizes.append(piece.size)
                return [{'text': '<|zh|><|NEUTRAL|><|Speech|><|withitn|>保持原路径。'}]
            with patch.object(self.server, '_generate_asr_once', side_effect=decode):
                call()
            self.assertEqual(sizes, [9 * 16000, round(7.59 * 16000)])

    def test_short_final_and_streaming_do_not_consult_new_window_planner(self):
        wav = self.np.zeros(6 * 16000, self.np.float32)
        with patch.object(self.server, 'plan_speech_windows', side_effect=AssertionError('unexpected plan')), \
             patch.object(self.server, '_generate_asr_once', return_value=[]):
            self.server.generate_final_asr(wav)
            self.server.generate_asr(wav)


if __name__ == '__main__':
    unittest.main()
