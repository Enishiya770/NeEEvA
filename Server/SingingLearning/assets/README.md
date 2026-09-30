# First teaching hypothesis

`bootstrap-panns-development.json` is a byte-for-byte copy of the project's
`work/singing-subsystem-20260929/segmentation/score-adaptation-20260929/run-01/full-development-adapters/panns.json`.
It contains only the nine-feature logistic head and its fitting/provenance
metadata, not recordings. It was fitted on the seven existing manually reviewed
development recordings listed in its `experiment_provenance.training_ids`.
The source protocol SHA is
`29a950daeb7804c99c54469d6f61a8fcefd0de81f981e6d81ba0f37cf5703b9a`.

This prior is used solely to propose editable regions when a user has no compatible
personal head. It is not a validated cross-user or unseen-song classifier and is
not a production gate for singing playback. Returned hypotheses explicitly say
`model_source: bootstrap_development`, `held_out_acceptance: false`, and
`execution_authorized: false`. User confirmations stay separate and append-only.
`proposals.py` requires the original PANNs checkpoint SHA and matching feature
semantics before evaluating it; the heavy checkpoint is not bundled here.

The initial personal head can learn from confirmed singing and non-singing
examples in one teaching session, but its stage is explicitly provisional and
has no validation evidence. The developer validation split becomes available
only when the independently grouped data meets that split's requirements.
