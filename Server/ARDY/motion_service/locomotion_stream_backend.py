"""Bounded native H40 candidates; retain only the audited 8/16-frame prefix."""
from __future__ import annotations

import time
import numpy as np

from Server.ARDY.locomotion import Route, constraint_tensors, export_locomotion
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY
from .backend import grounded_source_root
from .locomotion_backend import project_full_body
from .locomotion_protocol import CORE_NAMES, FullBodyFrame
from .protocol import ServiceError
from .locomotion_stream_protocol import STREAM_PROTOCOL


from .locomotion_quality import (MAX_QUALITY_ATTEMPTS, QUALITY_SEED_STRIDE,
    decoded_window_quality as _prefix_quality)


def generate_stream_step(backend, request, embedding, context, cancelled):
    from scipy.spatial.transform import Rotation
    torch = backend.torch
    began = time.perf_counter()
    if cancelled.is_set():
        raise ServiceError(409, "cancelled_revision", "Stream was cancelled")
    if backend.model.gen_horizon_len != 40 or backend.model.motion_rep.fps != 20:
        raise RuntimeError("Stream v1 requires the evaluated Core H40 model")
    if list(backend.names) != list(CORE_NAMES):
        raise RuntimeError("Stream quality audit requires the pinned Core27 joint order")
    source_root, _ = grounded_source_root(backend.neutral, backend.names)
    source_height = float(source_root[1])
    route = Route(np.array([[getattr(t.rootPosition, k) for k in "xyz"] for t in request.targets]),
                  np.radians([t.headingDegrees for t in request.targets]), -1).validate()
    cuda_index = None
    if backend.device.type == "cuda":
        cuda_index = backend.device.index if backend.device.index is not None else torch.cuda.current_device()
    with torch.random.fork_rng(devices=[] if cuda_index is None else [cuda_index]):
        with torch.inference_mode():
            if context.get("history") is None:
                history, projection = project_full_body(backend, request.initialHistory)
            else:
                history = context["history"]
                projection = {"kind": "client-acknowledged-generated-prefix-v1", "frames": 16,
                    "previousRequestId": request.previousRequestId,
                    "meaning": "previous retained history plus only its acknowledged output prefix; never the unplayed prediction tail"}
            condition = context.get("condition")
            if condition is None:
                condition = backend.adapter(torch.as_tensor(np.asarray(embedding)[None], dtype=torch.float32,
                    device=backend.device)).reshape(1, 1, 4096).detach()
            if not torch.isfinite(condition).all():
                raise RuntimeError("Adapter returned nonfinite condition")
            observed, mask, count = constraint_tensors(backend.model, route, 0, 16, lookahead=len(request.targets))
            retained = backend.model.motion_rep.inverse(history, is_normalized=True)
            candidates = []
            for attempt in range(MAX_QUALITY_ATTEMPTS):
                if cancelled.is_set():
                    raise ServiceError(409, "cancelled_revision", "Stream was cancelled between quality candidates")
                seed = (request.seed + request.stepIndex + attempt*QUALITY_SEED_STRIDE) % 2147483648
                torch.random.default_generator.manual_seed(seed)
                if cuda_index is not None:
                    torch.cuda.default_generators[cuda_index].manual_seed(seed)
                    torch.cuda.synchronize(backend.device)
                step_began = time.perf_counter()
                motion = backend.model.autoregressive_step(num_frames=count,
                    num_denoising_steps=int(backend.model.diffusion.num_base_steps), motion_mask=mask,
                    observed_motion=observed, cfg_weight=(2., 2.), text_feat=condition,
                    text_pad_mask=torch.ones((1, 1), dtype=torch.bool, device=backend.device), init_history_sequence=history)
                if motion.shape[:2] != (1, 56) or not torch.isfinite(motion).all():
                    raise RuntimeError("Invalid stream motion window")
                decoded = backend.model.motion_rep.inverse(motion, is_normalized=True)
                if cuda_index is not None:
                    torch.cuda.synchronize(backend.device)
                window_ms = (time.perf_counter()-step_began)*1000
                if cancelled.is_set():
                    raise ServiceError(409, "cancelled_revision", "Cancelled stream candidate is discarded")
                quality_began = time.perf_counter()
                reasons = _prefix_quality(decoded, retained, request.commitFrames, source_height)
                candidates.append({"attempt": attempt+1, "seed": seed, "windowMs": window_ms,
                    "qualityMs": (time.perf_counter()-quality_began)*1000, "accepted": not reasons, "reasons": reasons})
                if not reasons:
                    break
            else:
                raise ServiceError(422, "stream_quality_rejected", "All three native motion candidates failed the committed-prefix quality audit",
                    {"timings": {"qualityAttempts": len(candidates), "qualityCandidates": candidates,
                        "windowsMs": [item["windowMs"] for item in candidates], "backendMs": (time.perf_counter()-began)*1000}})
            # ARDY's decoded history is not used to rewrite previously accepted samples.
            # For an 8-frame commit, retain 8 original history frames + the 8 new frames.
            next_history = torch.cat((history, motion[:, 16:16+request.commitFrames]), dim=1)[:, -16:].detach().clone()
            arrays = {k: v[0, 16:].detach().float().cpu().numpy() for k, v in decoded.items() if torch.is_tensor(v)}
    if cancelled.is_set():
        raise ServiceError(409, "cancelled_revision", "Cancelled stream output is discarded")
    if any(len(v) != 40 or not np.isfinite(v).all() for v in arrays.values()):
        raise RuntimeError("Invalid decoded stream body")
    arrays.update(fps=np.asarray(20), text=np.asarray(context["description"]),
                  joint_names=np.asarray(backend.names), joint_parents=backend.parents)
    clip = export_locomotion(arrays, Route(route.positions[:40], route.headings[:40], -1), backend,
        f"stream-{request.characterId}-{request.revision}-{request.stepIndex}",
        {"mode": "room-ardy-stream", "streamProtocol": STREAM_PROTOCOL,
         "generation": "shared Qwen condition + adapter + bounded native H40 candidate sampling",
         "ardyRevision": backend.lock["ardy"], "adapterSha256": backend.adapter_sha,
         "initialHistoryProjection": projection,
         "generationConstraints": "Root2D XZ and heading; continuously changing speed and direction",
         "terrain": "client must audit retained prefix and usable prediction tail on one support plane",
         "postprocessing": "none", "languageModelsLoaded": False, "naturalnessAccepted": False})
    end = request.commitFrames-1
    unity_rotations = ARDY_TO_UNITY @ arrays["global_rot_mats"][end] @ ARDY_TO_UNITY
    quats = Rotation.from_matrix(unity_rotations).as_quat()
    seam = FullBodyFrame(rootPosition=clip["frames"][end]["rootPosition"],
        globalRotations=[dict(zip("xyzw", map(float, q))) for q in quats])
    clip["timings"] = {"windowsMs": [item["windowMs"] for item in candidates],
        "qualityAttempts": len(candidates), "qualityCandidates": candidates, "backendMs": (time.perf_counter()-began)*1000}
    return {"clip": clip, "history": next_history, "condition": condition, "seam": seam}
