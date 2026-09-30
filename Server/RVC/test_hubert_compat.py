import unittest
from hubert_compat import remap_weight_norm_state

PREFIX = "encoder.pos_conv_embed.conv."
OLD = [PREFIX + "weight_g", PREFIX + "weight_v"]
NEW = [PREFIX + "parametrizations.weight.original0", PREFIX + "parametrizations.weight.original1"]


class WeightNormCompatibilityTests(unittest.TestCase):
    def test_new_to_legacy_preserves_exact_tensors_and_source(self):
        a, b, other = object(), object(), object()
        state = {NEW[0]: a, NEW[1]: b, "other": other}
        mapped, changed = remap_weight_norm_state(state, OLD)
        self.assertTrue(changed)
        self.assertIs(mapped[OLD[0]], a)
        self.assertIs(mapped[OLD[1]], b)
        self.assertIs(mapped["other"], other)
        self.assertIn(NEW[0], state)
        self.assertNotIn(NEW[0], mapped)

    def test_legacy_to_new_preserves_exact_tensors(self):
        a, b = object(), object()
        mapped, changed = remap_weight_norm_state(dict(zip(OLD, [a, b])), NEW)
        self.assertTrue(changed)
        self.assertIs(mapped[NEW[0]], a)
        self.assertIs(mapped[NEW[1]], b)

    def test_matching_checkpoint_is_unchanged(self):
        for keys in (OLD, NEW):
            state = dict(zip(keys, [object(), object()]))
            mapped, changed = remap_weight_norm_state(state, keys)
            self.assertFalse(changed)
            self.assertIs(mapped, state)

    def test_partial_pair_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "incomplete"):
            remap_weight_norm_state({NEW[0]: object()}, OLD)

    def test_conflicting_pair_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "conflicting"):
            remap_weight_norm_state(dict.fromkeys(NEW + OLD, object()), OLD)


if __name__ == "__main__":
    unittest.main()
