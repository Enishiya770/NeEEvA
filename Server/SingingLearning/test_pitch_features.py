"""Clock, causality, reset and bounded-memory contracts for pitch evidence."""
import copy
import importlib.util
import unittest
from unittest import mock

import numpy as np

from .pitch_features import (CausalPitchStream, FEATURE_NAMES, FFTProvider,
                             PitchBackpressure, PitchError, PestoProvider,
                             TrailingPitchAccumulator, build_trailing_features,
                             extract_track, provider_identity)


def tone(seconds=1., hz=220., *, level=.1):
    return (level * np.sin(2 * np.pi * hz * np.arange(round(seconds * 16000)) / 16000)).astype(np.float32)


class PitchContractTests(unittest.TestCase):
    def test_original_clock_and_no_future_tail_padding(self):
        stream = CausalPitchStream('clock')
        self.assertEqual(stream.push(tone(.039)), [])
        frames = stream.push(tone(.031))
        self.assertEqual([f['available_sample'] for f in frames], [640, 800, 960, 1120])
        self.assertEqual(frames[0]['frame_start_sample'], 0)
        self.assertEqual(frames[0]['center_sample'], 320.)
        self.assertEqual(frames[0]['frame_end_sample'], 640)
        self.assertEqual(stream.track(frames)['sample_count'], 1120)
        short = extract_track(tone(.039), stream_id='short')
        self.assertEqual(short['frames'], [])

    def test_arbitrary_packetization_is_identical(self):
        audio = np.r_[tone(.41), tone(.277, 330), np.zeros(507, dtype=np.float32)]
        expected = extract_track(audio, stream_id='same', chunk_samples=16000)
        for chunk in [1, 159, 160, 161, 317, 320, 1337]:
            with self.subTest(chunk=chunk):
                self.assertEqual(extract_track(audio, stream_id='same', chunk_samples=chunk), expected)

    def test_future_audio_cannot_change_prefix_frames_or_features(self):
        prefix = tone(.827)
        first = extract_track(np.r_[prefix, tone(.65, 110)], stream_id='a')
        second = extract_track(np.r_[prefix, np.ones(10400, dtype=np.float32) * -.45], stream_id='a')
        self.assertEqual([f for f in first['frames'] if f['available_sample'] <= len(prefix)],
                         [f for f in second['frames'] if f['available_sample'] <= len(prefix)])
        ends = [0, 639, 640, 5100, len(prefix)]
        np.testing.assert_array_equal(build_trailing_features(first, ends), build_trailing_features(second, ends))

    def test_reset_restores_recording_start_and_discards_history(self):
        stream = CausalPitchStream('one')
        stream.push(tone(.5, 440))
        with self.assertRaises(PitchError):
            stream.push(tone(.02), stream_id='two')
        stream.reset('two')
        frames = stream.push(tone(.7), stream_id='two', start_sample=0)
        self.assertEqual(stream.track(frames), extract_track(tone(.7), stream_id='two'))

    def test_bad_clock_or_pcm_never_advances(self):
        stream = CausalPitchStream('input')
        for audio in [np.array([np.nan], dtype=np.float32), np.array([np.inf], dtype=np.float32),
                      np.array([1.1], dtype=np.float32), np.array([4], dtype=np.int16), np.zeros((1, 2))]:
            with self.subTest(audio=audio):
                with self.assertRaises(PitchError):
                    stream.push(audio)
                self.assertEqual(stream.sample_count, 0)
        with self.assertRaises(PitchError):
            stream.push(tone(.02), start_sample=1)
        with self.assertRaises(PitchError):
            CausalPitchStream('rate', sample_rate=48000)
        self.assertEqual(stream.sample_count, 0)

    def test_backpressure_retry_is_explicit_and_lossless(self):
        stream = CausalPitchStream('limited', max_chunk_samples=320)
        with self.assertRaises(PitchBackpressure):
            stream.push(tone(.04))
        self.assertEqual(stream.sample_count, 0)
        audio = tone(.04)
        self.assertEqual(stream.push(audio[:320]), [])
        self.assertEqual(len(stream.push(audio[320:])), 1)

    def test_stream_memory_is_bounded(self):
        stream = CausalPitchStream('bounded')
        accumulator = TrailingPitchAccumulator(stream.metadata, stream_id='bounded')
        for _ in range(40):
            frames = stream.push(tone(.31))
            accumulator.push(frames, stream_id='bounded')
            accumulator.features_at(stream.sample_count)
            self.assertLess(stream.buffered_samples, stream.metadata['window_samples'])
            self.assertLessEqual(len(accumulator.frames), 100)

    def test_silence_dc_and_noise_are_not_voiced_or_singing(self):
        rng = np.random.default_rng(812)
        for audio in [np.zeros(16000, np.float32), np.ones(16000, np.float32) * .25,
                      rng.normal(0, .08, 16000).astype(np.float32)]:
            track = extract_track(audio, stream_id='noise')
            self.assertFalse(any(f['voiced'] for f in track['frames']))
            self.assertTrue(all(f['f0_hz'] == 0. for f in track['frames']))
            self.assertFalse(track['execution_authorized'])
            self.assertNotIn('singing', track['frames'][0])

    def test_pure_tones_and_frame_local_dc(self):
        for frequency in [65, 110, 220, 440, 800]:
            track = extract_track(tone(.25, frequency) + .2, stream_id='tone')
            voiced = [f['f0_hz'] for f in track['frames'] if f['voiced']]
            self.assertEqual(len(voiced), len(track['frames']))
            self.assertLess(abs(np.median(voiced) - frequency), 1.)

    def test_low_frequency_drift_with_only_negative_local_peaks(self):
        # Regression for a real-batch crash: max local peak can be negative.
        rng = np.random.default_rng(392)
        for _ in range(8):
            drift = np.cumsum(rng.normal(size=640))
        drift -= drift.mean()
        drift /= max(abs(drift))
        frame = extract_track(drift.astype(np.float32), stream_id='drift')['frames'][0]
        self.assertFalse(frame['voiced'])
        self.assertEqual(frame['f0_hz'], 0.)

    def test_temporal_features_are_relative_to_pitch_not_absolute_register(self):
        a = extract_track(tone(.6), stream_id='relative')
        b = copy.deepcopy(a)
        for frame in b['frames']:
            frame['f0_hz'] *= 2
        np.testing.assert_allclose(build_trailing_features(a, [8000]), build_trailing_features(b, [8000]), atol=1e-12)

    def test_window_boundary_uses_availability_and_same_online_path(self):
        audio = np.r_[tone(.7), np.zeros(4800, np.float32), tone(.5, 330)]
        track = extract_track(audio, stream_id='window')
        stream = CausalPitchStream('window')
        accumulator = TrailingPitchAccumulator(stream.metadata, stream_id='window')
        for start in range(0, len(audio), 347):
            frames = stream.push(audio[start:start + 347], start_sample=start)
            accumulator.push(frames, stream_id='window')
            expected = build_trailing_features(track, [stream.sample_count])[0]
            np.testing.assert_array_equal(accumulator.features_at(stream.sample_count), expected)
        selected = build_trailing_features(track, [640])[0]
        self.assertAlmostEqual(selected[FEATURE_NAMES.index('pitch_coverage_fraction')], .01)
        self.assertFalse(build_trailing_features(track, [639])[0].any())

    def test_unvoiced_gap_breaks_continuity(self):
        track = extract_track(np.r_[tone(.35), np.zeros(4800, np.float32), tone(.35)], stream_id='gap')
        row = dict(zip(FEATURE_NAMES, build_trailing_features(track, [16000])[0]))
        self.assertGreater(row['pitch_voicing_transitions_per_second'], 1.)
        self.assertLess(row['pitch_longest_voiced_seconds'], .45)
        self.assertLess(row['pitch_trailing_voiced_seconds'], .40)

    def test_corrupt_cache_evidence_is_rejected(self):
        original = extract_track(tone(.1), stream_id='badcache')
        for key, value in [('center_sample', 0.), ('f0_hz', float('nan')),
                           ('available_sample', 600), ('confidence', 1.1)]:
            track = copy.deepcopy(original)
            track['frames'][0][key] = value
            with self.assertRaises(PitchError):
                build_trailing_features(track, [1000])
        with self.assertRaises(PitchError):
            build_trailing_features(original, [1601])

    def test_accumulator_rejects_duplicates_and_old_identity(self):
        stream = CausalPitchStream('a')
        frames = stream.push(tone(.1))
        accumulator = TrailingPitchAccumulator(stream.metadata, stream_id='a')
        with self.assertRaises(PitchError):
            accumulator.push(frames, stream_id='b')
        accumulator.push(frames, stream_id='a')
        with self.assertRaises(PitchError):
            accumulator.push(frames, stream_id='a')
        with self.assertRaises(PitchError):
            accumulator.features_at(639)

    def test_identity_binds_provider_policy(self):
        first = FFTProvider()
        self.assertEqual(provider_identity('fft'), first.metadata['provider_identity'])
        self.assertIn('not_singing_probability', first.metadata['confidence_semantics'])

    def test_optional_pesto_does_not_break_fft_when_missing(self):
        with mock.patch('Server.SingingLearning.pitch_features.importlib.import_module', side_effect=ImportError('isolated')):
            with self.assertRaises(PitchError):
                PestoProvider.load()
            self.assertTrue(extract_track(tone(.1), stream_id='stillfft')['frames'])
        with self.assertRaises(PitchError):
            extract_track(tone(.1), stream_id='explicit', provider='pesto')


@unittest.skipUnless(importlib.util.find_spec('pesto'), 'optional PESTO not installed in this environment')
class OptionalPestoContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import torch
        torch.set_num_threads(2)
        cls.provider = PestoProvider.load()

    def test_real_pesto_packetization_reset_clock_and_prefix(self):
        audio = tone(.36)
        first = extract_track(audio, stream_id='pesto', provider=self.provider, chunk_samples=320)
        second = extract_track(audio, stream_id='pesto', provider=self.provider, chunk_samples=137)
        self.assertEqual(first, second)
        frame = first['frames'][0]
        self.assertEqual(frame['available_sample'], 320)
        self.assertEqual(frame['center_sample'], 320 - first['window_samples'] / 2)
        self.assertEqual(frame['observed_start_sample'], 0)
        changed = extract_track(np.r_[audio, tone(.2, 440)], stream_id='pesto', provider=self.provider)
        self.assertEqual(first['frames'], changed['frames'][:len(first['frames'])])

    def test_real_pesto_silence_reset_does_not_inherit_previous_pitch(self):
        extract_track(tone(.2), stream_id='prior', provider=self.provider)
        track = extract_track(np.zeros(3200, np.float32), stream_id='silence', provider=self.provider)
        self.assertTrue(all(not frame['voiced'] for frame in track['frames']))


if __name__ == '__main__':
    unittest.main()
