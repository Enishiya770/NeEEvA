"""Pinned ARDY inference and honest conversion of projected Unity history."""
from __future__ import annotations

import hashlib
import json
import time
from pathlib import Path

import numpy as np

from .protocol import FEATURE_CONTRACT, HISTORY_FRAMES, NEW_FRAMES, ServiceError

ROOT = Path(__file__).resolve().parents[3]


def grounded_source_root(neutral, names):
    """Place synthetic neutral source feet on Y=0; joints.p is root-relative."""
    neutral = np.asarray(neutral, dtype=np.float64)
    feet = ("LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase")
    if neutral.shape != (len(names), 3) or not np.isfinite(neutral).all():
        raise ValueError("Invalid source neutral joint positions")
    hips = neutral[names.index("Hips")]
    relative_y = np.array([neutral[names.index(name), 1]-hips[1] for name in feet])
    height = -float(relative_y.min())
    if not np.isfinite(height) or height <= 0:
        raise ValueError("Source foot/toe support plane must be below neutral Hips")
    root = np.array([0.0, height, 0.0])
    return root, dict(zip(feet, map(float, relative_y+height)))


class ArdyBackend:
    def __init__(self, device="cuda", minimum_free_mib=3072, adapter_selection=None, adapter_release=None):
        if adapter_selection is not None and adapter_release is not None:
            raise ValueError("Choose one adapter selection or verified preview release")
        self.device_name = device
        self.minimum_free_mib = minimum_free_mib
        self.adapter_selection = adapter_selection
        self.adapter_release = adapter_release
        self.info = {"ready": False, "languageModelsLoaded": False}

    def load(self):
        import torch
        import ardy
        from ardy.model import load_model
        from huggingface_hub import snapshot_download
        from Tools.MotionAdapter.adapter import load_adapter
        from Tools.MotionAdapter.export_unity import load_core27_reference

        self.torch = torch
        self.device = torch.device(self.device_name)
        if self.device.type == "cuda":
            free, total = torch.cuda.mem_get_info(self.device)
            if free < self.minimum_free_mib * 2**20:
                raise RuntimeError(f"ARDY needs {self.minimum_free_mib} MiB free, found {free / 2**20:.0f}")
            self.gpu_preflight = {"freeMiB": free / 2**20, "totalMiB": total / 2**20}
        else:
            self.gpu_preflight = None
        self.lock = json.loads((ROOT / "Tools/MotionAdapter/upstream-lock.json").read_text(encoding="utf-8-sig"))
        source = ROOT / "Server/ARDY/vendor" / ("ardy-" + self.lock["ardy"])
        if not Path(ardy.__file__).resolve().is_relative_to(source.resolve()):
            raise RuntimeError("Imported ARDY differs from the pinned vendor source")
        if self.adapter_release is not None:
            from .adapter_release import load_release_cpu
            self.adapter, checkpoint, adapter_info = load_release_cpu(ROOT, self.adapter_release)
            self.adapter = self.adapter.to(self.device)
            self.adapter_sha = adapter_info["adapterSha256"]
        elif self.adapter_selection is None:
            adapter_path = ROOT / "Tools/MotionAdapter/runtime/mlp-seed1.pt"
            self.adapter_sha = hashlib.sha256(adapter_path.read_bytes()).hexdigest()
            approved = json.loads((ROOT / "Tools/MotionAdapter/reports/unity-preview-generation.json").read_text(encoding="utf-8"))
            if self.adapter_sha != approved["sources"]["adapter_sha256"]:
                raise RuntimeError("Selected adapter differs from the evaluated MLP checkpoint")
            self.adapter, checkpoint = load_adapter(adapter_path, self.device, FEATURE_CONTRACT)
            adapter_info = {"adapter": "mlp-seed1", "adapterMode": "evaluated-baseline", "candidate": False,
                            "adapterSha256": self.adapter_sha}
        else:
            from .adapter_selection import load_candidate_cpu
            self.adapter, checkpoint, adapter_info = load_candidate_cpu(ROOT, self.adapter_selection)
            self.adapter = self.adapter.to(self.device)
            self.adapter_sha = adapter_info["adapterSha256"]
        if checkpoint["architecture"] != "mlp":
            raise RuntimeError("The service requires the selected MLP adapter")
        snapshot = Path(snapshot_download(repo_id=self.lock["ardy_core_repo"],
            revision=self.lock["ardy_core_revision"], local_files_only=True,
            cache_dir=ROOT / "Server/ARDY/models/hf-cache/hub"))
        self.model = load_model(snapshot.name, checkpoints_dir=str(snapshot.parent),
                                device=str(self.device), text_encoder=False)
        if self.model.text_encoder is not None:
            raise RuntimeError("The ARDY service must never load a language model")
        if self.model.gen_horizon_len != NEW_FRAMES or self.model.motion_rep.fps != 20:
            raise RuntimeError("The service requires the pinned 20 FPS, horizon-40 model")
        self.names, self.parents, self.neutral, _ = load_core27_reference(source)
        self.info = {"ready": True, "languageModelsLoaded": False, **adapter_info,
                     "adapterSha256": self.adapter_sha, "featureContract": FEATURE_CONTRACT,
                     "ardyRevision": self.lock["ardy"], "coreRevision": self.lock["ardy_core_revision"],
                     "fps": 20, "newFrames": NEW_FRAMES, "historyFrames": HISTORY_FRAMES,
                     "gpuPreflight": self.gpu_preflight}

    def project_history(self, history):
        """The lower body is explicitly synthetic; foot contacts are ARDY's heuristic."""
        from scipy.spatial.transform import Rotation
        from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY
        torch = self.torch
        quats = np.asarray([[[getattr(q, k) for k in "xyzw"] for q in frame.globalRotations]
                            for frame in history.frames], dtype=np.float64)
        globals_unity = Rotation.from_quat(quats.reshape(-1, 4)).as_matrix().reshape(-1, 27, 3, 3)
        globals_ardy = ARDY_TO_UNITY @ globals_unity @ ARDY_TO_UNITY
        count = history.conditioningFrames
        if type(count) is not int or count not in (4, 8, HISTORY_FRAMES):
            raise ValueError("Initial conditioning length must be 4, 8 or 16 frames")
        supplied = len(globals_ardy)
        used = min(supplied, count)
        globals_ardy = globals_ardy[-used:]
        if used < count:
            globals_ardy = np.concatenate([np.repeat(globals_ardy[:1], count-used, axis=0), globals_ardy])
        local = globals_ardy.copy()
        for joint, parent in enumerate(self.parents):
            if parent >= 0:
                local[:, joint] = globals_ardy[:, parent].swapaxes(-1, -2) @ globals_ardy[:, joint]
        # Skeleton joints.p is pelvis-relative: its Hips is [0,0,0], not a
        # standing world position. Setting world pelvis Y=0 buries both feet
        # and feeds an extreme crouched/falling root condition to ARDY.
        root, projected_feet_y = grounded_source_root(self.neutral, self.names)
        roots = np.repeat(root[None], count, axis=0)
        with torch.inference_mode():
            normalized = self.model.motion_rep(
                torch.as_tensor(local[None], dtype=torch.float32, device=self.device),
                torch.as_tensor(roots[None], dtype=torch.float32, device=self.device),
                to_normalize=True, to_canonicalize=False)
            decoded = self.model.motion_rep.inverse(normalized, is_normalized=True)
        error = float(np.max(np.abs(decoded["global_rot_mats"][0].cpu().numpy()-globals_ardy)))
        return normalized, {"suppliedFrames": supplied, "usedFrames": used,
             "discardedFrames": supplied-used, "paddedFrames": count-used, "conditioningFrames": count,
             "rotationRoundtripMaxAbs": error,
             "rootPositionMeters": root.tolist(),
             "rootHeightSource": "negative minimum neutral foot/toe Y relative to Hips; synthetic support plane Y=0",
             "projectedFootWorldY": projected_feet_y,
             "lowerBody": "synthetic neutral Core27 legs with pelvis above the source foot/toe support plane; not observed Unity legs",
             "footContacts": "ARDY source-FK height/velocity heuristic on synthetic stationary feet"}

    def generate(self, request, embedding, previous):
        from Tools.MotionAdapter.export_unity import build_clip
        torch = self.torch
        start = time.perf_counter()
        history = previous.get("history")
        projection = None
        if history is not None:
            history = history.to(self.device)
            history_source = "ardy-self-history-last-16"
        elif request.initialHistory is not None:
            history, projection = self.project_history(request.initialHistory)
            history_source = "unity-upper-body-projection-v1"
        else:
            history_source = "ardy-cold-start-no-live-pose"
        history_len = 0 if history is None else history.shape[1]
        expected = history_len + NEW_FRAMES
        cuda_index = (self.device.index if self.device.index is not None else torch.cuda.current_device()) if self.device.type == "cuda" else None
        # Identity isolates stored histories/RNG streams; it must not silently
        # change an explicitly chosen seed. Identical inputs + seed can now be
        # compared across characters, turns and revisions.
        derived_seed = request.seed
        with torch.random.fork_rng(devices=[] if cuda_index is None else [cuda_index]):
            prior_rng = previous.get("rng")
            if prior_rng is None:
                torch.random.default_generator.manual_seed(derived_seed)
                if cuda_index is not None:
                    torch.cuda.default_generators[cuda_index].manual_seed(derived_seed)
            else:
                torch.set_rng_state(prior_rng["cpu"])
                if cuda_index is not None:
                    torch.cuda.set_rng_state(prior_rng["cuda"], cuda_index)
            if cuda_index is not None:
                torch.cuda.synchronize(self.device)
            generation_start = time.perf_counter()
            with torch.inference_mode():
                condition = self.adapter(torch.as_tensor(np.asarray(embedding)[None], dtype=torch.float32,
                                                         device=self.device)).reshape(1, 1, 4096)
                if not torch.isfinite(condition).all():
                    raise RuntimeError("Adapter emitted nonfinite features")
                motion = self.model.autoregressive_step(num_frames=expected,
                    num_denoising_steps=int(self.model.diffusion.num_base_steps),
                    motion_mask=None, observed_motion=None, cfg_weight=(2.0, 2.0),
                    text_feat=condition, text_pad_mask=torch.ones((1,1), dtype=torch.bool, device=self.device),
                    init_history_sequence=history)
                if motion.shape[:2] != (1, expected) or not torch.isfinite(motion).all():
                    raise RuntimeError("ARDY returned invalid normalized motion")
                decoded = self.model.motion_rep.inverse(motion, is_normalized=True)
            if cuda_index is not None:
                torch.cuda.synchronize(self.device)
            generation_ms = (time.perf_counter()-generation_start)*1000
            rng = {"cpu": torch.get_rng_state().clone()}
            if cuda_index is not None:
                rng["cuda"] = torch.cuda.get_rng_state(cuda_index).clone()
        arrays = {k: v[0, history_len:].detach().float().cpu().numpy()
                  for k,v in decoded.items() if torch.is_tensor(v)}
        if any(len(v) != NEW_FRAMES or not np.isfinite(v).all() for v in arrays.values()):
            raise RuntimeError("ARDY returned invalid decoded motion")
        arrays.update(fps=np.asarray(20), text=np.asarray(request.description),
                      joint_names=np.asarray(self.names), joint_parents=self.parents)
        clip = build_clip(arrays, clip_id=f"dynamic-{request.characterId}-{request.revision}-{request.chunkIndex}",
            expected_names=self.names, expected_parents=self.parents, neutral_joints=self.neutral,
            source={"mode": "dynamic-ardy-native", "languageModelsLoaded": False,
                    "ardyRevision": self.lock["ardy"], "coreRevision": self.lock["ardy_core_revision"],
                    "adapterSha256": self.adapter_sha, "historySource": history_source,
                    "seedPolicy": "explicit seed initializes each private revision RNG stream",
                    "initialHistoryProjection": projection,
                    "stationaryApplication": "Unity UpperBody mask; root translation and legs not applied",
                    "generationConstraints": "none; mask is Unity application scope, not ARDY inpainting",
                    "naturalnessAccepted": False})
        return {"clip": clip, "history": motion[:, -HISTORY_FRAMES:].detach().float().cpu().clone(),
                "rng": rng, "historySource": history_source, "historyFrames": history_len,
                "projection": projection, "derivedSeed": derived_seed,
                "generationMs": generation_ms, "backendMs": (time.perf_counter()-start)*1000,
                "peakTorchAllocatedMiB": torch.cuda.max_memory_allocated(self.device)/2**20 if cuda_index is not None else None}
