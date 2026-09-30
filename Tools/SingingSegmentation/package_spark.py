"""Package only service code and explicitly supplied PANNs assets, never user data."""
import argparse
import hashlib
import json
from pathlib import Path
import tarfile
import io


def main():
    project = Path(__file__).resolve().parents[2]
    assets = project / 'work/singing-subsystem-20260929/segmentation'
    parser = argparse.ArgumentParser()
    parser.add_argument('--checkpoint', type=Path, default=assets / 'models/Cnn14_DecisionLevelMax_mAP=0.385.pth')
    parser.add_argument('--labels', type=Path, default=assets / 'models/class_labels_indices.csv')
    parser.add_argument('--dependencies', type=Path, default=assets / 'deps')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    files = {}
    for source in (project / 'Server/SingingLearning').glob('*.py'):
        if not source.name.startswith('test_') and source.name != 'proposal_smoke.py':
            files[source.relative_to(project).as_posix()] = source
    for relative in ('Server/SingingLearning/assets/bootstrap-panns-development.json',
                     'Server/SenseVoice/singing_segmentation.py', 'Tools/SingingSegmentation/fit_score_adapter.py'):
        files[relative] = project / relative
    files.update({'models/panns.pth': args.checkpoint, 'models/class_labels_indices.csv': args.labels})
    for name in ('models.py', 'pytorch_utils.py'):
        files['deps/panns_inference/' + name] = args.dependencies / 'panns_inference' / name
    records = []
    for relative, source in sorted(files.items()):
        if not source.is_file():
            raise FileNotFoundError(source)
        records.append(dict(path=relative, sha256=hashlib.sha256(source.read_bytes()).hexdigest(), bytes=source.stat().st_size))
    checkpoint_sha = next(row['sha256'] for row in records if row['path'] == 'models/panns.pth')
    if checkpoint_sha != 'dd3b4043a87d4ec13df8082c0fcfee3fb5084151808e47e060987a95eabdd142':
        raise ValueError('This service requires the frozen DecisionLevelMax checkpoint; wrong SHA256')
    manifest = dict(schema='neeeva.spark-learning-release.v1', files=records,
                    total_bytes=sum(row['bytes'] for row in records), includes_user_recordings=False)
    manifest['release_sha256'] = hashlib.sha256(json.dumps(records, sort_keys=True).encode()).hexdigest()
    args.output.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8')
    with tarfile.open(args.output / 'release.tar', 'w') as archive:
        for relative, source in sorted(files.items()):
            archive.add(source, arcname=relative, recursive=False)
        info = tarfile.TarInfo('manifest.json'); info.size = len(encoded)
        archive.addfile(info, io.BytesIO(encoded))
    (args.output / 'manifest.json').write_bytes(encoded)
    print(json.dumps(dict(release_sha256=manifest['release_sha256'], bytes=manifest['total_bytes'], files=len(records))))


if __name__ == '__main__':
    main()
