"""Source identity, true local detail and honest availability on the 20 ms grid."""
import copy
import unittest

import numpy as np

from . import pitch_features as pitch
from . import sequence_features as seq


ACOUSTIC = {'names': [f'acoustic_{i}' for i in range(9)], 'version': 'fixed-test-v1'}


def tone(samples, hz=220., level=.2):
    return (level * np.sin(2 * np.pi * hz * np.arange(samples) / 16000)).astype(np.float32)


def prepared(audio, *, native_edges=None, track=None, values=None):
    edges = np.asarray(native_edges if native_edges is not None else [0, len(audio)], dtype=np.int64)
    if values is None:
        values = np.zeros((len(edges) - 1, 9))
    if track is None:
        track = seq.make_pitch_track(audio, stream_id='test-recording')
    return seq.build_features(audio, edges, values, ACOUSTIC, track)


class SequenceFeatureTests(unittest.TestCase):
    def test_exact_grid_preserves_short_tail_and_observed_window(self):
        audio = tone(657)
        edges, x, metadata = prepared(audio)
        np.testing.assert_array_equal(edges, [0, 320, 640, 657])
        self.assertEqual(x.shape, (3, 63))
        self.assertEqual(metadata['timeline']['source_start_samples'], [0, 320, 640])
        self.assertEqual(metadata['timeline']['source_end_samples'], [320, 640, 657])
        self.assertEqual(metadata['timeline']['local_features_available_samples'], [320, 640, 657])
        self.assertEqual(metadata['timeline']['combined_features_available_samples'], [657] * 3)
        self.assertAlmostEqual(x[-1, -6], np.sqrt(np.mean(audio[17:].astype(np.float64) ** 2)))
        self.assertFalse(metadata['definition']['fully_causal'])
        self.assertFalse(metadata['execution_authorized'])

    def test_short_recording_has_actual_samples_no_future_padding(self):
        audio = np.asarray([.25], dtype=np.float32)
        edges, x, _ = prepared(audio)
        np.testing.assert_array_equal(edges, [0, 1])
        self.assertEqual(x.shape, (1, 63))
        self.assertEqual(x[0, -6], .25)
        self.assertTrue(np.all(x[:, 9:57] == 0.))
        self.assertTrue(np.all(x[:, -4:] == 0.))

    def test_future_audio_does_not_change_any_past_local_evidence(self):
        prefix = tone(13231)
        first = np.r_[prefix, tone(5000, hz=110)]
        second = np.r_[prefix, np.zeros(5000, dtype=np.float32)]
        edges_a, x_a, _ = prepared(first)
        edges_b, x_b, _ = prepared(second)
        ends = edges_a[1:] <= len(prefix)
        np.testing.assert_array_equal(edges_a, edges_b)
        np.testing.assert_array_equal(x_a[ends, 9:], x_b[ends, 9:])

    def test_acoustic_repeat_is_explicit_no_interpolation_or_false_resolution(self):
        audio = tone(700)
        values = np.vstack((np.arange(9), np.arange(9) + 100))
        edges, x, metadata = prepared(audio, native_edges=[0, 400, 700], values=values)
        np.testing.assert_array_equal(x[:, :9], values[[0, 0, 1]])
        self.assertEqual(metadata['timeline']['acoustic_native_cell_indices'], [0, 0, 1])
        definition = metadata['definition']['acoustic']
        self.assertFalse(definition['interpolation'])
        self.assertEqual(definition['causality'], 'frozen_whole_recording_noncausal')

    def test_real_sub320ms_waveform_changes_remain_visible_with_constant_panns(self):
        audio = np.r_[np.zeros(1280, dtype=np.float32), tone(1280), np.zeros(1280, dtype=np.float32)]
        _, x, _ = prepared(audio)
        np.testing.assert_array_equal(x[:, :9], np.zeros((12, 9)))
        rms = x[:, -6]
        self.assertEqual(rms[2], 0.)
        self.assertGreater(rms[4], .08)
        self.assertEqual(rms[-1], 0.)
        self.assertGreater(x[4, -2], 0.)

    def test_raw_evidence_uses_no_recording_amplitude_normalization(self):
        audio = tone(3200)
        _, first, _ = prepared(audio)
        _, quiet, _ = prepared(audio * .1)
        np.testing.assert_allclose(quiet[:, -6], first[:, -6] * .1, atol=1e-8)
        np.testing.assert_allclose(quiet[:, -5], first[:, -5] - 1., atol=1e-7)

    def test_silence_is_finite_and_never_implies_a_singing_gate(self):
        _, x, meta = prepared(np.zeros(771, dtype=np.float32))
        self.assertTrue(np.isfinite(x).all())
        np.testing.assert_array_equal(x[:, -6], [0., 0., 0.])
        np.testing.assert_array_equal(x[:, -5], [-8., -8., -8.])
        np.testing.assert_array_equal(x[:, -4:], np.zeros((3, 4)))
        self.assertEqual(x.shape[1], len(meta['definition']['names']))
        self.assertNotIn('selected', meta)

    def test_arbitrary_pitch_packetization_produces_identical_features(self):
        audio = np.r_[tone(7111), np.zeros(537, dtype=np.float32)]
        a = seq.make_pitch_track(audio, stream_id='same')
        b = pitch.extract_track(audio, stream_id='same', chunk_samples=1337)
        b['source'] = copy.deepcopy(a['source'])
        ea, xa, ma = prepared(audio, track=a)
        eb, xb, mb = prepared(audio, track=b)
        np.testing.assert_array_equal(ea, eb)
        np.testing.assert_array_equal(xa, xb)
        self.assertEqual(ma, mb)

    def test_current_definition_stable_but_source_bound_to_each_original(self):
        a, b = tone(1000), tone(1700, hz=330)
        _, _, ma = prepared(a)
        _, _, mb = prepared(b)
        self.assertEqual(ma['definition'], mb['definition'])
        self.assertEqual(ma['definition_sha256'], mb['definition_sha256'])
        self.assertNotEqual(ma['source_binding_sha256'], mb['source_binding_sha256'])
        self.assertNotEqual(ma['feature_values_sha256'], mb['feature_values_sha256'])
        self.assertEqual(seq.current_definition(ACOUSTIC, seq.make_pitch_track(a, stream_id='another')), ma['definition'])
        self.assertIn('extractor_source_sha256', ma['definition'])
        self.assertIn('extractor_source_sha256', ma['definition']['pitch'])

    def test_cached_pcm_track_source_verified_without_container_claim(self):
        pcm = np.rint(tone(1600) * 32767).astype('<i2')
        audio = pcm.astype(np.float32) / 32768.
        track = seq.make_pitch_track(audio, stream_id='cached')
        track['source'].pop('pcm_float32_sha256')
        track['source']['audio_sha256'] = 'caller-verified-wav-sha'
        _, _, metadata = prepared(audio, track=track)
        self.assertEqual(metadata['source']['pcm_sha256'], track['source']['pcm_sha256'])
        self.assertEqual(metadata['source']['wav_binding_verification'], 'caller_must_verify_original_container')
        with self.assertRaisesRegex(ValueError, 'source_content_mismatch'):
            prepared(audio * .5, track=track)

    def test_wrong_same_length_track_and_missing_identity_are_rejected(self):
        a, b = tone(1600), tone(1600, hz=330)
        with self.assertRaisesRegex(ValueError, 'source_content_mismatch'):
            prepared(a, track=seq.make_pitch_track(b, stream_id='wrong'))
        with self.assertRaisesRegex(ValueError, 'source_content_mismatch'):
            prepared(a, track=pitch.extract_track(a, stream_id='unbound'))
        track = seq.make_pitch_track(a, stream_id='wrong-clock')
        track['sample_count'] += 1
        with self.assertRaisesRegex(ValueError, 'original_clock_mismatch'):
            prepared(a, track=track)

    def test_invalid_audio_grid_or_provider_cannot_make_candidate_features(self):
        for audio in [np.zeros(0, dtype=np.float32), np.zeros((4, 2)), np.asarray([1], dtype=np.int16),
                      np.asarray([np.nan]), np.asarray([np.inf]), np.asarray([1.1])]:
            with self.subTest(audio=audio):
                with self.assertRaises(ValueError):
                    prepared(audio)
        audio = tone(1000)
        for edges in [[1, 1000], [0, 0, 1000], [0, 1001]]:
            with self.subTest(edges=edges):
                with self.assertRaisesRegex(ValueError, 'acoustic_edges'):
                    prepared(audio, native_edges=edges)
        track = seq.make_pitch_track(audio, stream_id='changed')
        track['provider_identity'] = 'stale-provider'
        with self.assertRaisesRegex(ValueError, 'current_causal_fft_provider'):
            prepared(audio, track=track)


if __name__ == '__main__':
    unittest.main()
