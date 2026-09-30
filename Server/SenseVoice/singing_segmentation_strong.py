"""Isolated AudioSet Strong FrameMN baseline. Candidate-only; no cut authority."""
from __future__ import annotations
import contextlib
import hashlib
import io
import math
from pathlib import Path
import sys
import time
from dataclasses import asdict

import numpy as np
from singing_segmentation import CandidatePolicy, LABELS, candidate_timeline, file_hash

CHECKPOINTS={
    '5b8be0664ee194c0413010ae0c6886560f4fd98a9ca1a0c2825e409dfdf33472':('frame_mn06',.6),
    '4a0fe320d5369987b772394c51881fb20a602967e6842f72f3e5c8181065ece7':('frame_mn10',1.0),
}


class StrongFramewiseProvider:
    def __init__(self, checkpoint, upstream_code, dependency_dir, device='cuda'):
        started=time.perf_counter()
        checkpoint=Path(checkpoint).resolve();upstream_code=Path(upstream_code).resolve()
        digest=file_hash(checkpoint)
        if digest not in CHECKPOINTS:
            raise ValueError('checkpoint_sha256_mismatch')
        self.variant,width=CHECKPOINTS[digest]
        sys.path.insert(0,str(Path(dependency_dir).resolve()))
        sys.path.insert(0,str(upstream_code))
        import torch
        from models.frame_mn.Frame_MN_wrapper import FrameMNWrapper
        from models.prediction_wrapper import PredictionsWrapper
        from data_util.audioset_classes import as_strong_train_classes
        self.torch=torch;self.device=device
        if device=='cuda' and not torch.cuda.is_available():
            raise RuntimeError('cuda_requested_but_unavailable')
        with contextlib.redirect_stdout(io.StringIO()):
            base=FrameMNWrapper(width)
        embed=base.state_dict()['frame_mn.features.16.1.bias'].shape[0]
        self.model=PredictionsWrapper(base,embed_dim=embed)
        self.model.load_state_dict(torch.load(checkpoint,map_location='cpu',weights_only=True),strict=True)
        self.model.to(device).eval().requires_grad_(False)
        self.all_labels=list(as_strong_train_classes)
        if len(self.all_labels)!=447:
            raise ValueError('strong_label_count_mismatch')
        self.labels=[label for label in LABELS if label in self.all_labels]
        self.indices=[self.all_labels.index(label) for label in self.labels]
        self.version=digest
        self.load_seconds=time.perf_counter()-started
        self.code_manifest_sha256=file_hash(upstream_code/'source-manifest.json')

    def analyze(self,audio_path,policy=CandidatePolicy(),cancelled=None):
        import soundfile as sf
        from scipy.signal import resample_poly
        started=time.perf_counter()
        if cancelled and cancelled():return {'status':'cancelled','execution_authorized':False},None
        raw=Path(audio_path).read_bytes()
        audio,sr=sf.read(io.BytesIO(raw),dtype='float32',always_2d=True)
        count,channels=audio.shape
        if count<=0 or count/sr>120 or not np.isfinite(audio).all():
            raise ValueError('invalid_audio')
        source={'audio_sha256':hashlib.sha256(raw).hexdigest(),'sample_rate':sr,
                'sample_count':count,'channels':channels,'coordinate_origin_sample':0,
                'sample_range_convention':'half-open'}
        divisor=math.gcd(sr,16000)
        waveform=resample_poly(audio.mean(axis=1),16000//divisor,sr//divisor).astype(np.float32)
        torch=self.torch
        if self.device=='cuda':torch.cuda.synchronize();torch.cuda.reset_peak_memory_stats()
        infer_started=time.perf_counter();chunks=[]
        with torch.inference_mode():
            for start in range(0,len(waveform),160000):
                if cancelled and cancelled():return {'status':'cancelled','source':source,'execution_authorized':False},None
                chunk=waveform[start:start+160000]
                chunk=np.pad(chunk,(0,160000-len(chunk)))
                logits,_=self.model(self.model.mel_forward(torch.from_numpy(chunk[None]).to(self.device)))
                chunks.append(torch.sigmoid(logits)[0].T.float().cpu().numpy())
        if self.device=='cuda':torch.cuda.synchronize()
        infer_seconds=time.perf_counter()-infer_started
        if cancelled and cancelled():return {'status':'cancelled','source':source,'execution_authorized':False},None
        full=np.concatenate(chunks,axis=0)[:math.ceil(len(waveform)/640)]
        selected=full[:,self.indices]
        segments,transitions=candidate_timeline(selected,self.labels,source,self.version,policy,
            cell_analysis_samples=640,analysis_rate=16000,context_support_seconds=10.0)
        result={'schema':1,'status':'candidate','provider':'PretrainedSED-'+self.variant+'-strong',
            'model_version':self.version,'source':source,'execution_authorized':False,'supported_range':None,
            'model_metadata':{'training':'AudioSet Strong frame annotations',
                'upstream_source_manifest_sha256':self.code_manifest_sha256,
                'analysis_sample_rate':16000,'output_grid_seconds':.04,'native_cell_seconds':.04,
                'theoretical_receptive_field_seconds':None,'global_context_seconds':10.0,
                'chunk_seconds':10.0,'chunk_overlap_seconds':0.0,
                'global_context_note':'Channel squeeze-excitation depends on chunk-wide statistics; 40 ms output does not establish 40 ms boundary accuracy.',
                'calibrated':False,'mutually_exclusive_classes':False},
            'policy':asdict(policy),'segments':segments,'transitions':transitions,
            'timeline':{'columns':self.labels,'scores':selected.tolist(),'analysis_cell_samples':640,
                'coordinate_sample_rate':sr,'source_start_samples':[min(count,(i*640*sr+8000)//16000) for i in range(len(selected))]},
            'timing':{'model_load_seconds':self.load_seconds,'inference_seconds':infer_seconds,
                'total_seconds':time.perf_counter()-started,
                'peak_cuda_allocated_bytes':torch.cuda.max_memory_allocated() if self.device=='cuda' else None,
                'device':self.device},
            'scope':'Independent framewise event candidates only. Official 10-second inference chunks. No ASR, transcripts, prior crops, human timing labels or cut authorization.'}
        return result,full
