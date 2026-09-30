"""Verify offline artifacts and summarize observations without invented truth."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import sys


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root',type=Path,required=True)
    p.add_argument('--dependencies',type=Path,required=True)
    a=p.parse_args()
    sys.path.insert(0,str(a.dependencies.resolve()))
    import numpy as np
    root=a.root
    manifest=json.loads((root/'evaluation-manifest.json').read_text(encoding='utf-8'))
    expected={r['id']:r for r in manifest}
    rows=[];checks=0
    for model in ('panns','strong','strong10'):
        results=[]
        for identity,entry in expected.items():
            path=root/f'results-{model}'/f'{identity}.json'
            r=json.loads(path.read_text(encoding='utf-8'));source=r['source']
            assert sha(Path(entry['wav']))==entry['sha256']==source['audio_sha256']
            assert r['status']=='candidate' and r['execution_authorized'] is False and r['supported_range'] is None
            assert r['ground_truth_available'] is False
            cursor=0
            for seg in r['segments']:
                assert seg['start_sample']==cursor and seg['end_sample']>cursor
                assert seg['status']=='candidate' and seg['boundary_verified'] is False
                cursor=seg['end_sample']
            assert cursor==source['sample_count']
            with np.load(path.with_suffix('.framewise.npz')) as raw:
                frames=raw['scores'];labels=list(raw['labels'])
                assert frames.shape[1]==(527 if model=='panns' else 447)
                assert np.isfinite(frames).all() and (frames>=0).all() and (frames<=1).all()
                selected=frames[:,[labels.index(k) for k in r['timeline']['columns']]]
                native=selected[::32] if model=='panns' else selected
                assert np.array_equal(native,np.array(r['timeline']['scores']))
                if model=='panns':
                    for start in range(0,len(frames),32):
                        assert np.array_equal(frames[start:min(start+32,len(frames))],np.repeat(frames[start:start+1],min(32,len(frames)-start),axis=0))
            singing_columns=[i for i,k in enumerate(r['timeline']['columns']) if k in ('Singing','Male singing','Female singing','Child singing')]
            row={'model':model,'id':identity,'duration_seconds':source['sample_count']/source['sample_rate'],
                 'inference_seconds':r['timing']['inference_seconds'],
                 'model_load_seconds':r['timing']['model_load_seconds'],
                 'peak_cuda_allocated_bytes':r['timing']['peak_cuda_allocated_bytes'],
                 'direct_singing_score_peak':float(native[:,r['timeline']['columns'].index('Singing')].max()),
                 'family_singing_score_peak':float(native[:,singing_columns].max()),
                 'family_0_2_regions':[]}
            sensitive=json.loads((root/f'sensitivity-{model}'/f'{identity}-family-0.2.json').read_text(encoding='utf-8'))
            row['family_0_2_regions']=[{'label':s['label'],'start_seconds':s['start_sample']/source['sample_rate'],
                'end_seconds':s['end_sample']/source['sample_rate']} for s in sensitive['segments']]
            rows.append(row);results.append(row);checks+=1
        warm=[r['inference_seconds'] for r in results if r['id']!='c9']
        print(json.dumps({'model':model,'load_seconds':results[0]['model_load_seconds'],
            'first_inference_seconds':results[0]['inference_seconds'],'warm_median_seconds':statistics.median(warm),
            'warm_range_seconds':[min(warm),max(warm)],'peak_allocated_mib':max(r['peak_cuda_allocated_bytes'] for r in results)/1048576}))
    output={'verified_source_model_artifacts':checks,'accuracy_metrics':None,
        'scope':'Source hashes, candidate-only protection, framewise extraction and coverage verified. No human timing truth; these are observations, not F1/IoU/precision/recall.',
        'results':rows}
    (root/'evaluation-summary.json').write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':main()
