"""Generate paired teacher/student motions, or an explicitly unconditioned core smoke test."""
import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np
import torch

from .adapter import load_adapter
from .data import read_bundle, read_records, records_hash


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--dataset', type=Path)
    p.add_argument('--teacher', type=Path)
    p.add_argument('--qwen', type=Path)
    p.add_argument('--adapter', type=Path)
    p.add_argument('--smoke-unconditioned', action='store_true')
    p.add_argument('--count', type=int, default=10)
    p.add_argument('--seed', type=int, default=0)
    p.add_argument('--device', default='cuda')
    args = p.parse_args()
    if args.count < 1:
        p.error('--count must be positive')
    if not args.smoke_unconditioned and (args.dataset is None or args.teacher is None):
        p.error('Teacher baseline needs --dataset and --teacher')
    if bool(args.qwen) != bool(args.adapter):
        p.error('--qwen and --adapter must be provided together')
    if args.smoke_unconditioned and any([args.teacher, args.dataset, args.qwen, args.adapter]):
        p.error('--smoke-unconditioned cannot be combined with conditioned inputs')
    from ardy.model import load_model
    from ardy.tools import seed_everything
    from huggingface_hub import snapshot_download
    lock = json.loads((Path(__file__).parent/'upstream-lock.json').read_text(encoding='utf-8-sig'))
    cache = Path(__file__).resolve().parents[2]/'Server/ARDY/models/hf-cache/hub'
    snapshot = Path(snapshot_download(repo_id=lock['ardy_core_repo'], revision=lock['ardy_core_revision'],
                                      cache_dir=cache))
    model = load_model(snapshot.name, checkpoints_dir=str(snapshot.parent), device=args.device, text_encoder=False)
    if model.text_encoder is not None:
        raise RuntimeError('This baseline must never load a language model')
    if args.device.startswith('cuda'):
        torch.cuda.reset_peak_memory_stats()
    args.output.mkdir(parents=True, exist_ok=True)
    if args.smoke_unconditioned:
        rows = [{'id':'unconditioned-smoke','text':'','split':'test'}]
        teacher = np.zeros((1,4096), dtype=np.float32)
        qm, qwen, student = None, None, None
    else:
        rows = read_records(args.dataset)
        teacher, tm = read_bundle(args.teacher, rows, 'teacher')
        student = None
        if args.adapter:
            qwen, qm = read_bundle(args.qwen, rows, 'qwen')
            student, checkpoint = load_adapter(args.adapter, args.device, qm['feature_contract'])
            if checkpoint['teacher_contract'] != tm['feature_contract']:
                raise ValueError('Teacher checkpoint contract mismatch')
    selected = [i for i,r in enumerate(rows) if r['split']=='test'][:args.count]
    if not selected:
        raise ValueError('Dataset has no held-out test descriptions')
    reports = []
    for index in selected:
        conditions = {'teacher':torch.from_numpy(teacher[index]).to(args.device)}
        if args.smoke_unconditioned:
            conditions = {'unconditioned':conditions['teacher']}
        elif student is not None:
            with torch.no_grad():
                conditions['student'] = student(torch.from_numpy(qwen[index:index+1]).to(args.device))[0]
        for name, feature in conditions.items():
            seed_everything(args.seed)
            if args.device.startswith('cuda'):
                torch.cuda.synchronize()
            start = time.perf_counter()
            mask = torch.tensor([[not args.smoke_unconditioned]], device=args.device)
            with torch.inference_mode():
                motion = model.autoregressive_step(num_frames=model.gen_horizon_len,
                    num_denoising_steps=int(model.diffusion.num_base_steps), motion_mask=None,
                    observed_motion=None, cfg_weight=0.0 if args.smoke_unconditioned else (2.0,2.0),
                    text_feat=feature.reshape(1,1,4096), text_pad_mask=mask)
                output = model.motion_rep.inverse(motion, is_normalized=True)
            if args.device.startswith('cuda'):
                torch.cuda.synchronize()
            elapsed = (time.perf_counter()-start)*1000
            # One clip per file, matching the upstream visualize.py [T,J,...] convention.
            arrays = {k:v[0].detach().float().cpu().numpy() for k,v in output.items() if torch.is_tensor(v)}
            if not arrays or any(not np.isfinite(v).all() for v in arrays.values()):
                raise RuntimeError('Invalid generated motion')
            path = args.output/f"{rows[index]['id']}-{name}.npz"
            np.savez(path, **arrays, fps=model.motion_rep.fps, text=rows[index]['text'],
                     joint_names=np.asarray(model.skeleton.bone_order_names),
                     joint_parents=model.skeleton.joint_parents.cpu().numpy())
            reports.append({'id':rows[index]['id'],'condition':name,'milliseconds':elapsed,
                            'shapes':{k:list(v.shape) for k,v in arrays.items()}})
    report = {'schema':1,'text_encoder_loaded':False,'postprocessing':False,'seed':args.seed,
              'condition_sources':None if args.smoke_unconditioned else {
                  'dataset_sha256':records_hash(rows), 'teacher_contract':tm['feature_contract'],
                  'qwen_contract':qm['feature_contract'] if args.adapter else None,
                  'adapter':args.adapter.name if args.adapter else None,
                  'adapter_sha256':hashlib.sha256(args.adapter.read_bytes()).hexdigest() if args.adapter else None},
              'ardy_revision':lock['ardy'], 'core_repo':lock['ardy_core_repo'],
              'core_revision':lock['ardy_core_revision'], 'fps':model.motion_rep.fps,
              'num_denoising_steps':int(model.diffusion.num_base_steps),
              'parameter_count':sum(p.numel() for p in model.parameters()),
              'peak_torch_allocated_mib':torch.cuda.max_memory_allocated()/2**20 if args.device.startswith('cuda') else None,
              'smoke_only':args.smoke_unconditioned,'results':reports,
              'limitation':'Unconditioned smoke proves runtime only.' if args.smoke_unconditioned else
                           'Cold-start unconstrained clips only; visual review and streaming tests remain required.'}
    (args.output/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps({'clips':len(reports),'text_encoder_loaded':False,'report':str(args.output/'report.json')}))


if __name__ == '__main__':
    main()
