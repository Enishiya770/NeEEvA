import argparse
import json
from pathlib import Path

from .data import read_records


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--dataset', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--count', type=int, default=8)
    args = p.parse_args()
    rows = read_records(args.dataset)
    if not 1 <= args.count <= len(rows):
        raise ValueError('Invalid count')
    rows = rows[:args.count]
    if any('\n' in r['text'] or '\r' in r['text'] for r in rows):
        raise ValueError('Native probe needs one-line motion descriptions')
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output/'prompts.txt').write_text(''.join(r['text']+'\n' for r in rows), encoding='utf-8')
    (args.output/'dataset.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in rows), encoding='utf-8')


if __name__ == '__main__':
    main()
