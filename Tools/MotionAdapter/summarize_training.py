"""Compare the four preregistered pilot runs using validation scores only."""
import argparse
import json
from pathlib import Path
from statistics import mean, pstdev


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--directory',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    args = p.parse_args()
    runs = []
    contracts = set()
    for architecture in ['linear','mlp']:
        for seed in [0,1]:
            name = f'{architecture}-seed{seed}'
            report = json.loads((args.directory/f'{name}.report.json').read_text(encoding='utf-8'))
            checkpoint = args.directory/f'{name}.pt'
            if not checkpoint.is_file():
                raise ValueError(f'Missing checkpoint for {name}')
            if report['arguments']['architecture'] != architecture or report['arguments']['seed'] != seed:
                raise ValueError('Run label mismatch')
            contracts.add((report['dataset_sha256'],report['qwen_metadata']['feature_contract'],
                           report['teacher_metadata']['feature_contract']))
            runs.append({'name':name,'architecture':architecture,'seed':seed,
                         'checkpoint':checkpoint.name,'best_epoch':report['best_epoch'],
                         'scores':report['scores'],'constant_train_mean_baseline':report['constant_train_mean_baseline']})
    if len(contracts) != 1:
        raise ValueError('Training runs used different data or feature contracts')
    aggregates = {}
    for architecture in ['linear','mlp']:
        scores = [r['scores']['val']['standardized_mse'] for r in runs if r['architecture']==architecture]
        aggregates[architecture] = {'validation_mse_mean':mean(scores),'validation_mse_std':pstdev(scores)}
    architecture = min(aggregates,key=lambda a:aggregates[a]['validation_mse_mean'])
    best = min((r for r in runs if r['architecture']==architecture),key=lambda r:r['scores']['val']['standardized_mse'])
    data_hash,qwen_contract,teacher_contract = next(iter(contracts))
    result = {'schema':1,'dataset_sha256':data_hash,'qwen_contract':qwen_contract,'teacher_contract':teacher_contract,
              'selection_rule':'Architecture by mean validation standardized MSE across seeds 0 and 1; checkpoint by best validation score within that architecture.',
              'selected_checkpoint':best['checkpoint'],'aggregates':aggregates,'runs':runs,
              'limitation':'Synthetic template corpus and two seeds only. Held-out feature scores do not establish real motion quality or production readiness.'}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({'selected_checkpoint':best['checkpoint'],'aggregates':aggregates}))


if __name__ == '__main__':
    main()
