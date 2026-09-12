"""Full-body history conversion and constrained generation on the shared ARDY."""
from __future__ import annotations

import time
import numpy as np

from Server.ARDY.locomotion import Route, constraint_tensors, export_locomotion
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY
from .backend import grounded_source_root
from .protocol import ServiceError


def capabilities(backend):
    if not backend.info.get("ready", False):
        raise ServiceError(503, "not_ready", "ARDY is not ready")
    root, _ = grounded_source_root(backend.neutral, backend.names)
    return {"ready": True, "schema": 1, "fps": 20, "historyFrames": 16, "maxFrames": 200,
        "sourceRootHeight": float(root[1]), "jointNames": backend.names,
        "coordinateSystem": "unity-lh-y-up-z-forward", "terrain": "single-flat-support-plane",
        "maxTargetSpeed": 1.5, "maxHeadingDegreesPerFrame": 9,
        "languageModelsLoaded": False, "adapterSha256": backend.adapter_sha}


def project_full_body(backend, history):
    from scipy.spatial.transform import Rotation
    torch = backend.torch
    quats = np.asarray([[[getattr(q, key) for key in "xyzw"] for q in frame.globalRotations] for frame in history.frames])
    unity = Rotation.from_quat(quats.reshape(-1, 4)).as_matrix().reshape(16, 27, 3, 3)
    global_rots = ARDY_TO_UNITY @ unity @ ARDY_TO_UNITY
    local = global_rots.copy()
    for joint, parent in enumerate(backend.parents):
        if parent >= 0:
            local[:, joint] = global_rots[:, parent].swapaxes(-1, -2) @ global_rots[:, joint]
    roots_unity = np.array([[getattr(f.rootPosition, key) for key in "xyz"] for f in history.frames])
    roots = roots_unity @ ARDY_TO_UNITY
    with torch.inference_mode():
        motion = backend.model.motion_rep(
            torch.as_tensor(local[None], dtype=torch.float32, device=backend.device),
            torch.as_tensor(roots[None], dtype=torch.float32, device=backend.device),
            to_normalize=True, to_canonicalize=False)
        decoded = backend.model.motion_rep.inverse(motion, is_normalized=True)
    error = float(np.max(np.abs(decoded["global_rot_mats"][0].cpu().numpy()-global_rots)))
    root_error = float(np.max(np.abs(decoded["root_positions"][0].cpu().numpy()-roots)))
    return motion, {"frames": 16, "rotationRoundtripMaxAbs": error, "rootRoundtripMaxAbs": root_error,
        "kind": history.kind, "rootAndLegs": "projected from actually rendered Humanoid poses; not neutral synthetic legs",
        "missingCoreJoints": "client maps intermediate/terminal joints from adjacent observed Humanoid rotations",
        "contacts": "source FK height/velocity heuristic; not observed target-avatar foot contact"}


def generate_room_motion(backend, request, embedding, cancelled):
    torch = backend.torch
    start = time.perf_counter()
    if cancelled.is_set():
        raise ServiceError(409, "cancelled_revision", "Room motion was cancelled")
    route = Route(np.array([[getattr(t.rootPosition, k) for k in "xyz"] for t in request.targets]),
                  np.radians([t.headingDegrees for t in request.targets]), len(request.targets)-20).validate()
    cuda_index = None
    if backend.device.type == "cuda":
        cuda_index = backend.device.index if backend.device.index is not None else torch.cuda.current_device()
    chunks, times = [], []
    with torch.random.fork_rng(devices=[] if cuda_index is None else [cuda_index]):
        torch.random.default_generator.manual_seed(request.seed)
        if cuda_index is not None:
            torch.cuda.default_generators[cuda_index].manual_seed(request.seed)
        history, projection = project_full_body(backend, request.initialHistory)
        with torch.inference_mode():
            condition = backend.adapter(torch.as_tensor(np.asarray(embedding)[None], dtype=torch.float32,
                device=backend.device)).reshape(1, 1, 4096)
        if not torch.isfinite(condition).all():
            raise RuntimeError("Adapter returned nonfinite condition")
        for index in range(0, len(request.targets), 40):
            if cancelled.is_set():
                raise ServiceError(409, "cancelled_revision", "Room motion was cancelled between generation windows")
            observed, mask, count = constraint_tensors(backend.model, route, index, 16)
            if cuda_index is not None:
                torch.cuda.synchronize(backend.device)
            began = time.perf_counter()
            with torch.inference_mode():
                motion = backend.model.autoregressive_step(num_frames=count,
                    num_denoising_steps=int(backend.model.diffusion.num_base_steps), motion_mask=mask,
                    observed_motion=observed, cfg_weight=(2., 2.), text_feat=condition,
                    text_pad_mask=torch.ones((1, 1), dtype=torch.bool, device=backend.device), init_history_sequence=history)
                if motion.shape[:2] != (1, 56) or not torch.isfinite(motion).all():
                    raise RuntimeError("Invalid locomotion motion window")
                decoded = backend.model.motion_rep.inverse(motion, is_normalized=True)
            if cuda_index is not None:
                torch.cuda.synchronize(backend.device)
            times.append((time.perf_counter()-began)*1000)
            arrays = {k: v[0, 16:].detach().float().cpu().numpy() for k, v in decoded.items() if torch.is_tensor(v)}
            if any(len(v) != 40 or not np.isfinite(v).all() for v in arrays.values()):
                raise RuntimeError("Invalid decoded full-body motion")
            chunks.append(arrays)
            history = motion[:, -16:].detach().clone()
    if cancelled.is_set():
        raise ServiceError(409, "cancelled_revision", "Cancelled output is discarded")
    arrays = {k: np.concatenate([chunk[k] for chunk in chunks]) for k in chunks[0]}
    arrays.update(fps=np.asarray(20), text=np.asarray(request.description),
                  joint_names=np.asarray(backend.names), joint_parents=backend.parents)
    clip = export_locomotion(arrays, route, backend, f"room-{request.characterId}-{request.revision}",
        {"mode": "room-ardy-native", "generation": "Qwen condition + adapter + native ARDY path constraints",
         "ardyRevision": backend.lock["ardy"], "adapterSha256": backend.adapter_sha,
         "initialHistoryProjection": projection, "generationConstraints": "Root2D XZ and heading, projected full-body history",
         "terrain": "client-validated single flat support plane; no stairs or slopes generated",
         "postprocessing": "none; client collision guard may refuse generated samples",
         "languageModelsLoaded": False, "naturalnessAccepted": False})
    clip.update(characterId=request.characterId, requestId=request.requestId, revision=request.revision,
                timings={"windowsMs": times, "backendMs": (time.perf_counter()-start)*1000})
    return clip
