"""Rescore saved model posteriors; no inference, no source audio editing."""
import argparse
import json
from pathlib import Path
import sys


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--results',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--dependencies',type=Path,required=True)
    args=p.parse_args()
    sys.path.insert(0,str(args.dependencies.resolve()))
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Server'/'SenseVoice'))
    from singing_segmentation import candidate_timeline,CandidatePolicy
    from evaluate import plot
    args.output.mkdir(parents=True,exist_ok=True)
    summary=[]
    for path in args.results.glob('*.json'):
        original=json.loads(path.read_text(encoding='utf-8'))
        if not isinstance(original,dict) or 'timeline' not in original:continue
        m=original['model_metadata']
        for threshold in (.1,.2,.5):
            policy=CandidatePolicy(active_threshold=threshold,score_aggregation='family_max')
            segments,transitions=candidate_timeline(original['timeline']['scores'],original['timeline']['columns'],original['source'],
                original['model_version'],policy,cell_analysis_samples=original['timeline']['analysis_cell_samples'],
                analysis_rate=m['analysis_sample_rate'],context_support_seconds=m.get('global_context_seconds') or m['theoretical_receptive_field_seconds'])
            result=dict(original,segments=segments,transitions=transitions,policy=policy.__dict__)
            result['sensitivity_scope']='Fixed .1/.2/.5 thresholds from upstream inference examples, no human ground truth or selected best threshold. Same saved raw scores; no repeated inference.'
            name=f'{path.stem}-family-{threshold}'
            (args.output/(name+'.json')).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
            if threshold==.2:plot(result,result['evaluation_sample']['wav'],args.output/(name+'.png'))
            summary.append({'id':path.stem,'threshold':threshold,'policy':policy.__dict__,
                'segments':[{k:v for k,v in s.items() if k!='posteriors'} for s in segments]})
    (args.output/'sensitivity-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(summary,ensure_ascii=False))


if __name__=='__main__':main()
