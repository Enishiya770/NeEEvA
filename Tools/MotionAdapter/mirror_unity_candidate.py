"""Export one explicitly derived right-wave candidate; never modify its source NPZ."""

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation

from .export_unity import (ARDY_TO_UNITY, build_clip, load_core27_reference,
                           quaternion_objects, validate_hierarchy)
from .review_unity_right_candidates import swap_side


ROOT = Path(__file__).resolve().parents[2]


def matrices(objects):
    return Rotation.from_quat([[q[key] for key in 'xyzw'] for q in objects]).as_matrix()


def mirror(values, indices):
    return ARDY_TO_UNITY @ values[..., indices, :, :] @ ARDY_TO_UNITY


def export_candidate(input_path, output_path):
    lock = json.loads(Path(__file__).with_name('upstream-lock.json').read_text(encoding='utf-8-sig'))
    names, parents, neutral, neutral_sha = load_core27_reference(
        ROOT / f'Server/ARDY/vendor/ardy-{lock["ardy"]}')
    source_sha = hashlib.sha256(input_path.read_bytes()).hexdigest()
    with np.load(input_path, allow_pickle=False) as motion:
        clip = build_clip(motion, clip_id='right-wave-mirrored-left', expected_names=names,
                          expected_parents=parents, neutral_joints=neutral,
                          source={'file': input_path.name, 'sha256': source_sha,
                                  'ardyRevision': lock['ardy'], 'coreRepo': lock['ardy_core_repo'],
                                  'coreRevision': lock['ardy_core_revision'],
                                  'neutralJointsSha256': neutral_sha})
    raw_text = clip['text']
    indices = np.asarray([names.index(swap_side(name)) for name in names])
    if not np.array_equal(indices[indices], np.arange(len(names))):
        raise ValueError('Left/right permutation is not an involution')
    for joint, other in enumerate(indices):
        expected_parent = -1 if parents[joint] < 0 else indices[parents[joint]]
        if parents[other] != expected_parent:
            raise ValueError('Left/right permutation does not preserve the hierarchy')
    validate_hierarchy(clip['jointNames'], np.asarray(clip['jointParents']))
    original = np.stack([matrices(frame['globalRotations']) for frame in clip['frames']])
    rest = matrices(clip['restGlobalRotations'])
    reflected = mirror(original, indices)
    reflected_rest = mirror(rest, indices)
    double_frame_error = float(np.max(np.abs(mirror(reflected, indices) - original)))
    double_rest_error = float(np.max(np.abs(mirror(reflected_rest, indices) - rest)))
    if double_frame_error > 1e-12 or double_rest_error > 1e-12:
        raise ValueError('Double mirror did not restore rotations and rest')
    clip['frames'] = [{'globalRotations': value}
                      for value in quaternion_objects(reflected, continuous=True)]
    clip['restGlobalRotations'] = quaternion_objects(reflected_rest)
    clip['text'] = 'Derived right-hand wave: mirrored reviewed left-hand wave; not a native right-hand-condition sample.'
    clip['source'].update({
        'derived': True,
        'rawText': raw_text,
        'rawSourcePath': input_path.relative_to(ROOT).as_posix(),
        'rawSourceSha256': source_sha,
        'generationConditionText': raw_text,
        'derivation': {
            'type': 'left-right-mirror', 'reflectionAxisInUnity': 'x',
            'formula': 'R_new[j] = Cx * R_old[swapLeftRight(j)] * Cx; same transform for rest rotations.',
            'jointPermutation': indices.tolist(),
            'jointNamesAndParents': 'Kept in canonical Core27 order; permutation verified to preserve the hierarchy.',
            'scope': 'All 27 global rotations and rest rotations. No root translation is exported.',
            'selectionStatus': 'Candidate only; pending Unity visual comparison.',
        },
    })
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(clip, ensure_ascii=False, separators=(',', ':'), allow_nan=False)+'\n', encoding='utf-8')
    reloaded = json.loads(output_path.read_text(encoding='utf-8'))
    serialized = np.stack([matrices(frame['globalRotations']) for frame in reloaded['frames']])
    serialized_error = float(np.max(np.abs(serialized-reflected)))
    if serialized_error > 1e-7:
        raise ValueError('Serialized mirror rotation error exceeds tolerance')
    if hashlib.sha256(input_path.read_bytes()).hexdigest() != source_sha:
        raise ValueError('Source NPZ changed during export')
    verification = {
        'schema': 1, 'candidate': output_path.relative_to(ROOT).as_posix(),
        'candidateSha256': hashlib.sha256(output_path.read_bytes()).hexdigest(),
        'rawSourcePath': input_path.relative_to(ROOT).as_posix(), 'rawSourceSha256': source_sha,
        'analysisScript': Path(__file__).relative_to(ROOT).as_posix(),
        'analysisScriptSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'reproduce': 'python -m Tools.MotionAdapter.mirror_unity_candidate',
        'frameCount': len(clip['frames']), 'fps': clip['fps'], 'jointCount': len(names),
        'jointPermutationInvolution': True, 'hierarchyPreserved': True,
        'jointNamesAndParentsUnchanged': (reloaded['jointNames'] == names and reloaded['jointParents'] == parents.tolist()),
        'doubleMirrorFrameMaximumMatrixError': double_frame_error,
        'doubleMirrorRestMaximumMatrixError': double_rest_error,
        'serializedFrameMaximumMatrixError': serialized_error,
        'sourceUnchanged': True, 'unityRun': False,
        'selectionStatus': 'Candidate only; pending Unity visual comparison.',
    }
    verification_path = output_path.with_suffix('.mirror-verification.json')
    verification_path.write_text(json.dumps(verification, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    return verification


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, default=ROOT/'Server/ARDY/runtime/unity-preview/left-wave.npz')
    parser.add_argument('--output', type=Path, default=ROOT/'Server/ARDY/runtime/unity-right-candidates/right-wave-mirrored-left.json')
    args = parser.parse_args()
    print(json.dumps(export_candidate(args.input.resolve(), args.output.resolve()), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
