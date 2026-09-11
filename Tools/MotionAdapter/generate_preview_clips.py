"""Generate continuous Unity baselines or bounded candidates from cached Qwen features.

Each clip cold-starts ARDY, then supplies the last 16 normalized explicit
motion frames to two further autoregressive windows. Only the new 40 frames
from each window are appended. This does not reproduce a live Unity pose.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np
import torch

from .adapter import load_adapter
from .data import read_bundle, read_records, records_hash


ROOT = Path(__file__).resolve().parents[2]
PREVIEWS = (
    ("left-wave", "motion-00053", "wave/left/moderate/at a moderate pace", "test",
     "Previously reviewed left-wave sample; moderate amplitude and pace."),
    ("right-wave", "motion-00004", "wave/right/moderate/at a moderate pace", "val",
     "Moderate right-wave is in validation, not the final test split."),
    ("nod", "motion-00057", "nod/both/small/at a moderate pace", "test",
     "No moderate-amplitude nod is held out; retain the small-amplitude test sample."),
    ("shake-head", "motion-00048", "shake_head/both/large/at a moderate pace", "test",
     "No moderate-amplitude head shake is held out; retain the large-amplitude test sample."),
)
HEAD_CANDIDATES = (
    ("nod", "motion-00010", "nod/both/large/at a moderate pace", "train",
     "Large-amplitude training description for a bounded demonstration diagnostic, not held-out evaluation."),
    ("shake-head", "motion-00048", "shake_head/both/large/at a moderate pace", "test",
     "Large-amplitude head-shake description; seed sweep is a demonstration diagnostic, not model selection."),
)
RIGHT_CANDIDATES = (
    ("right-wave", "motion-00004", "wave/right/moderate/at a moderate pace", "val",
     "Same validation description and condition as the preserved seed-0 baseline; bounded seeds 1/2/3 for native ARDY visual diagnostics only."),
)
HISTORY_FRAMES = 16
NEW_FRAMES = 40
WINDOWS = 3


def pose_difference(first, second, joint_names=None):
    """Trajectory changes or reconstruction differences, not quality scores."""
    joints_a = np.asarray(first["posed_joints"], dtype=np.float64)
    joints_b = np.asarray(second["posed_joints"], dtype=np.float64)
    roots_a = np.asarray(first["root_positions"], dtype=np.float64)
    roots_b = np.asarray(second["root_positions"], dtype=np.float64)
    rot_a = np.asarray(first["local_rot_mats"], dtype=np.float64)
    rot_b = np.asarray(second["local_rot_mats"], dtype=np.float64)
    if joints_a.shape != joints_b.shape or rot_a.shape != rot_b.shape:
        raise ValueError("Pose diagnostic shape mismatch")
    joint_distance = np.linalg.norm(joints_a - joints_b, axis=-1)
    aligned_distance = np.linalg.norm(
        (joints_a - roots_a[:, None]) - (joints_b - roots_b[:, None]), axis=-1)
    cosine = (np.einsum("...ij,...ij->...", rot_a, rot_b) - 1.0) / 2.0
    degrees = np.rad2deg(np.arccos(cosine.clip(-1.0, 1.0)))
    result = {
        "root_mean_m": float(np.linalg.norm(roots_a - roots_b, axis=-1).mean()),
        "joint_mean_m": float(joint_distance.mean()),
        "joint_max_m": float(joint_distance.max()),
        "root_aligned_joint_mean_m": float(aligned_distance.mean()),
        "local_rotation_mean_degrees": float(degrees.mean()),
        "local_rotation_max_degrees": float(degrees.max()),
    }
    if joint_names is not None:
        result["max_rotation_joint"] = str(joint_names[np.unravel_index(degrees.argmax(), degrees.shape)[-1]])
    return result


def clip_activity(arrays, joint_names):
    """Expose weak or near-static previews without selecting a favorable seed."""
    names = list(joint_names)
    result = {}
    for bone in ("Neck", "Head", "LeftHand", "RightHand"):
        index = names.index(bone)
        rotations = arrays["local_rot_mats"][:, index].astype(np.float64)
        cosine = (np.einsum("...ij,ij->...", rotations, rotations[0]) - 1.0) / 2.0
        angles = np.rad2deg(np.arccos(cosine.clip(-1.0, 1.0)))
        relative_positions = arrays["posed_joints"][:, index] - arrays["root_positions"]
        result[bone] = {
            "root_relative_position_range_xyz_m": np.ptp(relative_positions, axis=0).tolist(),
            "max_local_rotation_from_first_frame_degrees": float(angles.max()),
        }
    return result


def head_motion_diagnostics(arrays, joint_names, family):
    """Measure head direction relative to upper spine in ARDY's Y-up basis."""
    names = list(joint_names)
    spine = arrays["global_rot_mats"][:, names.index("Spine3")].astype(np.float64)
    head = arrays["global_rot_mats"][:, names.index("Head")].astype(np.float64)
    relative = spine.swapaxes(-1, -2) @ head
    forward = relative[..., 2]  # Head local +Z expressed in Spine3's frame.
    yaw = np.rad2deg(np.unwrap(np.arctan2(forward[:, 0], forward[:, 2])))
    pitch = np.rad2deg(np.unwrap(np.arctan2(
        -forward[:, 1], np.hypot(forward[:, 0], forward[:, 2]))))
    pitch_range, yaw_range = float(np.ptp(pitch)), float(np.ptp(yaw))
    result = {
        "basis": "Head local +Z forward relative to Spine3; Y-up, yaw=atan2(x,z), pitch=atan2(-y,hypot(x,z))",
        "pitch_peak_to_peak_degrees": pitch_range,
        "yaw_peak_to_peak_degrees": yaw_range,
        "pitch_degrees": pitch.tolist(), "yaw_degrees": yaw.tolist(),
    }
    if family == "nod":
        result["visibility_criterion"] = "pitch peak-to-peak >= 8 degrees and pitch >= yaw"
        result["visible_candidate"] = pitch_range >= 8.0 and pitch_range >= yaw_range
    elif family == "shake_head":
        result["visibility_criterion"] = "yaw peak-to-peak >= 10 degrees and yaw >= pitch"
        result["visible_candidate"] = yaw_range >= 10.0 and yaw_range >= pitch_range
    result["limitation"] = "Head-direction range is a visibility screen, not human verification of repeated nodding or shaking."
    return result


def right_arm_diagnostics(arrays, joint_names):
    """Describe source geometry in its moving chest frame, without a quality gate."""
    names = list(joint_names)
    chest_index = names.index("Spine3")
    chest_rotation = arrays["global_rot_mats"][:, chest_index].astype(np.float64)
    chest_position = arrays["posed_joints"][:, chest_index].astype(np.float64)
    result = {
        "basis": "Core27 Spine3-local; anatomical right is -X, up +Y, forward +Z",
        "limitation": "Source skeleton geometry only; no target-avatar retargeting, naturalness pass or visual acceptance.",
    }
    for bone in ("RightArm", "RightForeArm", "RightHand"):
        position = arrays["posed_joints"][:, names.index(bone)].astype(np.float64)
        relative = (chest_rotation.swapaxes(-1, -2) @ (position - chest_position)[..., None])[..., 0]
        result[bone] = {
            "chest_relative_xyz_m": relative.tolist(),
            "min_xyz_m": relative.min(axis=0).tolist(),
            "max_xyz_m": relative.max(axis=0).tolist(),
            "range_xyz_m": np.ptp(relative, axis=0).tolist(),
            "frames_anatomical_left_of_chest_midline": int(np.count_nonzero(relative[:, 0] > 0)),
            "fraction_anatomical_left_of_chest_midline": float(np.mean(relative[:, 0] > 0)),
        }
    return result


def to_arrays(output, expected_frames):
    arrays = {key: value[0].detach().float().cpu().numpy()
              for key, value in output.items() if torch.is_tensor(value)}
    required = {"posed_joints", "local_rot_mats", "root_positions", "foot_contacts"}
    if not required.issubset(arrays):
        raise RuntimeError("ARDY output is missing required pose arrays")
    if any(value.shape[0] != expected_frames or not np.isfinite(value).all()
           for value in arrays.values()):
        raise RuntimeError("ARDY output has an unexpected length or nonfinite values")
    return arrays


def select_rows(rows, descriptions=PREVIEWS):
    indices = {row["id"]: index for index, row in enumerate(rows)}
    selected = []
    for name, identifier, group, split, reason in descriptions:
        if identifier not in indices:
            raise ValueError(f"Preview description is missing: {identifier}")
        index = indices[identifier]
        row = rows[index]
        if row["semantic_group"] != group or row["split"] != split:
            raise ValueError(f"Preview description contract changed: {identifier}")
        selected.append((name, index, reason))
    return selected


def synchronize(device):
    if device.type == "cuda":
        torch.cuda.synchronize(device)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=Path, default=ROOT / "Tools/MotionAdapter/runtime/prompts.jsonl")
    parser.add_argument("--qwen", type=Path, default=ROOT / "Tools/MotionAdapter/runtime/qwen-2000.npz")
    parser.add_argument("--adapter", type=Path, default=ROOT / "Tools/MotionAdapter/runtime/mlp-seed1.pt")
    parser.add_argument("--output", type=Path, help="Defaults to a separate mode directory under Server/ARDY/runtime")
    candidate_mode = parser.add_mutually_exclusive_group()
    candidate_mode.add_argument("--head-candidates", action="store_true",
                        help="Bounded large nod/shake demonstration diagnostic, fixed seeds 0/1/2, including training text")
    candidate_mode.add_argument("--right-candidates", action="store_true",
                        help="Same baseline right-wave condition, fixed seeds 1/2/3; separate raw NPZ and Unity JSON candidates")
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()
    directory = "unity-right-candidates" if args.right_candidates else "unity-head-candidates" if args.head_candidates else "unity-preview"
    if args.output is None:
        args.output = ROOT / "Server/ARDY/runtime" / directory
    if args.right_candidates and args.output.resolve() in {
            (ROOT / "Server/ARDY/runtime/unity-preview").resolve(),
            (ROOT / "Server/ARDY/runtime/unity-head-candidates").resolve(),
            (ROOT / "Assets/AIChatTookit/Resources/ARDY").resolve()}:
        parser.error("Right candidates must not overwrite the baseline, head candidates or formal Resources")
    device = torch.device(args.device)
    gpu_preflight = None
    if args.right_candidates and device.type == "cuda":
        free_bytes, total_bytes = torch.cuda.mem_get_info(device)
        gpu_preflight = {"free_mib": free_bytes / 2**20, "total_mib": total_bytes / 2**20,
                         "minimum_free_mib": 3072, "checked_before_adapter_and_ardy_load": True}
        if free_bytes < 3 * 2**30:
            raise RuntimeError(f"Right-candidate generation requires >= 3 GiB free CUDA memory: {free_bytes / 2**20:.0f} MiB free")
    rows = read_records(args.dataset)
    descriptions = RIGHT_CANDIDATES if args.right_candidates else HEAD_CANDIDATES if args.head_candidates else PREVIEWS
    selected = select_rows(rows, descriptions)
    seeds = [1, 2, 3] if args.right_candidates else [0, 1, 2] if args.head_candidates else [args.seed]
    runs = [(f"{name}-seed{seed}" if args.head_candidates or args.right_candidates else name, index, reason, seed)
            for name, index, reason in selected
            for seed in seeds]
    qwen, metadata = read_bundle(args.qwen, rows, "qwen")
    adapter, checkpoint = load_adapter(args.adapter, device, metadata["feature_contract"])
    if checkpoint["architecture"] != "mlp":
        raise ValueError("Unity preview requires the selected MLP architecture")
    adapter_sha256 = hashlib.sha256(args.adapter.read_bytes()).hexdigest()
    if args.right_candidates:
        baseline = json.loads((ROOT / "Tools/MotionAdapter/reports/unity-preview-generation.json").read_text(encoding="utf-8"))
        if baseline["sources"]["adapter_sha256"] != adapter_sha256 or baseline["sources"]["qwen_contract"] != metadata["feature_contract"] or baseline["sources"]["dataset_sha256"] != records_hash(rows):
            raise ValueError("Right candidates must use the baseline dataset, Qwen feature contract and selected adapter SHA")

    import ardy
    from ardy.model import load_model
    from ardy.tools import seed_everything
    from huggingface_hub import snapshot_download

    lock = json.loads((Path(__file__).parent / "upstream-lock.json").read_text(encoding="utf-8-sig"))
    source = ROOT / "Server/ARDY/vendor" / f"ardy-{lock['ardy']}"
    if not Path(ardy.__file__).resolve().is_relative_to(source.resolve()):
        raise RuntimeError("The imported ARDY source does not match the pinned source directory")
    cache = ROOT / "Server/ARDY/models/hf-cache/hub"
    snapshot = Path(snapshot_download(
        repo_id=lock["ardy_core_repo"], revision=lock["ardy_core_revision"],
        cache_dir=cache, local_files_only=True))
    model = load_model(snapshot.name, checkpoints_dir=str(snapshot.parent),
                       device=str(device), text_encoder=False)
    if model.text_encoder is not None:
        raise RuntimeError("Preview generation must never load a language model")
    if model.gen_horizon_len != NEW_FRAMES or model.motion_rep.fps != 20:
        raise RuntimeError("Preview protocol requires the pinned 20 FPS / 40 frame model")
    if HISTORY_FRAMES % model.num_frames_per_token:
        raise RuntimeError("History length must align with the model's motion tokens")
    if device.type == "cuda":
        torch.cuda.reset_peak_memory_stats(device)
    args.output.mkdir(parents=True, exist_ok=True)
    reports = []
    steps = int(model.diffusion.num_base_steps)
    for name, index, reason, clip_seed in runs:
        row = rows[index]
        seed_everything(clip_seed)  # Once per clip; random state continues between windows.
        with torch.inference_mode():
            feature = adapter(torch.from_numpy(qwen[index:index + 1]).to(device)).reshape(1, 1, 4096)
        if not torch.isfinite(feature).all():
            raise RuntimeError("Adapter produced a nonfinite condition")
        mask = torch.ones((1, 1), dtype=torch.bool, device=device)
        history = None
        chunks = []
        normalized_chunks = []
        window_reports = []
        for window_index in range(WINDOWS):
            history_length = 0 if history is None else history.shape[1]
            expected_frames = history_length + NEW_FRAMES
            synchronize(device)
            start = time.perf_counter()
            with torch.inference_mode():
                motion = model.autoregressive_step(
                    num_frames=expected_frames, num_denoising_steps=steps,
                    motion_mask=None, observed_motion=None, cfg_weight=(2.0, 2.0),
                    text_feat=feature, text_pad_mask=mask, init_history_sequence=history)
                if motion.ndim != 3 or motion.shape[:2] != (1, expected_frames):
                    raise RuntimeError("Unexpected normalized motion window shape")
                if not torch.isfinite(motion).all():
                    raise RuntimeError("Nonfinite normalized motion")
                decoded = model.motion_rep.inverse(motion, is_normalized=True)
            synchronize(device)
            milliseconds = (time.perf_counter() - start) * 1000
            arrays = to_arrays(decoded, expected_frames)
            new_chunk = {key: value[history_length:].copy() for key, value in arrays.items()}
            window_report = {
                "window": window_index, "history_frames": history_length,
                "returned_frames": expected_frames, "appended_frames": NEW_FRAMES,
                "milliseconds": milliseconds,
            }
            if chunks:
                previous_end = {key: value[-1:] for key, value in chunks[-1].items()}
                next_start = {key: value[:1] for key, value in new_chunk.items()}
                previous_overlap = {key: value[-HISTORY_FRAMES:] for key, value in chunks[-1].items()}
                returned_overlap = {key: value[:history_length] for key, value in arrays.items()}
                window_report["seam_frame"] = window_index * NEW_FRAMES
                window_report["seam_pose_step"] = pose_difference(
                    previous_end, next_start, model.skeleton.bone_order_names)
                window_report["reconstructed_history_difference"] = pose_difference(
                    previous_overlap, returned_overlap)
                window_report["reconstructed_normalized_history_max_abs"] = float(
                    (motion[:, :history_length] - history).abs().max().item())
            chunks.append(new_chunk)
            normalized_new = motion[:, history_length:].detach().clone()
            normalized_chunks.append(normalized_new[0].float().cpu().numpy())
            history = normalized_new[:, -HISTORY_FRAMES:].clone()
            window_reports.append(window_report)
            print(f"{name}: window {window_index + 1}/{WINDOWS}, "
                  f"history={history_length}, new={NEW_FRAMES}, {milliseconds:.1f} ms", flush=True)

        full = {key: np.concatenate([chunk[key] for chunk in chunks], axis=0) for key in chunks[0]}
        normalized = np.concatenate(normalized_chunks, axis=0)
        if normalized.shape[0] != NEW_FRAMES * WINDOWS or any(
                value.shape[0] != NEW_FRAMES * WINDOWS or not np.isfinite(value).all()
                for value in full.values()):
            raise RuntimeError("The assembled preview must contain exactly 120 finite frames")
        frame_changes = pose_difference(
            {key: value[:-1] for key, value in full.items()},
            {key: value[1:] for key, value in full.items()})
        path = args.output / f"{name}.npz"
        np.savez(path, **full, normalized_motion=normalized,
                 fps=np.asarray(model.motion_rep.fps), text=np.asarray(row["text"]),
                 joint_names=np.asarray(model.skeleton.bone_order_names),
                 joint_parents=model.skeleton.joint_parents.cpu().numpy(),
                 source_id=np.asarray(row["id"]), source_split=np.asarray(row["split"]),
                 history_source=np.asarray("ARDY cold start, then its own last 16 normalized frames; no Unity pose"))
        reports.append({
            "name": name, "file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "seed": clip_seed,
            "record": row, "selection_reason": reason, "frames": len(normalized),
            "duration_seconds": len(normalized) / model.motion_rep.fps,
            "windows": window_reports, "all_adjacent_frame_changes": frame_changes,
            "activity_diagnostics": clip_activity(full, model.skeleton.bone_order_names),
            "head_motion_diagnostics": head_motion_diagnostics(full, model.skeleton.bone_order_names, row["family"]),
            "shapes": {key: list(value.shape) for key, value in full.items()},
        })
        if args.right_candidates:
            from .export_unity import export_clip
            reports[-1]["right_arm_diagnostics"] = right_arm_diagnostics(full, model.skeleton.bone_order_names)
            reports[-1]["condition_sha256"] = hashlib.sha256(feature.float().cpu().numpy().tobytes()).hexdigest()
            reports[-1]["naturalness_accepted"] = False
            json_path = args.output / f"{name}.json"
            clip = export_clip(path, json_path, ardy_source=source, clip_id=name)
            clip["source"]["candidateProvenance"] = {
                "status": "unreviewed native ARDY candidate, not naturalness accepted",
                "record": row, "seed": clip_seed,
                "adapterSha256": adapter_sha256, "qwenContract": metadata["feature_contract"],
                "conditionSha256": reports[-1]["condition_sha256"],
                "historySource": "ARDY self-history only; no live Unity pose", "languageModelLoaded": False,
                "ardyRevision": lock["ardy"], "coreRevision": lock["ardy_core_revision"],
            }
            json_path.write_text(json.dumps(clip, ensure_ascii=False, separators=(",", ":"), allow_nan=False), encoding="utf-8")
            reports[-1]["unity_json"] = {"file": json_path.name,
                                          "sha256": hashlib.sha256(json_path.read_bytes()).hexdigest(),
                                          "frames": len(clip["frames"]), "source_npz_sha256": clip["source"]["sha256"]}
    report = {
        "schema": 1, "text_encoder_loaded": False, "qwen_model_loaded": False,
        "postprocessing": False, "seeds": seeds,
        "mode": "right-wave-demonstration-candidates" if args.right_candidates else "head-demonstration-candidates" if args.head_candidates else "four-preview-baselines",
        "rng_reset": "once per clip, never between windows",
        "history_source": "ARDY self-history; no common idle and no live Unity body pose supplied",
        "history_frames": HISTORY_FRAMES, "windows_per_clip": WINDOWS, "new_frames_per_window": NEW_FRAMES,
        "fps": model.motion_rep.fps, "num_denoising_steps": steps, "cfg_weight": [2.0, 2.0],
        "sources": {
            "dataset_sha256": records_hash(rows), "qwen_contract": metadata["feature_contract"],
            "adapter": args.adapter.name, "adapter_sha256": adapter_sha256,
            "teacher_space_contract": checkpoint["teacher_contract"],
            "ardy_revision": lock["ardy"], "core_repo": lock["ardy_core_repo"],
            "core_revision": lock["ardy_core_revision"],
        },
        "peak_torch_allocated_mib": torch.cuda.max_memory_allocated(device) / 2**20 if device.type == "cuda" else None,
        "results": reports,
        "limitations": [
            "Offline continuation inputs, not a live streaming service or live avatar history test.",
            "First window cold-starts; there is no shared initial stance across descriptions.",
            "Seam metrics measure adjacent poses, and include normal motion; they are not ground-truth errors.",
            "ARDY re-encodes supplied history. Returned history is measured but never replaces previously appended frames.",
            "Cached template descriptions only; record splits are preserved, and no adapter model selection is performed here.",
            "Head-candidate mode includes a training description and a bounded seed sweep; it is demonstration screening, not held-out evaluation.",
            "No motion correction, retargeting, visual acceptance, or instruction-compliance claim.",
            "Timings include ARDY generation and decoding, with no warmup; exclude feature encoding, loading and Unity.",
        ],
    }
    if args.right_candidates:
        report["gpu_preflight"] = gpu_preflight
        report["sources"]["qwen_bundle_sha256"] = hashlib.sha256(args.qwen.read_bytes()).hexdigest()
        report["naturalness_accepted"] = False
        report["limitations"].append("Three fixed right-wave seeds preserve the original seed-0 baseline; no benchmark or formal asset selection is performed.")
    report_path = args.output / ("unity-right-candidates.json" if args.right_candidates else "report.json")
    report_path.write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
    if args.right_candidates:
        (ROOT / "Tools/MotionAdapter/reports/unity-right-candidates.json").write_bytes(report_path.read_bytes())
    print(json.dumps({"clips": len(reports), "report": str(report_path), "text_encoder_loaded": False}), flush=True)


if __name__ == "__main__":
    main()
