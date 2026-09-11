"""CPU-only synthetic math checks; never creates teacher/checkpoint artifacts."""
import unittest
from unittest.mock import patch

import numpy as np
import torch
from torch import nn

from . import train_extension as extension


class TinyAdapter(nn.Module):
    def __init__(self, architecture="mlp"):
        super().__init__()
        self.network = nn.Linear(3, 4)
        self.register_buffer("target_mean", torch.tensor([.1, .2, .3, .4]))
        self.register_buffer("target_std", torch.tensor([.5, 1., 1.5, 2.]))

    def standardized(self, q):
        return self.network(q)

    def forward(self, q):
        return self.standardized(q) * self.target_std + self.target_mean


class ExtensionPolicyTests(unittest.TestCase):
    def setUp(self):
        self.baseline = {"old": {"standardized_mse": 1., "cosine": .9},
                         "new": {"standardized_mse": 2., "cosine": .7}}
        self.good = {"old": {"standardized_mse": 1.04, "cosine": .8995},
                     "new": {"standardized_mse": 1.6, "cosine": .75}}

    def test_old_domain_cannot_be_hidden_by_new_domain_gain(self):
        self.good["old"]["standardized_mse"] = 1.051
        self.good["new"]["standardized_mse"] = .1
        self.assertIn("old-validation-mse-regression", extension.eligibility(self.good, self.baseline, {"lower": .1}))

    def test_old_cosine_and_substantial_new_improvement_are_required(self):
        self.assertEqual([], extension.eligibility(self.good, self.baseline, {"lower": .1}))
        self.good["old"]["cosine"] = .8989
        self.good["new"]["standardized_mse"] = 1.801
        failures = extension.eligibility(self.good, self.baseline, {"lower": .1})
        self.assertIn("old-validation-cosine-regression", failures)
        self.assertIn("new-validation-mse-improvement-under-10-percent", failures)

    def test_new_group_evidence_must_exclude_zero(self):
        self.assertIn("new-validation-group-bootstrap-does-not-establish-positive-improvement",
                      extension.eligibility(self.good, self.baseline, {"lower": 0}))

    def test_nonfinite_validation_never_qualifies(self):
        self.good["old"]["cosine"] = float("nan")
        self.assertEqual(extension.eligibility(self.good, self.baseline, {"lower": .1}), ["nonfinite-validation-metrics"])

    def test_validation_score_is_equal_domain_relative_mse(self):
        self.assertAlmostEqual(extension.balanced_score(self.good, self.baseline), .5 * 1.04 + .5 * .8)

    def test_bootstrap_counts_semantic_groups_not_paraphrase_volume(self):
        # A group with 100 paraphrases has the same weight as one with one.
        ci = extension.group_bootstrap_improvement(np.ones(101), np.array([.9] * 100 + [0.]), ["a"] * 100 + ["b"])
        self.assertAlmostEqual(ci["mean_group_improvement"], .55)
        self.assertEqual(ci["groups"], 2)
        self.assertGreater(ci["lower"], 0)

    def test_scratch_and_finetune_never_read_poisoned_test_targets(self):
        torch.manual_seed(42)
        q = torch.randn(16, 3)
        baseline = TinyAdapter().eval()
        target = baseline(q).detach() + torch.randn(16, 4) * .03
        train = {"old": [0, 1, 2, 3], "new": [8, 9, 10, 11]}
        val = {"old": [4, 5, 6], "new": [12, 13, 14]}
        target[[7, 15]] = float("nan")
        rows = [{"semantic_group": f"group-{i}", "split": "test" if i in [7, 15] else "train"} for i in range(16)]
        scores, losses = {}, {}
        for domain in ["old", "new"]:
            scores[domain], losses[domain] = extension.evaluate(baseline, q, target, val[domain], baseline.target_std)
        with patch.object(extension, "MotionAdapter", TinyAdapter):
            for mode in ["scratch", "finetune"]:
                state, report = extension.fit_candidate(q, target, rows, train, val, baseline, {}, scores, losses,
                                                        mode, 0, epochs=2, patience=2, batch=4)
                self.assertTrue(all(torch.isfinite(value).all() for value in state.values()))
                self.assertTrue(torch.equal(state["target_std"], baseline.target_std))
                self.assertTrue(torch.equal(state["target_mean"], baseline.target_mean))
                self.assertFalse(report["test_metrics_computed"])


if __name__ == "__main__":
    torch.set_num_threads(2)
    unittest.main()
