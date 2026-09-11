"""Locked candidate on the four previously seen descriptions, offline only."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from Server.ARDY.diagnose_generated_motion import generate,history_diagnostics,semantic_evaluation
from Server.ARDY.motion_service.backend import ArdyBackend,ROOT
from Server.ARDY.motion_service.protocol import InitialHistory


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main(args):
    args.output=args.output.resolve()
    if args.output.exists() and any(args.output.iterdir()):
        raise FileExistsError("Use a new seen-diagnostic directory; old/teacher evidence is preserved")
    args.output.mkdir(parents=True,exist_ok=True)
    rows=[json.loads(line) for line in args.prompts.read_text(encoding="utf-8-sig").splitlines() if line.strip()][:4]
    if [row["id"] for row in rows]!=["raise-actual","explain-actual","raise-style","explain-style"]:
        raise ValueError("This is exactly the four previously seen diagnostics")
    with np.load(args.qwen_features,allow_pickle=False) as data:
        features=data["features"].astype(np.float32)
        if data["ids"].tolist()!=[row["id"] for row in rows] or data["texts"].tolist()!=[row["text"] for row in rows]:
            raise ValueError("Frozen Qwen IDs/texts do not match the seen diagnostic manifest")
    if features.shape!=(4,2048) or not np.isfinite(features).all():
        raise ValueError("Invalid frozen Qwen features")
    backend=ArdyBackend(adapter_selection=args.selection.resolve())
    backend.load()
    torch=backend.torch
    history_value=json.loads(args.history.read_text(encoding="utf-8-sig"))
    history,projection=backend.project_history(InitialHistory(**history_value.get("initialHistory",history_value)))
    report={"schema":1,"mode":"locked-candidate-seen-diagnostic","complete":False,"seenDiagnostic":True,
        "purpose":"Recheck previously analyzed descriptions after candidate selection; not held-out generalization or a checkpoint selection score.",
        "prompts":rows,"seeds":[0,1],"histories":["cold","grounded-unity-idle16"],"results":[],
        "qwenFeatures":str(args.qwen_features.resolve()),"qwenFeaturesSha256":sha(args.qwen_features),
        "selection":str(args.selection.resolve()),"selectionSha256":sha(args.selection),
        "history":str(args.history.resolve()),"historySha256":sha(args.history),"projection":projection,
        "historyDiagnostics":history_diagnostics(backend,history),"backend":backend.info,
        "languageModelsLoadedInThisProcess":False,"liveFeatureRequestPerformed":False,"servicesStarted":False,
        "sourceScriptsSha256":{name:sha(Path(__file__).parent/name) for name in ("diagnose_seen_candidate.py","diagnose_generated_motion.py","motion_service/backend.py","motion_service/adapter_selection.py")}}
    try:
        for i,row in enumerate(rows):
            with torch.inference_mode():
                condition=backend.adapter(torch.tensor(features[i:i+1],device=backend.device)).reshape(1,1,4096)
            for history_name,initial in (("cold",None),("grounded-unity-idle16",history)):
                for seed in (0,1):
                    identifier=f"{row['id']}-candidate-{history_name}-seed{seed}"
                    result=generate(backend,condition,initial,seed,args.output,identifier,
                        description=row["text"],condition_source="offline-frozen-qwen-locked-candidate-seen-diagnostic")
                    result.update(promptId=row["id"],historyKind=history_name,effectiveSeed=seed)
                    result["semanticEvaluation"]=semantic_evaluation(result,row["id"])
                    report["results"].append(result)
                    args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
                    print(identifier,"bothAboveHeadFraction",result["hands"]["bothAboveHeadFraction"],flush=True)
        report["complete"]=True
        (args.output/"render-manifest.json").write_text(json.dumps({"clips":[{"id":row["id"],"path":row["clip"]} for row in report["results"]]},indent=2),encoding="utf-8")
    finally:
        args.report.parent.mkdir(parents=True,exist_ok=True)
        args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selection",type=Path,default=ROOT/"Tools/MotionAdapter/runtime/generalization-v1/candidates/selection.json")
    parser.add_argument("--prompts",type=Path,default=ROOT/"Tools/MotionAdapter/runtime/teacher-motion-diagnostic/prompts.jsonl")
    parser.add_argument("--qwen-features",type=Path,default=ROOT/"Server/ARDY/runtime/generated-condition-qwen-diagnosis/qwen-features.npz")
    parser.add_argument("--history",type=Path,default=ROOT/"Server/ARDY/runtime/generate-diagnostics/animator-idle-history.json")
    parser.add_argument("--output",type=Path,default=ROOT/"Server/ARDY/runtime/seen-candidate-diagnostics-v1")
    parser.add_argument("--report",type=Path,default=ROOT/"Tools/MotionAdapter/reports/seen-candidate-motion-diagnostics-v1.json")
    main(parser.parse_args())
