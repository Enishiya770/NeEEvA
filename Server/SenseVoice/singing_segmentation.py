"""Offline framewise Speech/Singing candidates; never an executable cut.

PANNs DecisionLevelMax is weakly supervised sound-event detection. Independent
sigmoids are NOT calibrated class probabilities or speech/song exclusivity.
The public 10 ms framewise output repeats native 320 ms cells. The receptive
field is approximately 3.502 s, so neither is a word-level boundary guarantee.
This module does not import or change the running SenseVoice server.
"""
from __future__ import annotations

import csv
from dataclasses import asdict, dataclass
import hashlib
import importlib
import io
import math
from pathlib import Path
import sys
import time
import types
from typing import Callable

import numpy as np

MODEL = 'PANNs-Cnn14_DecisionLevelMax-AudioSet'
OFFICIAL_MD5 = '70539c43c18b6a289b3199c503a82c5a'
ANALYSIS_RATE = 32000
HOP = 320
NATIVE_STRIDE = 32
RECEPTIVE_FIELD_SECONDS = 3.502
LABELS = ('Speech', 'Singing', 'Male speech, man speaking',
          'Female speech, woman speaking', 'Child speech, kid speaking', 'Conversation', 'Narration, monologue',
          'Male singing', 'Female singing', 'Child singing', 'Humming',
          'Rapping', 'Music')
SPEECH_FAMILY = ('Speech','Male speech, man speaking','Female speech, woman speaking','Child speech, kid speaking')
SINGING_FAMILY = ('Singing','Male singing','Female singing','Child singing')


@dataclass(frozen=True)
class CandidatePolicy:
    """Exploratory, fixed-before-evaluation thresholds, not a trained classifier."""
    active_threshold: float = 0.5
    dominance_margin: float = 0.15
    minimum_run_seconds: float = 0.64
    score_aggregation: str = 'direct'

    def validate(self):
        if not 0 < self.active_threshold <= 1 or not 0 <= self.dominance_margin <= 1:
            raise ValueError('invalid_threshold')
        if not math.isfinite(self.minimum_run_seconds) or self.minimum_run_seconds < 0:
            raise ValueError('invalid_minimum_run')
        if self.score_aggregation not in ('direct','family_max'):
            raise ValueError('invalid_score_aggregation')


def family_max_scores(scores, labels):
    """Maximum observed family member score; not a union probability.

    AudioSet Strong asks annotators for the most specific available class, so
    parent Speech/Singing need not activate with male/female child classes.
    """
    return tuple(np.max(scores[:,[labels.index(k) for k in family if k in labels]],axis=1)
                 for family in (SPEECH_FAMILY,SINGING_FAMILY))


def file_hash(path: Path, algorithm='sha256') -> str:
    h = hashlib.new(algorithm)
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def _check_source(source):
    digest = source.get('audio_sha256', '')
    if len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest):
        raise ValueError('invalid_audio_sha256')
    if type(source.get('sample_rate')) is not int or source['sample_rate'] <= 0:
        raise ValueError('invalid_source_sample_rate')
    if type(source.get('sample_count')) is not int or source['sample_count'] <= 0:
        raise ValueError('invalid_source_sample_count')


def candidate_timeline(native_scores, labels, source, model_version,
                       policy=CandidatePolicy(), *, cell_analysis_samples=HOP*NATIVE_STRIDE,
                       analysis_rate=ANALYSIS_RATE, context_support_seconds=RECEPTIVE_FIELD_SECONDS):
    """Map native cells to original source samples, preserving ambiguous runs.

    Direct classes or explicitly selected max-family evidence choose a label;
    neither score aggregation is a calibrated probability union. No ASR,
    old trim times, lyrics, language or user intent enters this classification.
    """
    policy.validate()
    _check_source(source)
    if type(cell_analysis_samples) is not int or cell_analysis_samples <= 0 or type(analysis_rate) is not int or analysis_rate <= 0:
        raise ValueError('invalid_time_grid')
    if not math.isfinite(context_support_seconds) or context_support_seconds < 0:
        raise ValueError('invalid_context_support')
    scores = np.asarray(native_scores, dtype=np.float64)
    if scores.ndim != 2 or scores.shape[1] != len(labels) or not len(scores):
        raise ValueError('invalid_score_shape')
    if not np.isfinite(scores).all() or np.any(scores < 0) or np.any(scores > 1):
        raise ValueError('invalid_scores')
    if 'Speech' not in labels or 'Singing' not in labels:
        raise ValueError('missing_primary_classes')
    sr, count = source['sample_rate'], source['sample_count']
    # Exact rational mapping of 10240 analysis samples to original sample rate.
    boundaries = [min(count, (i * cell_analysis_samples * sr + analysis_rate // 2)
                      // analysis_rate) for i in range(len(scores) + 1)]
    if boundaries[-1] < count:
        raise ValueError('scores_do_not_cover_source')
    valid = [(i, boundaries[i], boundaries[i+1]) for i in range(len(scores))
             if boundaries[i+1] > boundaries[i]]
    speech = scores[:, labels.index('Speech')]
    singing = scores[:, labels.index('Singing')]
    if policy.score_aggregation=='family_max':speech,singing=family_max_scores(scores,labels)
    states = []
    for i, _, _ in valid:
        a, b = speech[i], singing[i]
        if a >= policy.active_threshold and b >= policy.active_threshold:
            label = 'mixed'
        elif b >= policy.active_threshold and b-a >= policy.dominance_margin:
            label = 'singing'
        elif a >= policy.active_threshold and a-b >= policy.dominance_margin:
            label = 'speech'
        else:
            label = 'uncertain'
        states.append(label)

    def runs(values):
        result = []
        for i, value in enumerate(values):
            if not result or result[-1][2] != value:
                result.append([i, i+1, value])
            else:
                result[-1][1] = i+1
        return result

    # Short high-score islands become uncertain, never silently joined across
    # a speech/mixed interruption or removed from the returned timeline.
    for left, right, label in runs(states):
        if label in ('speech', 'singing') and (valid[right-1][2]-valid[left][1])/sr < policy.minimum_run_seconds:
            states[left:right] = ['uncertain'] * (right-left)
    segments = []
    for left, right, label in runs(states):
        start, end = valid[left][1], valid[right-1][2]
        selected = scores[[valid[i][0] for i in range(left, right)]]
        identity = f"{source['audio_sha256']}:{start}:{end}:{label}:{model_version}:{asdict(policy)}"
        segments.append({
            'segment_id':'sed-'+hashlib.sha256(identity.encode()).hexdigest()[:24],
            'status':'candidate', 'label':label, 'start_sample':start, 'end_sample':end,
            'coordinate_sample_rate':sr,
            'posteriors': {name:{'mean':float(selected[:,j].mean()),
                                 'min':float(selected[:,j].min()),
                                 'max':float(selected[:,j].max())} for j,name in enumerate(labels)},
            'calibrated':False, 'boundary_verified':False,
        })
    radius = math.ceil(context_support_seconds / 2 * sr)
    transitions = []
    for previous, current in zip(segments, segments[1:]):
        split = current['start_sample']
        transitions.append({
            'from':previous['label'], 'to':current['label'],
            'nominal_sample':split,
            'context_support_start_sample':max(0, split-radius),
            'context_support_end_sample':min(count, split+radius),
            'uncertainty_kind':'model-context-support-not-statistical-confidence-interval',
            'boundary_verified':False,
        })
    return segments, transitions


class PannsFramewiseProvider:
    """Explicit local checkpoint loader. No auto-download and no server calls."""
    def __init__(self, checkpoint, labels_csv, dependency_dir, device='cuda'):
        started = time.perf_counter()
        checkpoint = Path(checkpoint).resolve()
        dependencies = Path(dependency_dir).resolve()
        if file_hash(checkpoint, 'md5') != OFFICIAL_MD5:
            raise ValueError('official_checkpoint_checksum_mismatch')
        if str(dependencies) not in sys.path:
            sys.path.insert(0, str(dependencies))
        # Do not execute panns_inference.__init__/config: upstream auto-fetches
        # labels into the user home. Load only its official model module.
        namespace = '_neeeva_isolated_panns'
        package = types.ModuleType(namespace)
        package.__path__ = [str(dependencies / 'panns_inference')]
        sys.modules[namespace] = package
        models = importlib.import_module(namespace + '.models')
        import torch
        self.torch = torch
        if device == 'cuda' and not torch.cuda.is_available():
            raise RuntimeError('cuda_requested_but_unavailable')
        self.device = device
        self.model = models.Cnn14_DecisionLevelMax(
            sample_rate=ANALYSIS_RATE, window_size=1024, hop_size=HOP,
            mel_bins=64, fmin=50, fmax=14000, classes_num=527,
            interpolate_mode='nearest')
        weights = torch.load(checkpoint, map_location='cpu', weights_only=True)
        self.model.load_state_dict(weights['model'], strict=True)
        self.model.to(device).eval()
        self.model.requires_grad_(False)
        with Path(labels_csv).open(encoding='utf-8', newline='') as stream:
            rows = list(csv.DictReader(stream))
        if len(rows) != 527 or [int(row['index']) for row in rows] != list(range(527)):
            raise ValueError('invalid_audioset_label_order')
        self.all_labels = [r['display_name'] for r in rows]
        self.labels = list(LABELS)
        self.indices = [self.all_labels.index(label) for label in self.labels]
        self.version = file_hash(checkpoint)
        self.labels_sha256 = file_hash(Path(labels_csv))
        self.model_code_sha256 = file_hash(dependencies / 'panns_inference' / 'models.py')
        self.load_seconds = time.perf_counter()-started

    def analyze(self, audio_path, policy=CandidatePolicy(), cancelled: Callable[[],bool] | None=None):
        import soundfile as sf
        from scipy.signal import resample_poly
        started = time.perf_counter()
        path = Path(audio_path).resolve()
        if cancelled and cancelled():
            return {'status':'cancelled','execution_authorized':False}, None
        # Hash and decode one immutable byte snapshot, never reread a potentially
        # updated capture file between decoding and ownership binding.
        raw_wav = path.read_bytes()
        audio, sample_rate = sf.read(io.BytesIO(raw_wav), dtype='float32', always_2d=True)
        count, channels = audio.shape
        if count <= 0 or count / sample_rate > 120:
            raise ValueError('audio_duration_outside_offline_limit')
        if not np.isfinite(audio).all():
            raise ValueError('nonfinite_audio')
        source = {'audio_sha256':hashlib.sha256(raw_wav).hexdigest(), 'sample_rate':sample_rate,
                  'sample_count':count, 'channels':channels,
                  'coordinate_origin_sample':0, 'sample_range_convention':'half-open'}
        mono = audio.mean(axis=1)
        divisor = math.gcd(sample_rate, ANALYSIS_RATE)
        resampled = resample_poly(mono, ANALYSIS_RATE//divisor, sample_rate//divisor).astype(np.float32)
        original_analysis_count = len(resampled)
        # Reflect STFT and five pooling levels need at least one full cell.
        if len(resampled) < ANALYSIS_RATE:
            resampled = np.pad(resampled, (0, ANALYSIS_RATE-len(resampled)))
        if cancelled and cancelled():
            return {'status':'cancelled','source':source,'execution_authorized':False}, None
        torch = self.torch
        if self.device == 'cuda':
            torch.cuda.synchronize()
            torch.cuda.reset_peak_memory_stats()
        infer_started = time.perf_counter()
        with torch.inference_mode():
            output = self.model(torch.from_numpy(resampled[None]).to(self.device))
            framewise = output['framewise_output'][0].float().cpu().numpy()
        if self.device == 'cuda':
            torch.cuda.synchronize()
        infer_seconds = time.perf_counter()-infer_started
        if cancelled and cancelled():
            return {'status':'cancelled','source':source,'execution_authorized':False}, None
        # Official interpolator repeats each learned native cell 32 times and
        # repeats the final cell to cover the STFT tail. Preserve this fact.
        frame_count = math.ceil(original_analysis_count/HOP)
        framewise = framewise[:frame_count]
        selected = framewise[:, self.indices]
        native = selected[::NATIVE_STRIDE]
        segments, transitions = candidate_timeline(native, self.labels, source, self.version, policy)
        result = {
            'schema':1, 'status':'candidate', 'provider':MODEL, 'model_version':self.version,
            'source':source, 'execution_authorized':False, 'supported_range':None,
            'model_metadata':{'checkpoint_md5':OFFICIAL_MD5,
                'labels_sha256':self.labels_sha256,'model_code_sha256':self.model_code_sha256,
                'training':'AudioSet weak clip labels', 'analysis_sample_rate':ANALYSIS_RATE,
                'output_grid_seconds':HOP/ANALYSIS_RATE,
                'native_cell_seconds':HOP*NATIVE_STRIDE/ANALYSIS_RATE,
                'theoretical_receptive_field_seconds':RECEPTIVE_FIELD_SECONDS,
                'interpolation':'nearest-repeat-32-then-last-cell-padding',
                'calibrated':False,'mutually_exclusive_classes':False},
            'policy':asdict(policy), 'segments':segments, 'transitions':transitions,
            'timeline':{'columns':self.labels,'scores':native.tolist(),
                'analysis_cell_samples':HOP*NATIVE_STRIDE,
                'coordinate_sample_rate':sample_rate,
                'source_start_samples':[min(count,round(i*HOP*NATIVE_STRIDE*sample_rate/ANALYSIS_RATE)) for i in range(len(native))]},
            'timing':{'model_load_seconds':self.load_seconds,'inference_seconds':infer_seconds,
                'total_seconds':time.perf_counter()-started,
                'peak_cuda_allocated_bytes':torch.cuda.max_memory_allocated() if self.device=='cuda' else None,
                'device':self.device},
            'scope':'Acoustic candidates only. No ASR windows, words, intent, previous crops, human labels, source selection or playback authority. Context support bands are not calibrated confidence intervals.'}
        return result, framewise
