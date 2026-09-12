# Local adapter and ARDY motion service

For the normal full stack after a reboot, use the project-root `start_all.cmd`.
It starts the existing shared Qwen in motion-feature mode, checks that live
feature route, and starts managed background ARDY on 8093 before the voice
services. `status.cmd` checks ARDY and its live dependency; `stop_all.cmd` stops
the recorded ARDY process before stopping Qwen. Logs are
`Server/RuntimeLogs/ardy.out.log` and `ardy.err.log`.

To restore only ARDY when Qwen feature mode is already running:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools/neeeva_ardy.ps1 start
```

The manager also accepts `status` and `stop`. Starts are idempotent, including
an already-loading managed process; stale PID records cannot stop a reused PID.
An independently launched healthy service can be reused, but must be stopped
from its original console. An active legacy Qwen is not silently replaced:
stop the stack while idle and run `start_all.cmd` again to select feature mode.
The foreground launcher below remains available for manual operation.

Run from the repository root with the existing ARDY environment:

```powershell
& Server/ARDY/start_motion_service.ps1
```

The service binds only `127.0.0.1:8093`. The default feature URL, `http://127.0.0.1:8080`, is the same local tunnel used for chat and reaches the single shared Qwen process. Without an active preview release, it loads the evaluated `mlp-seed1.pt`; a valid active release selects its bound adapter. Both modes load pinned ARDY Core 20 FPS / Horizon 40 with `text_encoder=False`. Weights must already exist in the local HF cache. It does not download or load a language model.

The wire contract is documented in `docs/ardy-live-protocol.md`. `POST /v1/motion/generate` accepts the character/turn/revision, request ID, chunk index, description, `UpperBody` mask and optional initial projected history. Every response contains exactly 40 new frames in the existing Unity schema 1. Chunks 0, 1 and 2 cover six seconds. `final` is true on chunk 2. The application mask affects Unity playback; it is not an ARDY inpainting constraint. Generated lower-body/root motion is not applied by this stationary upper-body client.

`initialHistory` is optional on chunk 0. Its `unity-upper-body-projection-v1` frames contain the fixed Core27 order, global canonical Unity quaternions, identity root/legs, and the Unity client's reconstructed upper body. The service reflects those rotations back to ARDY, converts them to local rotations, places the synthetic source foot/toe support plane at world Y=0 and calls ARDY's official motion representation. Pelvis height is derived from the minimum neutral foot/toe Y relative to Hips; the reference skeleton's Hips `[0,0,0]` is not a valid standing world position. With the default conditioning length, fewer than 16 frames are padded at the beginning. Foot contacts are inferred from synthetic stationary source legs, not observed avatar contacts. Continuations use the last 16 normalized frames from the last successfully committed generation. Returned history reconstruction is never appended to the client or used to replace already played frames.

Unity resamples adjacent actually rendered upper-body poses onto a fixed 20 Hz timeline using quaternion Slerp. A render gap longer than 0.15 seconds clears the ring instead of inventing motion through the stall. These are resampled played poses, not a capture of full-body contacts, and a newly selected revision starts from that ring.

Revision numbers must increase monotonically per character, including across turns. A new revision starts from its submitted played-pose history, never the unplayed future of the previous revision. `POST /v1/motion/cancel` immediately tombstones the revision. Waiting feature HTTP work is cancelled. CUDA inference cannot be forcibly stopped halfway through; its result is discarded before any history/RNG update. One GPU worker serializes ARDY's mutable diffusion state. A private CPU/CUDA RNG state for each revision is restored inside `torch.random.fork_rng`; interleaving other characters cannot change its generated sequence.

The explicit request `seed` initializes each private RNG stream directly. Character, turn and revision IDs select the stored stream but do not alter the seed. Matching initial history, condition and explicit seed are therefore reproducible across identities; different played histories can still produce different motion at the same seed.

An explicit initial-history experiment may set `initialHistory.conditioningFrames` to the integer 4 or 8. Omission preserves 16, and 16 is also accepted explicitly. The backend keeps the newest N supplied frames and pads the beginning only when fewer than N were supplied. It reports supplied/used/discarded/padded counts and returns the actual N in first-window `historyFrames`; every continuation still uses 16 frames. Unity's `initialConditioningFrames` defaults to 16 and its default wire serialization omits the extension for legacy-server compatibility. The 16-frame measured-pose ring is unchanged. Fractional/string/zero/negative lengths are rejected. This setting is an opt-in experiment, not a default deployment change.

There are at most four queued jobs plus one running job, and 64 character slots including cancellation tombstones. Slots are not silently evicted, because that could allow a delayed old revision to become valid again. Each slot retains only the latest revision, last chunk response, 16 history frames, condition and RNG. A duplicate of the most recent request returns the same response. Changed request contents, changed descriptions/seeds within a revision, gaps, cancelled revisions and older revisions return 409. Queue or character capacity returns 429. Invalid schema returns 400; the JSON body limit is 128 KiB. A unavailable service/feature route returns 503, feature protocol mismatch 502, and deadline expiry 504. Client disconnection discards its job. Deadlines include feature queueing, ARDY queueing and inference and are capped at 20 seconds.

The first chunk of a revision fetches a fresh raw 2048-dimensional Qwen representation. The response must echo the exact text/request ID, feature contract, selected model SHA256, raw-final-token pooling and shared-model marker. Later chunks reuse that same live condition within the revision and report this explicitly. Live failure never falls back to cached embeddings. The four accepted local Unity baselines are not changed by this service. All dynamic output carries `mode: dynamic-ardy-native` and is not automatically declared naturalness-approved.

Verification:

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m unittest Server.ARDY.test_motion_service -v
& Server/ARDY/.venv/Scripts/python.exe -m Server.ARDY.validate_motion_service
& Server/ARDY/.venv/Scripts/python.exe -m Server.ARDY.validate_motion_service --live --report Tools/MotionAdapter/reports/ardy-motion-service-live.json
```

To check the already-running formal service without loading another ARDY instance, run `python -m Server.ARDY.smoke_motion_service` with the ARDY environment. It verifies two real HTTP windows, source/model binding, cancellation and the final health state, then leaves the service running.

The validation process uses temporary port 8094 and exits its own service when finished. It exercises real ARDY over HTTP, three-window history, exact RNG replay after another character interleaves, initial-history conversion, idempotency, actual-inference cancellation and stale-result rejection. Its initial history is a synthetic projection fixture; the live Unity capture is a separate integration test. The first validation command without `--live` deliberately injects cached test features and labels them `test-cache-fixture`. The production launcher only enables that path with explicit `-TestFixture`.

The existing ARDY environment already supplies FastAPI, uvicorn, httpx, numpy, scipy and CUDA torch. No existing process is stopped by these commands. The server runs in the foreground so its lifecycle is explicit; background callers must track and stop only the instance they launched.

For an explicitly selected experimental adapter, use the Python CLI on a separate port:

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Server.ARDY.motion_service.app `
  --port 8095 --feature-url http://127.0.0.1:8080 `
  --adapter-selection <candidate-directory/selection.json>
```

This is an isolated evaluation entry; selecting a candidate here does not activate it for ordinary startup. Candidate mode refuses port 8093 and cached test features. It requires the fixed four-candidate validation selection, an eligible winning checkpoint, its unchanged SHA, the matching preregistered policy and merge manifest, the original Qwen contract, the exact MLP tensor shapes, finite weights and the original frozen mean/std. The checkpoint must reside beside its selection lock. The baseline checkpoint must still match the original preview report; no approval report is rewritten. Ordinary startup follows the evidence-bound release mechanism below.

Candidate `/health` exposes `backend.adapterMode="candidate"`, `candidate=true`, `approvedForRuntime=false`, checkpoint SHA, selection SHA and locked time. Feature qualification does not establish motion quality. The lock binds local artifacts and is not a cryptographic signature. Generation/history/cancellation behavior is identical to the default service, and output provenance identifies the actual candidate checkpoint SHA. Start the process only when an isolated GPU window is available; exit that process after evaluation.

CPU-only lock validation: `python -m unittest Server.ARDY.test_adapter_selection -v`. Its synthetic selection metrics test rejection and provenance handling; they are not real candidate quality results.

An evidence-bound preview release allows a validated candidate to be selected by ordinary startup. Build an **inactive** manifest only after all actual validation runs finish:

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Server.ARDY.motion_service.adapter_release create `
  --selection Tools/MotionAdapter/runtime/generalization-v1/candidates/selection.json `
  --native-final Server/ARDY/runtime/generalization-final-supported-v1/report.json `
  --feature-final Tools/MotionAdapter/reports/generalization-final-feature-audit-v1.json `
  --http-live Server/ARDY/runtime/candidate-live-http-v1/report.json `
  --unity-live Tools/MotionAdapter/reports/candidate-unity-live-v1.json `
  --output Server/ARDY/runtime/releases/generalization-v1.json

& Server/ARDY/.venv/Scripts/python.exe -m Server.ARDY.motion_service.adapter_release verify `
  --manifest Server/ARDY/runtime/releases/generalization-v1.json
```

Creation refuses incomplete final native or feature audits, mismatched checkpoint/selection hashes, failed HTTP/Unity reports, incomplete native trajectories, and Unity traces whose three responses do not bind the exact candidate, live Qwen feature contract and request lifecycle. Reports and the completed Unity trace are archived byte-for-byte beside the new manifest; later rotation of the live eight-trace ring does not invalidate the archive. The locked selection and checkpoint remain at their original paths and are strictly revalidated on load. The creator refuses the active-file destination, never overwrites existing releases, and never starts a service.

For explicit preview use, run `Server/ARDY/start_motion_service.ps1 -AdapterRelease <manifest>`; port 8093 is permitted. After reviewing a verified manifest, activation is a separate explicit file copy to `Server/ARDY/runtime/active-adapter-release.json`. Subsequent ordinary launcher runs validate that file and its evidence before using the released adapter. An invalid active manifest stops startup; it never silently falls back. `-AdapterSelection <selection.json> -Port 8095` keeps isolated testing independent of an active release. The three launcher options `-AdapterRelease`, `-AdapterSelection`, and `-BaselineAdapter` are mutually exclusive and pass directly to the corresponding Python CLI checks.

To use the preserved original baseline regardless of an active release, run `Server/ARDY/start_motion_service.ps1 -BaselineAdapter`. The Python equivalent is `python -m Server.ARDY.motion_service.app --baseline-adapter`. No old checkpoint or preview approval report is replaced. Preview health reports `adapterMode="verified-preview"`, `previewReleaseVerified=true`, release ID and all evidence hashes. It retains `naturalnessAccepted=false` and the checkpoint's `approvedForRuntime=false`: completed integration and descriptive final audits permit this explicit preview, but do not certify arbitrary-action success or subjective naturalness. Default initial conditioning remains 16 frames.

CPU-only publication checks: `python -m unittest Server.ARDY.test_adapter_release -v`. Synthetic fixture reports verify the evidence gate itself and must never be used as real release evidence.
