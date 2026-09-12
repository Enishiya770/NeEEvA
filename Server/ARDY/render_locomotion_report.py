"""Read existing locomotion NPZ evidence and render CPU scientific diagnostics.

No generated trajectory, threshold, original report or model is changed. Height
strata are exploratory explanations of the original failed screen, not a new
acceptance threshold. Predicted contacts are a second, explicitly non-ground-truth
view; selection by measured speed is never used to define a planted foot.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation, PillowWriter
import numpy as np

from Server.ARDY.locomotion_metrics import FOOT_NAMES
from Tools.MotionAdapter.export_unity import load_core27_reference
from Server.ARDY.motion_service.backend import grounded_source_root


ROOT = Path(__file__).resolve().parents[2]
HEIGHTS = (.02, .04, .06, .10)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def stats(values):
    values = np.asarray(values, float)
    if not values.size:
        return {"count": 0, "minimum": None, "mean": None, "median": None,
                "p95": None, "maximum": None}
    return {"count": int(values.size), "minimum": float(values.min()),
            "mean": float(values.mean()), "median": float(np.median(values)),
            "p95": float(np.percentile(values, 95)), "maximum": float(values.max())}


def load_runs(directory):
    result = []
    for child in sorted(Path(directory).glob("*-seed*")):
        with np.load(child / "native.npz", allow_pickle=False) as data:
            arrays = {key: data[key].copy() for key in data.files}
        report = json.loads((child / "report.json").read_text(encoding="utf-8"))
        names = list(arrays["joint_names"])
        p = np.asarray(arrays["posed_joints"], float)
        fps = float(arrays["fps"])
        feet = p[:, [names.index(name) for name in FOOT_NAMES]]
        contacts = np.asarray(arrays["foot_contacts"])
        if (contacts.shape != feet.shape[:2] or not np.isin(contacts, [0, 1]).all()
                or not np.isfinite(feet).all()):
            raise ValueError("Expected finite Core27 feet and four binary predicted contact channels")
        result.append({"id": child.name, "directory": child, "arrays": arrays,
                       "report": report, "names": names, "fps": fps, "feet": feet,
                       "height": feet[..., 1], "contacts": contacts.astype(bool),
                       "speed": np.linalg.norm(np.diff(feet[..., [0, 2]], axis=0), axis=-1) * fps})
    if not result:
        raise ValueError("No existing *-seed*/native.npz reports found")
    return result


def summarize_selection(run, selection):
    speed = run["speed"]
    return {"intervalSampleFraction": float(selection.mean()),
            "horizontalSpeedMetersPerSecond": stats(speed[selection]),
            "perJoint": {name: stats(speed[:, index][selection[:, index]])
                         for index, name in enumerate(FOOT_NAMES)}}


def contact_runs(run):
    """Consecutive predicted-contact episodes, with no speed filter."""
    output = []
    for joint, name in enumerate(FOOT_NAMES):
        flags = run["contacts"][:, joint]
        edges = np.diff(np.r_[False, flags, False].astype(int))
        for start, end in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)):
            if end - start < 3:
                continue
            planar = run["feet"][start:end, joint][:, [0, 2]]
            displacement = np.linalg.norm(planar - planar[0], axis=-1)
            speed = run["speed"][start:end - 1, joint]
            output.append({"joint": name, "startFrame": int(start), "endFrameExclusive": int(end),
                           "sampledSeconds": float((end - start - 1) / run["fps"]),
                           "maximumExcursionMeters": float(displacement.max()),
                           "endpointDisplacementMeters": float(displacement[-1]),
                           "pathLengthMeters": float(speed.sum() / run["fps"]),
                           "horizontalSpeedMetersPerSecond": stats(speed),
                           "heightMeters": stats(run["height"][start:end, joint])})
    return output


def diagnostic_report(runs, input_directory):
    lock_path = ROOT / "Tools/MotionAdapter/upstream-lock.json"
    lock = json.loads(lock_path.read_text(encoding="utf-8-sig"))
    vendor = ROOT / "Server/ARDY/vendor" / ("ardy-" + lock["ardy"])
    names, _, neutral, neutral_sha = load_core27_reference(vendor)  # CPU asset only.
    root, neutral_heights = grounded_source_root(neutral, names)
    records = []
    for run in runs:
        h, c, v, fps = run["height"], run["contacts"], run["speed"], run["fps"]
        max_h = np.maximum(h[:-1], h[1:])
        predicted = c[:-1] & c[1:]
        unpredicted = ~c[:-1] & ~c[1:]
        transition = c[:-1] != c[1:]
        strata = {f"{height:.2f}": summarize_selection(run, max_h <= height) for height in HEIGHTS}
        # Disjoint bands make clear which height strata contribute fast samples.
        bands = {}
        lower = -float("inf")
        for upper in HEIGHTS:
            label = f"{'-inf' if not np.isfinite(lower) else f'{lower:.2f}'}_to_{upper:.2f}"
            bands[label] = summarize_selection(run, (max_h > lower) & (max_h <= upper))
            lower = upper
        bands["above_0.10"] = summarize_selection(run, max_h > .10)
        low_fast = (max_h <= .10) & (v > .30)
        very_low_fast = (max_h <= .02) & (v > .30)
        largest = sorted(np.argwhere(very_low_fast), key=lambda ij: -v[tuple(ij)])[:10]
        examples = [{"intervalStartFrame": int(i), "intervalEndFrame": int(i + 1),
                     "startSeconds": float(i / fps), "joint": FOOT_NAMES[j],
                     "speedMetersPerSecond": float(v[i, j]),
                     "endpointHeightsMeters": h[i:i + 2, j].tolist(),
                     "endpointPredictedContacts": c[i:i + 2, j].tolist(),
                     "endpointPositionsMeters": run["feet"][i:i + 2, j].tolist()}
                    for i, j in largest]
        episodes = contact_runs(run)
        phases = {}
        stop = run["report"]["metrics"]["stop"]["commandedStartFrame"]
        for label, start, end in (("movingAndBraking", 0, stop), ("commandedStop", stop, len(h) - 1)):
            phase = np.zeros(v.shape, bool)
            phase[start:end] = True
            phases[label] = {"intervalStartFrame": start, "intervalEndFrameExclusive": end,
                             "allFeetSpeedMetersPerSecond": stats(v[phase]),
                             "predictedContactSpeedMetersPerSecond": stats(v[phase & predicted]),
                             "heightStrata": {f"{height:.2f}": summarize_selection(run, phase & (max_h <= height))
                                              for height in HEIGHTS}}
        records.append({
            "id": run["id"], "fps": fps, "frames": len(h),
            "sourceHashes": {name: sha(run["directory"] / name) for name in ("native.npz", "report.json")},
            "originalScreen": run["report"]["metrics"]["checks"]["lowFootHorizontalSpeedP95"],
            "heightMetersPerJoint": {name: stats(h[:, j]) for j, name in enumerate(FOOT_NAMES)},
            "heightOnlyCumulativeStrata": strata, "heightOnlyDisjointBands": bands,
            "predictedContact": {"warning": "Model-predicted contact, not a physical sensor or independent ground truth.",
                                 "frameFractionPerJoint": c.mean(axis=0).tolist(),
                                 "bothEndpointsTrue": summarize_selection(run, predicted),
                                 "bothEndpointsFalse": summarize_selection(run, unpredicted),
                                 "transitionIntervals": summarize_selection(run, transition),
                                 "episodesAtLeastThreeFrames": episodes,
                                 "episodeMaximumExcursionMeters": stats([episode["maximumExcursionMeters"] for episode in episodes])},
            "lowAndFastIntervals": {"definition": "Both heights <=.10m and measured horizontal speed >.30m/s; diagnostic intersection only, never a stance-selection rule.",
                                    "count": int(low_fast.sum()),
                                    "bothPredictedContactTrue": int(np.count_nonzero(low_fast & predicted)),
                                    "bothPredictedContactFalse": int(np.count_nonzero(low_fast & unpredicted)),
                                    "predictedContactTransition": int(np.count_nonzero(low_fast & transition))},
            "veryLowAndFastIntervals": {"definition": "Both heights <=.02m, speed >.30m/s; near-ground motion warrants avatar/sole inspection.",
                                        "count": int(very_low_fast.sum()), "largestExamples": examples},
            "phases": phases,
        })
    predicted_p95 = [record["predictedContact"]["bothEndpointsTrue"]["horizontalSpeedMetersPerSecond"]["p95"] for record in records]
    predicted_max = [record["predictedContact"]["bothEndpointsTrue"]["horizontalSpeedMetersPerSecond"]["maximum"] for record in records]
    low2_p95 = [record["heightOnlyCumulativeStrata"]["0.02"]["horizontalSpeedMetersPerSecond"]["p95"] for record in records]
    episode_excursion = [record["predictedContact"]["episodeMaximumExcursionMeters"]["maximum"] for record in records]
    original_statuses = {record["id"]: record["originalScreen"]["status"] for record in records}
    episode_examples = []
    for record in records:
        for episode in record["predictedContact"]["episodesAtLeastThreeFrames"]:
            episode_examples.append({"id": record["id"], **episode})
    episode_examples.sort(key=lambda episode: -episode["maximumExcursionMeters"])
    return {
        "schema": "ardy-locomotion-foot-diagnostic-v1", "createdUtc": datetime.now(timezone.utc).isoformat(),
        "inputDirectory": str(Path(input_directory).resolve()), "diagnosticScriptSha256": sha(__file__),
        "originalPlanSha256": sha(Path(input_directory) / "plan.json"),
        "originalThresholdsPreserved": True, "originalResultsOverwritten": False,
        "method": {"coordinates": "Unaligned original ARDY source world positions in metres; +Y up; ground Y=0.",
                   "selection": "Each interval needs BOTH sample endpoints <= height cutoff; horizontal displacement / dt. All strata are height-only, with no velocity selection.",
                   "contactChannels": list(FOOT_NAMES), "contactChannelProvenance": "Pinned Core27 uses left foot/toe then right foot/toe. inverse() thresholds model-predicted channels >.5; this file does not recompute contacts from measured speed.",
                   "speedStatistics": "Adjacent 20Hz interval-average speed, not continuous peak speed.",
                   "interpretation": "Exploratory post-hoc diagnosis of a failed engineering screen. No new pass/fail gate, no naturalness rating, and no physical-stance claim."},
        "neutralSkeleton": {"sourceRevision": lock["ardy"], "neutralAssetSha256": neutral_sha,
                            "groundedPelvisYMeters": float(root[1]), "groundedFootSampleYMeters": neutral_heights,
                            "footSampleMeaning": "Foot is the foot rotation joint under Leg, with ToeBase its child. Foot (training channel named heel) is not the mesh heel/sole surface: its neutral position is about 5.85cm above the ToeBase support plane.",
                            "neutralFootPositionsMeters": {name: (neutral[names.index(name)] + root).tolist() for name in FOOT_NAMES}},
        "findings": {
            "originalScreen": "Original lowFootHorizontalSpeedP95 statuses and .10m/.30m/s thresholds are retained; this diagnosis does not replace acceptance results.",
            "originalScreenStatuses": original_statuses,
            "predictedContactSpeedP95RangeMps": [min(predicted_p95), max(predicted_p95)],
            "predictedContactSpeedMaximumRangeMps": [min(predicted_max), max(predicted_max)],
            "heightOnly02mSpeedP95RangeMps": [min(low2_p95), max(low2_p95)],
            "predictedContactEpisodeMaximumExcursionRangeMeters": [min(episode_excursion), max(episode_excursion)],
            "largestPredictedContactExcursionExamples": episode_examples[:3],
            "interpretation": "The .10m height-only screen mixes low-clearance swing motion with support samples. Low-and-fast samples are absent when both endpoint contacts are predicted true. This supports selection contamination as the primary explanation for the approximately 2.2m/s p95, rather than establishing sustained physical stance sliding at that speed.",
            "slowDrift": "Low predicted-contact speed does not mean a foot is locked: consecutive predicted-contact episodes still show centimetre-scale accumulated horizontal excursion. Turning foot pivots, prediction error and physical slipping cannot be distinguished from these joint samples alone; inspect actual avatar soles.",
            "remainingUncertainty": "Predicted contacts are not independent truth and may omit true contacts. Several toe samples move quickly at 1-2cm height, particularly redirect seed1; source point clearance does not establish avatar shoe/sole clearance. Actual retargeted foot contact and visible scuffing remain unverified.",
            "naturalness": "unknown", "physicalContact": "unknown"},
        "runs": records,
    }


def plot_overview(runs, path):
    fig, axes = plt.subplots(int(np.ceil(len(runs) / 3)), 3, figsize=(14, 8), squeeze=False, layout="constrained")
    for ax, run in zip(axes.flat, runs):
        arrays = run["arrays"]
        p = arrays["posed_joints"][:, run["names"].index("Hips")]
        target = arrays["target_root_positions"]
        ax.plot(target[:, 0], target[:, 2], color="#283747", lw=2.8, alpha=.55, label="Timed target")
        ax.plot(p[:, 0], p[:, 2], color="#dd7833", lw=1.4, label="Generated Hips")
        ax.scatter(p[::40, 0], p[::40, 2], s=22, color="#dd7833", zorder=3, label="Window starts")
        ax.scatter(target[-1, 0], target[-1, 2], color="#283747", marker="x", s=50, zorder=4)
        if run["id"].startswith("redirect"):
            ax.annotate("goal changes", xy=(p[80, 0], p[80, 2]), xytext=(8, -20), textcoords="offset points", fontsize=8)
        metric = run["report"]["metrics"]["rootTracking"]
        ax.set_title(f"{run['id']}\nRMSE {100 * metric['rmseMeters']:.2f} cm | end {100 * metric['endpointErrorMeters']:.2f} cm", fontsize=10)
        ax.set(xlabel="Source world X (m)", ylabel="Source world Z (m)")
        ax.set_aspect("equal", adjustable="datalim")
        ax.grid(alpha=.2)
    for ax in list(axes.flat)[len(runs):]:
        ax.set_visible(False)
    axes.flat[0].legend(fontsize=8)
    fig.suptitle("Native ARDY locomotion: target and generated root in original coordinates\nNo fitted alignment, root snapping, foot IK, or avatar retargeting", fontsize=14)
    fig.savefig(path, dpi=160)
    plt.close(fig)


def plot_foot_height_speed(runs, diagnostics, path):
    fig, axes = plt.subplots(int(np.ceil(len(runs) / 3)), 3, figsize=(15, 9), squeeze=False, layout="constrained")
    max_speed = max(float(run["speed"].max()) for run in runs)
    max_height = max(float(run["height"].max()) for run in runs)
    for ax, run, record in zip(axes.flat, runs, diagnostics["runs"]):
        h, c, v = run["height"], run["contacts"], run["speed"]
        x = 100 * np.maximum(h[:-1], h[1:])
        planted = c[:-1] & c[1:]
        neither = ~c[:-1] & ~c[1:]
        transition = c[:-1] != c[1:]
        for selection, color, label, size in ((neither, "#d77935", "Both predicted false", 9),
                                             (transition, "#bbbbbb", "Prediction transition", 10),
                                             (planted, "#26779c", "Both predicted true", 11)):
            ax.scatter(x[selection], v[selection], s=size, color=color, alpha=.55, linewidths=0, label=label)
        for height in HEIGHTS:
            ax.axvline(height * 100, color="#888888", ls=":" if height < .1 else "--", lw=.8)
        ax.axhline(.30, color="#962d37", ls="--", lw=.8)
        low_p95 = record["heightOnlyCumulativeStrata"]["0.10"]["horizontalSpeedMetersPerSecond"]["p95"]
        pred_p95 = record["predictedContact"]["bothEndpointsTrue"]["horizontalSpeedMetersPerSecond"]["p95"]
        ax.set_title(f"{run['id']}\nP95 <=10cm: {low_p95:.2f} m/s | predicted-contact: {pred_p95:.3f} m/s", fontsize=9)
        ax.set(xlabel="Max endpoint foot-sample height (cm)", ylabel="Horizontal interval speed (m/s)",
               xlim=(-1, max_height * 100 + 1), ylim=(-.05, max_speed * 1.06))
        ax.grid(alpha=.12)
    for ax in list(axes.flat)[len(runs):]:
        ax.set_visible(False)
    axes.flat[0].legend(fontsize=7, loc="upper right")
    fig.suptitle("Why a low-foot speed screen includes swing motion\nEach dot: one joint / one 20 Hz interval. Predicted contacts are not physical contact truth.\nHeight cuts: 2 / 4 / 6 / 10 cm. Neutral Foot joint is 5.85 cm high; ToeBase is 0 cm.", fontsize=12)
    fig.savefig(path, dpi=160)
    plt.close(fig)


def plot_skeleton_gif(run, path):
    arrays, names = run["arrays"], run["names"]
    p = arrays["posed_joints"].astype(float)
    parents = arrays["joint_parents"]
    # Plot mapping is horizontal X/Z with vertical Y. Camera follows generated
    # root translation only; no pose or root data are transformed for evaluation.
    fig = plt.figure(figsize=(7, 7))
    ax = fig.add_subplot(111, projection="3d")
    lines = []
    for index, parent in enumerate(parents):
        if parent < 0:
            continue
        color = "#d56e2e" if names[index].startswith("Left") else "#2a7994" if names[index].startswith("Right") else "#454545"
        line, = ax.plot([], [], [], color=color, lw=2.5, marker="o", markersize=2.2)
        lines.append((line, index, int(parent)))
    ax.set(xlabel="Source X (m)", ylabel="Source Z (m)", zlabel="Height Y (m)")
    ax.set_box_aspect((2, 2, 2))
    ax.view_init(elev=14, azim=-58)
    caption = fig.text(.5, .035, "", ha="center", fontsize=10)
    fig.suptitle(f"{run['id']} | raw Core27 skeleton\nCamera follows generated Hips; full legs visible; no correction", fontsize=12)
    ground, = ax.plot([], [], [], color="#aaaaaa", lw=1)
    root_index = names.index("Hips")

    def update(frame):
        pose = p[frame]
        root = pose[root_index]
        for line, joint, parent in lines:
            points = pose[[parent, joint]]
            line.set_data_3d(points[:, 0], points[:, 2], points[:, 1])
        ax.set_xlim(root[0] - .9, root[0] + .9)
        ax.set_ylim(root[2] - .9, root[2] + .9)
        ax.set_zlim(-.1, max(1.9, float(p[..., 1].max()) + .1))
        ground.set_data_3d(root[0] + np.array([-.8, .8, .8, -.8, -.8]),
                           root[2] + np.array([-.8, -.8, .8, .8, -.8]), np.zeros(5))
        caption.set_text(f"t={frame/run['fps']:.2f}s | frame {frame} | X={root[0]:.2f}m Z={root[2]:.2f}m\nOrange: left limbs | Blue: right limbs | Ground Y=0")
        return [entry[0] for entry in lines] + [ground, caption]

    animation = FuncAnimation(fig, update, frames=len(p), interval=1000 / run["fps"], blit=False)
    animation.save(path, writer=PillowWriter(fps=run["fps"]), dpi=90)
    plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--diagnostics", type=Path, help="Optional separate diagnostics JSON path")
    parser.add_argument("--gif", action="store_true", help="Also render turn-stop-seed0 skeleton.gif")
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise ValueError("Use an empty review directory; existing results are never overwritten")
    diagnostics_path = args.diagnostics or args.output / "foot-diagnostics.json"
    if diagnostics_path.exists():
        raise ValueError("Existing diagnostics are never overwritten")
    args.output.mkdir(parents=True, exist_ok=True)
    diagnostics_path.parent.mkdir(parents=True, exist_ok=True)
    runs = load_runs(args.input)
    diagnostics = diagnostic_report(runs, args.input)
    diagnostics_path.write_text(json.dumps(diagnostics, indent=2, allow_nan=False), encoding="utf-8")
    print(json.dumps({"diagnostics": str(diagnostics_path.resolve()), "findings": diagnostics["findings"]}), flush=True)
    plot_overview(runs, args.output / "overview.png")
    plot_foot_height_speed(runs, diagnostics, args.output / "foot-height-speed.png")
    print(json.dumps({"plotsComplete": True, "output": str(args.output.resolve())}), flush=True)
    if args.gif:
        selected = next((run for run in runs if run["id"] == "turn-stop-seed0"), None)
        if selected is None:
            raise ValueError("--gif needs an existing turn-stop-seed0 run")
        plot_skeleton_gif(selected, args.output / "skeleton.gif")
    manifest = {"input": str(args.input.resolve()), "renderScriptSha256": sha(__file__),
                "diagnostics": {"path": str(diagnostics_path.resolve()), "sha256": sha(diagnostics_path)},
                "artifacts": {file.name: {"path": str(file.resolve()), "sha256": sha(file)}
                              for file in sorted(args.output.iterdir()) if file.is_file()}}
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2, allow_nan=False), encoding="utf-8")
    print(json.dumps({"complete": True, "manifest": str((args.output / 'manifest.json').resolve())}), flush=True)


if __name__ == "__main__":
    main()
