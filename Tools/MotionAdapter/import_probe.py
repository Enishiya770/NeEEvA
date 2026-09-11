import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from .data import read_records, write_bundle


def import_export(directory, output):
    directory = Path(directory)
    manifest = json.loads((directory/'export-manifest.json').read_text(encoding='utf-8-sig'))
    if manifest.get('schema') != 1:
        raise ValueError('Unsupported export manifest')
    for filename, key in [('prompts.txt', 'prompts_sha256'), ('features.f32', 'features_sha256'),
                          ('report.json', 'report_sha256'), ('provenance.json', 'provenance_sha256')]:
        actual = hashlib.sha256((directory/filename).read_bytes()).hexdigest()
        if actual != manifest[key].lower():
            raise ValueError(f'Export file hash mismatch: {filename}')
    report = json.loads((directory/'report.json').read_text(encoding='utf-8-sig'))
    provenance = json.loads((directory/'provenance.json').read_text(encoding='utf-8-sig'))
    rows = read_records(directory/'dataset.jsonl')
    if (directory/'prompts.txt').read_text(encoding='utf-8').splitlines() != [r['text'] for r in rows]:
        raise ValueError('Exported prompts do not match dataset text/order')
    if (report.get('schema') != 1 or report.get('feature_contract') != 'qwen-motion-raw-last-v1'
            or report['rows'] != len(rows) or report['dimension'] != 2048
            or report['chat_interleave_equal'] is not True):
        raise ValueError('Native probe did not pass or input contract changed')
    if (report['model_load_count'] != 1 or report['context_count'] != 2
            or not 0 <= report['repeat_max_abs'] <= 1e-4):
        raise ValueError('Single-weight/repeatability requirement failed')
    features = np.fromfile(directory/'features.f32', dtype='<f4').reshape(len(rows), 2048)
    provenance['probe_exe_sha256'] = manifest['probe_exe_sha256'].lower()
    contract = hashlib.sha256(json.dumps(provenance, sort_keys=True).encode()).hexdigest()
    write_bundle(output, features, rows, {'kind':'qwen', 'feature_contract':contract,
                 'provenance':provenance, 'native_report':report, 'export_manifest':manifest})
    return {'rows':len(rows), 'feature_contract':contract}


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--directory', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    print(json.dumps(import_export(args.directory, args.output)))


if __name__ == '__main__':
    main()
