# Personal learning service implementation review

The independent service is implemented entirely in this directory. It imports the existing frozen `PannsFramewiseProvider` and the tested NumPy score adapter; it does not import or change the ASR app. `python -m Server.SingingLearning` starts one local HTTP process on port 9884. Root's launcher supplies local weights/dependencies; feature extraction is lazy, on CPU by default with two Torch threads.

## Verified in this task

Command from repository root:

```powershell
& 'Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe' -m unittest Server.SingingLearning.test_learning -v
```

18 tests passed (4.637 s on the recorded run). The HTTP test starts and stops an isolated ephemeral-port uvicorn process in a thread, uses stdlib urllib with proxies disabled, and exchanges synthetic PCM only. It covers the actual multipart endpoint, immutable byte-for-byte WAV return, explicit human annotation, compare-and-swap rejection, asynchronous completion, and status polling. No user audio is played, no ASR service is called, and no model is downloaded.

The remaining tests cover original hash/dimension checks, WAV/size/range bounds, path and Windows case aliases, append-only annotation revisions, explicit playback/mixed/unknown exclusion, blank/uncertain/tolerance masks, exact partial-cell duration overlap, connected components across every provenance relation, insufficient-data reports without model creation, actual numeric fit and held-out validation, deliberately different validation distributions excluded from standardization, source-bound feature caching, new unlabelled recordings scored by an out-of-date candidate, old-source correction invalidation, corrections while training, persisted failed-training snapshots, asynchronous queue coalescing/bounds/restart, and the exclusive data-directory process lock.

The feature backend in these tests is frozen synthetic score data. Real PANNs CPU timing and real Unity integration are separate root-owned validation; these 18 tests do not establish real-world recognition accuracy.

## Deliberate limits

- All outputs remain candidate/shadow with `execution_authorized=false`; native score cells are not exact vocal boundaries and are never connected to automatic cropping.
- Development validation re-splits from metadata components when the dataset changes. It is not a frozen blind holdout. All related sessions/content/PCM must stay in one component; three independent components and positive and negative known duration on both sides are required.
- Training weights each connected training group equally. It does not deduplicate overlapping physical samples inside a group; many crops can still affect that group's internal label ratio.
- Human labels are the only target source. `source_kind=user` is an explicit human assertion, not an automatic biometric verification. Empty or unconfirmed audio cannot silently become negative training data.
- New recordings make a candidate out of date but allow shadow use. Changing any annotation in its full frozen source snapshot invalidates it; an explicitly requested old model remains inspectable with flags. A correction never overwrites old data or model reports.
- One worker owns feature extraction and training; queued jobs are bounded and auto-training is debounced/coalesced. Interrupted jobs are not silently retried after restart. The endpoint returns persisted originals even when optional feature extraction fails.
- A data-directory process lease enforces one writer. Long inference is not force-killed during shutdown. The lease remains held if the worker has not exited; normal process exit releases it.

Files: `API.md`, `config.py`, `store.py`, `backend.py`, `pipeline.py`, `jobs.py`, `app.py`, `__init__.py`, `__main__.py`, `test_learning.py`, `.gitignore`, and this review.
