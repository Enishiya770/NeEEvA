"""Run an isolated PANNs framewise checkpoint over explicitly listed WAVs."""
import argparse
import csv
import json
from pathlib import Path
import sys


def plot(result, wav, path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    import numpy as np
    import soundfile as sf
    import hashlib, io
    raw=Path(wav).read_bytes()
    if hashlib.sha256(raw).hexdigest()!=result['source']['audio_sha256']:
        raise ValueError('plot_source_hash_changed')
    y, sr = sf.read(io.BytesIO(raw), always_2d=True)
    y = y.mean(axis=1)
    scores = np.array(result['timeline']['scores'])
    t = np.array(result['timeline']['source_start_samples']) / sr
    fig, axes = plt.subplots(3, 1, figsize=(13, 6.5), sharex=True,
                             gridspec_kw={'height_ratios':[1,2,0.55]})
    hop = max(1, len(y)//20000)
    axes[0].plot(np.arange(0,len(y),hop)/sr, y[::hop], lw=.45, color='#657482')
    axes[0].set_ylabel('PCM amplitude')
    colors = {'Speech':'#2171b5','Singing':'#d94801','Music':'#756bb1','Humming':'#31a354'}
    for label, color in colors.items():
        if label not in result['timeline']['columns']:continue
        j = result['timeline']['columns'].index(label)
        axes[1].step(t,scores[:,j],where='post',label=label,color=color,lw=1.5)
    if result['policy'].get('score_aggregation')=='family_max':
        from singing_segmentation import family_max_scores
        speech,singing=family_max_scores(scores,result['timeline']['columns'])
        axes[1].step(t,speech,where='post',label='Speech family max',color='#08519c',lw=1.7,ls='--')
        axes[1].step(t,singing,where='post',label='Singing family max',color='#a63603',lw=1.7,ls='--')
    axes[1].set_ylim(0,1)
    axes[1].set_ylabel('Independent sigmoid score\n(not calibrated probability)')
    axes[1].legend(loc='upper right', ncol=4)
    palette={'speech':'#6baed6','singing':'#fdae6b','mixed':'#9e9ac8','uncertain':'#dedede'}
    for segment in result['segments']:
        a,b=segment['start_sample']/sr,segment['end_sample']/sr
        axes[2].axvspan(a,b,color=palette[segment['label']])
        if b-a>0.8:axes[2].text((a+b)/2,.5,segment['label'],ha='center',va='center',fontsize=8)
    axes[2].set_yticks([])
    axes[2].set_xlabel('Original WAV seconds; candidate cells, not verified cut boundaries')
    axes[0].set_title(Path(wav).parent.name+' / '+Path(wav).name+' — '+result['provider']+'; candidate output only')
    axes[2].set_xlim(0,len(y)/sr)
    for axis in axes[:2]: axis.grid(alpha=.2)
    fig.tight_layout()
    fig.savefig(path,dpi=150)
    plt.close(fig)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=Path,required=True)
    parser.add_argument('--checkpoint',type=Path,required=True)
    parser.add_argument('--labels',type=Path)
    parser.add_argument('--backend',choices=['panns','strong'],default='panns')
    parser.add_argument('--upstream-code',type=Path)
    parser.add_argument('--dependencies',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--device',choices=['cpu','cuda'],default='cuda')
    parser.add_argument('--threshold',type=float,default=.5)
    parser.add_argument('--aggregation',choices=['direct','family_max'],default='direct')
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[2]
    sys.path.insert(0,str(args.dependencies.resolve()))
    sys.path.insert(0,str(root/'Server'/'SenseVoice'))
    import numpy as np
    from singing_segmentation import PannsFramewiseProvider,CandidatePolicy
    samples=json.loads(args.manifest.read_text(encoding='utf-8'))
    args.output.mkdir(parents=True,exist_ok=True)
    if args.backend=='panns':
        if args.labels is None:parser.error('--labels required for PANNs')
        provider=PannsFramewiseProvider(args.checkpoint,args.labels,args.dependencies,args.device)
    else:
        if args.upstream_code is None:parser.error('--upstream-code required for strong baseline')
        from singing_segmentation_strong import StrongFramewiseProvider
        provider=StrongFramewiseProvider(args.checkpoint,args.upstream_code,args.dependencies,args.device)
    summary=[]
    for row in samples:
        wav=Path(row['wav']).resolve()
        result, full_scores=provider.analyze(wav,CandidatePolicy(active_threshold=args.threshold,score_aggregation=args.aggregation))
        if result['status']!='candidate':raise RuntimeError('offline_analysis_cancelled')
        if row.get('sha256') and row['sha256']!=result['source']['audio_sha256']:
            raise ValueError('evaluation_manifest_audio_hash_mismatch')
        result['evaluation_sample'] = row
        result['ground_truth_available']=False
        stem=args.output/row['id']
        stem.with_suffix('.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        metadata=result['model_metadata']
        rate=metadata['analysis_sample_rate']
        np.savez_compressed(stem.with_suffix('.framewise.npz'),scores=full_scores,labels=np.array(provider.all_labels),analysis_sample_rate=rate,hop_samples=round(rate*metadata['output_grid_seconds']))
        with stem.with_suffix('.csv').open('w',newline='',encoding='utf-8') as stream:
            writer=csv.writer(stream)
            writer.writerow(['source_start_sample','source_end_sample','start_seconds',*provider.labels])
            starts=result['timeline']['source_start_samples']
            ends=starts[1:]+[result['source']['sample_count']]
            for a,b,scores in zip(starts,ends,result['timeline']['scores']):
                writer.writerow([a,b,a/result['source']['sample_rate'],*scores])
        plot(result,wav,stem.with_suffix('.png'))
        item={'id':row['id'],'source':result['source'],'timing':result['timing'],
              'segments':[{k:v for k,v in s.items() if k!='posteriors'} for s in result['segments']],
              'score_max':{k:float(full_scores[:,provider.all_labels.index(k)].max()) for k in provider.labels},
              'scope':'No human boundary truth; model output, not performance accuracy.'}
        summary.append(item)
        print(json.dumps(item,ensure_ascii=False),flush=True)
    (args.output/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':
    main()
