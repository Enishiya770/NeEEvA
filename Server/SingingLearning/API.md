# Personal singing learning service (v1)

Standalone development service. Default bind `127.0.0.1:9884`; no ASR integration, automatic playback, cropping or production promotion. Every model and prediction has `execution_authorized: false` and scores are **uncalibrated_score**. CPU feature backend loads lazily in the one background worker.

All JSON responses use UTF-8. Domain errors: HTTP 400/404/409/413/429 with `detail: {code, message}`; framework request-shape errors may use FastAPI's HTTP 422 detail list. IDs: 1–64 ASCII letters/digits/underscore/hyphen, start alphanumeric; Windows reserved names are rejected. `parent_recording_id` is an exception: it is a bounded 256-character opaque grouping token (including recorder `pcm:...` identities), never a path. No client filesystem paths. Recording ranges use integer original PCM samples, half-open `[start_sample,end_sample)`.

## Profiles and sessions

- `GET /health` → `{ok:true, service:"singing-learning", schema:1, execution_authorized:false}` (does not load a model).
- `GET /learning/profiles` → `{profiles:[{profile_id,display_name,created_at,dataset_revision}]}`.
- `POST /learning/profiles` JSON `{profile_id,display_name?}` → profile object; duplicate 409.
- `POST /learning/profiles/{profile_id}/sessions` JSON `{session_id,content_group}` → persisted object. Both IDs are required, immutable and must be set before collection; changing song/session uses a new session. `content_group` represents known shared song/content (not arbitrary per recording random ID).

## Recordings

`POST /learning/profiles/{profile_id}/recordings` multipart:

- `audio_file`: original unmodified PCM16 WAV (mono or stereo; 8–96 kHz; max 120 s, 24 MiB).
- `manifest`: JSON string `{recording_id,session_id,content_group,capture_serial,parent_recording_id?,source_kind:"user|playback|mixed|unknown",lineage_json?,audio_sha256?,sample_rate?,sample_count?}`.
- `lineage_json` may be the recorder's JSON string or object `{known,basis,regions:[{sourceId,...}]}`; source IDs add grouping links. Server computes WAV SHA256/rate/count; supplied values must match. No ASR-derived acceptance filter.

Upload returns recording object with `{recording_id,session_id,content_group,capture_serial,source_kind,audio_sha256,sample_rate,sample_count,channels,duration_seconds,annotation_revision:0,feature_status:"queued|pending|ready|failed",feature_job_id?,proposal_job_id?,...}`. Identical retry of same ID+bytes+metadata is idempotent; conflicts 409. Backend absence does not prevent recording storage; feature failure is visible. New uploads queue a first acoustic hypothesis after feature extraction, without creating or changing a human annotation.

- `GET /learning/profiles/{profile_id}/recordings` → `{recordings:[...],dataset_revision}`.
- `GET /learning/profiles/{profile_id}/recordings/{recording_id}` → recording plus current `annotation` or null.
- `GET /learning/profiles/{profile_id}/recordings/{recording_id}/audio` → original WAV bytes.
- `POST /learning/profiles/{profile_id}/recordings/{recording_id}/features` → `{job_id,status}`; retries failed feature extraction explicitly.

## Explicit human annotations

`PUT /learning/profiles/{profile_id}/recordings/{recording_id}/annotations` JSON:

```json
{"expected_revision":0,"confirmed":true,"source_kind":"user","auto_train":true,"regions":[{"start_sample":48000,"end_sample":224000,"label":"singing","tolerance_samples":800}]}
```

`expected_revision` is the current annotation revision; conflict returns 409. `confirmed` must be literally true. `source_kind` is a separate explicit human source confirmation, not inherited from upload. Labels: `speech`, `singing`, `environment`, `uncertain`. Sorted non-overlapping positive-length regions within original sample count; integer coordinates/tolerance only. Empty list is legal but contributes no training labels. Blank gaps, `uncertain`, and boundary tolerance bands are excluded, never automatically negative. `speech` and `environment` are non-singing but preserve fine labels. Any `source_kind != user` excludes the entire recording. Revisions are append-only; response `{annotation_revision,dataset_revision,annotation,auto_train_job_id?}`. Defaults `auto_train:true`; confirmations debounce for 2 s and coalesce to at most one queued auto-training job per profile. Queue saturation stores the annotation and returns `auto_train_deferred:true`; manual retry is available.

Up to 1024 regions may be confirmed. Optional `proposal_id` must belong to this recording and match its original WAV hash and current annotation revision; otherwise 404/409. Optional `correction_text` is at most 2000 characters. Saved annotations include `assistance:{proposal_id,model_source,model_id,adapter_sha256}` or null, and the correction text. This distinguishes assisted teaching examples from unassisted annotations. It does not imply blind evaluation; all current model reports remain development-validation reports. A proposal never becomes a label without the same explicit confirmation request.

## First hypotheses for teaching

- `POST /learning/profiles/{profile_id}/recordings/{recording_id}/proposal` JSON `{}` → `{job_id,status}` while a job is queued/running; a fresh cached result returns `{status:"ready",proposal:{...}}` immediately.
- `GET` the same route → `{status:"ready|stale|missing|pending",proposal?:{...},job_id?:"..."}`. A stale proposal is for display/history only; request a fresh one before using its ID for confirmation. `pending` may include the previous stale proposal. No worker starts from GET.
- `GET /learning/jobs/{job_id}` on a completed `proposal` job has its full proposal in `result`.

Proposal shape:

```json
{"proposal_id":"p-...","schema":"neeeva.teaching-proposal.v1","status":"proposal",
 "recording_id":"...","audio_sha256":"...","sample_rate":16000,"sample_count":305120,
 "annotation_revision":0,"model_source":"bootstrap_development","model_id":"panns-development-20260929",
 "regions":[{"start_sample":0,"end_sample":5120,"label":"speech","tolerance_samples":0,"score":0.1,"basis":"speech_family_and_low_singing_score"}],
 "summary":"我先听到：……你可以确认，也可以拖动边界纠正我。",
 "requires_confirmation":true,"execution_authorized":false}
```

Full results also contain model provenance, adapter/backend identity, policy, original sample cell edges and uncalibrated scores. Native regions cover the entire original recording and merge only consecutive cells with the same label. They are rough hypotheses, not verified vocal boundaries. `singing` uses score >= .5; `speech` requires raw speech-family max >= .15 and singing score < .4. Only extremely low PCM RMS (<= .001 of full scale in every channel, about -60 dBFS) is provisionally `environment`; other negative/ambiguous cells are `uncertain`. Acoustic context can span seconds despite the native 320 ms cell grid. No ASR word timing, smoothing, gap filling or audio cropping is claimed.

The latest non-invalidated compatible personal head is preferred; out-of-date heads remain usable for proposals while new samples accumulate. `model_source:"personal_provisional"` denotes an initial head fitted on confirmed examples without independent validation; `personal` denotes a head with the existing development split. Cold start uses `assets/bootstrap-panns-development.json`, the earlier seven-recording development head, with exact checkpoint SHA and feature-definition checks. Its `model_source` and provenance explicitly report that it has **no held-out acceptance or cross-user/song validation**. It is an editable initial guess, not a production singing gate. Confirmed annotations stay authoritative; requesting predictions never overwrites them. Caches are immutable and keyed by original audio hash, annotation revision, acoustic backend, adapter and policy. A concurrent confirmation cannot replace the new human revision with an old proposal.

## Training, jobs, status and shadow

- `POST /learning/profiles/{profile_id}/train` JSON `{}` → `{job_id,status}`; background feature preparation and frozen snapshot training/evaluation. Training snapshots revisions at job execution. Each version is immutable. Corrections to any recording annotation in the frozen source snapshot invalidate that candidate. Merely adding new recordings makes it `stale/out_of_date` but does not invalidate its previous training evidence.
- `GET /learning/jobs/{job_id}` → `{job_id,profile_id,kind,status:"queued|running|completed|failed|interrupted",created_at,started_at?,finished_at?,progress?,result?,error?}`.
- `GET /learning/profiles/{profile_id}/status` → `{profile_id,dataset_revision,recording_count,feature_counts,label_seconds,independent_group_count,readiness_reasons,current_candidate,latest_training,jobs,execution_authorized:false}`. Current candidate includes `model_id,source_dataset_revision,stale,out_of_date,invalidated`; latest training exposes `status:insufficient_data` with reasons. Label seconds are `{singing,speech,environment,excluded}`. Completed train job result includes `dataset_summary` and, for a candidate, `validation_duration_confusion:{tp_seconds,fn_seconds,fp_seconds,tn_seconds}`; failed training retains a full immutable source snapshot and a failed report.
- `GET /learning/profiles/{profile_id}/models` → `{models:[...]}`.
- `GET /learning/profiles/{profile_id}/models/{model_id}` → immutable full report (source revisions, group assignments, adapter, validation predictions, duration confusion and predicted range endpoints).
- `POST /learning/profiles/{profile_id}/shadow` JSON `{recording_id,model_id?}` → queued job. Defaults latest non-invalidated candidate, including an out-of-date candidate after new unlabelled uploads. Explicit old invalidated model is allowed only for inspection, with flags. Result includes `stale,invalidated,trained_dataset_revision,current_dataset_revision`. Prediction never modifies annotations/audio and returns sample range candidates only. Previously unlabelled recordings in an old full snapshot that receive their first annotation also conservatively invalidate that snapshot.

Personalization can begin before validation is eligible. Once explicit human-confirmed user regions contain at least .32 seconds each of singing and non-singing after boundary tolerance exclusion, an initial head can fit all available known examples. It has `status:"candidate"`, `model_stage:"provisional"`, `evaluation_scope:"training_only_unvalidated_personalization"`, `validation_available:false`, and **no validation confusion or accuracy claim**. Its reports mark predictions `split:"training_only"`; the previous development eligibility reasons are retained in `validation_reasons`. It changes only future editable hypotheses. For these provisional hypotheses, the score is a fixed **75% bootstrap / 25% personal** mixture, to bound the effect of sparse lessons. This mixture is explicitly stored in `score_blend`, with raw personal/prior scores available separately and the prior hash in the cache key; it is not a calibrated probability or proof of improvement. Training reports retain the unblended fit. The bootstrap checkpoint/feature checks also apply to this mixture. Repeated/same-song samples remain in their true connected group; one lesson is not presented as multiple independent lessons. One-class or insufficient-duration feedback is stored, but cannot fit a binary personal classifier yet: `personalization_reasons` (also insufficient train `reasons`) contains `need_confirmed_singing_example` and/or `need_confirmed_non_singing_example`.

For development validation, grouping takes connected components over same session, parent record links/parent tokens, content group, exact WAV hash and recorder lineage source IDs. Labels and model scores never determine split. Group identity is stable hash of sorted member recording IDs. SHA-sorted groups: last `max(1,floor(n/3))` are validation, others training. Require >=3 independent components, >=2 training groups and >=1 validation group, and positive and negative known duration in both splits. If eligible, the head uses the train split only and has `model_stage:"development_validated"` plus validation predictions. Otherwise only the explicitly unvalidated provisional path above is allowed; do not reshuffle labels to manufacture eligibility. This is **development validation re-split on dataset changes**, not frozen blind-test evidence or new-song-generalization proof.

Native acoustic cells use exact original-sample overlap with known labels; soft target = positive known samples / known samples. Weight normalizes each training connected group to equal total known duration. No class weighting. This is group equal weighting, not unique-PCM overlap deduplication; multiple crops inside a group can affect its internal positive/negative ratio. Feature standardizer fits train only; deterministic L2 logistic lambda .1. Threshold .5, no smoothing/gap fill/short-region filtering. Validation reports exact known-sample duration TP/FP/TN/FN, label coverage and uncalibrated per-cell scores plus integer range endpoints. Tolerance/unknown gaps do not contribute metric denominators.

Configuration environment: `SINGING_LEARNING_DATA`, `SINGING_LEARNING_CHECKPOINT`, `SINGING_LEARNING_LABELS`, `SINGING_LEARNING_DEPS`, `SINGING_LEARNING_DEVICE` (default cpu), `SINGING_LEARNING_TORCH_THREADS` (default 2), `SINGING_LEARNING_HOST` (default 127.0.0.1), `SINGING_LEARNING_PORT` (default 9884). Run `python -m Server.SingingLearning`; requires existing FastAPI/uvicorn/python-multipart/numpy; PANNs runtime dependencies are configured separately. No dependency installation or automatic model download occurs.
