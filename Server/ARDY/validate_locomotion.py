"""Run a small, reproducible native ARDY path/turn/stop feasibility matrix.

Uses the active adapter release and one existing shared Qwen feature route.
No HTTP service is installed, no model is downloaded, no language model is
loaded locally, and the existing stationary service is not changed.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import time
from datetime import datetime, timezone
from dataclasses import asdict
from pathlib import Path
from types import SimpleNamespace

import numpy as np

from Server.ARDY.locomotion import (DESCRIPTION, FPS, HISTORY, WINDOW, Route,
    constraint_tensors, export_locomotion, make_route, standing_history)
from Server.ARDY.motion_service.backend import ArdyBackend, ROOT
from Server.ARDY.motion_service.service import LiveFeatureProvider
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY


def dump(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False, ensure_ascii=False), encoding="utf-8")


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def verify_heading(backend):
    """Check our angle mapping against the pinned representation, not GUI labels."""
    from ardy.motion_rep.tools import compute_heading_angle
    from Tools.MotionAdapter.export_unity import fk_positions
    from scipy.spatial.transform import Rotation
    torch = backend.torch
    angles = np.array([0, np.pi/2, -np.pi/2])
    rotations = np.repeat(Rotation.from_rotvec(angles[:, None] * np.array([0., 1., 0.])).as_matrix()[:, None], 27, axis=1)
    joints = fk_positions(rotations, backend.neutral, backend.parents)
    actual = compute_heading_angle(torch.as_tensor(joints[None], dtype=torch.float32,
        device=backend.device), backend.model.motion_rep.skeleton).cpu().numpy().ravel()
    if not np.allclose(actual, angles, atol=1e-5):
        raise RuntimeError("Native heading convention differs from target conversion")
    return {"requestedRadians": angles.tolist(), "officialComputedRadians": actual.tolist(), "passed": True}


def generate_case(backend, condition, case, seed, directory, feature_info):
    from Server.ARDY.locomotion_metrics import measure_locomotion
    torch = backend.torch
    model = backend.model
    history, source_height = standing_history(backend)
    route = make_route(case)
    chunks, timings, targets, headings = [], [], [], []
    windows = []
    cuda_index = backend.device.index if backend.device.index is not None else torch.cuda.current_device()
    with torch.random.fork_rng(devices=[cuda_index]):
        torch.random.default_generator.manual_seed(seed)
        torch.cuda.default_generators[cuda_index].manual_seed(seed)
        for start in range(0, len(route.positions), WINDOW):
            if case == "redirect-stop" and start == 80:
                previous = chunks[-1]["root_positions"] @ ARDY_TO_UNITY
                route = make_route(case, changed=True, anchor=previous[-1],
                    velocity=(previous[-1]-previous[-2])*FPS)
            observed, mask, count = constraint_tensors(model, route, start, HISTORY)
            torch.cuda.synchronize(backend.device)
            began = time.perf_counter()
            with torch.inference_mode():
                motion = model.autoregressive_step(num_frames=count,
                    num_denoising_steps=int(model.diffusion.num_base_steps),
                    motion_mask=mask, observed_motion=observed, cfg_weight=(2., 2.),
                    text_feat=condition,
                    text_pad_mask=torch.ones((1, 1), dtype=torch.bool, device=backend.device),
                    init_history_sequence=history)
                if motion.shape[:2] != (1, HISTORY+WINDOW) or not torch.isfinite(motion).all():
                    raise RuntimeError("Invalid motion window")
                decoded = model.motion_rep.inverse(motion, is_normalized=True)
            torch.cuda.synchronize(backend.device)
            seconds = time.perf_counter()-began
            arrays = {k: v[0, HISTORY:].detach().float().cpu().numpy()
                      for k, v in decoded.items() if torch.is_tensor(v)}
            if any(len(v) != WINDOW or not np.isfinite(v).all() for v in arrays.values()):
                raise RuntimeError("Invalid decoded window")
            # Only newly emitted frames are committed; reconstructed old history
            # is never spliced back into the published trajectory.
            history = motion[:, -HISTORY:].detach().clone()
            chunks.append(arrays)
            targets.append(route.positions[start:start+WINDOW].copy())
            headings.append(route.headings[start:start+WINDOW].copy())
            timings.append(seconds)
            windows.append({"startFrame": start, "newFrames": WINDOW, "historyFrames": HISTORY,
                "visibleFrames": count, "inferenceSeconds": seconds,
                "constraintNonzero": int(torch.count_nonzero(mask)),
                "routeChangedThisWindow": case == "redirect-stop" and start == 80})
            print(json.dumps({"case": case, "seed": seed, **windows[-1]}), flush=True)
    arrays = {k: np.concatenate([c[k] for c in chunks]) for k in chunks[0]}
    arrays.update(fps=np.asarray(FPS), text=np.asarray(DESCRIPTION),
                  joint_names=np.asarray(backend.names), joint_parents=backend.parents)
    committed_route = Route(np.concatenate(targets), np.concatenate(headings), route.stop_frame, route.change_frame)
    native_positions = committed_route.positions @ ARDY_TO_UNITY
    native_heading_vectors = np.stack((-np.sin(committed_route.headings), np.cos(committed_route.headings)), axis=1)
    metrics = measure_locomotion(arrays, backend.names, native_positions,
        fps=FPS, target_headings=native_heading_vectors, window_frames=WINDOW,
        window_generation_seconds=timings, stop_start_frame=route.stop_frame,
        target_change_frames=[80] if case == "redirect-stop" else None)
    clip_id = f"{case}-seed{seed}"
    provenance = {"mode": "ardy-locomotion-native", "adapterSha256": backend.adapter_sha,
        "ardyRevision": backend.lock["ardy"], "coreRevision": backend.lock["ardy_core_revision"],
        "featureSource": feature_info["source"], "featureContract": feature_info["featureContract"],
        "seed": seed, "text": DESCRIPTION, "languageModelsLoaded": False,
        "generationConstraints": "official Root2DConstraintSet: dense XZ and heading; history mask zero",
        "postprocessing": "none; no root snapping, foot locking, IK or baked walking animation",
        "initialHistory": "synthetic 16-frame standing full Core27 pose, not measured Unity history",
        "continuationHistory": "latest 16 generated full-body normalized frames",
        "redirectSemantics": "goal changes at window boundary 4s; complete previous window treated as played in this harness",
        "naturalnessAccepted": False}
    output = directory / clip_id
    output.mkdir()
    np.savez_compressed(output / "native.npz", **arrays, target_root_positions=native_positions,
        target_heading_vectors=native_heading_vectors)
    clip = export_locomotion(arrays, committed_route, backend, clip_id, provenance)
    dump(output / "locomotion.json", clip)
    report = {"id": clip_id, "case": case, "seed": seed, "sourceRootHeight": source_height,
        "frames": len(committed_route.positions), "seconds": len(committed_route.positions)/FPS,
        "windows": windows, "metrics": metrics, "provenance": provenance,
        "artifacts": {name: {"path": str((output/name).resolve()), "sha256": sha(output/name)}
                      for name in ("native.npz", "locomotion.json")}}
    dump(output / "report.json", report)
    return report


async def main_async(args):
    if args.output.exists() and any(args.output.iterdir()):
        raise ValueError("Use an empty output directory; existing evidence is never overwritten")
    if any(seed < 0 or seed > 2147483647 for seed in args.seeds) or len(set(args.seeds)) != len(args.seeds):
        raise ValueError("Seeds must be distinct nonnegative int32 values")
    args.output.mkdir(parents=True, exist_ok=True)
    from Server.ARDY.locomotion_metrics import LocomotionThresholds
    plan = {"schema": 1, "createdUtc": datetime.now(timezone.utc).isoformat(),
        "description": DESCRIPTION, "cases": args.cases, "seeds": args.seeds,
        "fps": FPS, "windowsPerCase": 5, "newFramesPerWindow": WINDOW,
        "scope": "flat-ground kinematic feasibility; not house navigation, physical contact, naturalness or end-to-end chat acceptance",
        "thresholds": asdict(LocomotionThresholds()),
        "sourceFiles": {name: sha(ROOT / "Server/ARDY" / name)
                        for name in ("locomotion.py", "locomotion_metrics.py", "validate_locomotion.py")}}
    dump(args.output / "plan.json", plan)
    provider = LiveFeatureProvider(args.feature_url)
    feature_start = time.perf_counter()
    feature = await provider.fetch(SimpleNamespace(description=DESCRIPTION,
        requestId="locomotion-"+args.output.name), 20)
    feature_seconds = time.perf_counter()-feature_start
    feature_serialized = {**feature, "embedding": feature["embedding"].tolist(),
        "description": DESCRIPTION, "featureSeconds": feature_seconds}
    dump(args.output / "feature.json", feature_serialized)
    active_release = ROOT / "Server/ARDY/runtime/active-adapter-release.json"
    backend = ArdyBackend(adapter_release=active_release if active_release.exists() else None)
    backend.load()
    torch = backend.torch
    with torch.inference_mode():
        condition = backend.adapter(torch.as_tensor(feature["embedding"][None], dtype=torch.float32,
            device=backend.device)).reshape(1, 1, 4096)
    if not torch.isfinite(condition).all():
        raise RuntimeError("Invalid adapter condition")
    report = {"schema": 1, "plan": plan, "backend": backend.info,
        "featureSeconds": feature_seconds, "featureInfo": provider.info,
        "headingConventionCheck": verify_heading(backend), "runs": [], "completed": False}
    dump(args.output / "report.json", report)
    for case in args.cases:
        for seed in args.seeds:
            report["runs"].append(generate_case(backend, condition, case, seed, args.output, feature))
            dump(args.output / "report.json", report)
    report["completed"] = True
    report["peakTorchAllocatedMiB"] = torch.cuda.max_memory_allocated()/2**20
    dump(args.output / "report.json", report)
    print(json.dumps({"completed": True, "runs": len(report["runs"]), "report": str(args.output / "report.json")}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--feature-url", default="http://127.0.0.1:8080")
    parser.add_argument("--seeds", type=int, nargs="+", default=[0, 1])
    parser.add_argument("--cases", nargs="+", choices=["straight-stop", "turn-stop", "redirect-stop"],
                        default=["straight-stop", "turn-stop", "redirect-stop"])
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except Exception as error:
        # Keep the original evidence immutable even if output reuse is rejected.
        failure = args.output / "failure.json"
        if args.output.is_dir() and not failure.exists() and not (args.output / "report.json").exists():
            dump(failure, {"completed": False, "error": repr(error)})
        raise


if __name__ == "__main__":
    main()
