# Frozen final-80 feature audit

`python -m Tools.MotionAdapter.audit_generalization` compares the approved old adapter and the validation-locked eligible candidate against real cached teacher features for the immutable `generalization-eval-v1` dataset. It runs on CPU and loads no language model, ARDY generator, service or Unity process.

Required inputs are `--selection`, `--merged-directory`, `--qwen-bundle`, `--teacher-bundle`, `--native-validation-report` and a new `--output` path. The feature bundles must bind all 80 exact records, including their labels. The selection must identify the unchanged eligible winner and old checkpoint. The native report must be complete, use validation records, and contain that same candidate SHA. Manual acceptance of native validation remains a prerequisite for the operator; completed metrics alone do not establish naturalness.

Use `--plan-only` to inspect contracts and file identities without loading adapters, predicting, reserving an audit, or writing results. Only an explicit `--final-test` allows predictions. Do not run it until the candidate and native validation are accepted.

Before any prediction the tool exclusively creates `reports/generalization-eval-v1-feature-audit-once.json`, binding the suite, selection, checkpoint and output. It refuses another audit even with a different candidate or output path. Failed attempts preserve the marker and their failure report; the tool never silently resets it.

All MSE values use the exact old checkpoint `target_std`, and candidate mean/std buffers must equal the old buffers. Results retain individual records, group means, and separate tracks for 32 supported groups and 8 capability-boundary groups. The bootstrap is a descriptive paired group interval for the already locked model; it is not a selection criterion. There is no combined success denominator, automatic action-success rate, model ranking or deployment approval.

Validation: `python -B -m unittest Tools.MotionAdapter.test_audit_generalization -v` checks synthetic CPU arithmetic, grouping, refusal guards, selection/native-report binding and the immutable test manifest. It does not predict real final-test features.
