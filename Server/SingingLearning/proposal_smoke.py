"""Opt-in real CPU HTTP smoke on isolated data; never touches user profiles.

Run python -m Server.SingingLearning.proposal_smoke. It uses the existing c9
fixture and its explicitly confirmed song/3.00-3.41s environment intervals.
"""
import hashlib
import json
from pathlib import Path
import socket
import threading
import time
from urllib.request import Request, build_opener, ProxyHandler
import uuid
import uvicorn
from .app import create_app
from .config import Config
from .pipeline import prediction_report
import numpy as np


def main():
    root = Path(__file__).resolve().parents[2]
    assets = root / 'work/singing-subsystem-20260929/segmentation'
    run = root / 'work/personal-singing-learning-20260929' / ('teaching-http-' + time.strftime('%Y%m%d-%H%M%S'))
    run.mkdir(parents=True)
    config = Config(run / 'data', str(assets / 'models/Cnn14_DecisionLevelMax_mAP=0.385.pth'),
                    str(assets / 'models/class_labels_indices.csv'), str(assets / 'deps'), 'cpu', 2,
                    auto_train_debounce_seconds=.05)
    app = create_app(config)
    sock = socket.socket()
    sock.bind(('127.0.0.1', 0))
    base = 'http://127.0.0.1:' + str(sock.getsockname()[1])
    server = uvicorn.Server(uvicorn.Config(app, log_level='error'))
    worker = threading.Thread(target=lambda: server.run(sockets=[sock]), daemon=True)
    worker.start()
    opener = build_opener(ProxyHandler({}))
    def request(path, method='GET', body=None, raw=False, content_type='application/json'):
        data = body if isinstance(body, bytes) else json.dumps(body).encode() if body is not None else None
        with opener.open(Request(base + path, data=data, method=method, headers={'Content-Type': content_type}), timeout=15) as response:
            value = response.read()
            return value if raw else json.loads(value)
    def wait_job(job):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            value = request('/learning/jobs/' + job)
            if value['status'] not in ('queued', 'running'):
                assert value['status'] == 'completed', value
                return value
            time.sleep(.15)
        raise AssertionError('job_timeout')
    report = dict(passed=False, scope='Real CPU acoustic teaching hypotheses and existing human labels in isolated data',
                  output_directory=str(run), original_proposal_file=str(run / 'c9-proposal.json'))
    try:
        deadline = time.monotonic() + 10
        while not server.started and time.monotonic() < deadline:
            time.sleep(.05)
        assert server.started
        request('/learning/profiles', 'POST', dict(profile_id='smoke-c9'))
        prefix = '/learning/profiles/smoke-c9'
        request(prefix + '/sessions', 'POST', dict(session_id='one-lesson', content_group='known-c9'))
        fixture = next(e for e in json.loads((assets / 'evaluation-manifest.json').read_text(encoding='utf-8-sig')) if e['id'] == 'c9')
        audio = Path(fixture['wav']).read_bytes()
        assert hashlib.sha256(audio).hexdigest() == fixture['sha256']
        manifest = dict(recording_id='c9', session_id='one-lesson', content_group='known-c9', capture_serial=9,
                        source_kind='unknown', audio_sha256=fixture['sha256'])
        boundary = 'neeeva-' + uuid.uuid4().hex
        raw = (f'--{boundary}\r\nContent-Disposition: form-data; name="manifest"\r\n\r\n'.encode() + json.dumps(manifest).encode() +
               f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="audio_file"; filename="original.wav"\r\nContent-Type: audio/wav\r\n\r\n'.encode() +
               audio + f'\r\n--{boundary}--\r\n'.encode())
        started = time.perf_counter()
        rec = request(prefix + '/recordings', 'POST', raw, content_type='multipart/form-data; boundary=' + boundary)
        report['upload_response_seconds'] = time.perf_counter() - started
        print('Real CPU service healthy; uploaded c9, waiting for automatic hypothesis', flush=True)
        rtts = []
        for _ in range(10):
            then = time.perf_counter()
            request('/health')
            rtts.append(time.perf_counter() - then)
            time.sleep(.1)
        report['health_rtt_while_acoustic_worker_seconds'] = rtts
        proposal_job = wait_job(rec['proposal_job_id'])
        report['cold_upload_to_hypothesis_seconds'] = time.perf_counter() - started
        proposal = proposal_job['result']
        (run / 'c9-proposal.json').write_text(json.dumps(proposal, ensure_ascii=False, indent=2), encoding='utf-8')
        regions = proposal['regions']
        assert regions[0]['start_sample'] == 0 and regions[-1]['end_sample'] == 305120
        assert all(a['end_sample'] == b['start_sample'] for a, b in zip(regions, regions[1:]))
        assert all(r['end_sample'] > r['start_sample'] for r in regions)
        assert proposal['model_source'] == 'bootstrap_development' and not proposal['execution_authorized']
        assert request(prefix + '/recordings/c9')['annotation'] is None
        assert request(prefix + '/recordings/c9/audio', raw=True) == audio
        route = prefix + '/recordings/c9/proposal'
        then = time.perf_counter()
        cached = request(route, 'POST', {})
        report['cached_proposal_http_seconds'] = time.perf_counter() - then
        assert cached['proposal']['proposal_id'] == proposal['proposal_id']
        report['bootstrap_regions_seconds'] = [dict(label=r['label'], start=r['start_sample']/16000, end=r['end_sample']/16000) for r in regions]
        features = json.loads(next((run / 'data/profiles/smoke-c9/features').glob('*.json')).read_text(encoding='utf-8'))
        report['feature_timing'] = features['timing']
        labels = json.loads((assets / 'human-evaluation-20260929/annotations-onset-v3.json').read_text(encoding='utf-8-sig'))
        gold = next(r for r in labels['recordings'] if r['recording_id'] == 'c9')['annotations'][0]
        # These boundaries are existing explicit user confirmations, not predictions.
        annotation = dict(expected_revision=0, confirmed=True, source_kind='user', auto_train=True,
            proposal_id=proposal['proposal_id'], correction_text='Existing human c9 onset3.7s and pure environment3.00–3.41s fixture',
            regions=[dict(start_sample=48000, end_sample=54560, label='environment', tolerance_samples=0),
                     dict(start_sample=gold['start_sample'], end_sample=gold['end_sample'], label='singing', tolerance_samples=800)])
        saved = request(prefix + '/recordings/c9/annotations', 'PUT', annotation)
        training = wait_job(saved['auto_train_job_id'])
        assert training['result']['model_stage'] == 'provisional'
        assert training['result']['evaluation_scope'] == 'training_only_unvalidated_personalization'
        queued = request(route, 'POST', {})
        updated = queued['proposal'] if queued.get('status') == 'ready' else wait_job(queued['job_id'])['result']
        assert updated['model_source'] == 'personal_provisional' and updated['annotation_revision'] == 1
        assert request(prefix + '/recordings/c9')['annotation'] == saved['annotation']
        labelled = request(prefix + '/recordings/c9')
        known_confusion = {}
        for name, score in [('bootstrap_before', proposal['scores']), ('personal_raw_inspection_only', updated['personal_raw_scores']), ('bounded_provisional_after', updated['scores'])]:
            known_confusion[name] = prediction_report(labelled, np.asarray(proposal['source_cell_edges']), np.asarray(score))['known_duration_confusion']
        report.update(passed=True, proposal_job_seconds=proposal_job['wall_seconds'], training=training,
                      updated_proposal=updated, original_audio=str(Path(fixture['wav'])),
                      known_duration_confusion=known_confusion,
                      metric_scope='Only existing confirmed singing3.7–14s with50ms tolerance and environment3–3.41s; all other audio excluded; not validation',
                      checks=['whole-source nonoverlapping cover', 'actual bootstrap acoustic model', 'immutable PCM roundtrip',
                              'proposal did not annotate', 'human revision retained', 'one-lesson provisional fit changed next guess'])
        print(json.dumps(dict(passed=True, result=str(run / 'result.json'), proposal=str(run / 'c9-proposal.json'),
                              regions=report['bootstrap_regions_seconds'], timing=report['feature_timing']), ensure_ascii=False), flush=True)
    finally:
        (run / 'result.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        server.should_exit = True
        worker.join(15)
        sock.close()
        if worker.is_alive():
            raise RuntimeError('isolated_smoke_server_did_not_stop')


if __name__ == '__main__':
    main()
