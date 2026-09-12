"""Render an evidence-based room review from the Unity audit/regression JSONs.

No meshes, room dimensions, furniture placement or paths are synthesized. This
figure is a local geometry review. Optional real HTTP generation is mapped into
the world XZ plane separately from the earlier geometric plan. Its full-playback
pass is report evidence; the mapped pelvis is not a per-frame world-root trace.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm
from matplotlib.colors import ListedColormap
from matplotlib.lines import Line2D
from matplotlib.patches import Circle, Rectangle
import numpy as np


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path: Path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def rectangle(entry, **kwargs):
    center, size = entry["center"], entry["size"]
    return Rectangle((center["x"] - size["x"] / 2, center["z"] - size["z"] / 2), size["x"], size["z"], **kwargs)


def xy(point):
    return point["x"], point["z"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    runtime = Path(__file__).resolve().parent / "runtime" / "room-v1"
    parser.add_argument("--audit", type=Path, default=runtime / "terrain-audit-v2" / "report.json")
    parser.add_argument("--navigation", type=Path, default=runtime / "actual-room-v4" / "report.json")
    parser.add_argument("--playback", type=Path, default=runtime / "room-playmode-v4")
    parser.add_argument("--world-start", type=float, nargs=2, default=(-2.9, -4.6))
    parser.add_argument("--world-target", type=float, nargs=2, default=(-4.35, -3.65))
    parser.add_argument("--output", type=Path, default=runtime / "review")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    audit, navigation = load(args.audit), load(args.navigation)
    play_report = load(args.playback / "report.json")
    request, response = load(args.playback / "request.json"), load(args.playback / "response.json")
    if play_report["status"] != "passed" or not play_report["completedNaturally"] or not play_report["realResponseReceived"]:
        raise ValueError("Actual HTTP generation and full playback must pass before adding that result.")
    source_targets = np.asarray([xy(item["rootPosition"]) for item in request["targets"]])
    source_generated = np.asarray([xy(item["rootPosition"]) for item in response["frames"]])
    # First targets hold the measured initial position while heading changes.
    # End targets stop at the known world target from the actual room fixture.
    # These two independently specified correspondences determine the planar
    # rotation+uniform scale+translation; no generated samples are fitted.
    source_start = complex(*source_targets[0])
    source_end = complex(*source_targets[-1])
    world_start, world_end = complex(*args.world_start), complex(*args.world_target)
    if abs(source_end - source_start) < .1:
        raise ValueError("Endpoint-based similarity requires a nontrivial route.")
    similarity = (world_end - world_start) / (source_end - source_start)
    angle = float(np.angle(similarity, deg=True))
    if abs(angle + play_report["initialYaw"]) > .1:
        raise ValueError("Recovered world rotation disagrees with the independently recorded initial yaw.")
    def to_world(points):
        transformed = (points[:, 0] + 1j * points[:, 1] - source_start) * similarity + world_start
        return np.column_stack((transformed.real, transformed.imag))
    world_targets, world_generated = to_world(source_targets), to_world(source_generated)
    seconds = len(source_generated) / response["rotationClip"]["fps"]
    if navigation["status"] != "passed":
        raise ValueError("The navigation report must pass before presenting a validated route.")
    normal = Path("C:/Windows/Fonts/msyh.ttc")
    bold = Path("C:/Windows/Fonts/msyhbd.ttc")
    if normal.exists():
        fm.fontManager.addfont(str(normal))
        if bold.exists():
            fm.fontManager.addfont(str(bold))
        plt.rcParams["font.family"] = fm.FontProperties(fname=str(normal)).get_name()
    plt.rcParams.update({"axes.unicode_minus": False, "font.size": 12, "figure.facecolor": "#f6f8fb", "axes.facecolor": "#f6f8fb"})
    colliders = audit["colliders"]
    living = next(item for item in colliders if item["path"].endswith("/リビング床"))
    main_floor = next(item for item in colliders if item["path"].endswith("/1階床"))
    sofa = next(item for item in colliders if item["path"].endswith("/1階家具/sofa"))
    renderers = audit["renderersWithoutOwnCollider"]
    table = next(item for item in renderers if item["path"].endswith("/1階家具/lowtable"))
    carpet = next(item for item in renderers if item["path"].endswith("/1階家具/carpet"))
    cases = {item["name"]: item for item in navigation["cases"]}
    route = cases["actual-living-room-same-level-route"]
    table_rejection = cases["actual-colliderless-coffee-table-target"]
    floor_rejection = cases["actual-sunken-living-to-main-floor"]
    if not all(item["passed"] for item in (route, table_rejection, floor_rejection)):
        raise ValueError("Selected route/rejection cases did not all pass.")

    fig = plt.figure(figsize=(14.5, 10.7), dpi=170)
    ax = fig.add_axes((.055, .115, .60, .79))
    notes = fig.add_axes((.69, .115, .275, .79))
    notes.axis("off")
    fig.text(.055, .952, "真实客厅 · 生成轨迹与地形验证", fontsize=23, fontweight="bold", color="#172b4d")
    fig.text(.055, .918, "实际生成轨迹；完整播放已通过  ·  俯视图（+Z 朝上）", fontsize=12, color="#5f7185")

    grid = audit["grid"]
    ids = np.asarray(grid["colliderId"]).reshape(grid["height"], grid["width"])
    elevations = np.asarray(grid["surfaceY"]).reshape(ids.shape)
    # These exact raycast cells show the L-shaped sofa rather than inventing a
    # rectangular silhouette from its enclosing bounds.
    cells = np.full(ids.shape, np.nan)
    cells[ids == living["id"]] = 0
    cells[ids == main_floor["id"]] = 1
    living_c, living_s = living["center"], living["size"]
    xs = np.linspace(grid["minX"], grid["maxX"], grid["width"] + 1)
    zs = np.linspace(grid["minZ"], grid["maxZ"], grid["height"] + 1)
    xc, zc = np.meshgrid((xs[:-1] + xs[1:]) / 2, (zs[:-1] + zs[1:]) / 2)
    inside = (abs(xc - living_c["x"]) < living_s["x"] / 2) & (abs(zc - living_c["z"]) < living_s["z"] / 2)
    cells[(ids >= 0) & inside & (elevations > living_c["y"] + .1)] = 2
    cells[ids == sofa["id"]] = 3
    ax.pcolormesh(xs, zs, np.ma.masked_invalid(cells), cmap=ListedColormap(["#dff0e8", "#e3e7ed", "#ddc8ad", "#7783a3"]), vmin=0, vmax=3, shading="flat", rasterized=True, alpha=.92, zorder=1)
    ax.add_patch(rectangle(living, fill=False, ec="#236c5a", lw=2, zorder=4))
    ax.add_patch(rectangle(carpet, fc="#e9dca9", ec="#b89d48", lw=1, alpha=.57, hatch="...", zorder=2))
    # Sofa raycast cells are painted again above the carpet bounding rectangle.
    sofa_cells = np.ma.masked_where(ids != sofa["id"], np.zeros(ids.shape))
    ax.pcolormesh(xs, zs, sofa_cells, cmap=ListedColormap(["#7884a3"]), shading="flat", zorder=3, rasterized=True)
    ax.add_patch(rectangle(table, fc="#b88561", ec="#765237", lw=1.4, zorder=4))
    ax.text(-5.45, -6.1, "沙发\n碰撞投影", ha="center", va="center", color="white", fontsize=11, zorder=6)
    ax.text(table["center"]["x"], table["center"]["z"] + .45, "茶几", ha="center", va="center", color="white", fontsize=12, zorder=6)
    ax.text(-4.28, -4.39, "地毯边界", ha="center", color="#766227", fontsize=10, zorder=6)
    ax.text(-1.05, -5.05, "客厅地板\nY = −0.730 m", color="#226c5b", ha="center", fontsize=10, zorder=6)
    ax.text(-6.75, -7.1, "一楼\nY ≈ 0 m", color="#66758b", ha="center", fontsize=12)

    coordinates = np.asarray([xy(point) for point in route["corners"]])
    ax.plot(coordinates[:, 0], coordinates[:, 1], color="#88b6b5", alpha=.7, lw=2.0, zorder=8)
    ax.scatter(*xy(route["start"]), s=120, color="#172b4d", edgecolor="white", linewidth=1.5, zorder=12)
    ax.add_patch(Circle(xy(route["start"]), .3, fill=False, edgecolor="#172b4d", lw=1, ls=(0, (3, 3)), zorder=9))
    ax.annotate("当前起点\n(−2.90, −4.60)", xy=xy(route["start"]), xytext=(-1.77, -3.84), fontsize=10, color="#172b4d",
                arrowprops={"arrowstyle": "-", "color": "#64758d", "lw": 1}, zorder=12)
    ax.scatter(*xy(route["target"]), marker="*", s=150, color="#88b6b5", edgecolor="white", linewidth=1, zorder=9)
    ax.annotate("先前 4.30 m 几何规划终点", xy=xy(route["target"]), xytext=(-2.8, -9.0), color="#78a4a8", fontsize=9,
                arrowprops={"arrowstyle": "-", "color": "#88b6b5", "lw": 1}, zorder=9)
    ax.plot(world_targets[:, 0], world_targets[:, 1], color="#b8a5d6", lw=2.4, ls=(0, (3, 3)), zorder=10)
    ax.plot(world_generated[:, 0], world_generated[:, 1], color="#6e4593", lw=3.1, zorder=11)
    ax.scatter(*args.world_target, marker="*", s=230, color="#6e4593", edgecolor="white", linewidth=1, zorder=12)
    ax.annotate("本次目标\n(−4.35, −3.65)", xy=args.world_target, xytext=(-5.5, -3.3), color="#6e4593", fontsize=10,
                arrowprops={"arrowstyle": "-", "color": "#6e4593", "lw": 1}, zorder=12)

    for rejection in (table_rejection, floor_rejection):
        ax.scatter(*xy(rejection["target"]), marker="x", s=130, color="#c44743", linewidth=2.7, zorder=13)
    # No path is drawn to rejected targets: these crosses are tested target points.
    ax.annotate("拒绝：跨越约 73 cm 高差", xy=xy(floor_rejection["target"]), xytext=(-7.35, -4.17), fontsize=10, color="#ba4540",
                arrowprops={"arrowstyle": "-", "color": "#ba4540", "lw": 1}, zorder=12)
    ax.annotate("拒绝：目标位于茶几内", xy=xy(table_rejection["target"]), xytext=(-2.49, -6.04), fontsize=10, color="#ba4540",
                arrowprops={"arrowstyle": "-", "color": "#ba4540", "lw": 1}, zorder=12)
    ax.set_xlim(-7.6, .28)
    ax.set_ylim(-9.65, -1.7)
    ax.set_aspect("equal")
    ax.set_xlabel("世界 X（米）", labelpad=8, color="#50637a")
    ax.set_ylabel("世界 Z（米）", labelpad=8, color="#50637a")
    ax.grid(color="#ffffff", lw=.9, alpha=.6, zorder=0)
    ax.spines[["top", "right"]].set_visible(False)
    ax.spines[["left", "bottom"]].set_color("#bdc9d5")
    ax.tick_params(colors="#5f7185", labelsize=10)

    notes.text(0, .97, "真实房间完整播放通过", va="top", fontsize=15, fontweight="bold", color="#172b4d")
    notes.text(0, .905, f"{seconds:.1f} 秒 · {len(source_generated)} 帧", va="top", fontsize=25, fontweight="bold", color="#6e4593")
    notes.text(0, .827, "Qwen 条件＋ARDY 实际生成结果", va="top", fontsize=11, color="#516579")
    notes.text(0, .74, f"• 播放终点检查误差 {play_report['actualEndpointError'] * 1000:.1f} mm\n\n• 从现有聊天位置出发（XZ）\n\n• 地毯顶面约高 2.6 cm\n\n• 茶几／73 cm 高差目标被拒绝", va="top", fontsize=11.5, color="#344c64", linespacing=1.5)
    notes.text(0, .45, "读图边界", va="top", fontsize=15, fontweight="bold", color="#172b4d")
    notes.text(0, .395, "深紫：生成骨盆的 XZ 投影\n紫虚线：传给 ARDY 的目标位置\n浅青：先前 4.30 m 几何规划\n红叉：已拒绝的目标点", va="top", fontsize=10.8, color="#516579", linespacing=1.6)
    notes.text(0, .235, "沙发使用真实碰撞采样投影。\n茶几与地毯使用实测边界框。\n其余色块不代表完整可通行区域。", va="top", fontsize=10.5, color="#65758a", linespacing=1.65)
    notes.text(0, .085, "骨盆轨迹由源坐标映射至世界平面。\n不是逐帧世界 root 观测，未补微小偏置。\n本轮仅客厅局部，未验证楼梯／全屋。", va="top", fontsize=10, color="#92633a", linespacing=1.7,
               bbox={"boxstyle": "round,pad=.7", "fc": "#fff3df", "ec": "none"})
    fig.text(.055, .034, "来源：terrain-audit-v2（地形）＋ actual-room-v4（规划）＋ room-playmode-v4（实际请求、生成与播放检查）。", fontsize=10, color="#6f7f92")
    output = args.output / "living-room-navigation-review.png"
    fig.savefig(output, dpi=170, facecolor=fig.get_facecolor())
    plt.close(fig)
    manifest = {"image": str(output.resolve()), "scope": "Local geometry navigation and world-mapped generated pelvis trajectory; completed playback is supported by its separate pass report. Not per-frame world-root observation.", "audit_status": audit["status"],
                "navigation_status": navigation["status"], "route_length_metres": route["length"],
                "sources": [{"path": str(path.resolve()), "sha256": sha(path)} for path in (args.audit, args.navigation, args.playback / "report.json", args.playback / "request.json", args.playback / "response.json")],
                "script_sha256": sha(Path(__file__)), "geometry_representation": {"living_floor": "actual collider XZ bounds", "sofa": "actual downward raycast collider-ID cells", "coffee_table_and_carpet": "measured renderer AABB"},
                "playback": {"report_status": play_report["status"], "completed_naturally": play_report["completedNaturally"], "frames": len(source_generated), "duration_seconds": seconds, "actual_endpoint_check_error_metres": play_report["actualEndpointError"]},
                "generated_projection": {"source_series": "response.frames[].rootPosition XZ (generated pelvis)", "world_start_xz": args.world_start, "world_target_xz": args.world_target, "world_anchors_provenance": "Known actual-room fixture request start and target, supplied independently of generated frames", "source_first_target_xz": source_targets[0].tolist(), "source_last_target_xz": source_targets[-1].tolist(), "uniform_scale": abs(similarity), "rotation_degrees_in_xz_complex_plane": angle, "independently_recorded_initial_unity_yaw_degrees": play_report["initialYaw"], "formula": "worldComplex = (sourceComplex - sourceFirstTargetComplex) * ((worldTargetComplex-worldStartComplex)/(sourceLastTargetComplex-sourceFirstTargetComplex)) + worldStartComplex", "limitations": "Uses target endpoints, not fitted generated endpoints. Omits millimetre-scale pelvis/root offset and is not an observed per-frame root track.", "world_targets_xz": world_targets.tolist(), "world_generated_pelvis_xz": world_generated.tolist()}}
    (args.output / "living-room-navigation-review.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
