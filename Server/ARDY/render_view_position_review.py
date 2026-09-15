"""Read real native view-position request/response pairs and render source motion.

CPU-only reconstruction from the pinned Core27 reference and saved Unity wire.
No model, network, Unity, retargeting, root correction or timing change is used.
Generated frames are not a claim that every frame was played (especially cancel).
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation, PillowWriter
import numpy as np
from scipy.spatial.transform import Rotation

from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY, fk_positions, load_core27_reference


ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def vectors(items):
    return np.asarray([[item[axis] for axis in "xyz"] for item in items], dtype=float)


def load_case(directory, case, reference):
    name = case["name"]
    request_path = directory / (name + ".request.json")
    response_path = directory / (name + ".response.json")
    request, response = read(request_path), read(response_path)
    if any(request[key] != response[key] for key in ("characterId", "requestId", "revision")):
        raise ValueError("Request/response identity mismatch: " + name)
    clip = response["rotationClip"]
    names, parents, neutral, neutral_sha = reference
    if clip["jointNames"] != names or not np.array_equal(clip["jointParents"], parents):
        raise ValueError("Wire skeleton differs from pinned Core27: " + name)
    if response["source"]["coordinateSystem"] != "unity-lh-y-up-z-forward":
        raise ValueError("Unsupported source coordinates")
    if clip["source"].get("rotationApplication"):
        raise ValueError("Only original absolute native rotation wires are accepted")
    fps = float(clip["fps"])
    quats = np.asarray([[[q[k] for k in "xyzw"] for q in f["globalRotations"]] for f in clip["frames"]])
    rest = np.asarray([[q[k] for k in "xyzw"] for q in clip["restGlobalRotations"]])
    root = vectors([frame["rootPosition"] for frame in response["frames"]])
    targets = vectors([target["rootPosition"] for target in request["targets"]])
    heading = np.asarray([target["headingDegrees"] for target in request["targets"]], dtype=float)
    if len(root) != len(quats) or root.shape != targets.shape or not np.isfinite(quats).all() or fps != 20:
        raise ValueError("Unexpected native dimensions or frame rate")
    if np.max(np.abs(np.linalg.norm(quats, axis=-1) - 1)) > .002:
        raise ValueError("Nonunit native quaternion")
    r = Rotation.from_quat(quats.reshape(-1, 4)).as_matrix().reshape(-1, 27, 3, 3)
    rest_r = Rotation.from_quat(rest).as_matrix()
    delta = r @ rest_r.swapaxes(-1, -2)
    # Same neutral/global convention as the repository's pinned exporter. The
    # root wire is the source world pelvis, including its generated Y; retain it.
    posed = fk_positions(delta, neutral @ ARDY_TO_UNITY, parents) + root[:, None, :]
    forward = delta[:, 0, :, 2]
    actual_heading = np.degrees(np.arctan2(forward[:, 0], forward[:, 2]))
    heading_error = (actual_heading - heading + 180) % 360 - 180
    feet = [names.index(name) for name in ("LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase")]
    speed = np.linalg.norm(np.diff(posed[:, feet][:, :, [0, 2]], axis=0), axis=-1) * fps
    return dict(name=name, case=case, request=request, response=response, fps=fps, names=names,
                parents=parents, root=root, targets=targets, heading=heading,
                actual_heading=actual_heading, heading_error=heading_error, forward=forward,
                posed=posed, feet=feet, speed=speed, neutral_sha=neutral_sha,
                files={str(path.resolve()): sha(path) for path in (request_path, response_path)})


def limits(run):
    root = run["root"]
    lower = root[:, [0, 2]].min(axis=0) - .55
    upper = root[:, [0, 2]].max(axis=0) + .55
    center = (lower + upper) / 2
    size = max(float(np.max(upper - lower)), 1.3)
    return center, size, max(1.85, float(run["posed"][..., 1].max()) + .1)


def style_skeleton(ax, run):
    center, size, height = limits(run)
    ax.set(xlim=(center[0] - size / 2, center[0] + size / 2),
           ylim=(center[1] - size / 2, center[1] + size / 2), zlim=(-.12, height),
           xlabel="X (m)", ylabel="Z (m)", zlabel="Y (m)")
    ax.set_box_aspect((size, size, height + .12))
    ax.view_init(elev=17, azim=-55)
    ax.plot(run["targets"][:, 0], run["targets"][:, 2], np.zeros(len(run["targets"])),
            "--", color="#8771a8", lw=1.4)
    ax.plot(run["root"][:, 0], run["root"][:, 2], np.zeros(len(run["root"])), color="#38526c", lw=1.0)


def bones(ax, run, frame):
    pose = run["posed"][frame]
    lines = []
    for index, parent in enumerate(run["parents"]):
        if parent < 0:
            continue
        color = "#d56e2e" if run["names"][index].startswith("Left") else "#287993" if run["names"][index].startswith("Right") else "#454545"
        p = pose[[parent, index]]
        line, = ax.plot(p[:, 0], p[:, 2], p[:, 1], color=color, lw=2.1, marker="o", markersize=2)
        lines.append((line, int(parent), index))
    return lines


def contact_sheet(run, path):
    fig = plt.figure(figsize=(13.8, 8.8), dpi=125)
    samples = np.unique(np.linspace(0, len(run["root"]) - 1, 6).round().astype(int))
    for slot, frame in enumerate(samples):
        ax = fig.add_subplot(2, 3, slot + 1, projection="3d")
        style_skeleton(ax, run); bones(ax, run, frame)
        ax.set_title(f"{frame / run['fps']:.2f} s | heading error {run['heading_error'][frame]:+.1f} deg", fontsize=10)
    status = run["case"]["result"]["status"]
    fig.suptitle(f"{run['name']} | actual ARDY source skeleton | Unity terminal: {status}", fontsize=15)
    fig.text(.5, .018, "Orange: left / Blue: right | dashed: requested source pelvis trajectory | solid: generated pelvis projection\n"
             "Pinned Core27 reconstruction; original rotations, pelvis heights and timing. Not an avatar render; full generated candidate may exceed actual played frames.",
             ha="center", fontsize=9)
    fig.subplots_adjust(left=.04, right=.98, bottom=.105, top=.89, wspace=.2, hspace=.18)
    fig.savefig(path); plt.close(fig)


def overview(run, path):
    fig, axes = plt.subplots(2, 2, figsize=(12, 8), dpi=130)
    root, target = run["root"], run["targets"]
    t = np.arange(len(root)) / run["fps"]
    ax = axes[0, 0]
    ax.plot(target[:, 0], target[:, 2], "--", color="#8771a8", label="Requested source pelvis")
    ax.plot(root[:, 0], root[:, 2], color="#38526c", label="Generated source pelvis")
    frames = np.unique(np.linspace(0, len(root) - 1, 9).astype(int))
    h = np.radians(run["heading"][frames])
    ax.quiver(target[frames, 0], target[frames, 2], np.sin(h), np.cos(h), color="#8771a8", angles="xy", scale_units="xy", scale=6, width=.005)
    ax.quiver(root[frames, 0], root[frames, 2], run["forward"][frames, 0], run["forward"][frames, 2], color="#38526c", angles="xy", scale_units="xy", scale=6, width=.005)
    ax.set(xlabel="Source X (m)", ylabel="Source Z (m)", title="Position and body heading are independent constraints")
    ax.set_aspect("equal"); ax.margins(.3); ax.legend(fontsize=8)
    axes[0, 1].plot(t, run["heading_error"], color="#38526c")
    axes[0, 1].set(xlabel="Time (s)", ylabel="Degrees", title="Actual pelvis minus requested body heading")
    for index, label in enumerate(("Left foot", "Left toe", "Right foot", "Right toe")):
        axes[1, 0].plot(t, run["posed"][:, run["feet"][index], 1], label=label)
        axes[1, 1].plot(t[:-1], run["speed"][:, index], label=label)
    axes[1, 0].axhline(0, color="gray", lw=.8)
    axes[1, 0].set(xlabel="Time (s)", ylabel="Source height (m)", title="Uncorrected foot/toe heights")
    axes[1, 0].legend(fontsize=8)
    axes[1, 1].set(xlabel="Time (s)", ylabel="Horizontal speed (m/s)", title="Foot/toe motion, not stance or physical contact proof")
    for ax in axes.flat:
        ax.grid(alpha=.2)
    fig.suptitle(run["name"] + " | native source diagnostic; not Unity world-root playback telemetry")
    fig.tight_layout(rect=(0, 0, 1, .95)); fig.savefig(path); plt.close(fig)


def animate(run, path):
    fig = plt.figure(figsize=(7, 6.5), dpi=95)
    ax = fig.add_subplot(111, projection="3d"); style_skeleton(ax, run)
    lines = bones(ax, run, 0)
    title = ax.set_title("")
    fig.text(.5, .035, "Actual native Core27 candidate | 20 FPS, original timing\nNot an avatar recording; cancelled cases include unplayed generated frames.", ha="center", fontsize=8)
    def update(frame):
        for line, parent, index in lines:
            p = run["posed"][frame, [parent, index]]
            line.set_data_3d(p[:, 0], p[:, 2], p[:, 1])
        title.set_text(f"{run['name']} | {frame / run['fps']:.2f}s | body heading {run['actual_heading'][frame]:+.1f} deg")
        return [line[0] for line in lines] + [title]
    animation = FuncAnimation(fig, update, frames=len(run["root"]), interval=1000 / run["fps"], blit=False)
    animation.save(path, writer=PillowWriter(fps=run["fps"]), dpi=95); plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="Native output/view-position-native directory")
    parser.add_argument("--output", type=Path, required=True, help="New empty evidence directory")
    parser.add_argument("--gif", action="store_true", help="Also create the original-20-FPS source skeleton GIFs")
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise ValueError("Existing review evidence is never overwritten")
    args.output.mkdir(parents=True, exist_ok=True)
    report_path = args.input / "report.json"; report = read(report_path)
    lock = read(ROOT / "Tools/MotionAdapter/upstream-lock.json")
    reference = load_core27_reference(ROOT / "Server/ARDY/vendor" / ("ardy-" + lock["ardy"]))
    manifest = {"schema": 1, "sourceReport": str(report_path.resolve()), "sourceReportSha256": sha(report_path),
                "sourceStatus": report.get("status"), "scriptSha256": sha(__file__), "neutralAssetSha256": reference[3],
                "scope": __doc__, "cases": []}
    for case in report["cases"]:
        if not (args.input / (case["name"] + ".response.json")).is_file():
            manifest["cases"].append({"name": case["name"], "rendered": False, "reason": "No exported native response; no substitute motion was created."})
            continue
        run = load_case(args.input, case, reference)
        contact_sheet(run, args.output / (run["name"] + "-poses.png"))
        overview(run, args.output / (run["name"] + "-diagnostic.png"))
        if args.gif:
            animate(run, args.output / (run["name"] + ".gif"))
        manifest["cases"].append({"name": run["name"], "rendered": True, "wireSources": run["files"],
            "frames": len(run["root"]), "fps": run["fps"], "terminalResult": case["result"],
            "requestedDescription": run["request"]["description"],
            "maximumAbsRequestedHeadingErrorDegrees": float(np.max(np.abs(run["heading_error"]))),
            "sourcePelvisEndpointErrorMeters": float(np.linalg.norm((run["root"][-1] - run["targets"][-1])[[0, 2]])),
            "minimumSourceFootToeHeightMeters": float(run["posed"][:, run["feet"], 1].min()),
            "maximumSourceFootToeHorizontalSpeedMetersPerSecond": float(run["speed"].max())})
        print(json.dumps({"rendered": run["name"], "frames": len(run["root"])}), flush=True)
    manifest["artifacts"] = {path.name: sha(path) for path in args.output.iterdir() if path.is_file()}
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2, allow_nan=False), encoding="utf-8")


if __name__ == "__main__":
    main()
