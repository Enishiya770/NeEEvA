"""Generator/identity checks using synthetic PCM only; no playback or real labels."""
import argparse
import array
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import wave


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workdir', type=Path, required=True)
    args = parser.parse_args()
    out = args.workdir.resolve()
    out.mkdir(parents=True, exist_ok=True)
    here = Path(__file__).resolve().parent
    generator = here / 'build_annotation_tool.py'
    protected_dir = here.parents[1] / 'work/singing-subsystem-20260929/segmentation/annotation'
    protected = [protected_dir / name for name in ('annotate.html', 'annotate-v2.html')]
    before = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in protected if p.exists()}
    rows = []
    for i in range(7):
        pcm = array.array('h', [((n * (i + 1)) % 1000) - 500 for n in range(16000 + 123 * i)])
        if sys.byteorder != 'little':
            pcm.byteswap()
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(16000)
            wav.writeframes(pcm.tobytes())
        raw = buf.getvalue()
        path = out / ('synthetic-%d.wav' % i)
        path.write_bytes(raw)
        rows.append({'id': 'synthetic-%d' % i, 'wav': str(path),
                     'sha256': hashlib.sha256(raw).hexdigest(),
                     'context_only_not_truth': 'must-not-appear-in-annotation'})

    checks = []

    def manifest(name, values):
        path = out / name
        path.write_text(json.dumps(values, indent=2), encoding='utf-8')
        return path

    def generate(name, source, dataset_id=None):
        target = out / name
        cmd = [sys.executable, str(generator), '--manifest', str(source), '--output', str(target)]
        if dataset_id is not None:
            cmd.extend(['--dataset-id', dataset_id])
        result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
        return target, result

    def read_dataset(path):
        html = path.read_text(encoding='utf-8')
        match = re.search(r'<script id="dataset" type="application/json">([\s\S]*?)</script>', html)
        assert match
        return html, json.loads(match.group(1))

    three = manifest('three-manifest.json', rows[:3])
    custom, result = generate('three-custom.html', three, 'synthetic-adaptation-three-v1')
    assert result.returncode == 0, result.stderr
    html, data = read_dataset(custom)
    assert data['dataset_id'] == 'synthetic-adaptation-three-v1'
    assert len(data['samples']) == 3 and '本批 3 份原始录音' in html
    assert '__ANNOTATION_SAMPLE_COUNT__' not in html
    assert 'must-not-appear-in-annotation' not in html
    assert all('annotations' not in sample and 'model_predictions' not in sample for sample in data['samples'])
    audit = json.loads(custom.with_suffix('.manifest.json').read_text(encoding='utf-8'))
    assert audit['dataset_id'] == data['dataset_id'] and len(audit['sources']) == 3
    assert audit['source_manifest_sha256'] == hashlib.sha256(three.read_bytes()).hexdigest()
    checks.append('custom_dataset_three_sources_dynamic_header_and_audit')

    bad = manifest('bad-hash-manifest.json', [{**rows[0], 'sha256': '0' * 64}])
    rejected, result = generate('rejected-hash.html', bad, 'synthetic-rejected')
    assert result.returncode != 0 and 'manifest_audio_sha256_mismatch' in result.stderr
    assert not rejected.exists()
    checks.append('changed_wav_hash_rejected_before_output')
    rejected, result = generate('rejected-blank.html', three, ' ')
    assert result.returncode != 0 and not rejected.exists()
    checks.append('blank_dataset_id_rejected')

    seven = manifest('seven-manifest.json', rows)
    legacy, result = generate('seven-default.html', seven)
    assert result.returncode == 0, result.stderr
    html, legacy_data = read_dataset(legacy)
    assert legacy_data['dataset_id'] == 'singing-subsystem-20260929-seven-original-wavs'
    assert len(legacy_data['samples']) == 7 and '本批 7 份原始录音' in html
    result = subprocess.run(['node', str(here / 'test_annotation.js'), str(legacy)],
                            capture_output=True, text=True, encoding='utf-8')
    assert result.returncode == 0, result.stdout + result.stderr
    (out / 'legacy-core-checks.txt').write_text(result.stdout, encoding='utf-8')
    checks.append('legacy_default_seven_sources_and_existing_eleven_core_pcm_checks')

    node_checks = r'''
const assert=require('node:assert/strict'),fs=require('node:fs');
const C=require(process.argv[1]);
const html=fs.readFileSync(process.argv[2],'utf8');
const D=JSON.parse(html.match(/<script id="dataset" type="application\/json">([\s\S]*?)<\/script>/)[1]);
const doc=C.exportDocument(D,{},'synthetic-generator-test','fixed');
assert.equal(doc.complete,false);
assert.equal(Object.keys(C.importDocument(doc,D)).length,3);
assert(doc.recordings.every(r=>r.annotations.length===0&&r.review_status==='unreviewed'));
assert.throws(()=>C.importDocument(doc,{...D,dataset_id:'different-batch'}));
assert.throws(()=>C.importDocument(doc,{...D,source_manifest_sha256:'f'.repeat(64)}));
const changed=JSON.parse(JSON.stringify(doc));changed.recordings[0].source.audio_sha256='0'.repeat(64);
assert.throws(()=>C.importDocument(changed,D));
const contaminated=JSON.parse(JSON.stringify(doc));contaminated.provenance.model_candidates_shown=true;
assert.throws(()=>C.importDocument(contaminated,D));
console.log('PASS actual three-WAV dataset empty roundtrip; foreign dataset/manifest/WAV/model-exposure rejected');
'''
    result = subprocess.run(['node', '-e', node_checks, str(here / 'annotation_core.js'), str(custom)],
                            capture_output=True, text=True, encoding='utf-8')
    assert result.returncode == 0, result.stdout + result.stderr
    (out / 'three-source-core-checks.txt').write_text(result.stdout, encoding='utf-8')
    checks.append('three_source_core_roundtrip_and_foreign_identity_prediction_rejection')
    after = {path: hashlib.sha256(Path(path).read_bytes()).hexdigest() for path in before}
    assert before == after
    checks.append('existing_annotation_html_files_unchanged')
    report = {'passed': True, 'checks': checks, 'synthetic_only': True,
              'real_audio_playback': False, 'browser_started': False, 'human_labels_created': False,
              'protected_html_before': before, 'protected_html_after': after,
              'implementation_hashes': {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                  for p in [generator, here / 'annotation_template.html', Path(__file__)]}}
    (out / 'results.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'passed': True, 'groups': len(checks), 'results': str(out / 'results.json')}))


if __name__ == '__main__':
    main()
