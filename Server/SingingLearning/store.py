"""One service process owns this store; immutable audio/revisions, atomic indexes."""
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import re
import threading
from datetime import datetime, timezone
import wave


def now():
    return datetime.now(timezone.utc).isoformat()


class LearningError(Exception):
    def __init__(self, code, message=None, status=400):
        super().__init__(message or code)
        self.code, self.message, self.status = code, message or code, status


def identifier(value, field='id'):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,63}', value):
        raise LearningError('invalid_id', field + ' must be a path-safe ASCII identifier')
    if re.fullmatch(r'(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])', value):
        raise LearningError('invalid_id', field + ' is reserved')
    return value


def integer(value, field, low=0):
    if type(value) is not int or value < low:
        raise LearningError('invalid_integer', field + ' must be an integer >= ' + str(low))
    return value


def atomic_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.tmp')
    with temporary.open('w', encoding='utf-8', newline='\n') as stream:
        json.dump(data, stream, ensure_ascii=False, allow_nan=False, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def wav_info(raw, config):
    if len(raw) > config.max_upload_bytes:
        raise LearningError('upload_too_large', status=413)
    try:
        with wave.open(io.BytesIO(raw), 'rb') as audio:
            if audio.getcomptype() != 'NONE' or audio.getsampwidth() != 2:
                raise LearningError('pcm16_required')
            channels, rate, count = audio.getnchannels(), audio.getframerate(), audio.getnframes()
            if channels not in (1, 2) or not 8000 <= rate <= 96000 or count <= 0:
                raise LearningError('invalid_audio_dimensions')
            if count / rate > config.max_duration_seconds:
                raise LearningError('audio_too_long', status=413)
            pcm = audio.readframes(count)
            if len(pcm) != count * channels * 2:
                raise LearningError('truncated_pcm')
    except (wave.Error, EOFError) as exc:
        raise LearningError('invalid_wav') from exc
    return dict(audio_sha256=hashlib.sha256(raw).hexdigest(), sample_rate=rate,
                sample_count=count, channels=channels, duration_seconds=count / rate)


class Store:
    def __init__(self, config):
        self.config = config
        self.root = Path(config.data_dir).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.lock = threading.RLock()

    def profile_path(self, profile):
        return self.root / 'profiles' / identifier(profile, 'profile_id')

    def _state(self, profile):
        path = self.profile_path(profile) / 'index.json'
        if not path.exists():
            raise LearningError('profile_not_found', status=404)
        return read_json(path)

    def _save(self, profile, state):
        atomic_json(self.profile_path(profile) / 'index.json', state)

    def snapshot(self, profile):
        with self.lock:
            return self._state(profile)

    def profiles(self):
        with self.lock:
            return [read_json(p)['profile'] for p in sorted((self.root / 'profiles').glob('*/index.json'))]

    def create_profile(self, body):
        profile = identifier(body.get('profile_id'), 'profile_id')
        name = body.get('display_name', profile)
        if not isinstance(name, str) or len(name) > 120:
            raise LearningError('invalid_display_name')
        with self.lock:
            if (self.profile_path(profile) / 'index.json').exists():
                raise LearningError('profile_exists', status=409)
            value = dict(profile_id=profile, display_name=name, created_at=now(), dataset_revision=0)
            self._save(profile, dict(profile=value, sessions={}, recordings={}, models=[], current_candidate=None))
            return value

    def create_session(self, profile, body):
        session = identifier(body.get('session_id'), 'session_id')
        content = identifier(body.get('content_group'), 'content_group')
        if content.lower() in ('unknown', 'unset', 'none'):
            raise LearningError('content_group_required', 'Name the shared song/content group before collecting')
        with self.lock:
            state = self._state(profile)
            existing = state['sessions'].get(session)
            if existing:
                if existing['content_group'] != content:
                    raise LearningError('session_conflict', status=409)
                return existing
            value = dict(session_id=session, content_group=content, created_at=now())
            state['sessions'][session] = value
            self._save(profile, state)
            return value

    def recordings(self, profile):
        state = self.snapshot(profile)
        return dict(recordings=list(state['recordings'].values()), dataset_revision=state['profile']['dataset_revision'])

    def recording(self, profile, recording):
        identifier(recording, 'recording_id')
        result = self.snapshot(profile)['recordings'].get(recording)
        if result is None:
            raise LearningError('recording_not_found', status=404)
        return result

    def audio_path(self, profile, recording):
        self.recording(profile, recording)
        return self.profile_path(profile) / 'recordings' / recording / 'original.wav'

    @staticmethod
    def _changed(state, corrected_recording=None, previous_model_sources=None):
        state['profile']['dataset_revision'] += 1
        if state['current_candidate']:
            state['current_candidate']['stale'] = True
            state['current_candidate']['out_of_date'] = True
            if corrected_recording and corrected_recording in (previous_model_sources or {}):
                state['current_candidate']['invalidated'] = True

    def upload(self, profile, manifest, raw):
        if not isinstance(manifest, dict):
            raise LearningError('invalid_manifest')
        recording = identifier(manifest.get('recording_id'), 'recording_id')
        session = identifier(manifest.get('session_id'), 'session_id')
        content = identifier(manifest.get('content_group'), 'content_group')
        parent = manifest.get('parent_recording_id') or None
        if parent:
            # Opaque provenance token only, never used in a path. Recorder PCM
            # identities contain colons; treating these as filenames drops lineage.
            if not isinstance(parent, str) or len(parent) > 256 or any(ord(x) < 32 for x in parent):
                raise LearningError('invalid_parent_token')
        capture = integer(manifest.get('capture_serial'), 'capture_serial')
        source = manifest.get('source_kind', 'unknown')
        if source not in ('user', 'playback', 'mixed', 'unknown'):
            raise LearningError('invalid_source_kind')
        info = wav_info(raw, self.config)
        for key in ('audio_sha256', 'sample_rate', 'sample_count'):
            if key in manifest and (type(manifest[key]) is not type(info[key]) or manifest[key] != info[key]):
                raise LearningError('source_mismatch', key + ' does not match original WAV', 409)
        lineage = manifest.get('lineage_json') or None
        if isinstance(lineage, str):
            if len(lineage) > 65536:
                raise LearningError('lineage_too_large')
            try:
                lineage = json.loads(lineage)
            except ValueError as exc:
                raise LearningError('invalid_lineage') from exc
        tokens = []
        if lineage is not None:
            if not isinstance(lineage, dict) or len(json.dumps(lineage)) > 65536:
                raise LearningError('invalid_lineage')
            regions = lineage.get('regions', [])
            if not isinstance(regions, list) or len(regions) > 512:
                raise LearningError('invalid_lineage')
            for region in regions:
                token = region.get('sourceId') if isinstance(region, dict) else None
                if not isinstance(token, str) or not token or len(token) > 256:
                    raise LearningError('invalid_lineage_source')
                tokens.append(token)
        metadata = dict(recording_id=recording, session_id=session, content_group=content,
                        capture_serial=capture, parent_recording_id=parent, source_kind=source,
                        lineage_json=lineage, lineage_source_ids=sorted(set(tokens)), **info)
        with self.lock:
            state = self._state(profile)
            if session not in state['sessions'] or state['sessions'][session]['content_group'] != content:
                raise LearningError('session_binding_mismatch', status=409)
            existing = state['recordings'].get(recording)
            if existing:
                if any(existing.get(key) != value for key, value in metadata.items()):
                    raise LearningError('recording_conflict', status=409)
                return existing, False
            if any(r.lower() == recording.lower() for r in state['recordings']):
                raise LearningError('recording_case_conflict', status=409)
            if len(state['recordings']) >= self.config.max_recordings:
                raise LearningError('profile_recording_limit', status=413)
            directory = self.profile_path(profile) / 'recordings' / recording
            directory.mkdir(parents=True, exist_ok=True)
            # No index can reference audio before all bytes have been flushed.
            with (directory / 'original.wav').open('wb') as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            value = dict(metadata, created_at=now(), annotation_revision=0, annotation=None,
                         feature_status='pending', feature_job_id=None)
            atomic_json(directory / 'manifest.json', metadata)
            state['recordings'][recording] = value
            self._changed(state)
            self._save(profile, state)
            return value, True

    def annotate(self, profile, recording, body):
        if body.get('confirmed') is not True:
            raise LearningError('human_confirmation_required')
        source = body.get('source_kind')
        if source not in ('user', 'playback', 'mixed', 'unknown'):
            raise LearningError('explicit_source_confirmation_required')
        expected = integer(body.get('expected_revision'), 'expected_revision')
        if type(body.get('auto_train', True)) is not bool:
            raise LearningError('invalid_auto_train')
        with self.lock:
            state = self._state(profile)
            rec = state['recordings'].get(identifier(recording, 'recording_id'))
            if rec is None:
                raise LearningError('recording_not_found', status=404)
            if expected != rec['annotation_revision']:
                raise LearningError('annotation_revision_conflict', 'Refresh current annotation revision before saving', 409)
            regions = body.get('regions')
            if not isinstance(regions, list) or len(regions) > 1024:
                raise LearningError('invalid_regions')
            clean, previous_end = [], 0
            for region in regions:
                if not isinstance(region, dict):
                    raise LearningError('invalid_region')
                start = integer(region.get('start_sample'), 'start_sample')
                end = integer(region.get('end_sample'), 'end_sample')
                tolerance = integer(region.get('tolerance_samples', 0), 'tolerance_samples')
                label = region.get('label')
                if start < previous_end or end <= start or end > rec['sample_count'] or tolerance > rec['sample_count']:
                    raise LearningError('invalid_range', 'Sorted, non-overlapping sample ranges inside the source are required')
                if label not in ('speech', 'singing', 'environment', 'uncertain'):
                    raise LearningError('invalid_label')
                clean.append(dict(start_sample=start, end_sample=end, label=label, tolerance_samples=tolerance))
                previous_end = end
            proposal_id = body.get('proposal_id') or None
            assisted = None
            if proposal_id is not None:
                proposal = self.proposal(profile, recording, proposal_id)
                if proposal['audio_sha256'] != rec['audio_sha256'] or proposal['annotation_revision'] != expected:
                    raise LearningError('proposal_revision_conflict', 'Refresh the proposed regions before confirming', 409)
                assisted = dict(proposal_id=proposal_id, model_source=proposal['model_source'],
                                model_id=proposal['model_id'], adapter_sha256=proposal['adapter_sha256'])
            correction = body.get('correction_text')
            if correction is not None and (not isinstance(correction, str) or len(correction) > 2000):
                raise LearningError('invalid_correction_text')
            revision = expected + 1
            annotation = dict(revision=revision, confirmed=True, source_kind=source, regions=clean,
                              audio_sha256=rec['audio_sha256'], sample_rate=rec['sample_rate'],
                              sample_count=rec['sample_count'], confirmed_at=now(), confirmation='explicit_human',
                              assistance=assisted, correction_text=correction)
            path = self.profile_path(profile) / 'recordings' / recording / 'annotations' / ('%06d.json' % revision)
            atomic_json(path, annotation)
            rec.update(annotation_revision=revision, annotation=annotation)
            previous_sources = {}
            if state['current_candidate']:
                previous_sources = self.model(profile, state['current_candidate']['model_id']).get('source_snapshot', {})
            self._changed(state, recording, previous_sources)
            self._save(profile, state)
            return dict(annotation_revision=revision, dataset_revision=state['profile']['dataset_revision'], annotation=annotation)

    def update_feature(self, profile, recording, **changes):
        with self.lock:
            state = self._state(profile)
            state['recordings'][recording].update(changes)
            self._save(profile, state)

    def proposal(self, profile, recording, proposal_id):
        self.recording(profile, recording)
        path = self.profile_path(profile) / 'recordings' / recording / 'proposals' / (identifier(proposal_id, 'proposal_id') + '.json')
        if not path.exists():
            raise LearningError('proposal_not_found', status=404)
        value = read_json(path)
        if value['recording_id'] != recording:
            raise LearningError('proposal_recording_mismatch', status=409)
        return value

    def save_proposal(self, profile, recording, proposal):
        with self.lock:
            state = self._state(profile)
            rec = state['recordings'][recording]
            if proposal['audio_sha256'] != rec['audio_sha256']:
                raise LearningError('proposal_source_mismatch', status=409)
            path = self.profile_path(profile) / 'recordings' / recording / 'proposals' / (identifier(proposal['proposal_id']) + '.json')
            if not path.exists():
                atomic_json(path, proposal)
            else:
                proposal = read_json(path)
            if proposal['annotation_revision'] == rec['annotation_revision']:
                rec['proposal_id'] = proposal['proposal_id']
                self._save(profile, state)
            return proposal

    def save_model(self, profile, report):
        with self.lock:
            state = self._state(profile)
            report = copy.deepcopy(report)
            report['stale_at_completion'] = report['source_dataset_revision'] != state['profile']['dataset_revision']
            report['invalidated_at_completion'] = self.model_invalidated(report, state)
            atomic_json(self.profile_path(profile) / 'models' / (identifier(report['model_id']) + '.json'), report)
            summary = {k: report[k] for k in ('model_id', 'created_at', 'status', 'source_dataset_revision', 'execution_authorized')}
            summary['model_stage'] = report.get('model_stage')
            summary['evaluation_scope'] = report.get('evaluation_scope')
            summary['stale'] = report['stale_at_completion']
            summary['out_of_date'] = report['stale_at_completion']
            summary['invalidated'] = report['invalidated_at_completion']
            summary['reasons'] = report.get('reasons', [])
            if 'temporal_fusion' in report:
                summary['temporal_fusion'] = self.fusion_summary(report['temporal_fusion'])
            if 'sequence_boundary' in report:
                summary['sequence_boundary'] = self.fusion_summary(report['sequence_boundary'])
            if 'local_boundary' in report:
                summary['local_boundary'] = self.fusion_summary(report['local_boundary'])
            if 'category_evidence' in report:
                summary['category_evidence'] = self.fusion_summary(report['category_evidence'])
            state['models'].append(summary)
            if report['status'] == 'candidate' and not report['invalidated_at_completion']:
                state['current_candidate'] = copy.deepcopy(summary)
            self._save(profile, state)
            return report

    @staticmethod
    def fusion_summary(report):
        return {key: copy.deepcopy(report[key]) for key in (
            'status', 'teaching_eligible', 'eligibility_reasons', 'provider', 'evaluation_scope',
            'recording_count', 'fold_count', 'complete', 'execution_authorized', 'production_eligible') if key in report}

    @staticmethod
    def model_invalidated(report, state):
        for rid, source in report['source_snapshot'].items():
            current = state['recordings'].get(rid)
            if current is None or current['audio_sha256'] != source['audio_sha256'] or current['annotation_revision'] != source['annotation_revision']:
                return True
        return False

    def model(self, profile, model):
        path = self.profile_path(profile) / 'models' / (identifier(model, 'model_id') + '.json')
        if not path.exists():
            raise LearningError('model_not_found', status=404)
        return read_json(path)
