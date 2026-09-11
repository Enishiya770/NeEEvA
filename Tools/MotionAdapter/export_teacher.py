"""Export official LLM2Vec vectors once; requires authorized access to its base model."""
import argparse
import hashlib
import json
import os
from importlib.metadata import version
from pathlib import Path

import numpy as np

from .data import read_records, write_bundle


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--dataset', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--device', default='cuda')
    args = p.parse_args()
    rows = read_records(args.dataset)
    # Keep weights in the project while preserving the normal hf auth login token location.
    cache = Path(__file__).resolve().parents[2]/'Server/ARDY/models/hf-cache/hub'
    os.environ.setdefault('HF_HUB_CACHE', str(cache))
    os.environ.setdefault('HUGGINGFACE_CACHE_DIR', str(cache))
    if os.environ.get('TEXT_ENCODERS_DIR'):
        raise RuntimeError('Unset TEXT_ENCODERS_DIR to export the verified upstream teacher configuration')
    from ardy.model.load_model import load_text_encoder, TEXT_ENCODER_PRESETS
    from huggingface_hub import HfApi
    api = HfApi()
    repos = ['meta-llama/Meta-Llama-3-8B-Instruct',
             'McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp',
             'McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised']
    # Checking access here avoids downloading adapter weights only to discover the gated base is unavailable.
    try:
        api.auth_check(repos[0], repo_type='model')
    except Exception:
        raise RuntimeError('Official teacher access is unavailable. Obtain access on the Meta model page and '
                           'run hf auth login locally; do not send tokens through chat. No teacher features were created.') from None
    revisions = {repo: api.model_info(repo).sha for repo in repos}
    encoder = load_text_encoder(mode='local', device=args.device)
    vectors = []
    for i, row in enumerate(rows):
        value, length = encoder(row['text'])
        if length != 1 or tuple(value.shape) != (1,4096):
            raise ValueError('Teacher feature shape changed')
        vectors.append(value[0].float().cpu().numpy())
        if (i+1) % 100 == 0:
            print(f'Encoded {i+1}/{len(rows)} teacher descriptions', flush=True)
    # Fail if an upstream revision moved while data were being exported.
    if revisions != {repo: api.model_info(repo).sha for repo in repos}:
        raise RuntimeError('Teacher repository revision changed during export; repeat with stable revisions.')
    lock = json.loads((Path(__file__).parent/'upstream-lock.json').read_text(encoding='utf-8-sig'))
    provenance = {'repos':revisions, 'ardy_revision':lock['ardy'], 'preset':TEXT_ENCODER_PRESETS['llm2vec'],
                  'device':args.device, 'pooling':'official-wrapper', 'output_scale':'raw',
                  'packages':{name:version(name) for name in ['torch','transformers','peft']},
                  'effective_model_name':encoder.model.model.config._name_or_path,
                  'pooling_mode':encoder.model.pooling_mode, 'max_length':encoder.model.max_length}
    contract = hashlib.sha256(json.dumps(provenance,sort_keys=True).encode()).hexdigest()
    write_bundle(args.output, np.stack(vectors), rows,
                 {'kind':'teacher','feature_contract':contract,'provenance':provenance})


if __name__ == '__main__':
    main()
