"""Causal pitch evidence. Voicing is deliberately never a singing decision.

Only completed windows produce frames; ending a recording adds no synthetic
future samples. ``center_sample`` describes the acoustic window, while
``available_sample`` describes when a consumer can first use its result. The
frame center is the midpoint of the half-open window (not a sample index).
Offline extraction replays the very same bounded, synchronous stream used by
live callers. Callers provide their own queue/backpressure and must not share a
PESTO provider between streams: its neural frontend contains mutable history.
"""
from collections import deque
import hashlib
import importlib
import json

import numpy as np


SCHEMA_VERSION = 'causal-pitch-v1'
SAMPLE_RATE = 16000
FEATURE_NAMES = (
    'pitch_coverage_fraction', 'pitch_voiced_fraction',
    'pitch_confidence_mean', 'pitch_confidence_std', 'pitch_latest_voiced',
    'pitch_iqr_semitones', 'pitch_span_p90_p10_semitones',
    'pitch_adjacent_delta_median', 'pitch_adjacent_delta_p90',
    'pitch_stable_adjacent_fraction', 'pitch_longest_voiced_seconds',
    'pitch_trailing_voiced_seconds', 'pitch_longest_stable_seconds',
    'pitch_recent_relative_semitones', 'pitch_relative_slope_per_second',
    'pitch_voicing_transitions_per_second',
)
FEATURE_POLICY = dict(version='trailing-relative-v1', window_samples=16000,
                      availability_rule='decision_end-window < available <= decision_end',
                      stable_semitones_per_second=25.0,
                      absolute_pitch_feature=False, silence_is_singing=False)


class PitchError(ValueError):
    """Invalid audio/clock or incompatible optional pitch provider."""


class PitchBackpressure(PitchError):
    """Oversized chunk was rejected before consuming any samples."""


def _identity(metadata):
    return hashlib.sha256(json.dumps(metadata, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def _audio(samples):
    array = np.asarray(samples)
    if array.ndim != 1 or array.dtype.kind != 'f':
        raise PitchError('expected mono normalized floating-point PCM')
    if not np.all(np.isfinite(array)) or np.any(np.abs(array) > 1.000001):
        raise PitchError('PCM must be finite and normalized to [-1, 1]')
    return np.asarray(array, dtype=np.float32)


class FFTProvider:
    """40 ms local-DC linear normalized autocorrelation, 10 ms stride.

    Normalization uses the energy of the two overlapping portions, rather than
    a whole-recording energy/percentile gate. Local maxima avoid picking the
    edge of a monotonic autocorrelation curve. The earliest peak within 95% of
    the best peak avoids selecting a lower octave on clean periodic tones.
    This is a pitch estimator, not a speech/song classifier.
    """
    hop_samples = 160
    window_samples = 640
    first_available_sample = 640

    def __init__(self):
        policy = dict(schema_version=SCHEMA_VERSION, provider='fft',
                      algorithm='linear-nsdf-local-peak-v1', sample_rate=SAMPLE_RATE,
                      hop_samples=self.hop_samples, window_samples=self.window_samples,
                      first_available_sample=self.first_available_sample,
                      confidence_semantics='normalized_autocorrelation_periodicity_not_singing_probability',
                      voicing_threshold=.6, min_rms=.0001, minimum_hz=55., maximum_hz=900.,
                      dc_removal='each_completed_640_sample_frame',
                      center_convention='midpoint_of_half_open_support', causal=True,
                      tail_policy='no_padding_or_flush', execution_authorized=False)
        self.metadata = dict(policy, provider_identity=_identity(policy))

    def reset(self):
        pass

    def estimate(self, samples):
        x = np.asarray(samples, dtype=np.float64)
        x = x - float(x.mean())
        rms = float(np.sqrt(np.mean(x * x)))
        if rms < self.metadata['min_rms']:
            return 0., 0., False, rms
        # Linear autocorrelation: FFT length >= 2*N-1, no circular wraparound.
        fft_size = 1 << (2 * len(x) - 1).bit_length()
        spectrum = np.fft.rfft(x, n=fft_size)
        autocorr = np.fft.irfft(spectrum * spectrum.conjugate(), n=fft_size)[:len(x)]
        minimum_lag = int(np.ceil(SAMPLE_RATE / self.metadata['maximum_hz']))
        maximum_lag = int(np.floor(SAMPLE_RATE / self.metadata['minimum_hz']))
        lags = np.arange(minimum_lag - 1, maximum_lag + 2)
        sums = np.r_[0., np.cumsum(x * x)]
        denominator = np.sqrt(np.maximum(sums[len(x) - lags] * (sums[-1] - sums[lags]), 1e-24))
        corr = np.clip(autocorr[lags] / denominator, -1., 1.)
        peaks = np.flatnonzero((corr[1:-1] >= corr[:-2]) & (corr[1:-1] > corr[2:])) + 1
        if not len(peaks):
            return 0., 0., False, rms
        best = float(corr[peaks].max())
        if best <= 0.:
            return 0., 0., False, rms
        peak = int(peaks[np.flatnonzero(corr[peaks] >= .95 * best)[0]])
        left, middle, right = corr[peak - 1:peak + 2]
        divisor = left - 2. * middle + right
        offset = float(np.clip(.5 * (left - right) / divisor, -1., 1.)) if abs(divisor) > 1e-12 else 0.
        f0 = float(SAMPLE_RATE / (lags[peak] + offset))
        confidence = float(np.clip(middle, 0., 1.))
        voiced = confidence >= self.metadata['voicing_threshold']
        return f0 if voiced else 0., confidence, bool(voiced), rms


class PestoProvider:
    """Opt-in PESTO 2 streaming adapter; never installs/downloads or falls back.

    Construct with an explicitly loaded 16 kHz / 20 ms / streaming / mirror=0
    model, or call ``load``. Optional dependencies are imported only here.
    CPU and CUDA reset preserve the existing cache devices.
    """
    hop_samples = 320
    first_available_sample = 320

    @classmethod
    def load(cls, checkpoint='mir-1k_g7', *, device='cpu'):
        try:
            pesto = importlib.import_module('pesto')
            model = pesto.load_model(checkpoint, step_size=20., sampling_rate=SAMPLE_RATE,
                                     streaming=True, mirror=0.).to(device)
        except (ImportError, FileNotFoundError) as exc:
            raise PitchError('PESTO requires an explicitly installed package and local checkpoint') from exc
        return cls(model)

    def __init__(self, model):
        try:
            self.torch = importlib.import_module('torch')
            pre = model.preprocessor
            kernels = pre.hcqt_kernels.cqt_kernels
            if pre.hcqt_sr != SAMPLE_RATE or pre.hop_size != 20. or len(kernels) != 1:
                raise PitchError('PESTO must use 16 kHz, 20 ms, single-harmonic streaming frontend')
            conv = kernels[0].conv
            width = int(conv.kernel_size[0])
            if (not pre.hcqt_kwargs.get('streaming') or pre.hcqt_kwargs.get('mirror', 0.) != 0.
                    or conv.cache.padding != width - self.hop_samples
                    or conv.stride != (self.hop_samples,)):
                raise PitchError('PESTO centered/mirrored frontend cannot be labelled causal')
            self.model = model.eval()
            self.window_samples = width
            self._caches = [m for m in model.modules() if m.__class__.__name__ == 'CachedPadding1d']
            if not self._caches:
                raise PitchError('PESTO streaming history is not resettable')
            # Cache identity includes all model weights, not a caller supplied name.
            digest = hashlib.sha256()
            for name, value in sorted(model.state_dict().items()):
                digest.update(name.encode())
                digest.update(value.detach().cpu().contiguous().numpy().tobytes())
            policy = dict(schema_version=SCHEMA_VERSION, provider='pesto',
                          algorithm='pesto-explicit-streaming-mirror0-v1',
                          model_state_sha256=digest.hexdigest(), sample_rate=SAMPLE_RATE,
                          hop_samples=self.hop_samples, window_samples=width,
                          first_available_sample=self.first_available_sample,
                          confidence_semantics='pesto_voicing_confidence_not_singing_probability',
                          voicing_threshold=.5, min_rms=.0001, dc_removal='none',
                          center_convention='midpoint_of_half_open_support',
                          causal=True, initial_history='zeros',
                          tail_policy='no_padding_or_flush', execution_authorized=False)
            self.metadata = dict(policy, provider_identity=_identity(policy))
            self.reset()
        except (AttributeError, IndexError, TypeError) as exc:
            raise PitchError('unsupported PESTO streaming model layout') from exc

    def reset(self):
        with self.torch.no_grad():
            for cache in self._caches:
                cache.pad.zero_()

    def estimate(self, samples):
        # Input is exactly one hop; underlying PESTO cache holds the full window.
        parameter = next(self.model.parameters())
        value = self.torch.from_numpy(np.array(samples, dtype=np.float32, copy=True)).to(parameter.device)
        with self.torch.inference_mode():
            f0, confidence, _ = self.model(value, SAMPLE_RATE, convert_to_freq=True, return_activations=False)
        if f0.numel() != 1 or confidence.numel() != 1:
            raise PitchError('PESTO must return one frame per 320 input samples')
        f0, confidence = float(f0.item()), float(confidence.item())
        if not np.isfinite(f0) or not np.isfinite(confidence) or not 0 <= confidence <= 1:
            raise PitchError('PESTO returned nonfinite or invalid pitch evidence')
        return f0, confidence


def provider_identity(provider='fft'):
    if provider == 'fft':
        return FFTProvider().metadata['provider_identity']
    if isinstance(provider, (FFTProvider, PestoProvider)):
        return provider.metadata['provider_identity']
    raise PitchError('PESTO identity requires an explicitly initialized provider')


class CausalPitchStream:
    """Bounded synchronous stream. Rejected input is never partly consumed.

    A stream owns its provider. Reset before using a new recording identity.
    Returned frames are caller-owned; this object stores no growing track.
    ``start_sample`` and ``stream_id`` should be supplied by live callers to
    reject dropped, duplicated, reordered, or cross-recording chunks.
    """
    def __init__(self, stream_id, *, provider='fft', sample_rate=SAMPLE_RATE, max_chunk_samples=SAMPLE_RATE):
        if sample_rate != SAMPLE_RATE or type(sample_rate) is not int:
            raise PitchError('only 16000 Hz mono PCM is supported; resample explicitly upstream')
        if type(max_chunk_samples) is not int or max_chunk_samples <= 0:
            raise PitchError('max_chunk_samples must be a positive integer')
        if provider == 'fft':
            provider = FFTProvider()
        if not isinstance(provider, (FFTProvider, PestoProvider)):
            raise PitchError('provider must be fft or an explicitly initialized PestoProvider')
        self.provider = provider
        self.metadata = dict(provider.metadata)
        self.max_chunk_samples = max_chunk_samples
        self.reset(stream_id)

    def reset(self, stream_id):
        if not isinstance(stream_id, str) or not stream_id.strip():
            raise PitchError('nonempty recording stream_id required')
        self.stream_id = stream_id
        self.sample_count = 0
        self._buffer_start = 0
        self._next_end = self.provider.first_available_sample
        self._buffer = np.empty(0, dtype=np.float32)
        self._failed = False
        self.provider.reset()

    @property
    def buffered_samples(self):
        return len(self._buffer)

    def push(self, samples, *, stream_id=None, start_sample=None):
        if self._failed:
            raise PitchError('provider failed; reset the stream before reuse')
        if stream_id is not None and stream_id != self.stream_id:
            raise PitchError('recording stream identity changed without reset')
        if start_sample is not None and (type(start_sample) is not int or start_sample != self.sample_count):
            raise PitchError('noncontiguous original sample position')
        # Check size before converting/copying a potentially very large input.
        if len(samples) > self.max_chunk_samples:
            raise PitchBackpressure('chunk exceeds max_chunk_samples; split it and retry')
        data = _audio(samples)
        if not len(data):
            return []
        self._buffer = np.concatenate((self._buffer, data))
        self.sample_count += len(data)
        result = []
        try:
            while self._next_end <= self.sample_count:
                end = self._next_end
                start = end - self.provider.window_samples
                lo = max(0, start) - self._buffer_start
                hi = end - self._buffer_start
                support = self._buffer[lo:hi]
                if isinstance(self.provider, FFTProvider):
                    f0, confidence, voiced, rms = self.provider.estimate(support)
                else:
                    f0, confidence = self.provider.estimate(self._buffer[hi - self.provider.hop_samples:hi])
                    # RMS covers observed support only; pre-recording zeros aren't observations.
                    centered = support.astype(np.float64) - float(support.mean())
                    rms = float(np.sqrt(np.mean(centered * centered)))
                    voiced = bool(confidence >= self.metadata['voicing_threshold']
                                  and rms >= self.metadata['min_rms'] and f0 > 0)
                    if not voiced:
                        f0 = 0.
                result.append(dict(frame_start_sample=start, frame_end_sample=end,
                                   observed_start_sample=max(0, start), center_sample=(start + end) / 2.,
                                   available_sample=end, f0_hz=f0, confidence=confidence,
                                   voiced=voiced, rms=rms))
                self._next_end += self.provider.hop_samples
        except Exception:
            # No continuation on a partially advanced optional neural cache.
            self._failed = True
            raise
        retain_from = max(0, self._next_end - self.provider.window_samples)
        self._buffer = self._buffer[retain_from - self._buffer_start:].copy()
        self._buffer_start = retain_from
        return result

    def track(self, frames):
        return dict(self.metadata, stream_id=self.stream_id, sample_count=self.sample_count, frames=list(frames))


def extract_track(audio, *, stream_id, provider='fft', sample_rate=SAMPLE_RATE,
                  chunk_samples=320, pesto_model=None):
    """Offline replay through the streaming path; no full-recording normalization."""
    if provider == 'pesto':
        if pesto_model is None:
            raise PitchError('PESTO requires an explicitly initialized pesto_model or PestoProvider')
        provider = PestoProvider(pesto_model)
    stream = CausalPitchStream(stream_id, provider=provider, sample_rate=sample_rate)
    if type(chunk_samples) is not int or not 0 < chunk_samples <= stream.max_chunk_samples:
        raise PitchError('invalid replay chunk_samples')
    audio = _audio(audio)
    frames = []
    for start in range(0, len(audio), chunk_samples):
        frames.extend(stream.push(audio[start:start + chunk_samples], stream_id=stream_id, start_sample=start))
    return stream.track(frames)


def _summarize(frames, decision_end, *, hop_samples, window_samples):
    if not frames:
        return np.zeros(len(FEATURE_NAMES), dtype=np.float64)
    n = len(frames)
    voiced = np.asarray([f['voiced'] for f in frames], dtype=bool)
    confidence = np.asarray([f['confidence'] for f in frames], dtype=np.float64)
    availability = np.asarray([f['available_sample'] for f in frames], dtype=np.int64)
    midi = np.asarray([69. + 12. * np.log2(f['f0_hz'] / 440.) if f['voiced'] else 0. for f in frames])
    contiguous = np.diff(availability) == hop_samples
    adjacent = voiced[:-1] & voiced[1:] & contiguous
    delta = np.abs(np.diff(midi))[adjacent]
    hop_seconds = hop_samples / SAMPLE_RATE
    stable = adjacent & (np.abs(np.diff(midi)) <= FEATURE_POLICY['stable_semitones_per_second'] * hop_seconds)
    longest = trailing = stable_run = longest_stable = 0
    for i, good in enumerate(voiced):
        trailing = trailing + 1 if good and (i == 0 or contiguous[i - 1]) else int(good)
        stable_run = stable_run + 1 if i and stable[i - 1] else int(good)
        longest = max(longest, trailing)
        longest_stable = max(longest_stable, stable_run)
    pitches = midi[voiced]
    half = decision_end - window_samples / 2
    earlier = voiced & (availability <= half)
    recent = voiced & (availability > half)
    relative = float(np.median(midi[recent]) - np.median(midi[earlier])) if np.any(earlier) and np.any(recent) else 0.
    slope = 0.
    if len(pitches) >= 2:
        times = availability[voiced].astype(np.float64) / SAMPLE_RATE
        times -= times.mean()
        slope = float(times @ (pitches - pitches.mean()) / max(times @ times, 1e-12))
    fresh = decision_end - availability[-1] < hop_samples
    duration = max(hop_seconds, min(window_samples, n * hop_samples) / SAMPLE_RATE)
    return np.asarray([
        min(1., n * hop_samples / window_samples), float(voiced.mean()),
        float(confidence.mean()), float(confidence.std()), float(voiced[-1] and fresh),
        float(np.percentile(pitches, 75) - np.percentile(pitches, 25)) if len(pitches) else 0.,
        float(np.percentile(pitches, 90) - np.percentile(pitches, 10)) if len(pitches) else 0.,
        float(np.median(delta)) if len(delta) else 0.,
        float(np.percentile(delta, 90)) if len(delta) else 0.,
        float(stable.sum() / adjacent.sum()) if adjacent.any() else 0.,
        longest * hop_seconds, trailing * hop_seconds if fresh else 0., longest_stable * hop_seconds,
        relative, slope, float(np.sum((voiced[:-1] != voiced[1:]) & contiguous) / duration),
    ], dtype=np.float64)


def build_trailing_features(track, decision_end_samples, *, window_samples=16000):
    """Return rows as of each original-audio endpoint, never a centered window.

    Precomputed later frames are permitted in ``track`` but cannot participate
    in an earlier row. Integer endpoints may equal zero (an all-zero row), and
    cannot exceed received PCM.
    """
    if (track.get('schema_version') != SCHEMA_VERSION or track.get('causal') is not True
            or track.get('sample_rate') != SAMPLE_RATE):
        raise PitchError('causal 16 kHz track metadata required')
    if type(window_samples) is not int or window_samples <= 0:
        raise PitchError('positive window_samples required')
    if (type(track.get('sample_count')) is not int or track['sample_count'] < 0
            or type(track.get('hop_samples')) is not int or track['hop_samples'] <= 0
            or type(track.get('window_samples')) is not int or track['window_samples'] <= 0):
        raise PitchError('invalid track sample coordinates')
    frames = track['frames']
    for frame in frames:
        _validate_frame(frame, track)
    available = np.asarray([f['available_sample'] for f in frames], dtype=np.int64)
    if len(available) and (available[0] <= 0 or np.any(np.diff(available) <= 0)
                           or available[-1] > track['sample_count']):
        raise PitchError('invalid pitch frame availability sequence')
    rows = []
    for raw_end in decision_end_samples:
        if not isinstance(raw_end, (int, np.integer)) or isinstance(raw_end, (bool, np.bool_)):
            raise PitchError('decision endpoints must be integer original samples')
        end = int(raw_end)
        if not 0 <= end <= track['sample_count']:
            raise PitchError('decision endpoint outside observed audio')
        left, right = np.searchsorted(available, [end - window_samples, end], side='right')
        rows.append(_summarize(frames[left:right], end, hop_samples=track['hop_samples'], window_samples=window_samples))
    return np.asarray(rows, dtype=np.float64).reshape((-1, len(FEATURE_NAMES)))


def _validate_frame(frame, metadata):
    for key in ('frame_start_sample', 'frame_end_sample', 'observed_start_sample', 'available_sample'):
        if type(frame.get(key)) is not int:
            raise PitchError('pitch frame coordinates must be integer original samples')
    start, end = frame['frame_start_sample'], frame['frame_end_sample']
    if (end <= 0 or end != frame['available_sample']
            or start != end - metadata['window_samples']
            or frame['observed_start_sample'] != max(0, start)
            or frame.get('center_sample') != (start + end) / 2.):
        raise PitchError('pitch support/center/availability mismatch')
    if type(frame.get('voiced')) is not bool:
        raise PitchError('pitch voiced flag must be boolean')
    for key in ('f0_hz', 'confidence', 'rms'):
        value = frame.get(key)
        if not isinstance(value, (float, int)) or not np.isfinite(value) or value < 0:
            raise PitchError('invalid pitch evidence')
    if (frame['confidence'] > 1 or (frame['voiced'] and frame['f0_hz'] <= 0)
            or (not frame['voiced'] and frame['f0_hz'] != 0)):
        raise PitchError('invalid voicing evidence')


class TrailingPitchAccumulator:
    """Bounded online feature history; query at the current received endpoint.

    Feed each returned frame once in order, then call features_at(received end).
    It rejects historical queries after history was discarded. Offline replay
    uses build_trailing_features, whose summarizer is exactly the same function.
    """
    def __init__(self, metadata, *, stream_id, window_samples=16000):
        if metadata.get('schema_version') != SCHEMA_VERSION or metadata.get('causal') is not True:
            raise PitchError('causal metadata required')
        if type(window_samples) is not int or window_samples <= 0:
            raise PitchError('positive window_samples required')
        self.metadata = dict(metadata)
        self.stream_id = stream_id
        self.window_samples = window_samples
        self.frames = deque()
        self.last_available = 0
        self.last_query = 0

    def push(self, frames, *, stream_id):
        if stream_id != self.stream_id:
            raise PitchError('feature accumulator stream identity mismatch')
        # Validate the whole packet before changing retained history.
        previous = self.last_available
        for frame in frames:
            _validate_frame(frame, self.metadata)
            available = frame['available_sample']
            if available <= previous:
                raise PitchError('duplicate or nonmonotonic frame')
            previous = available
        for frame in frames:
            available = frame['available_sample']
            self.frames.append(dict(frame))
            self.last_available = available
            while self.frames and self.frames[0]['available_sample'] <= available - self.window_samples:
                self.frames.popleft()

    def features_at(self, decision_end):
        if type(decision_end) is not int or decision_end < max(self.last_available, self.last_query):
            raise PitchError('bounded accumulator requires a current monotonic endpoint')
        self.last_query = decision_end
        while self.frames and self.frames[0]['available_sample'] <= decision_end - self.window_samples:
            self.frames.popleft()
        return _summarize(list(self.frames), decision_end, hop_samples=self.metadata['hop_samples'], window_samples=self.window_samples)
