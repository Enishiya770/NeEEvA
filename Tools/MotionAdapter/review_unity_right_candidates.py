"""Screen right-wave candidates on the complete rest hierarchies of both VRM assets.

This reproduces the player's fully blended chest-anchored rotation targets with
all target nodes, translations, rotations and scales. It does not run Unity,
change any clip, or select a final candidate. No model or GPU is loaded.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

import numpy as np
import scipy
from scipy.spatial.transform import Rotation, Slerp

from .export_unity import ARDY_TO_UNITY, unity_rotations


ROOT = Path(__file__).resolve().parents[2]
TARGETS = ('Assets/Model/NEVA.vrm', 'Assets/Model/NeEEvA.vrm')
CANDIDATES = (
    ('native-seed0', 'Server/ARDY/runtime/unity-preview/right-wave.npz', False),
    ('mirrored-reviewed-left', 'Server/ARDY/runtime/unity-preview/left-wave.npz', True),
    *((f'native-seed{seed}', f'Server/ARDY/runtime/unity-right-candidates/right-wave-seed{seed}.npz', False)
      for seed in (1, 2, 3)),
)
ARM_MAP = {'rightShoulder': 'RightShoulder', 'rightUpperArm': 'RightArm',
           'rightLowerArm': 'RightForeArm', 'rightHand': 'RightHand'}
PREFERRED_SIDE_MARGIN = 0.12


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class VrmRestHierarchy:
    def __init__(self, relative_path):
        path = ROOT/relative_path
        raw = path.read_bytes()
        magic, version, declared_size = struct.unpack_from('<III', raw)
        size, kind = struct.unpack_from('<II', raw, 12)
        if magic != 0x46546c67 or version != 2 or declared_size != len(raw) or kind != 0x4e4f534a:
            raise ValueError('Expected a GLB 2.0 VRM: '+relative_path)
        document = json.loads(raw[20:20+size])
        if 'VRM' not in document.get('extensions', {}):
            raise ValueError('This analysis explicitly implements the project VRM0 -> Unity import convention')
        self.nodes = document['nodes']
        self.human = {item['bone']: item['node'] for item in document['extensions']['VRM']['humanoid']['humanBones']}
        count = len(self.nodes)
        self.parents = np.full(count, -1, dtype=int)
        for index, node in enumerate(self.nodes):
            for child in node.get('children', []):
                if self.parents[child] != -1:
                    raise ValueError('Node has multiple parents')
                self.parents[child] = index
        self.order = []
        def visit(index):
            if index in self.order:
                raise ValueError('Repeated/cyclic hierarchy node')
            self.order.append(index)
            for child in self.nodes[index].get('children', []):
                visit(child)
        for index in np.flatnonzero(self.parents < 0):
            visit(index)
        if len(self.order) != count:
            raise ValueError('Incomplete hierarchy traversal')
        # VrmLib.Model.ConvertCoordinate(Vrm0 -> Unity) uses ReverseZ.
        # The subsequent Unity -> VRM1 -> Unity steps cancel their X reflections.
        conversion = np.diag([1.0, 1.0, -1.0])
        self.local = np.broadcast_to(np.eye(4), (count, 4, 4)).copy()
        self.rest_local_rotations = np.empty((count, 3, 3))
        self.scales = np.empty((count, 3))
        for index, node in enumerate(self.nodes):
            if 'matrix' in node:
                raise ValueError('Matrix-node decomposition is outside this project-asset analysis')
            rotation = conversion @ Rotation.from_quat(node.get('rotation', [0, 0, 0, 1])).as_matrix() @ conversion
            self.rest_local_rotations[index] = rotation
            self.scales[index] = node.get('scale', [1, 1, 1])
            self.local[index, :3, :3] = rotation @ np.diag(self.scales[index])
            self.local[index, :3, 3] = conversion @ node.get('translation', [0, 0, 0])
        self.rest_positions, self.rest_rotations = self.fk({})
        self.chest = self.human['upperChest']
        self.metadata = {'sourcePath': relative_path, 'sourceSha256': sha(path), 'nodeCount': count,
                         'coordinateConversion': 'VRM0 -> Unity: reflect Z; confirmed in VrmLib.Model.ConvertCoordinate',
                         'restPositionsMetres': {bone: self.rest_positions[self.human[bone]].tolist()
                                                 for bone in ('upperChest', 'head', *ARM_MAP)},
                         'restRotationsXyzw': {bone: Rotation.from_matrix(self.rest_rotations[self.human[bone]]).as_quat().tolist()
                                              for bone in ('upperChest', 'head', *ARM_MAP)}}

    def fk(self, desired_global):
        world = np.empty_like(self.local)
        rotations = np.empty_like(self.rest_local_rotations)
        for index in self.order:
            parent = self.parents[index]
            parent_rotation = np.eye(3) if parent < 0 else rotations[parent]
            local_rotation = self.rest_local_rotations[index]
            matrix = self.local[index].copy()
            if index in desired_global:
                local_rotation = parent_rotation.T @ desired_global[index]
                matrix[:3, :3] = local_rotation @ np.diag(self.scales[index])
            world[index] = matrix if parent < 0 else world[parent] @ matrix
            rotations[index] = parent_rotation @ local_rotation
        return world[:, :3, 3], rotations

    def sample(self, source_rotations, names):
        source_chest = source_rotations[names.index('Spine3')]
        target_chest = self.rest_rotations[self.chest]
        action_frame = target_chest @ self.rest_rotations[self.chest].T @ source_chest.T
        desired = {self.human[human]: action_frame @ source_rotations[names.index(source)] @ self.rest_rotations[self.human[human]]
                   for human, source in ARM_MAP.items()}
        return self.fk(desired)


def swap_side(name):
    if name.startswith('Left'):
        return 'Right'+name[4:]
    if name.startswith('Right'):
        return 'Left'+name[5:]
    return name


def prepare_source(path, mirror):
    with np.load(path, allow_pickle=False) as data:
        names = [str(name) for name in data['joint_names']]
        rotations = unity_rotations(data['global_rot_mats'])
        fps = float(data['fps'])
        text = str(data['text'])
    if mirror:
        indices = [names.index(swap_side(name)) for name in names]
        rotations = ARDY_TO_UNITY @ rotations[:, indices] @ ARDY_TO_UNITY
    sample_times = np.arange(int(round(len(rotations)/fps*60)))/60
    source_times = np.arange(len(rotations))/fps
    interpolated = np.stack([Slerp(source_times, Rotation.from_matrix(rotations[:, joint]))(
        np.minimum(sample_times, source_times[-1])).as_matrix() for joint in range(len(names))], axis=1)
    return names, sample_times, interpolated, {'sourceSha256': sha(path), 'rawSourceText': text,
                                            'sourceFrameCount': len(rotations), 'sourceFps': fps,
                                            'mirrored': mirror,
                                            'transform': 'Reflect global rotations about X and exchange every Left/Right joint index; names/hierarchy stay fixed.' if mirror else 'Native generated rotations; no mirror or postprocessing.'}


def vector_summary(values, times):
    index = int(np.argmin(values[:, 0]))
    return {'minimumXyzMetres': values.min(axis=0).tolist(), 'maximumXyzMetres': values.max(axis=0).tolist(),
            'minimumSideTimeSeconds': float(times[index]), 'crossCentreCount': int(np.sum(values[:, 0] < 0)),
            'crossCentreFraction': float(np.mean(values[:, 0] < 0)),
            'belowPreferredSideMarginCount': int(np.sum(values[:, 0] < PREFERRED_SIDE_MARGIN)),
            'belowPreferredSideMarginFraction': float(np.mean(values[:, 0] < PREFERRED_SIDE_MARGIN))}


def screen(target, names, times, source_rotations):
    wrists, shoulders, head_distances, hand_rotations = [], [], [], []
    for source_frame in source_rotations:
        positions, rotations = target.sample(source_frame, names)
        inverse_chest = rotations[target.chest].T
        wrist = positions[target.human['rightHand']]
        chest = positions[target.chest]
        wrists.append(inverse_chest @ (wrist-chest))
        shoulders.append(inverse_chest @ (positions[target.human['rightShoulder']]-chest))
        head_distances.append(float(np.linalg.norm(wrist-positions[target.human['head']])))
        hand_rotations.append(inverse_chest @ rotations[target.human['rightHand']])
    wrists, shoulders, hand_rotations = map(np.asarray, (wrists, shoulders, hand_rotations))
    after_blend = times >= 0.3
    raised = after_blend & (wrists[:, 1] >= shoulders[:, 1])
    raised_adjacent = raised[:-1] & raised[1:]
    steps = hand_rotations[1:] @ np.swapaxes(hand_rotations[:-1], -1, -2)
    angle_steps = np.rad2deg(Rotation.from_matrix(steps).magnitude())
    report = {'sampleCountAfterBlendIn': int(after_blend.sum()),
              'allAfterBlendIn': vector_summary(wrists[after_blend], times[after_blend]),
              'raisedFrameCount': int(raised.sum()), 'raisedFractionAfterBlendIn': float(raised.sum()/after_blend.sum()),
              'maximumWristHeightRiseFromSourceStartMetres': float(wrists[after_blend, 1].max()-wrists[0, 1]),
              'minimumWristToHeadBoneDistanceMetres': float(np.asarray(head_distances)[after_blend].min()),
              'raisedAdjacentWristTravelMetres': float(np.linalg.norm(np.diff(wrists, axis=0), axis=1)[raised_adjacent].sum()),
              'raisedAdjacentHandRotationTravelDegrees': float(angle_steps[raised_adjacent].sum()),
              'maximumInterFrameHandRotationDegrees': float(angle_steps.max()),
              'raised': vector_summary(wrists[raised], times[raised]) if raised.any() else None,
              'samplesEveryHalfSecond': [{'timeSeconds': float(times[i]), 'wristInChestMetres': wrists[i].tolist()}
                                         for i in range(0, len(times), 30)]}
    return report


def build_report(require_all):
    targets = [VrmRestHierarchy(path) for path in TARGETS]
    report = {
        'schema': 1, 'generatedAtUtc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Complete target-rest-hierarchy FK screening only; final candidate selection remains pending Unity visual review.',
        'analysisScript': 'Tools/MotionAdapter/review_unity_right_candidates.py',
        'analysisScriptSha256': sha(Path(__file__)),
        'numerics': {'numpy': np.__version__, 'scipy': scipy.__version__, 'dtype': 'float64'},
        'reproduce': 'python -m Tools.MotionAdapter.review_unity_right_candidates --require-all',
        'method': {
            'targetGeometry': 'Parse every .vrm GLB node, exact rest translations/rotations/scales and intermediate ancestors, convert VRM0 coordinates as UniVRM, and evaluate the complete hierarchy.',
            'targetCoordinateEvidence': ['Assets/VRM10/vrmlib/Runtime/Model.cs:369-377: VRM0/Unity use ZReverser',
                                         'Assets/VRM10/Runtime/IO/Vrm10Importer.cs:40: VRM1 import uses Axes.X'],
            'playerTransfer': 'Keep target rest torso; desiredGlobal = targetChestCurrent * inverse(targetChestRest) * inverse(sourceSpine3) * sourceBoneGlobal * targetBoneRest. Apply four right-arm global rotations in hierarchy order, preserving all local translations/scales.',
            'sampling': '60 Hz; independent quaternion SLERP per source joint, same as ArdyMotionClip; clamp to last frame. Metrics begin at 0.3 seconds when the default fade-in has finished.',
            'raisedPhase': 'Wrist chest-local Y >= right-shoulder chest-local Y. This marks a geometric raised phase, not verified waving.',
            'headDistance': 'Euclidean wrist-to-head-bone distance, not face mesh clearance or screen-space occlusion.',
            'activity': 'Raised-phase wrist path length and hand-rotation path length include all adjacent raised samples; they do not by themselves prove a wave.',
        },
        'demonstrationPreference': {
            'preferredRaisedWristSideMarginMetres': PREFERRED_SIDE_MARGIN,
            'rationale': 'For these two avatars, favour raised-hand lateral room near 12 cm or more and less forward reach to leave visible room beside the face; avoid stationary candidates.',
            'notUniversalThreshold': True, 'finalSelection': None, 'finalSelectionAuthority': 'User-facing Unity visual inspection; no automatic acceptance by this script.',
        },
        'priorApproximationCorrection': 'An earlier approximation changed only upper/forearm lengths while retaining the wider ARDY shoulder layout. It predicted positive side clearance and was insufficient. This full-target analysis includes the actual narrower shoulders and reproduces the observed small cross-centre excursion.',
        'unitySeed0Reference': {'target': 'NEVA', 'reportedMinimumSideMetres': -0.01084,
                               'reportedSideAt07833SecondsMetres': -0.005,
                               'source': 'Parent task reported an independent Unity render/regression; this Python run does not perform that Unity check.'},
        'targets': [target.metadata for target in targets], 'candidates': [],
        'limitations': ['No Animator, blend transient, ControlRig, constraints, spring bones, sleeves, fingers, palm mesh, camera projection or render is simulated.',
                        'These specific assets contain the rest geometry used here; scene overrides or non-rest torso deformation can change placement.',
                        'Seed screening and mirroring are explicit demonstration preparation, not adapter generalization evaluation.',
                        'The mirrored candidate is a derived reviewed-left motion, not native right-prompt output. No source NPZ, production JSON, builder or player is changed.'],
    }
    for name, relative, mirror in CANDIDATES:
        path = ROOT/relative
        if not path.is_file():
            if require_all:
                raise FileNotFoundError(path)
            report['candidates'].append({'name': name, 'sourcePath': relative, 'status': 'not-generated-yet'})
            continue
        names, times, source, metadata = prepare_source(path, mirror)
        result = {'name': name, 'sourcePath': relative, 'status': 'screened', **metadata,
                  'targets': {Path(target.metadata['sourcePath']).stem: screen(target, names, times, source) for target in targets}}
        if name == 'native-seed0':
            calculated = result['targets']['NEVA']['allAfterBlendIn']['minimumXyzMetres'][0]
            report['unitySeed0Reference']['pythonMinimumSideMetres'] = calculated
            report['unitySeed0Reference']['absoluteDifferenceFromRoundedUnityMinimumMetres'] = abs(calculated + 0.01084)
        if mirror:
            exported = ROOT/'Server/ARDY/runtime/unity-right-candidates/right-wave-mirrored-left.json'
            if exported.is_file():
                document = json.loads(exported.read_text(encoding='utf-8'))
                if document['source'].get('rawSourceSha256') != metadata['sourceSha256'] or not document['source'].get('derived'):
                    raise ValueError('Mirrored exported candidate provenance differs from this source')
                result['unityCandidateExport'] = {'path': exported.relative_to(ROOT).as_posix(), 'sha256': sha(exported),
                                                'id': document['id'], 'text': document['text'],
                                                'verificationPath': exported.with_suffix('.mirror-verification.json').relative_to(ROOT).as_posix()}
        report['candidates'].append(result)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--require-all', action='store_true')
    parser.add_argument('--output', type=Path, default=Path(__file__).parent/'reports/unity-right-candidate-screening.json')
    args = parser.parse_args()
    report = build_report(args.require_all)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)+'\n', encoding='utf-8')
    for candidate in report['candidates']:
        if candidate['status'] != 'screened':
            print(candidate['name'], candidate['status'])
            continue
        values = candidate['targets']['NEVA']
        print(candidate['name'], 'min side', round(values['allAfterBlendIn']['minimumXyzMetres'][0], 5),
              'max forward', round(values['allAfterBlendIn']['maximumXyzMetres'][2], 5),
              'raised frames', values['raisedFrameCount'], 'raised min side',
              None if values['raised'] is None else round(values['raised']['minimumXyzMetres'][0], 5))
    print(args.output.resolve())


if __name__ == '__main__':
    main()
