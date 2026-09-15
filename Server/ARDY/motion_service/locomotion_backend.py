"""Full-body history conversion and constrained generation on the shared ARDY."""
from __future__ import annotations

import time
import numpy as np

from Server.ARDY.locomotion import Route, constraint_tensors, export_locomotion
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY
from .backend import grounded_source_root
from .protocol import ServiceError
from .locomotion_protocol import CORE_NAMES
from .locomotion_quality import MAX_QUALITY_ATTEMPTS, QUALITY_SEED_STRIDE, decoded_window_quality


def capabilities(backend):
    if not backend.info.get("ready", False):
        raise ServiceError(503, "not_ready", "ARDY is not ready")
    root, _ = grounded_source_root(backend.neutral, backend.names)
    return {"ready": True, "schema": 1, "fps": 20, "historyFrames": 16, "maxFrames": 200,
        "sourceRootHeight": float(root[1]), "jointNames": backend.names,
        "coordinateSystem": "unity-lh-y-up-z-forward", "terrain": "single-flat-support-plane",
        "maxTargetSpeed": 1.5, "maxHeadingDegreesPerFrame": 9,
        "languageModelsLoaded": False, "adapterSha256": backend.adapter_sha,
        "streamProtocol": "ardy-room-stream-v1", "streamCommitFrames": 8,
        "streamGenerationFrames": 40, "streamAllowedCommitFrames": [8, 16],
        "streamHistoryFrames": 16, "streamMaxLookaheadFrames": 184,
        "streamRequiresClientAudit": True}


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
        "kind": history.kind, "rootAndLegs": ("projected from actually rendered Humanoid poses; not neutral synthetic legs"
            if history.kind == "unity-full-body-projection-v1" else
            "predicted source history from an audited previous continuation seam; not an observed Unity pose"),
        "missingCoreJoints": ("client maps intermediate/terminal joints from adjacent observed Humanoid rotations"
            if history.kind == "unity-full-body-projection-v1" else "none; complete generated Core27 source history"),
        "contacts": "source FK height/velocity heuristic; not observed target-avatar foot contact"}


def _sample_standing_window(backend, request, history, condition, observed, mask, count,
                            cancelled, source_height, index, cuda_index):
    """Sample at most three native alternatives from the SAME retained history.

    The first attempt consumes the existing RNG stream. Only a retry reseeds,
    using the stream policy's stride; failed candidates never advance history.
    """
    torch = backend.torch
    with torch.inference_mode():
        retained = backend.model.motion_rep.inverse(history, is_normalized=True)
    candidates = []
    for attempt in range(MAX_QUALITY_ATTEMPTS):
        if cancelled.is_set():
            raise ServiceError(409, "cancelled_revision", "Room motion was cancelled between quality candidates")
        seed = request.seed if index == 0 else None
        if attempt:
            seed = (request.seed + index // 40 + attempt*QUALITY_SEED_STRIDE) % 2147483648
            torch.random.default_generator.manual_seed(seed)
            if cuda_index is not None:
                torch.cuda.default_generators[cuda_index].manual_seed(seed)
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
        window_ms = (time.perf_counter()-began)*1000
        if cancelled.is_set():
            raise ServiceError(409, "cancelled_revision", "Room motion was cancelled during quality generation")
        quality_began = time.perf_counter()
        reasons = decoded_window_quality(decoded, retained, 40, source_height)
        candidates.append({"attempt": attempt+1, "seed": seed,
            "rng": "explicit-retry-seed" if attempt else "existing-first-candidate-rng",
            "windowMs": window_ms, "qualityMs": (time.perf_counter()-quality_began)*1000,
            "accepted": not reasons, "reasons": reasons})
        if not reasons:
            return motion, decoded, candidates
    raise ServiceError(422, "locomotion_quality_rejected",
        "All three native candidates failed the complete-window standing locomotion audit",
        {"windowStartFrame": index, "timings": {"qualityAttempts": len(candidates), "qualityCandidates": candidates},
         "scope": "Candidate rejection only; no skeleton, root height, contact, or pose was repaired. No failed candidate advances retained history."})


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
    if list(backend.names) != list(CORE_NAMES):
        raise RuntimeError("Standing locomotion quality audit requires the pinned Core27 joint order")
    source_root, _ = grounded_source_root(backend.neutral, backend.names)
    source_height = float(source_root[1])
    chunks, times, quality_windows = [], [], []
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
            try:
                motion, decoded, candidates = _sample_standing_window(backend, request, history, condition,
                    observed, mask, count, cancelled, source_height, index, cuda_index)
            except ServiceError as error:
                if error.code == "locomotion_quality_rejected":
                    error.details["previousAcceptedWindows"] = quality_windows
                raise
            quality_windows.append({"startFrame": index, "auditedFrames": 40, "candidates": candidates})
            times.extend(item["windowMs"] for item in candidates)
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
         "qualityAudit": "Complete generated windows: same standing height, source-root step and applied-joint step checks as continuous follow; at most three untouched native candidates per window. This does not certify naturalness.",
         "postprocessing": "none; client collision guard may refuse generated samples",
         "languageModelsLoaded": False, "naturalnessAccepted": False})
    clip.update(characterId=request.characterId, requestId=request.requestId, revision=request.revision,
                timings={"windowsMs": times, "qualityAttempts": sum(len(w["candidates"]) for w in quality_windows),
                    "qualityWindows": quality_windows, "backendMs": (time.perf_counter()-start)*1000})
    return clip
