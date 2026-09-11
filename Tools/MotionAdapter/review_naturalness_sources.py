"""Reproduce source-only diagnostics for the three rejected/adjusted Unity previews.

Reads original NPZ files and pinned Core27 assets; writes only the requested JSON
report. No language model, ARDY network, Unity session or GPU is loaded.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

import numpy as np
import scipy
from scipy.spatial.transform import Rotation

from .export_unity import ARDY_TO_UNITY, fk_positions, load_core27_reference, unity_rotations


ROOT = Path(__file__).resolve().parents[2]
SOURCES = {
    'right-wave': 'Server/ARDY/runtime/unity-preview/right-wave.npz',
    'nod': 'Server/ARDY/runtime/unity-head-candidates/nod-seed1.npz',
    'shake-head': 'Server/ARDY/runtime/unity-head-candidates/shake-head-seed2.npz',
}
AXES = ('pitchX', 'yawY', 'rollZ')


def rounded(value):
    value = np.asarray(value)
    return np.round(value, 6).tolist()


def bounds(value):
    return {'minimum': rounded(value.min(axis=0)), 'maximum': rounded(value.max(axis=0)),
            'peakToPeak': rounded(np.ptp(value, axis=0))}


def side_summary(vector):
    wrong = vector[:, 0] < 0
    return {**bounds(vector), 'crossesToLeftFrameCount': int(wrong.sum()),
            'crossesToLeftFrameFraction': float(wrong.mean())}


def angular_speed(rotations, fps):
    steps = rotations[1:] @ np.swapaxes(rotations[:-1], -1, -2)
    degrees = np.rad2deg(Rotation.from_matrix(steps).magnitude())
    speed = degrees * fps
    index = int(speed.argmax())
    return {'meanDegreesPerSecond': float(speed.mean()), 'p95DegreesPerSecond': float(np.percentile(speed, 95)),
            'maximumDegreesPerSecond': float(speed.max()), 'maximumStepDegrees': float(degrees.max()),
            'maximumIntervalSeconds': [index / fps, (index + 1) / fps]}


def head_diagnostics(rotations, names, fps):
    head = rotations[:, names.index('Head')]
    chest = rotations[:, names.index('Spine3')]
    relative = np.swapaxes(chest, -1, -2) @ head
    euler = Rotation.from_matrix(relative).as_euler('xyz', degrees=True)
    times = np.arange(len(head)) / fps
    components = {}
    for index, axis in enumerate(AXES):
        values = euler[:, index]
        components[axis] = {**bounds(values), 'startDegrees': float(values[0]), 'endDegrees': float(values[-1]),
                            'minimumTimeSeconds': float(times[values.argmin()]),
                            'maximumTimeSeconds': float(times[values.argmax()]),
                            'maximumSampledComponentRateDegreesPerSecond': float(np.max(np.abs(np.diff(values))) * fps)}
    sample_indices = sorted(set(range(0, len(head), round(fps / 2))) | {len(head) - 1})
    windows = []
    for start, end in ((0, 1), (1, 2), (2, 6)):
        selected = (times >= start) & (times < end)
        if selected.any():
            windows.append({'startInclusiveSeconds': start, 'endExclusiveSeconds': end,
                            'eulerDegrees': bounds(euler[selected])})
    return {
        'chestRelativeEulerDegrees': components,
        'largestPeakToPeakAxis': AXES[int(np.ptp(euler, axis=0).argmax())],
        'chestGlobalEulerDegrees': bounds(Rotation.from_matrix(chest).as_euler('xyz', degrees=True)),
        'headGlobalAngularSpeed': angular_speed(head, fps),
        'headChestRelativeAngularSpeed': angular_speed(relative, fps),
        'headChestRelativeEulerSamples': [{'timeSeconds': float(times[i]), 'pitchYawRollDegrees': rounded(euler[i])}
                                         for i in sample_indices],
        'timeWindows': windows,
    }


def right_wave_diagnostics(positions, rotations, names, parents, neutral, fps):
    hand, hips, chest, shoulder = [names.index(name) for name in ('RightHand', 'Hips', 'Spine3', 'RightShoulder')]
    root_relative = positions[:, hand] - positions[:, hips]
    chest_origin = positions[:, hand] - positions[:, chest]
    inverse_chest = np.swapaxes(rotations[:, chest], -1, -2)
    chest_relative = (inverse_chest @ chest_origin[..., None])[..., 0]
    shoulder_relative = (inverse_chest @ (positions[:, hand] - positions[:, shoulder])[..., None])[..., 0]
    chest_euler = Rotation.from_matrix(rotations[:, chest]).as_euler('xyz', degrees=True)
    before, after = [np.broadcast_to(np.eye(3), rotations.shape).copy() for _ in range(2)]
    for name in ('RightShoulder', 'RightArm', 'RightForeArm', 'RightHand'):
        joint = names.index(name)
        before[:, joint] = rotations[:, joint]
        after[:, joint] = inverse_chest @ rotations[:, joint]
    before_positions, after_positions = [fk_positions(value, neutral, parents) for value in (before, after)]
    before_vector = before_positions[:, hand] - before_positions[:, chest]
    after_vector = after_positions[:, hand] - after_positions[:, chest]
    sample_indices = [round(t * fps) for t in (0, 0.5, 1, 1.5, 2, 3, 4, 5, (len(positions)-1)/fps)]
    return {
        'rootOriginCharacterAxesMetres': side_summary(root_relative),
        'chestOriginCharacterAxesMetres': side_summary(chest_origin),
        'chestOriginChestAxesMetres': side_summary(chest_relative),
        'shoulderOriginChestAxesMetres': side_summary(shoulder_relative),
        'sourceChestGlobalEulerDegrees': bounds(chest_euler),
        'samples': [{'timeSeconds': i/fps, 'chestOriginCharacterAxesMetres': rounded(chest_origin[i]),
                     'chestOriginChestAxesMetres': rounded(chest_relative[i])} for i in sample_indices],
        'neutralTorsoCore27FkPrediction': {
            'absoluteArmGlobalRotations': side_summary(before_vector),
            'chestAnchoredArmRotations': side_summary(after_vector),
            'anchoredVsSourceChestRelativeMaximumPositionDifferenceMetres': float(np.max(np.abs(after_vector-chest_relative))),
            'assumptions': 'Core27 neutral torso and original segment lengths; replace only the four right-arm global rotations; no Unity target geometry, Animator or blending.',
        },
    }


def build_report():
    lock = json.loads(Path(__file__).with_name('upstream-lock.json').read_text(encoding='utf-8-sig'))
    vendor = ROOT/f'Server/ARDY/vendor/ardy-{lock["ardy"]}'
    names, parents, neutral, neutral_sha = load_core27_reference(vendor)
    neutral = neutral @ ARDY_TO_UNITY
    report = {
        'schema': 1, 'generatedAtUtc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Original source clips and mathematical expectations only; not a Unity or subjective naturalness acceptance report.',
        'reproduce': 'python -m Tools.MotionAdapter.review_naturalness_sources',
        'analysisScript': 'Tools/MotionAdapter/review_naturalness_sources.py',
        'analysisScriptSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'versions': {'numpy': np.__version__, 'scipy': scipy.__version__, 'ardyRevision': lock['ardy']},
        'neutralJointsSha256': neutral_sha,
        'methods': {
            'coordinates': 'ARDY RH +Y up/+Z forward -> Unity-style coordinates with C=diag(-1,1,1); p_u=C*p_a and R_u=C*R_a*C. Positive X is the character right side.',
            'rootOriginCharacterAxes': 'RightHand minus Hips positions, still in character/world axes. This subtracts translation only, not the generated pelvis rotation.',
            'chestOriginCharacterAxes': 'RightHand minus Spine3 positions, still in character/world axes. This is the basis of the earlier 52.5% cross-centre observation.',
            'chestOriginChestAxes': 'inverse(R_Spine3)*(p_RightHand-p_Spine3); negative X marks the left half-space of the source chest.',
            'euler': 'scipy Rotation.as_euler("xyz", degrees=True), extrinsic axes: X pitch, Y yaw, Z roll; head measured as inverse(R_Spine3)*R_Head. Bounds are sampled extrema, not anatomical joint limits.',
            'angularSpeed': 'Geodesic angle of R[t+1]*inverse(R[t]) multiplied by 20 FPS. Finite inter-frame mean rates, not measured continuous peak velocities. Euler component rates are reported separately and are not vector angular velocity.',
            'anchorCorrection': 'For a partial mask: targetChestCurrent * inverse(targetChestRest) * sourceChestRest * inverse(sourceChestCurrent) * sourceBoneCurrent * inverse(sourceBoneRest) * targetBoneRest. Source rest rotations are identity here.',
            'headSelectionIssue': 'The former visibility screen compared yaw range with pitch range, without a roll exclusion or a temporal-shape/repetition check.',
        },
        'clips': {},
        'limitations': [
            'The nod and shake-head sources are the rejected raw ARDY candidates, not the newer explicitly controlled conversational envelopes.',
            'Cross-centre counts and angular ranges do not establish naturalness, semantic success, face clearance or comfort.',
            'The FK prediction uses Core27 geometry only. Unity retargeting, target proportions, Animator timing and fades require separate validation.',
            'No original NPZ or generation report is modified; no model generation or GPU work is performed.',
        ],
    }
    for label, relative_path in SOURCES.items():
        path = ROOT/relative_path
        with np.load(path, allow_pickle=False) as data:
            if list(data['joint_names']) != names or not np.array_equal(data['joint_parents'], parents):
                raise ValueError(f'Unexpected skeleton: {path}')
            fps = float(data['fps'])
            positions = np.asarray(data['posed_joints'], dtype=np.float64) @ ARDY_TO_UNITY
            rotations = unity_rotations(data['global_rot_mats'])
            clip = {'sourcePath': relative_path, 'sourceAbsolutePath': path.as_posix(),
                    'sourceSha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'sourceText': str(data['text']),
                    'frameCount': len(positions), 'fps': fps, 'lastSampleSeconds': (len(positions)-1)/fps,
                    'clipDurationSeconds': len(positions)/fps}
            clip['diagnostics'] = right_wave_diagnostics(positions, rotations, names, parents, neutral, fps) if label == 'right-wave' else head_diagnostics(rotations, names, fps)
            report['clips'][label] = clip
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path(__file__).parent/'reports/motion-naturalness-source-review.json')
    args = parser.parse_args()
    report = build_report()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)+'\n', encoding='utf-8')
    print(args.output.resolve())


if __name__ == '__main__':
    main()
