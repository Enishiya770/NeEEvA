"""Export or resume exact production Qwen features without loading another model."""
from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path
import time
import uuid

import numpy as np

from Server.ARDY.motion_service.protocol import FEATURE_CONTRACT, QWEN_MODEL_SHA256, GenerateRequest, ServiceError
from Server.ARDY.motion_service.service import LiveFeatureProvider
from .data import read_bundle, read_records, records_hash, write_bundle


async def export(args):
    rows = read_records(args.dataset)
    if any(len(r['text']) > 240 for r in rows):
        raise ValueError('Descriptions must fit the actual 240-character motion request contract')
    if args.output.exists():
        raise FileExistsError('Refusing to replace an existing final feature bundle')
    progress = args.output.with_suffix('.partial.npz')
    report_path = args.output.with_suffix('.export.json')
    session = uuid.uuid4().hex
    provenance = {'source': 'production-shared-qwen-http', 'base_url': args.url,
                  'model_sha256': QWEN_MODEL_SHA256, 'context_tokens': 512,
                  'pooling': 'last_input_token_raw', 'shared_model': True,
                  'private_dialogue_used': False}
    meta = {'kind': 'qwen', 'feature_contract': FEATURE_CONTRACT, 'provenance': provenance}
    features, times = [], []
    output_completed = False
    if progress.exists():
        if not args.resume:
            raise FileExistsError('Partial export exists; use --resume to validate and continue it')
        with np.load(progress, allow_pickle=False) as bundle:
            count = len(bundle['ids'])
        previous, prior_meta = read_bundle(progress, rows[:count], 'qwen')
        if prior_meta['feature_contract'] != FEATURE_CONTRACT or prior_meta['provenance'] != provenance:
            raise ValueError('Partial export belongs to another model/endpoint/feature contract')
        features.extend(previous)
    resumed = len(features)
    provider = LiveFeatureProvider(args.url)
    start = time.perf_counter()

    def checkpoint():
        if features:
            temporary = progress.with_suffix('.writing.npz')
            write_bundle(temporary, np.stack(features), rows[:len(features)], meta)
            temporary.replace(progress)
        report = {'status': 'complete' if output_completed else 'partial',
                  'rows': len(rows), 'completed': len(features), 'resumed_rows': resumed,
                  'records_sha256': records_hash(rows), 'session': session,
                  'elapsed_seconds': time.perf_counter() - start,
                  'feature_contract': FEATURE_CONTRACT, 'provenance': provenance,
                  'new_request_seconds': {'median': float(np.median(times)) if times else None,
                                          'p95': float(np.percentile(times, 95)) if times else None},
                  'limitation': 'Offline feature export timings, not full dialogue and voice concurrency latency.'}
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')

    try:
        for i in range(resumed, len(rows)):
            row = rows[i]
            request = GenerateRequest(characterId='offline-feature-export', turnId=session,
                revision=1, requestId=session + '-' + row['id'], chunkIndex=0, description=row['text'])
            begun = time.perf_counter()
            for retry in range(3):
                try:
                    result = await provider.fetch(request, 45)
                    break
                except ServiceError as error:
                    if error.status not in (429, 503, 504) or retry == 2:
                        raise
                    await asyncio.sleep(1 + retry)
            features.append(result['embedding'])
            times.append(time.perf_counter() - begun)
            if len(features) % 32 == 0:
                checkpoint()
                print(json.dumps({'completed': len(features), 'total': len(rows),
                                  'elapsed_seconds': round(time.perf_counter()-start, 1)}), flush=True)
        write_bundle(args.output, np.stack(features), rows, meta)
        output_completed = True
    finally:
        checkpoint()
    print(json.dumps({'output': str(args.output), 'rows': len(rows), 'status': 'complete'}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dataset', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--url', default='http://127.0.0.1:8080')
    parser.add_argument('--resume', action='store_true')
    asyncio.run(export(parser.parse_args()))


if __name__ == '__main__':
    main()
