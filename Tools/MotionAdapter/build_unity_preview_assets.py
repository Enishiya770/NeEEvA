"""Validate and publish a fixed, curated set of offline Unity demonstration assets.

This is a deterministic CPU export step. It never trains or loads a language
model, and it preserves the failed head candidates in the audit reports.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import tempfile
import uuid
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation

from .conversational_head import apply_profile
from .data import read_bundle, read_records, records_hash
from .export_unity import build_clip, export_clip, load_core27_reference
from .generate_preview_clips import HEAD_CANDIDATES, PREVIEWS, RIGHT_CANDIDATES, head_motion_diagnostics, right_arm_diagnostics
from .mirror_unity_candidate import export_candidate as export_mirrored_candidate


ROOT = Path(__file__).resolve().parents[2]
REPORTS = ROOT / "Tools/MotionAdapter/reports"
ASSETS = ROOT / "Assets/AIChatTookit/Resources/ARDY"
DEFAULT_RIGHT_REPORT = ROOT / "Server/ARDY/runtime/unity-right-candidates/unity-right-candidates.json"
REPORT_NAMES = {"baseline": "unity-preview-generation.json", "heads": "unity-head-candidates.json",
                "rights": "unity-right-candidates.json"}
SELECTION = (
    ("left-wave", "baseline", "left-wave", "motion-00053", "test", 0),
    ("right-wave", "mirrored-left", "left-wave", "motion-00053", "test", 0),
    ("nod", "heads", "nod-seed1", "motion-00010", "train", 1),
    ("shake-head", "heads", "shake-head-seed2", "motion-00048", "test", 2),
)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def project_path(path):
    return Path(path).resolve().relative_to(ROOT).as_posix()


def publish_bytes(data, target):
    """Atomically publish through a new file inheriting the destination directory ACL.

    Windows keeps a file's ACL when it is moved out of TemporaryDirectory's
    private staging directory. Create the final replacement alongside its
    target instead, so Unity running as the real user can read the result.
    """
    target = Path(target)
    temporary = target.parent / f".{target.name}.{uuid.uuid4().hex}.tmp"
    created = False
    try:
        with temporary.open("xb") as stream:
            created = True
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, target)
    finally:
        if created:
            temporary.unlink(missing_ok=True)


def verify_npz(entry, directory, records):
    """Reject a changed file or record before any Unity resource is touched."""
    filename = entry["file"]
    if Path(filename).name != filename or not filename.endswith(".npz"):
        raise ValueError("Generation report must use a plain NPZ filename")
    path = directory / filename
    if sha256(path) != entry["sha256"]:
        raise ValueError(f"NPZ SHA256 mismatch: {filename}")
    record = entry["record"]
    if record != records.get(record["id"]):
        raise ValueError(f"Generation record differs from the source dataset: {filename}")
    with np.load(path, allow_pickle=False) as motion:
        if (str(motion["source_id"]) != record["id"]
                or str(motion["source_split"]) != record["split"]
                or str(motion["text"]) != record["text"]):
            raise ValueError(f"NPZ record metadata mismatch: {filename}")
        if float(motion["fps"]) != 20.0 or entry["frames"] != 120:
            raise ValueError(f"Expected a 120-frame, 20 FPS clip: {filename}")
        for key, tail in (("global_rot_mats", (27, 3, 3)), ("local_rot_mats", (27, 3, 3)),
                          ("posed_joints", (27, 3)), ("root_positions", (3,))):
            if motion[key].shape != (120, *tail) or not np.isfinite(motion[key]).all():
                raise ValueError(f"Invalid {key} array: {filename}")
        if motion["normalized_motion"].shape[0] != 120 or not np.isfinite(motion["normalized_motion"]).all():
            raise ValueError(f"Invalid normalized history: {filename}")
        measured = head_motion_diagnostics(motion, motion["joint_names"].tolist(), record["family"])
        logged = entry["head_motion_diagnostics"]
        for key in ("pitch_peak_to_peak_degrees", "yaw_peak_to_peak_degrees"):
            if not np.isclose(measured[key], logged[key], rtol=0.0, atol=1e-6):
                raise ValueError(f"Head activity report mismatch: {filename}")
        if measured.get("visible_candidate") != logged.get("visible_candidate"):
            raise ValueError(f"Head visibility screening mismatch: {filename}")
    return path


def verify_right_candidate(entry, path, expected_sources, reference):
    """Check the source diagnostic and every exported JSON rotation against its NPZ."""
    condition_sha = entry.get("condition_sha256", "")
    if not isinstance(condition_sha, str) or not re.fullmatch(r"[0-9a-f]{64}", condition_sha):
        raise ValueError("Missing right-candidate condition hash")
    if entry.get("naturalness_accepted") is not False:
        raise ValueError("Generation report must not claim a right candidate passed naturalness review")
    logged = entry["unity_json"]
    filename = logged["file"]
    if filename != f"{entry['name']}.json":
        raise ValueError("Unexpected right-candidate Unity filename")
    json_path = path.parent / filename
    if (sha256(json_path) != logged["sha256"] or logged["frames"] != 120
            or logged["source_npz_sha256"] != entry["sha256"]):
        raise ValueError("Right-candidate Unity export hash or source mismatch")
    clip = json.loads(json_path.read_text(encoding="utf-8"))
    names, parents, neutral, neutral_sha = reference
    source = {"file": path.name, "sha256": entry["sha256"],
              "ardyRevision": expected_sources["ardy_revision"], "coreRepo": expected_sources["core_repo"],
              "coreRevision": expected_sources["core_revision"], "neutralJointsSha256": neutral_sha}
    with np.load(path, allow_pickle=False) as motion:
        measured = right_arm_diagnostics(motion, motion["joint_names"].tolist())
        if measured != entry.get("right_arm_diagnostics"):
            raise ValueError("Right-arm source geometry report mismatch")
        expected = build_clip(motion, clip_id=entry["name"], expected_names=names,
                              expected_parents=parents, neutral_joints=neutral, source=source)
    expected["source"]["candidateProvenance"] = {
        "status": "unreviewed native ARDY candidate, not naturalness accepted",
        "record": entry["record"], "seed": entry["seed"],
        "adapterSha256": expected_sources["adapter_sha256"], "qwenContract": expected_sources["qwen_contract"],
        "conditionSha256": condition_sha, "historySource": "ARDY self-history only; no live Unity pose",
        "languageModelLoaded": False, "ardyRevision": expected_sources["ardy_revision"],
        "coreRevision": expected_sources["core_revision"],
    }
    if clip != expected:
        raise ValueError("Right-candidate JSON differs from the verified raw NPZ export or provenance")


def verify_generation(report, directory, records, expected_sources, kind):
    if report.get("schema") != 1 or report.get("sources") != expected_sources:
        raise ValueError("Generation schema or model/condition provenance mismatch")
    expected = {
        "text_encoder_loaded": False, "qwen_model_loaded": False, "postprocessing": False,
        "history_frames": 16, "windows_per_clip": 3, "new_frames_per_window": 40,
        "fps": 20, "num_denoising_steps": 10, "cfg_weight": [2.0, 2.0],
        "history_source": "ARDY self-history; no common idle and no live Unity body pose supplied",
        "rng_reset": "once per clip, never between windows",
        "mode": {"heads": "head-demonstration-candidates", "rights": "right-wave-demonstration-candidates",
                 "baseline": "four-preview-baselines"}[kind],
        "seeds": {"heads": [0, 1, 2], "rights": [1, 2, 3], "baseline": [0]}[kind],
    }
    if any(report.get(key) != value for key, value in expected.items()):
        raise ValueError("Generation settings differ from the fixed preview protocol")
    descriptions = {"heads": HEAD_CANDIDATES, "rights": RIGHT_CANDIDATES, "baseline": PREVIEWS}[kind]
    expected_entries = {
        f"{name}-seed{seed}" if kind != "baseline" else name: (identifier, group, split, seed)
        for name, identifier, group, split, _ in descriptions
        for seed in expected["seeds"]
    }
    reference = None
    if kind == "rights":
        if report.get("naturalness_accepted") is not False:
            raise ValueError("Right generation must not claim naturalness acceptance")
        preflight = report.get("gpu_preflight")
        if preflight is not None and (preflight.get("checked_before_adapter_and_ardy_load") is not True
                or preflight.get("minimum_free_mib") != 3072
                or not np.isfinite(preflight.get("free_mib", np.nan)) or preflight["free_mib"] < 3072):
            raise ValueError("Right-candidate CUDA preflight did not meet the recorded requirement")
        reference = load_core27_reference(ROOT / "Server/ARDY/vendor" / f"ardy-{expected_sources['ardy_revision']}")
    results = report["results"]
    if len(results) != len(expected_entries) or {entry["name"] for entry in results} != set(expected_entries):
        raise ValueError("Generation report omitted or duplicated expected candidates")
    validated = {}
    for entry in results:
        identifier, group, split, seed = expected_entries[entry["name"]]
        if (entry["record"]["id"], entry["record"]["semantic_group"], entry["record"]["split"], entry["seed"]) != (identifier, group, split, seed):
            raise ValueError("Generation candidate identity or seed mismatch")
        windows = entry["windows"]
        if len(windows) != 3:
            raise ValueError("Expected three generated windows")
        for index, window in enumerate(windows):
            history = 0 if index == 0 else 16
            if (window["window"], window["history_frames"], window["returned_frames"], window["appended_frames"]) != (index, history, history + 40, 40):
                raise ValueError("History or append protocol mismatch")
            if not np.isfinite(window["milliseconds"]) or window["milliseconds"] < 0:
                raise ValueError("Invalid window generation timing")
            if index and window.get("seam_frame") != index * 40:
                raise ValueError("Invalid continuation boundary frame")
        if entry["duration_seconds"] != 6.0:
            raise ValueError("Expected a six-second source clip")
        path = verify_npz(entry, directory, records)
        if kind == "rights":
            if entry["file"] != f"{entry['name']}.npz":
                raise ValueError("Right-candidate source filename differs from its name")
            verify_right_candidate(entry, path, expected_sources, reference)
        validated[entry["name"]] = (entry, path)
    if kind == "rights" and len({entry["condition_sha256"] for entry in results}) != 1:
        raise ValueError("Right candidates must all use the same encoded condition")
    return validated


def validate_inputs(baseline_report, head_report, dataset, qwen, adapter, right_report=DEFAULT_RIGHT_REPORT):
    rows = read_records(dataset)
    records = {row["id"]: row for row in rows}
    _, qwen_metadata = read_bundle(qwen, rows, "qwen")
    lock = json.loads((Path(__file__).parent / "upstream-lock.json").read_text(encoding="utf-8-sig"))
    selection = json.loads((REPORTS / "adapter-pilot.json").read_text(encoding="utf-8"))
    if adapter.name != selection["selected_checkpoint"]:
        raise ValueError("Adapter is not the validation-selected checkpoint")
    expected_sources = {
        "dataset_sha256": records_hash(rows), "qwen_contract": qwen_metadata["feature_contract"],
        "adapter": adapter.name, "adapter_sha256": sha256(adapter),
        "teacher_space_contract": selection["teacher_contract"],
        "ardy_revision": lock["ardy"], "core_repo": lock["ardy_core_repo"],
        "core_revision": lock["ardy_core_revision"],
    }
    if selection["dataset_sha256"] != expected_sources["dataset_sha256"] or selection["qwen_contract"] != expected_sources["qwen_contract"]:
        raise ValueError("Adapter selection report refers to different conditioning data")
    reports, validated = {}, {}
    for kind, path in (("baseline", baseline_report), ("heads", head_report), ("rights", right_report)):
        reports[kind] = json.loads(path.read_text(encoding="utf-8"))
        kind_sources = {**expected_sources, "qwen_bundle_sha256": sha256(qwen)} if kind == "rights" else expected_sources
        validated[kind] = verify_generation(reports[kind], path.parent, records, kind_sources, kind)
    selected = []
    for resource_id, kind, source_name, identifier, split, seed in SELECTION:
        source_kind = "baseline" if kind == "mirrored-left" else kind
        entry, path = validated[source_kind][source_name]
        if (entry["record"]["id"], entry["record"]["split"], entry["seed"]) != (identifier, split, seed):
            raise ValueError("Fixed Unity selection changed")
        if kind == "mirrored-left" and (resource_id, source_name, identifier, split, seed) != (
                "right-wave", "left-wave", "motion-00053", "test", 0):
            raise ValueError("Derived right-wave must mirror the reviewed left-wave baseline")
        selected.append((resource_id, kind, entry, path))
    return reports, selected, expected_sources, qwen_metadata


def build_assets(baseline_report, head_report, dataset, qwen, adapter, verify_only=False, right_report=DEFAULT_RIGHT_REPORT):
    reports, selected, sources, qwen_metadata = validate_inputs(baseline_report, head_report, dataset, qwen, adapter, right_report)
    validated_count = sum(len(report["results"]) for report in reports.values())
    if verify_only:
        return {"validated_generation_clips": validated_count, "selected_assets": len(selected), "resources_written": False}
    staging_parent = ROOT / "Server/ARDY/runtime"
    staging_parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="unity-export-", dir=staging_parent) as temporary:
        staging = Path(temporary)
        assets = []
        for resource_id, kind, entry, source_path in selected:
            staged = staging / f"{resource_id}.json"
            mirror_verification = None
            if kind == "mirrored-left":
                mirror_verification = export_mirrored_candidate(source_path, staged)
                result = json.loads(staged.read_text(encoding="utf-8"))
                result["id"] = resource_id
            else:
                result = export_clip(source_path, staged, clip_id=resource_id)
            if result["source"]["sha256"] != entry["sha256"]:
                raise ValueError("Source clip changed during export; resources were not written")
            head = entry["head_motion_diagnostics"]
            raw_metrics = {
                "label": "Raw ARDY source only; not measurements of the controlled head curve" if kind == "heads" else "Raw ARDY source geometry; not target-avatar visual acceptance",
                "pitch_peak_to_peak_degrees": head["pitch_peak_to_peak_degrees"],
                "yaw_peak_to_peak_degrees": head["yaw_peak_to_peak_degrees"],
                "historical_visibility_candidate": head.get("visible_candidate"),
                "visibility_is_not_naturalness": True,
            }
            if kind == "heads":
                with np.load(source_path, allow_pickle=False) as raw:
                    names = raw["joint_names"].tolist()
                    global_rotations = raw["global_rot_mats"].astype(np.float64)
                    relative = global_rotations[:, names.index("Spine3")].swapaxes(-1, -2) @ global_rotations[:, names.index("Head")]
                    yaw_pitch_roll = np.rad2deg(np.unwrap(Rotation.from_matrix(relative).as_euler("YXZ"), axis=0))
                raw_metrics["roll_peak_to_peak_degrees"] = float(np.ptp(yaw_pitch_roll[:, 2]))
                raw_metrics["rotation_measurement"] = "Head relative to Spine3, YXZ Euler axes, unwrapped"
                raw_metrics["human_review"] = "Rejected as unnatural; the raw trajectory is not used for playback"
                result["source"].update({"rawConditionProvenance": sources, "rawRecord": entry["record"],
                                         "rawSeed": entry["seed"], "rawMetrics": raw_metrics})
                result = apply_profile(result, resource_id)
                staged.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n", encoding="utf-8")
            elif kind == "rights":
                raw_metrics["right_arm_diagnostics"] = entry["right_arm_diagnostics"]
                result["source"]["candidateSelection"] = {
                    "purpose": "Curated native ARDY demonstration, not unscreened instruction-compliance evaluation",
                    "record": entry["record"], "seed": entry["seed"], "conditionSha256": entry["condition_sha256"],
                    "conditionAndModelSources": sources, "candidateSeeds": [1, 2, 3],
                    "baselineSeed": 0, "baselineReview": "Rejected: the retargeted right hand crossed the face/midline",
                    "candidateReport": REPORT_NAMES["rights"],
                    "historySource": reports["rights"]["history_source"], "languageModelsLoaded": False,
                }
                staged.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n", encoding="utf-8")
            elif kind == "mirrored-left":
                raw_metrics["label"] = "Original left-conditioned ARDY source only; mirrored playback is derived, not a right-condition generation result"
                selection_reason = (
                    "Selected for the current avatar demonstration after inspecting Unity frames 16, 25, 40, 55, 85 and 115: "
                    "the hand waves on the body's right side without covering the face or taking a salute-like pose. "
                    "The native right-condition seed-0 baseline and seeds 1/2/3 remain audit inputs and were not selected.")
                result["text"] = "Derived right-hand wave: mirrored reviewed left-hand wave; not a native right-hand-condition sample."
                result["displayIntent"] = result["text"]
                result["source"].update({
                    "generationConditionRecord": entry["record"], "generationSeed": entry["seed"],
                    "generationConditionAndModelSources": sources,
                    "generationHistory": {key: reports["baseline"][key] for key in (
                        "history_source", "history_frames", "windows_per_clip", "new_frames_per_window",
                        "text_encoder_loaded", "qwen_model_loaded", "rng_reset")},
                    "nativeRightConditionOutput": False,
                })
                result["source"]["derivation"].update({
                    "selectionStatus": "Selected derived demonstration; not native right-condition instruction success",
                    "selectionReason": selection_reason, "reviewedUnityFrames": [16, 25, 40, 55, 85, 115],
                    "verification": {key: mirror_verification[key] for key in (
                        "analysisScript", "analysisScriptSha256", "frameCount", "fps", "jointCount",
                        "jointPermutationInvolution", "hierarchyPreserved", "jointNamesAndParentsUnchanged",
                        "doubleMirrorFrameMaximumMatrixError", "doubleMirrorRestMaximumMatrixError",
                        "serializedFrameMaximumMatrixError", "sourceUnchanged")},
                })
                staged.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n", encoding="utf-8")
            assets.append({
                "id": resource_id, "resource": project_path(ASSETS / staged.name),
                "sha256": sha256(staged), "bytes": staged.stat().st_size,
                "source_npz": project_path(source_path), "source_npz_sha256": entry["sha256"],
                "source_report": REPORT_NAMES["baseline" if kind == "mirrored-left" else kind],
                "record": entry["record"], "seed": entry["seed"], "frames": len(result["frames"]), "fps": result["fps"],
                "selection": "Explicit controlled conversational curve replacing a rejected raw head trajectory" if kind == "heads" else "Reviewed left-wave mirrored to the right; native right-condition baseline and all three candidates were not selected" if kind == "mirrored-left" else "Curated native ARDY right-wave candidate after a rejected seed-0 baseline" if kind == "rights" else "Fixed ARDY preview baseline",
                "record_role": "Raw audit provenance only; the controlled curve is not conditioned by this record" if kind == "heads" else "Original left-hand generation condition; used as the source of a mirrored right-hand demonstration, not a right-hand training or generation record" if kind == "mirrored-left" else "Original ARDY motion condition",
                "motion_origin": "controlled-conversational-envelope, not native ARDY" if kind == "heads" else "derived-mirrored-left, not native right-condition output" if kind == "mirrored-left" else "raw-ARDY-motion",
                "rotation_application": result["source"].get("rotationApplication", "retarget-global"),
                "display_intent": result.get("displayIntent", result["text"]),
                "raw_source_metrics": raw_metrics,
                "processing": result["source"].get("processing"),
                "profile_statistics": result["source"].get("profileStatistics"),
                "neutral_joints_sha256": result["source"]["neutralJointsSha256"],
            })
            if kind == "mirrored-left":
                assets[-1]["derivation"] = result["source"]["derivation"]
        candidates = reports["heads"]["results"]
        failed = [entry["name"] for entry in candidates if not entry["head_motion_diagnostics"]["visible_candidate"]]
        passed = [entry["name"] for entry in candidates if entry["head_motion_diagnostics"]["visible_candidate"]]
        baseline_head_screen = {entry["name"]: entry["head_motion_diagnostics"]["visible_candidate"]
                                for entry in reports["baseline"]["results"] if entry["record"]["family"] in ("nod", "shake_head")}
        left_wave = next(entry for entry in reports["baseline"]["results"] if entry["name"] == "left-wave")
        wrist_step = left_wave["windows"][1]["seam_pose_step"]["local_rotation_max_degrees"]
        manifest = {
            "schema": 1, "purpose": "Unity demonstrations: native ARDY left-wave, explicitly mirrored right-wave, and controlled conversational head curves; not a model benchmark",
            "curated_demo_selection": True, "condition_and_model_sources": sources,
            "condition_and_model_sources_role": "Provenance of the raw ARDY audit clips only; the replacement head curves do not use model conditioning",
            "head_motion_origin": "Explicit controlled curves; not raw model output or evidence of improved ARDY head generation",
            "right_motion_origin": "Left-conditioned ARDY source mirrored for the reviewed avatar demonstration; not native right-condition generation success",
            "qwen_model_provenance": qwen_metadata["provenance"],
            "qwen_feature_bundle_sha256": sha256(qwen),
            "generation": {key: reports["baseline"][key] for key in (
                "history_source", "history_frames", "windows_per_clip", "new_frames_per_window",
                "text_encoder_loaded", "qwen_model_loaded", "postprocessing", "rng_reset")},
            "export_language_models_loaded": False,
            "validated_generation_clips": validated_count,
            "right_candidate_screen": {
                "purpose": "Bounded native ARDY demonstration screening; not an unscreened instruction success rate",
                "condition_record": "motion-00004", "split": "val",
                "additional_candidate_count": len(reports["rights"]["results"]),
                "candidate_names": [entry["name"] for entry in reports["rights"]["results"]],
                "candidate_seeds": reports["rights"]["seeds"],
                "baseline": {"name": "right-wave", "seed": 0, "status": "rejected",
                             "reason": "Right hand crossed the face/midline on the actual avatar even after chest anchoring"},
                "selected": next(({"name": entry["name"], "seed": entry["seed"]} for _, kind, entry, _ in selected if kind == "rights"), None),
                "final_playback": {"kind": "derived-mirrored-left", "source_record": "motion-00053", "source_split": "test", "source_seed": 0,
                                   "native_right_condition_output": False,
                                   "reason": "Reviewed mirror kept the hand on the right side, avoiding face occlusion and the native seed-3 salute-like pose",
                                   "reviewed_unity_frames": [16, 25, 40, 55, 85, 115]},
                "limitation": "Source chest-local geometry and chosen playback do not establish instruction-compliance or naturalness rates; all candidates remain in the audit report",
            },
            "head_candidate_screen": {"total": len(candidates), "failed_count": len(failed), "failed": failed,
                                      "status": "Historical visibility screen only; both previously selected raw head candidates were subsequently rejected as unnatural",
                                      "passed_count": len(candidates) - len(failed),
                                      "passed": passed,
                                      "criteria": {entry["record"]["family"]: entry["head_motion_diagnostics"]["visibility_criterion"] for entry in candidates}},
            "baseline_head_visibility": baseline_head_screen,
            "generation_reports": {
                "unity-preview-generation.json": {"source": project_path(baseline_report), "sha256": sha256(baseline_report)},
                "unity-head-candidates.json": {"source": project_path(head_report), "sha256": sha256(head_report)},
                "unity-right-candidates.json": {"source": project_path(right_report), "sha256": sha256(right_report)},
            },
            "assets": assets,
            "limitations": [
                "Raw nod provenance refers to a training description; the controlled nod curve is not generated from that description. Native right-condition audit clips belong to validation; final right playback mirrors a test-split left-condition clip.",
                "The final right-wave is explicitly derived by reflection and a left/right joint permutation. It does not establish successful right-hand instruction following by Qwen, the adapter or ARDY.",
                "Both previously curated raw head candidates were rejected during human review. Their motion rotations are not played; all raw candidates and reports remain intact.",
                "Raw head activity statistics and historical visibility results do not describe the replacement controlled curves.",
                "The short head curves have explicit amplitudes and C2 timing. They require visual review with the current avatar and do not establish improved model instruction compliance.",
                "Wave clips start without a common idle or Unity pose and continue with ARDY self-history. Controlled head curves add offsets to the current pose and do not replace its body motion.",
                f"The left-wave first seam has a {wrist_step:.2f}-degree maximum joint rotation step; review its wrist transition.",
                "The exporter omits root translation. JSON assets do not establish successful live service or avatar integration.",
            ],
        }
        # Stage the audit files before publishing any asset.
        for source, name in ((baseline_report, REPORT_NAMES["baseline"]), (head_report, REPORT_NAMES["heads"]), (right_report, REPORT_NAMES["rights"])):
            (staging / name).write_bytes(source.read_bytes())
        (staging / "unity-preview-assets.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
        ASSETS.mkdir(parents=True, exist_ok=True)
        REPORTS.mkdir(parents=True, exist_ok=True)
        for entry in assets:
            publish_bytes((staging / f"{entry['id']}.json").read_bytes(), ASSETS / f"{entry['id']}.json")
        for name in (*REPORT_NAMES.values(), "unity-preview-assets.json"):
            publish_bytes((staging / name).read_bytes(), REPORTS / name)
    return {"assets": len(assets), "validated_generation_clips": validated_count,
            "head_candidates_failed": len(failed), "manifest": project_path(REPORTS / "unity-preview-assets.json")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-report", type=Path, default=ROOT / "Server/ARDY/runtime/unity-preview/report.json")
    parser.add_argument("--head-report", type=Path, default=ROOT / "Server/ARDY/runtime/unity-head-candidates/report.json")
    parser.add_argument("--right-report", type=Path, default=DEFAULT_RIGHT_REPORT)
    parser.add_argument("--dataset", type=Path, default=ROOT / "Tools/MotionAdapter/runtime/prompts.jsonl")
    parser.add_argument("--qwen", type=Path, default=ROOT / "Tools/MotionAdapter/runtime/qwen-2000.npz")
    parser.add_argument("--adapter", type=Path, default=ROOT / "Tools/MotionAdapter/runtime/mlp-seed1.pt")
    parser.add_argument("--verify-only", action="store_true", help="Validate every baseline and candidate source without writing Unity resources")
    args = parser.parse_args()
    print(json.dumps(build_assets(args.baseline_report, args.head_report, args.dataset, args.qwen, args.adapter,
                                 args.verify_only, right_report=args.right_report)))


if __name__ == "__main__":
    main()
