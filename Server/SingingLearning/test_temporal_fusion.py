import copy
import unittest
import numpy as np

from . import temporal_fusion as fusion


def record(rid='a', source='user', end=100):
    return dict(recording_id=rid, audio_sha256='0' * 64, sample_rate=100, sample_count=end,
                annotation_revision=1, session_id='s', content_group='c',
                annotation=dict(confirmed=True, source_kind=source, regions=[
                    dict(start_sample=0, end_sample=end // 2, tolerance_samples=0, label='speech'),
                    dict(start_sample=end // 2, end_sample=end, tolerance_samples=0, label='singing')]))


def item(rec):
    return dict(recording=rec, edges=np.array([0, rec['sample_count'] // 2, rec['sample_count']]),
                features=np.array([[-1., 0.], [1., 0.]]), feature_metadata=dict(names=['a', 'b']),
                feature_sha256='1' * 64, pitch_source_sha256='2' * 64)


class TemporalFusionTests(unittest.TestCase):
    def test_training_standardization_uses_only_supplied_weighted_rows(self):
        x = np.array([[-1., 2.], [1., 4.], [1000., 1000.]])
        head = fusion.fit_head(x, [0, 1, 1], [1, 3, 0], feature_metadata=dict(names=['x', 'y']))
        np.testing.assert_allclose(head['standardizer']['mean'], [.5, 3.5])
        self.assertFalse(head['execution_authorized'])
        self.assertGreater(fusion.predict_scores(head, x)[1], fusion.predict_scores(head, x)[0])

    def test_excluded_sources_do_not_supply_negative_or_positive_training(self):
        prepared = {'a': item(record()), 'b': item(record('b', 'playback')),
                    'c': item(record('c', 'mixed'))}
        candidate = fusion.fit_candidate(prepared)
        self.assertEqual(set(candidate['source_bindings']), {'a'})
        self.assertEqual(candidate['deduplicated_known_seconds']['singing'], .5)

    def test_missing_data_and_single_class_rejected(self):
        with self.assertRaisesRegex(ValueError, 'no_known'):
            fusion.fit_candidate({'a': item(record(source='playback'))})
        with self.assertRaisesRegex(ValueError, 'one_class'):
            fusion.fit_head([[1], [2]], [1, 1], [1, 1], feature_metadata=dict(names=['a']))

    def test_unknown_does_not_become_negative(self):
        rec = record()
        rec['annotation']['regions'][0]['label'] = 'uncertain'
        masses = fusion.training_masses({'a': item(rec)}, ['a'])['a']
        self.assertEqual(masses['speech'].sum(), 0)
        self.assertEqual(masses['singing'].sum(), .5)

    def test_prefix_multiplicity_applies_only_when_other_labels_known(self):
        a, b = item(record('a')), item(record('b'))
        a['duplicate_prefixes'] = {'b': 100}
        b['duplicate_prefixes'] = {'a': 100}
        masses = fusion.training_masses({'a': a, 'b': b}, ['a', 'b'])
        self.assertEqual(sum(v['singing'].sum() for v in masses.values()), .5)
        b['recording']['annotation']['source_kind'] = 'mixed'
        masses = fusion.training_masses({'a': a, 'b': b}, ['a', 'b'])
        self.assertEqual(masses['a']['singing'].sum(), .5)

    def test_source_binding_revision_and_hash_fail_closed(self):
        rec = record()
        candidate = fusion.fit_candidate({'a': item(rec)})
        fusion.validate_candidate(candidate, {'a': rec})
        changed = copy.deepcopy(rec)
        changed['annotation_revision'] += 1
        with self.assertRaisesRegex(ValueError, 'source_changed'):
            fusion.validate_candidate(candidate, {'a': changed})
        candidate['source_bindings']['a']['audio_sha256'] = 'f' * 64
        with self.assertRaisesRegex(ValueError, 'bindings_hash'):
            fusion.validate_candidate(candidate)

    def test_serialized_head_dimensions_and_definition_are_validated(self):
        head = fusion.fit_candidate({'a': item(record())})['head']
        with self.assertRaisesRegex(ValueError, 'definition_mismatch'):
            fusion.predict_scores(head, [[0, 0]], dict(names=['b', 'a']))
        bad = copy.deepcopy(head)
        bad['weights'].append(0.)
        with self.assertRaisesRegex(ValueError, 'vectors'):
            fusion.validate_head(bad)
        bad = copy.deepcopy(head)
        bad['execution_authorized'] = True
        with self.assertRaisesRegex(ValueError, 'policy'):
            fusion.validate_head(bad)

    def test_fixed_guard_no_improvement_tradeoff_authorizes_production(self):
        baseline = dict(speech_fp_seconds=1., environment_fp_seconds=2., fn_seconds=3.)
        good = fusion.evaluate_guard(baseline, baseline, scope='exact_prefix_component_holdout', fold_count=5)
        self.assertTrue(good['teaching_eligible'])
        self.assertFalse(good['production_eligible'])
        self.assertFalse(good['execution_authorized'])
        bad = fusion.evaluate_guard(dict(baseline, speech_fp_seconds=1.1), baseline,
                                    scope='exact_prefix_component_holdout', fold_count=5)
        self.assertFalse(bad['teaching_eligible'])

    def test_teaching_guard_requires_both_references_and_boundary_nonregression(self):
        summary = dict(metrics=dict(speech_fp_seconds=1., environment_fp_seconds=2., fn_seconds=3.),
                       boundaries=dict(completely_missed_gold_count=0, internal_fragment_splits=1))
        result = fusion.evaluate_teaching_guard(summary, {'bootstrap_proposal': summary}, fold_count=5)
        self.assertFalse(result['teaching_eligible'])
        self.assertIn('missing_reference:panns_uniform', result['eligibility_reasons'])
        worse = copy.deepcopy(summary)
        worse['boundaries']['internal_fragment_splits'] = 2
        result = fusion.evaluate_teaching_guard(worse, {'bootstrap_proposal': summary, 'panns_uniform': summary}, fold_count=5)
        self.assertFalse(result['teaching_eligible'])
        self.assertIn('bootstrap_proposal:internal_fragment_splits_regression', result['eligibility_reasons'])

    def test_malformed_grid_rejected_before_overlap_weights(self):
        prepared = item(record())
        prepared['edges'] = np.array([10, 50, 100])
        with self.assertRaisesRegex(ValueError, 'source_grid'):
            fusion.fit_candidate({'a': prepared})


if __name__ == '__main__':
    unittest.main()
