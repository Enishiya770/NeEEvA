"""Coordinate/provenance tests; no model, network or audio playback."""
import unittest
import numpy as np
from singing_segmentation import CandidatePolicy, PannsFramewiseProvider, candidate_timeline


class FramewiseCandidateTests(unittest.TestCase):
    def source(self, rate=16000, duration=3.2):
        return {'audio_sha256':'a'*64,'sample_rate':rate,'sample_count':round(rate*duration)}

    def test_multi_interval_singing_not_merged_across_speech(self):
        scores=np.array([[.05,.9]]*3+[[.9,.05]]*3+[[.05,.9]]*4)
        segments, transitions=candidate_timeline(scores,['Speech','Singing'],self.source(),'model')
        self.assertEqual([s['label'] for s in segments],['singing','speech','singing'])
        self.assertEqual([s['start_sample'] for s in segments],[0,15360,30720])
        self.assertTrue(all(s['status']=='candidate' and not s['boundary_verified'] for s in segments))
        self.assertEqual(len(transitions),2)
        self.assertTrue(all(t['uncertainty_kind'].endswith('not-statistical-confidence-interval') for t in transitions))

    def test_overlap_is_mixed_not_renormalized_into_winner(self):
        segments,_=candidate_timeline([[.8,.9]]*10,['Speech','Singing'],self.source(),'model')
        self.assertEqual(segments[0]['label'],'mixed')
        self.assertAlmostEqual(segments[0]['posteriors']['Speech']['mean'],.8)
        self.assertFalse(segments[0]['calibrated'])

    def test_low_scores_and_brief_island_are_uncertain(self):
        segments,_=candidate_timeline([[.01,.01]]*4+[[.01,.9]]+[[.01,.01]]*5,
            ['Speech','Singing'],self.source(),'model')
        self.assertEqual(len(segments),1)
        self.assertEqual(segments[0]['label'],'uncertain')

    def test_nonstandard_rate_and_tail_preserve_exact_source_samples(self):
        source=self.source(44100,3.13)
        segments,_=candidate_timeline([[.01,.9]]*10,['Speech','Singing'],source,'model')
        self.assertEqual(segments[0]['end_sample'],source['sample_count'])
        self.assertEqual(segments[0]['coordinate_sample_rate'],44100)

    def test_original_hash_and_policy_bind_ids(self):
        values=[[.01,.9]]*10
        a,_=candidate_timeline(values,['Speech','Singing'],self.source(),'model')
        b,_=candidate_timeline(values,['Speech','Singing'],self.source(),'model')
        self.assertEqual(a[0]['segment_id'],b[0]['segment_id'])
        source=self.source();source['audio_sha256']='b'*64
        c,_=candidate_timeline(values,['Speech','Singing'],source,'model')
        self.assertNotEqual(a[0]['segment_id'],c[0]['segment_id'])

    def test_incomplete_coverage_and_invalid_scores_fail_closed(self):
        for scores in ([[.5,.9]],[[float('nan'),.8]]*10,[[1.2,.1]]*10):
            with self.assertRaises(ValueError):
                candidate_timeline(scores,['Speech','Singing'],self.source(),'model')

    def test_no_speech_class_is_not_assumed_silence(self):
        with self.assertRaises(ValueError):
            candidate_timeline([[.1,.9]]*10,['Music','Singing'],self.source(),'model')

    def test_invalid_source_and_policy_fail(self):
        for source in ({'sample_rate':16000,'sample_count':100}, self.source(0)):
            with self.assertRaises(ValueError):
                candidate_timeline([[.1,.9]]*10,['Speech','Singing'],source,'model')
        with self.assertRaises(ValueError):
            candidate_timeline([[.1,.9]]*10,['Speech','Singing'],self.source(),'model',CandidatePolicy(active_threshold=-1))

    def test_strong_specific_labels_remain_explicit_family_max(self):
        values=[[.001,.001,.8,.05]]*80
        segments,_=candidate_timeline(values,['Speech','Singing','Male speech, man speaking','Male singing'],
            self.source(),'strong',CandidatePolicy(score_aggregation='family_max'),cell_analysis_samples=640,
            analysis_rate=16000,context_support_seconds=10.0)
        self.assertEqual(segments[0]['label'],'speech')
        self.assertAlmostEqual(segments[0]['posteriors']['Speech']['mean'],.001)
        self.assertFalse(segments[0]['calibrated'])
        self.assertEqual(segments[0]['end_sample'],51200)

    def test_cancel_before_loading_audio_returns_no_executable_result(self):
        from singing_segmentation_strong import StrongFramewiseProvider
        for provider_type in (PannsFramewiseProvider,StrongFramewiseProvider):
            provider=object.__new__(provider_type)
            result, scores=provider.analyze('not-a-real-audio.wav',cancelled=lambda:True)
            self.assertEqual(result['status'],'cancelled')
            self.assertFalse(result['execution_authorized'])
            self.assertIsNone(scores)


if __name__=='__main__':unittest.main()
