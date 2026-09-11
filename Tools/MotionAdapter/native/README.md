# Shared Qwen motion feature route

This overlay extends the pinned llama.cpp b8919 server. The normal chat and image
routes retain their configuration and three 65,536-token slots. A separate
512-token context uses the **same `llama_model *`**. No extra model is loaded.

`POST /neeeva/motion-features` accepts `text`, `request_id`, and
`feature_contract: "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0"`.
The protocol name is `qwen-motion-raw-last-v1`. It returns an unnormalised
2048-float `embedding` from the last input token of the exact training template
`Motion description: {text}\nRepresentation:`. The context is cleared before and
after each request. Work runs only on the existing inference loop, when all chat
slots are idle; one request may wait for at most 15 seconds. Oversize prompts,
queue saturation and expired work fail explicitly, without falling back to a
different feature source.

The response echoes the exact `text` and `request_id` and includes
`feature_source: "live-qwen"`, `shared_model`, `model_sha256`, slot/context sizes,
and `timings: {queue_ms, encode_ms}`. HTTP 400 means invalid contract, text or
input size. HTTP 503 means the one-entry queue is full or its 15-second wait
expired. The route never silently truncates a prompt.

This is an opt-in build. Enablement requires `NEEEVA_MOTION_FEATURES=1` and the
model hash verified by the launcher. The overlay and build scripts never replace or stop the
existing production server. Remote startup must verify no other Qwen process is
loaded first. The first integration uses port 8082.

`apply_overlay.py` copies the pinned tree into an ignored build source directory,
then applies checked, single-match edits. The original pinned checkout is kept
unchanged. `feature-context.inc` is included inside `server_context_impl`;
`feature-route.inc` is included inside `server_routes::init_routes`.

Build with `native/build_server.ps1`, stage with
`native/deploy_server.ps1 -Action stage`, and start with `-Action start`.
The deployed executable uses **all the original official b8919 DLLs**, including
the llama core, common library, vision projector and CUDA backends. Newly built
DLLs are not deployed. This preserves the training numerical runtime and the
existing vision library. The new executable SHA is recorded separately in
`provenance.json`; `--version` reports the original common DLL's build identity.

`validate_http.py --base-url http://127.0.0.1:18082` checks live output against
cached training features, interleaved chat/cache isolation, priority/queue limits,
and a real mmproj request using a synthetic red image. Matching the configured
65,536-token slot capacity is not a claim that all slots have been stress-tested
while filled to that length.

## Normal NeEEvA startup

From the project root, use:

```powershell
./Tools/neeeva_remote_llm.ps1 -Action start -Mode feature
```

Chat, vision and motion features are then available on **the same local
`http://127.0.0.1:8080`**. BGE remains on local port 8090. ARDY should use
`http://127.0.0.1:8080/neeeva/motion-features` as its feature endpoint.

The 4090-side wrapper asks the remote controller for the running Qwen's actual
port. The currently validated feature instance uses remote 8082, so the usual
local 8080 SSH forward targets remote 8082. This reuses the loaded model without
restarting it. On a future cold start, feature mode requests remote 8080. No
extra model or HTTP proxy is required. The temporary 18082 validation tunnel is
independent and may remain available while an ongoing integration test uses it.

Calling the ordinary `-Action start` while feature mode is running also reuses
that same instance and its actual port. It cannot silently load a second Qwen.
If legacy Qwen is already running, requesting feature mode fails explicitly and
leaves it running. A deliberate mode switch can be made after the conversation
ends with `-Action restart -Mode feature`; restarting loses in-memory KV state,
so this is never done implicitly by `start`.

Both remote launch paths use a shared named startup mutex and inspect all Qwen
processes. Unknown or duplicate Qwen processes are refused. Feature startup
verifies the full model SHA before loading and checks again for an existing
Qwen under that lock. An already-running feature server is reused even when its
port differs from the requested cold-start port.

`native/deploy_server.ps1 -Action stage` installs the isolated executable and
the two controller scripts; it refuses to update binaries while the feature
server is running. `-Action controllers` updates only the scripts without
interrupting the model. The previous remote production controller is preserved
as `D:\NeEEvA\services\neeeva_remote_llm.before-feature.ps1`; the original
production executable and DLLs are unchanged. The feature launcher is under
`D:\NeEEvA\motion-feature-server` and manages only that executable.

`test_startup_guards.ps1` tests refusal/reuse logic against mocked process
snapshots, without starting or stopping processes. The actual reuse and
localhost-8080 startup checks are recorded in
`reports/shared-qwen-startup.json`.
