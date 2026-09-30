"""Source-bound 20 ms evidence for teaching sequence candidates.

The waveform and pitch columns are causal at each row's *end*. PANNs columns
remain frozen whole-recording evidence, repeated from the native cell containing
the row's start. A denser output grid does not improve PANNs' temporal resolution
or make the complete feature vector causal. No feature is a singing decision.
"""
from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path

import numpy as np

from . import pitch_features as pitch


SCHEMA = 'neeeva.sequence-features.v1'
SAMPLE_RATE = 16000
HOP_SAMPLES = 320
RAW_WINDOW_SAMPLES = 640
PITCH_WINDOWS = (1280, 5120, 16000)
RAW_NAMES = ('waveform_rms_40ms', 'waveform_log10_rms_40ms',
             'waveform_zero_crossings_40ms', 'waveform_spectral_flatness_40ms',
             'waveform_positive_spectral_flux_40ms', 'waveform_log10_rms_change')


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                    allow_nan=False).encode()).hexdigest()


def _audio(audio):
    values = np.asarray(audio)
    if (values.ndim != 1 or values.dtype.kind != 'f' or not len(values)
            or not np.isfinite(values).all() or np.any(np.abs(values) > 1.000001)):
        raise ValueError('sequence_requires_nonempty_normalized_finite_mono_float_pcm')
    return np.ascontiguousarray(values, dtype='<f4')


def _audio_identity(audio):
    # Canonical float32 is exact for source PCM16 / 32768. The PCM hash is only
    # available if quantization reproduces every input value; arbitrary floating
    # audio cannot borrow the identity of an approximately similar PCM recording.
    result = dict(pcm_float32_sha256=hashlib.sha256(audio.tobytes()).hexdigest(),
                  sample_rate=SAMPLE_RATE, channels=1, sample_count=int(len(audio)))
    scaled = audio.astype(np.float64) * 32768.
    if np.all(scaled == np.rint(scaled)) and np.all(scaled >= -32768) and np.all(scaled <= 32767):
        result['pcm_sha256'] = hashlib.sha256(scaled.astype('<i2').tobytes()).hexdigest()
    return result


def make_pitch_track(audio, *, stream_id):
    """Replay the established causal FFT extractor with explicit source binding."""
    audio = _audio(audio)
    track = pitch.extract_track(audio, stream_id=stream_id, provider='fft')
    track['source'] = _audio_identity(audio)
    return track


def _validate_provider(track):
    if (not isinstance(track, dict) or track.get('provider') != 'fft'
            or track.get('schema_version') != pitch.SCHEMA_VERSION
            or track.get('provider_identity') != pitch.provider_identity('fft')):
        raise ValueError('sequence_requires_current_causal_fft_provider')
    expected = pitch.FFTProvider().metadata
    if any(track.get(key) != value for key, value in expected.items()):
        raise ValueError('sequence_pitch_provider_definition_mismatch')


def current_definition(acoustic_metadata, pitch_track):
    """Stable feature definition for head compatibility and disk-cache checks.

    Source hashes and row clocks deliberately live outside this definition;
    two recordings must share the same definition to train a common head.
    """
    _validate_provider(pitch_track)
    names = acoustic_metadata.get('names') if isinstance(acoustic_metadata, dict) else None
    if (not isinstance(names, (list, tuple)) or len(names) != 9
            or any(not isinstance(name, str) or not name for name in names)
            or len(set(names)) != len(names)):
        raise ValueError('sequence_requires_nine_named_frozen_acoustic_features')
    acoustic = copy.deepcopy(acoustic_metadata)
    # Reject unhashable/nonfinite definition values before using them in a cache.
    _digest(acoustic)
    pitch_names = [f'{name}_trailing_{round(window / 16)}ms'
                   for window in PITCH_WINDOWS for name in pitch.FEATURE_NAMES]
    definition = dict(schema=SCHEMA, names=list(names) + pitch_names + list(RAW_NAMES),
        sample_rate=SAMPLE_RATE, hop_samples=HOP_SAMPLES,
        grid='0:320:sample_count plus exact last sample; half-open original PCM cells',
        tail_policy='preserve exact observed tail; no synthetic audio samples',
        acoustic=dict(metadata=acoustic, causality='frozen_whole_recording_noncausal',
            mapping='repeat_native_cell_containing_output_start_sample',
            interpolation=False, resolution='unchanged_native_acoustic_cells'),
        pitch=dict(provider_identity=pitch_track['provider_identity'],
            provider_metadata=copy.deepcopy(pitch.FFTProvider().metadata),
            feature_policy=copy.deepcopy(pitch.FEATURE_POLICY),
            windows_samples=list(PITCH_WINDOWS),
            availability='only completed pitch frames available at or before row_end_sample',
            extractor_source_sha256=hashlib.sha256(Path(pitch.__file__).read_bytes()).hexdigest()),
        waveform=dict(window_samples=RAW_WINDOW_SAMPLES, causal=True,
            window='observed samples [max(0,row_end-640),row_end)',
            amplitude_normalization='none', rms='uncentered observed source waveform',
            log10_rms_floor=1e-8, zero_crossings='local-DC-removed adjacent sign changes / (N-1)',
            spectrum='local-DC-removed Hann window; nfft1024 numerical FFT zero extension only',
            flatness='geometric/arithmetic mean power over bins excluding DC; silence zero',
            spectral_flux='L2 norm positive change of unit-L2 magnitude spectra versus preceding row',
            energy_change='current log10 RMS minus preceding row; first row zero'),
        extractor_source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        scope='mixed: frozen whole-recording PANNs plus causal local waveform and multiscale pitch',
        fully_causal=False, execution_authorized=False)
    if len(set(definition['names'])) != len(definition['names']):
        raise ValueError('sequence_feature_names_must_be_unique')
    return definition


def _validate_source(audio, track):
    actual = _audio_identity(audio)
    source = track.get('source') or {}
    if (type(track.get('sample_count')) is not int or track['sample_count'] != len(audio)
            or not isinstance(track.get('stream_id'), str) or not track['stream_id']):
        raise ValueError('sequence_pitch_original_clock_mismatch')
    for key in ('sample_count', 'sample_rate', 'channels'):
        if key in source and (type(source[key]) is not int or source[key] != actual[key]):
            raise ValueError('sequence_pitch_source_coordinates_mismatch')
    bindings = [key for key in ('pcm_sha256', 'pcm_float32_sha256') if key in source]
    if not bindings or any(source[key] != actual.get(key) for key in bindings):
        raise ValueError('sequence_pitch_source_content_mismatch')
    actual['stream_id'] = track['stream_id']
    if 'audio_sha256' in source:
        # A WAV container hash cannot be recomputed from decoded PCM alone.
        # Preserve its provenance, but never claim it was checked here.
        actual['declared_wav_sha256'] = source['audio_sha256']
        actual['wav_binding_verification'] = 'caller_must_verify_original_container'
    return actual


def _waveform_features(audio, ends):
    rows = []
    previous_spectrum, previous_log = None, None
    for end in ends:
        frame = audio[max(0, int(end) - RAW_WINDOW_SAMPLES):int(end)].astype(np.float64)
        rms = float(np.sqrt(np.mean(frame * frame)))
        log_rms = float(np.log10(max(rms, 1e-8)))
        centered = frame - frame.mean()
        zcr = float(np.count_nonzero(np.signbit(centered[1:]) != np.signbit(centered[:-1])) / (len(frame) - 1)) if len(frame) > 1 else 0.
        window = np.hanning(len(frame)) if len(frame) > 2 else np.ones(len(frame))
        spectrum = np.abs(np.fft.rfft(centered * window, n=1024))[1:]
        power = spectrum * spectrum
        if float(power.sum()) <= 1e-24:
            normalized = np.zeros_like(spectrum)
            flatness = 0.
        else:
            floor = max(float(power.max()) * 1e-12, 1e-300)
            flatness = float(np.clip(np.exp(np.mean(np.log(np.maximum(power, floor)))) / power.mean(), 0., 1.))
            normalized = spectrum / np.sqrt(float(power.sum()))
        flux = float(np.linalg.norm(np.maximum(0., normalized - previous_spectrum))) if previous_spectrum is not None else 0.
        change = log_rms - previous_log if previous_log is not None else 0.
        rows.append((rms, log_rms, zcr, flatness, flux, change))
        previous_spectrum, previous_log = normalized, log_rms
    return np.asarray(rows, dtype=np.float64)


def build_features(audio, acoustic_edges, acoustic_features, acoustic_metadata, pitch_track):
    """Return ``(edges, X, metadata)`` with exact source clocks and 63 columns.

    ``metadata['definition']`` is shared across recordings and is the metadata
    to bind a fitted head. The rest of ``metadata`` binds this particular source,
    frozen acoustic input and pitch evidence. Cached FFT tracks require a matching
    source PCM SHA; use :func:`make_pitch_track` for new floating-point input.
    """
    audio = _audio(audio)
    definition = current_definition(acoustic_metadata, pitch_track)
    source = _validate_source(audio, pitch_track)
    native_edges = np.asarray(acoustic_edges)
    values = np.asarray(acoustic_features, dtype=np.float64)
    if (native_edges.ndim != 1 or native_edges.dtype.kind not in 'iu' or len(native_edges) < 2
            or native_edges[0] != 0 or native_edges[-1] != len(audio)
            or np.any(np.diff(native_edges.astype(np.int64)) <= 0)):
        raise ValueError('sequence_invalid_original_acoustic_edges')
    native_edges = native_edges.astype(np.int64)
    if values.shape != (len(native_edges) - 1, 9) or not np.isfinite(values).all():
        raise ValueError('sequence_invalid_frozen_acoustic_values')
    edges = np.r_[np.arange(0, len(audio), HOP_SAMPLES, dtype=np.int64), len(audio)]
    indices = np.searchsorted(native_edges, edges[:-1], side='right') - 1
    repeated = values[indices]
    trailing = [pitch.build_trailing_features(pitch_track, edges[1:], window_samples=window)
                for window in PITCH_WINDOWS]
    raw = _waveform_features(audio, edges[1:])
    features = np.column_stack((repeated, *trailing, raw))
    if not np.isfinite(features).all():
        raise ValueError('sequence_nonfinite_features')
    metadata = dict(schema=SCHEMA, definition=definition, definition_sha256=_digest(definition),
        source=source, source_binding_sha256=_digest(source),
        acoustic_input_sha256=_digest(dict(edges=native_edges.tolist(), features=values.tolist(),
                                         metadata=acoustic_metadata)),
        pitch_track_sha256=_digest(pitch_track),
        timeline=dict(coordinate_sample_rate=SAMPLE_RATE, source_start_samples=edges[:-1].tolist(),
            source_end_samples=edges[1:].tolist(), local_features_available_samples=edges[1:].tolist(),
            combined_features_available_samples=[int(len(audio))] * (len(edges) - 1),
            combined_availability_clock='recording EOF lower bound; extractor compute latency excluded',
            acoustic_native_cell_indices=indices.tolist(),
            acoustic_features_available_after='whole_recording_extraction',
            combined_availability='not_streaming_due_to_whole_recording_acoustics'),
        feature_values_sha256=hashlib.sha256(np.asarray(features, dtype='<f8').tobytes()).hexdigest(),
        execution_authorized=False)
    return edges, features, metadata
