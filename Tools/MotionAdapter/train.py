"""Train solely on validated cached features; does not load either language model."""
import argparse
import json
import random
from pathlib import Path

import numpy as np
import torch
from torch.nn import functional as F

from .adapter import MotionAdapter
from .data import read_bundle, read_records, records_hash


def prediction_metrics(pred, target, std):
    return {"standardized_mse": ((pred-target)/std).square().mean().item(),
            "cosine": F.cosine_similarity(pred, target).mean().item(),
            "norm_ratio": (pred.norm(dim=-1)/target.norm(dim=-1).clamp_min(1e-8)).mean().item()}


def metrics(model, q, target):
    with torch.no_grad():
        return prediction_metrics(model(q), target, model.target_std)


def fit(q, target, rows, *, architecture="mlp", epochs=100, batch=256, lr=1e-4,
        patience=10, seed=0, device="cpu"):
    if epochs < 1 or batch < 1 or patience < 1 or not np.isfinite(lr) or lr <= 0:
        raise ValueError("epochs, batch, patience and lr must be positive")
    torch.manual_seed(seed)
    random.seed(seed)
    np.random.seed(seed)
    indices = {s: [i for i, r in enumerate(rows) if r['split'] == s] for s in ['train', 'val', 'test']}
    if any(not indices[s] for s in indices):
        raise ValueError("All three splits must be nonempty")
    q = torch.as_tensor(q, dtype=torch.float32, device=device)
    target = torch.as_tensor(target, dtype=torch.float32, device=device)
    model = MotionAdapter(architecture).to(device)
    train_target = target[indices['train']]
    model.target_mean.copy_(train_target.mean(0))
    model.target_std.copy_(train_target.std(0, unbiased=False).clamp_min(1e-3))
    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-2)
    best, best_state, stale, log, best_epoch = float('inf'), None, 0, [], None
    for epoch in range(epochs):
        order = torch.tensor(indices['train'], device=device)[torch.randperm(len(indices['train']), device=device)]
        model.train()
        for ids in order.split(batch):
            standardized = model.standardized(q[ids])
            prediction = standardized * model.target_std + model.target_mean
            loss = F.mse_loss(standardized, (target[ids]-model.target_mean)/model.target_std)
            loss = loss + .1 * (1-F.cosine_similarity(prediction, target[ids])).mean()
            if not torch.isfinite(loss):
                raise RuntimeError("Training produced nonfinite loss")
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            optimizer.step()
        model.eval()
        val = metrics(model, q[indices['val']], target[indices['val']])
        log.append({"epoch": epoch+1, **val})
        if epoch == 0 or (epoch+1) % 10 == 0:
            print(json.dumps(log[-1]), flush=True)
        if val['standardized_mse'] < best:
            best, stale = val['standardized_mse'], 0
            best_epoch = epoch+1
            best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
        else:
            stale += 1
        if stale >= patience:
            break
    model.load_state_dict(best_state)
    scores = {s: metrics(model, q[idx], target[idx]) for s, idx in indices.items()}
    constant = {s: prediction_metrics(model.target_mean.expand(len(idx),-1), target[idx], model.target_std)
                for s,idx in indices.items()}
    return model, {"history": log, "best_epoch":best_epoch, "scores": scores,
                   "constant_train_mean_baseline":constant, "counts": {s: len(i) for s, i in indices.items()}}


def main():
    parser = argparse.ArgumentParser()
    for name in ['dataset', 'qwen', 'teacher', 'output']:
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--architecture', choices=['linear', 'mlp'], default='mlp')
    parser.add_argument('--epochs', type=int, default=100)
    parser.add_argument('--batch', type=int, default=256)
    parser.add_argument('--patience', type=int, default=10)
    parser.add_argument('--lr', type=float, default=1e-4)
    parser.add_argument('--seed', type=int, default=0)
    parser.add_argument('--device', default='cuda' if torch.cuda.is_available() else 'cpu')
    args = parser.parse_args()
    rows = read_records(args.dataset)
    q, qm = read_bundle(args.qwen, rows, 'qwen')
    teacher, tm = read_bundle(args.teacher, rows, 'teacher')
    model, report = fit(q, teacher, rows, architecture=args.architecture, epochs=args.epochs,
                        batch=args.batch, lr=args.lr, patience=args.patience, seed=args.seed, device=args.device)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.save({"schema": 1, "architecture": args.architecture,
                "state_dict": {k: v.detach().cpu() for k, v in model.state_dict().items()},
                "qwen_contract": qm['feature_contract'], "teacher_contract": tm['feature_contract'],
                "dataset_sha256": records_hash(rows)}, args.output)
    report.update({"arguments": {k: str(v) if isinstance(v, Path) else v for k, v in vars(args).items()},
                   "dataset_sha256": records_hash(rows), "qwen_metadata": qm, "teacher_metadata": tm,
                   "limitation": "Feature metrics only; motion comparison is required before deployment."})
    args.output.with_suffix('.report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
