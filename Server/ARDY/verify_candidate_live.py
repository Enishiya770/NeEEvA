"""Live HTTP verification of a locked candidate on preselected validation records."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time
import uuid

import httpx
import numpy as np

from Tools.MotionAdapter.data import read_records
from Server.ARDY.motion_service.protocol import FEATURE_CONTRACT, QWEN_MODEL_SHA256

ROOT = Path(__file__).resolve().parents[2]
IDS = ('expanded-v1-00040', 'expanded-v1-00832', 'expanded-v1-00904',
       'expanded-v1-00320', 'expanded-v1-01376', 'expanded-v1-01832')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:8095')
    parser.add_argument('--selection', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--conditioning-frames', type=int, choices=(4, 8, 16), default=16)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError('Use a new output directory; preserve previous live evidence')
    locked = json.loads(args.selection.read_text(encoding='utf-8'))
    if not locked['selected']:
        raise ValueError('No locked eligible candidate')
    dataset = ROOT/'Tools/MotionAdapter/runtime/generalization-v1/extension-v1.jsonl'
    rows = {row['id']: row for row in read_records(dataset)}
    selected = [rows[identity] for identity in IDS]
    if any(row['split'] != 'val' or row['template_id'] != 0 for row in selected):
        raise ValueError('Live verification must use the preregistered validation descriptions')
    initial_path = ROOT/'Server/ARDY/runtime/generate-diagnostics/animator-idle-history.json'
    history = json.loads(initial_path.read_text(encoding='utf-8-sig'))
    history = history.get('initialHistory', history)
    if args.conditioning_frames != 16:
        history = {**history, 'conditioningFrames': args.conditioning_frames}
    args.output.mkdir(parents=True)
    report = {'schema': 1, 'passed': False, 'mode': 'locked-candidate-live-http',
        'url': args.url, 'selectionSha256': hashlib.sha256(args.selection.read_bytes()).hexdigest(),
        'checkpointSha256': locked['checkpoint_sha256'],
        'historySha256': hashlib.sha256(initial_path.read_bytes()).hexdigest(),
        'datasetSha256': hashlib.sha256(dataset.read_bytes()).hexdigest(),
        'recordIds': list(IDS), 'assertions': [], 'cases': [], 'languageModelsLoadedByClient': False,
        'scope': 'Real Qwen feature requests and candidate ARDY service, without concurrent voice/chat generation. HTTP timing is not Unity playback or semantic action-onset latency.',
        'naturalnessAccepted': False, 'semanticSuccessRate': None,
        'initialConditioningFrames': args.conditioning_frames}
    active_identity = None

    def check(condition, message):
        if not condition:
            raise AssertionError(message)
        report['assertions'].append(message)

    try:
        with httpx.Client(base_url=args.url, timeout=25) as client:
            response = client.get('/health')
            response.raise_for_status()
            health = response.json()
            report['initialHealth'] = health
            check(health['ready'] and health['backend']['languageModelsLoaded'] is False, 'ready; ARDY has no language model')
            check(health['backend'].get('candidate') is True and health['backend'].get('approvedForRuntime') is False,
                  'candidate provenance is explicit')
            check(health['backend']['adapterSha256'] == locked['checkpoint_sha256'], 'the locked candidate weights are running')
            check(health['backend']['adapterSelectionSha256'] == report['selectionSha256'], 'the exact selection lock is running')
            for row in selected:
                identity = {'characterId': 'candidate-live-'+uuid.uuid4().hex,
                            'turnId': row['id'], 'revision': 1}
                active_identity = identity
                responses, elapsed, first_request = [], [], None
                case = {'recordId': row['id'], 'text': row['text'], 'responses': [], 'seed': 0}
                for index in range(3):
                    request = {**identity, 'requestId': uuid.uuid4().hex, 'description': row['text'],
                               'mask': 'UpperBody', 'seed': 0, 'chunkIndex': index, 'timeoutMs': 20000, 'maxChunks': 3}
                    if index == 0:
                        request['initialHistory'] = history
                        first_request = request['requestId']
                    started = time.perf_counter()
                    response = client.post('/v1/motion/generate', json=request)
                    elapsed.append((time.perf_counter()-started)*1000)
                    response.raise_for_status()
                    result = response.json()
                    check(all(result[key] == value for key, value in identity.items()) and result['requestId'] == request['requestId'],
                          f'{row["id"]}/{index}: identity retained')
                    check(result['newFrames'] == 40 and result['startFrame'] == 40*index and result['fps'] == 20,
                          f'{row["id"]}/{index}: contiguous 40-frame window')
                    check(result['chunkIndex'] == index and result['final'] == (index == 2), f'{row["id"]}/{index}: chunk lifecycle matches')
                    check(result['historyFrames'] == (16 if index else args.conditioning_frames),
                          f'{row["id"]}/{index}: actual conditioning length matches')
                    provenance = result['provenance']
                    check(provenance['featureSource'] == ('live-qwen' if index == 0 else 'live-condition-reused')
                          and provenance['featureRequestId'] == first_request,
                          f'{row["id"]}/{index}: actual live feature then same-action reuse')
                    check(result['historySource'] == ('unity-upper-body-projection-v1' if index == 0 else 'ardy-self-history-last-16')
                          and provenance['conditionReusedWithinRevision'] == (index > 0),
                          f'{row["id"]}/{index}: correct live-pose or self-history source')
                    check(provenance['featureContract'] == FEATURE_CONTRACT and provenance['modelSha256'] == QWEN_MODEL_SHA256,
                          f'{row["id"]}/{index}: deployment feature/model binding')
                    check(result['clip']['source']['adapterSha256'] == locked['checkpoint_sha256']
                          and provenance['derivedSeed'] == 0,
                          f'{row["id"]}/{index}: actual clip binds the locked adapter and explicit seed')
                    quaternions = np.array([[[q[key] for key in 'xyzw'] for q in frame['globalRotations']]
                                            for frame in result['clip']['frames']])
                    check(quaternions.shape == (40, 27, 4) and np.isfinite(quaternions).all()
                          and np.max(np.abs(np.linalg.norm(quaternions, axis=-1)-1)) < 1e-6,
                          f'{row["id"]}/{index}: finite normalized Core27 output')
                    artifact = args.output/f'{row["id"]}-chunk{index}.json'
                    artifact.write_text(json.dumps({'request': request, 'response': result}, separators=(',', ':')), encoding='utf-8')
                    responses.append(result)
                    case['responses'].append({'path': str(artifact.resolve()), 'httpMilliseconds': elapsed[-1],
                                              'serverTimings': result['timings']})
                case.update(firstWindowHttpMilliseconds=elapsed[0], allWindowsHttpMilliseconds=sum(elapsed),
                            generatedFrames=sum(result['newFrames'] for result in responses))
                cancel = client.post('/v1/motion/cancel', json={**identity, 'requestId': uuid.uuid4().hex, 'reason': 'candidate-live-verification-complete'})
                check(cancel.status_code == 200 and cancel.json()['cancelled'], f'{row["id"]}: action state cancellation succeeds')
                active_identity = None
                report['cases'].append(case)
            final_response = client.get('/health')
            final_response.raise_for_status()
            report['finalHealth'] = final_response.json()
            check(report['finalHealth']['queue']['waiting'] == 0 and report['finalHealth']['queue']['running'] == 0, 'queue drained')
        first = [case['firstWindowHttpMilliseconds'] for case in report['cases']]
        report['timings'] = {'firstWindowMedianMilliseconds': float(np.median(first)),
                             'firstWindowP95Milliseconds': float(np.percentile(first, 95)),
                             'allWindowsMedianMilliseconds': float(np.median([case['allWindowsHttpMilliseconds'] for case in report['cases']]))}
        report['passed'] = True
    except Exception as error:
        report['error'] = repr(error)
        raise
    finally:
        if active_identity is not None:
            try:
                cleanup = httpx.post(args.url+'/v1/motion/cancel', json={**active_identity,
                    'requestId': uuid.uuid4().hex, 'reason': 'candidate-verification-failed-cleanup'}, timeout=5)
                report['failureCleanupStatus'] = cleanup.status_code
            except Exception as cleanup_error:
                report['failureCleanupError'] = repr(cleanup_error)
        report['assertionCount'] = len(report['assertions'])
        (args.output/'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps({key: report.get(key) for key in ('passed', 'assertionCount', 'timings')}), flush=True)


if __name__ == '__main__':
    main()
