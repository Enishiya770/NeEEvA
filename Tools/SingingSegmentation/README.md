# Offline speech/singing candidates

For in-project personal data collection and human feedback, see [personal singing learning](../../docs/personal-singing-learning-20260929.md). The Unity learning page uses the separate `Server/SingingLearning` service, which reuses the frozen PANNs provider and score adapter. Its models remain shadow candidates; it does not replace the production ASR or authorize playback cuts. `start-personal-learning.ps1` starts the optional local worker with existing, explicit runtime assets.

These modules produce **acoustic candidates**, not executable cuts. They are not imported by the production SenseVoice server. No service, Unity scene, ASR transcript or existing crop is changed.

For independent manual ground truth collection, see [the offline annotation tool](ANNOTATION.md). The generated self-contained HTML embeds the seven original recordings without model candidates or prefilled labels.

The fixed, offline score-adaptation experiment is documented in [the stage report](../../docs/singing-score-adaptation-20260929.md). `fit_score_adapter.py` learns a nine-feature L2 logistic head from caller-selected training rows; `run_score_adaptation.py` verifies source-bound annotations/saved scores and runs predeclared grouped development folds. It does not import a production server, run the acoustic backbone, or authorize crops. All fitted models and thresholds are reported; current folds are same-person/song development diagnostics, not blind acceptance. [The next dataset plan](ADAPTATION_DATASET.md) keeps new train/validation/blind sources separate.

```powershell
& 'Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe' Tools/SingingSegmentation/run_score_adaptation.py --protocol work/singing-subsystem-20260929/segmentation/score-adaptation-20260929/protocol-v2.json --output <new-unused-output-directory>
```

The placeholder must be replaced with a new output directory; the runner refuses to overwrite an existing experiment. The frozen protocol binds the annotation, human confirmation, baseline and manifest hashes. Saved acoustic model JSON and NPZ arrays are checked before fitting. Full-development exports are explicitly separate from fold models and are not scored as held-out predictions.

`Server/SenseVoice/singing_segmentation.py` provides `PannsFramewiseProvider`; `singing_segmentation_strong.py` provides a comparison using the official PretrainedSED FrameMN06/MN10 checkpoints. Each `analyze(wav, policy, cancelled)` returns `(result, raw_framewise_scores)`. Cancellation returns `(cancelled_result, None)`. Model files and dependency directories must be explicit; the providers do not download models or mutate an environment.

The input WAV is hashed and decoded from one byte snapshot. Coordinates use its original sample rate, with integer half-open `[start_sample, end_sample)` ranges. Resampling for inference does not change the source coordinate system. Output retains separate speech, singing, mixed and uncertain regions; it does not join singing regions through intervening speech.

Every result has `status=candidate`, `execution_authorized=false`, `supported_range=null`. Every region has `boundary_verified=false`. A future verified refinement provider must separately bind the current request, source revision, evidence revision and selected segment IDs through the canonical boundary contract. These acoustic candidate IDs cannot authorize playback.

## Interpretation

- Outputs are independent sigmoid scores, **not calibrated probabilities** and not mutually exclusive classes. `family_max` means the maximum of parent/male/female/child class scores, not a union probability. All constituent raw scores are retained.
- PANNs outputs a 10 ms array by repeating its native 320 ms cells. Its approximately 3.502 s theoretical receptive field prevents a 10 ms precision claim. Full 527-class framewise arrays are retained so that this can be inspected.
- Strong FrameMN uses a 40 ms output grid and the official non-overlapping 10 s inference chunks. Channel squeeze-excitation uses chunk-wide statistics. The grid is not a claim of 40 ms boundary accuracy; a chunk seam may affect scores.
- Transition `context_support_*` ranges describe model context support. They are deliberately **not statistical confidence intervals**, word timestamps or verified cut uncertainty.
- Default policy: direct Speech/Singing, threshold 0.5, dominance margin 0.15, minimum confident speech/singing run 0.64 s. Short islands become uncertain. `score_sensitivity.py` explicitly compares family maxima at fixed 0.1/0.2/0.5 thresholds from the upstream examples; it does not choose a best threshold from these recordings.

## Reproducing the local evaluation

Run commands from the repository root. Use a separate Python environment with CUDA PyTorch. The recorded run reused the existing CUDA interpreter without installing into it; supplementary packages were installed with `pip --target` under the work directory. The work directory contains official checkpoint checksum metadata and an environment/source manifest. Do not copy these dependencies into a production environment implicitly.

```powershell
$py = 'Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe'
$data = 'work/singing-subsystem-20260929/segmentation'
& $py Tools/SingingSegmentation/evaluate.py --backend panns --manifest "$data/evaluation-manifest.json" --checkpoint "$data/models/Cnn14_DecisionLevelMax_mAP=0.385.pth" --labels "$data/models/class_labels_indices.csv" --dependencies "$data/deps" --output "$data/results-panns"
& $py Tools/SingingSegmentation/evaluate.py --backend strong --manifest "$data/evaluation-manifest.json" --checkpoint "$data/models/frame_mn06_strong_1.pt" --upstream-code "$data/ptsed_subset" --dependencies "$data/deps" --output "$data/results-strong"
& $py Tools/SingingSegmentation/evaluate.py --backend strong --manifest "$data/evaluation-manifest.json" --checkpoint "$data/models/frame_mn10_strong_1.pt" --upstream-code "$data/ptsed_subset" --dependencies "$data/deps" --output "$data/results-strong10"
& $py Tools/SingingSegmentation/score_sensitivity.py --results "$data/results-panns" --output "$data/sensitivity-panns" --dependencies "$data/deps"
# Run score_sensitivity for results-strong / results-strong10 as well.
& $py Tools/SingingSegmentation/verify_results.py --root $data --dependencies "$data/deps"
python -m unittest discover -s Server/SenseVoice -p test_singing_segmentation.py -v
```

The downloader only fetches the official PANNs checkpoint. It resumes explicit HTTP ranges, validates `Content-Range`, total length and official MD5 before replacing the destination. Strong model files come from the official release linked below. Production code must never treat an incomplete download as a model.

The evaluator saves JSON/CSV timelines, full class-score NPZ files, PNG plots and a summary. It verifies manifest audio hashes before publishing each result and checks the plot uses the same source bytes. `verify_results.py` checks hashes, sample coverage, no execution authority, raw/native score correspondence and PANNs repeated frames. It does **not** calculate invented accuracy without human labels.

## Sources

PANNs officially publishes framewise decision-level detection models and its inference wrapper returns `framewise_output`: [model repository](https://github.com/qiuqiangkong/audioset_tagging_cnn), [inference code](https://github.com/qiuqiangkong/panns_inference), [official checkpoints](https://zenodo.org/records/3987831).

The comparison uses [PretrainedSED](https://github.com/fschmid56/PretrainedSED), including its released FrameMN models and official chunked inference procedure. The locally preserved code subset matches commit `1aa47e482f7e89904cba2338999345025d8b4e36` and retains the upstream MIT license.

[AudioSet Strong annotation guidance](https://research.google.com/audioset/download_strong.html) asks annotators for specific classes rather than redundant parent labels, which explains the explicit family-score comparison. Its annotation policy also limits detail inside music segments; stronger frame supervision does not establish clean a-cappella speech/singing boundaries for these recordings.
