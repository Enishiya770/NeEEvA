"""Paired Qwen-adapter/teacher-feature experiments; this process loads no LM."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path

import numpy as np

from Server.ARDY.diagnose_generated_motion import generate, history_diagnostics, semantic_evaluation
from Server.ARDY.motion_service.backend import ArdyBackend, ROOT
from Server.ARDY.motion_service.protocol import GenerateRequest, InitialHistory
from Server.ARDY.motion_service.service import LiveFeatureProvider
from Tools.MotionAdapter.data import read_bundle, read_records


async def main(args):
    all_rows = read_records(args.prompts)
    rows = all_rows[:4]
    if len(rows) != 4 or len({row["id"] for row in rows}) != 4:
        raise ValueError("Exactly four distinct actual/canonical diagnostic prompts are required")
    args.output.mkdir(parents=True,exist_ok=True)
    features = {}
    teacher_controls = []
    feature_metadata = None
    if args.teacher:
        values, feature_metadata = read_bundle(args.teacher,all_rows,"teacher")
        ids = [row["id"] for row in all_rows]
        with np.load(ROOT/"Tools/MotionAdapter/runtime/teacher-2000.npz",allow_pickle=False) as cache:
            cache_ids, cache_values = cache["ids"].tolist(), cache["features"].astype(np.float32)
        for control in ("motion-00004","motion-00053"):
            actual, expected = values[ids.index(control)], cache_values[cache_ids.index(control)]
            cosine = float(np.dot(actual,expected)/(np.linalg.norm(actual)*np.linalg.norm(expected)))
            relative_l2 = float(np.linalg.norm(actual-expected)/np.linalg.norm(expected))
            if cosine < .9999 or relative_l2 > .005:
                raise ValueError(f"Offline teacher control {control} differs from the evaluated teacher space")
            teacher_controls.append({"id":control,"cosine":cosine,"relativeL2":relative_l2})
        features = {row["id"]:values[ids.index(row["id"])] for row in rows}
        mode = "offline-teacher-feature"
    elif args.qwen_features:
        with np.load(args.qwen_features,allow_pickle=False) as data:
            ids, texts, values = data["ids"].tolist(), data["texts"].tolist(), data["features"].astype(np.float32)
        if (ids != [row["id"] for row in rows] or texts != [row["text"] for row in rows]
                or values.shape != (4,2048) or not np.isfinite(values).all()
                or np.any(np.linalg.norm(values,axis=1)<1e-8)):
            raise ValueError("Frozen Qwen features must match the exact four diagnostic texts and IDs")
        features = dict(zip(ids,values))
        mode = "frozen-previously-live-qwen-adapter"
    else:
        provider = LiveFeatureProvider(args.feature_url)
        for row in rows:
            request = GenerateRequest(characterId="condition-diagnostic",turnId=row["id"],revision=1,
                requestId="condition-"+row["id"],chunkIndex=0,description=row["text"])
            feature = await provider.fetch(request,20)
            features[row["id"]] = feature["embedding"]
        np.savez(args.output/"qwen-features.npz",ids=np.array([r["id"] for r in rows]),
                 features=np.stack([features[r["id"]] for r in rows]),texts=np.array([r["text"] for r in rows]))
        mode = "live-qwen-adapter"
    backend = ArdyBackend()
    backend.load()
    value = json.loads(args.history.read_text(encoding="utf-8-sig"))
    if "initialHistory" in value:
        value = value["initialHistory"]
    history, projection = backend.project_history(InitialHistory(**value))
    histories = {"cold":None,"grounded-unity-idle16":history}
    report = {"schema":1,"mode":mode,"prompts":rows,"seeds":[0,1],"results":[],
        "history":str(args.history),"historySha256":hashlib.sha256(args.history.read_bytes()).hexdigest(),
        "projection":projection,"historyDiagnostics":history_diagnostics(backend,history),
        "teacherFeatures":str(args.teacher) if args.teacher else None,
        "frozenQwenFeatures":str(args.qwen_features) if args.qwen_features else None,
        "featureBundleMetadata":feature_metadata,
        "teacherControlChecks":teacher_controls,
        "languageModelsLoadedInThisProcess":False,"backend":backend.info,
        "limitations":["Four specific prompts and two fixed seeds, not a broad validation of motion-following quality.",
                       "Offline teacher vectors are an ablation only; production still uses a single Qwen model."]}
    torch = backend.torch
    for row in rows:
        with torch.inference_mode():
            raw = torch.tensor(features[row["id"]][None],dtype=torch.float32,device=backend.device)
            condition = (raw if args.teacher else backend.adapter(raw)).reshape(1,1,4096)
        for label, initial in histories.items():
            for seed in (0,1):
                identifier=f"{row['id']}-{mode}-{label}-seed{seed}"
                result=generate(backend,condition,initial,seed,args.output,identifier,
                                description=row["text"],condition_source=mode)
                result.update(promptId=row["id"],historyKind=label,effectiveSeed=seed)
                result["semanticEvaluation"] = semantic_evaluation(result,row["id"])
                report["results"].append(result)
                print(identifier,"overheadCriterionApplicable",result["semanticEvaluation"]["overheadCriterionApplicable"],
                      "SpineTilt",round(result["bones"]["Spine3"]["global"]["tiltMaxDegrees"],2),flush=True)
                args.report.parent.mkdir(parents=True,exist_ok=True)
                args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    report["complete"] = True
    report["scriptSha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    if args.teacher:
        report["teacherFeaturesSha256"] = hashlib.sha256(args.teacher.read_bytes()).hexdigest()
    if args.qwen_features:
        report["frozenQwenFeaturesSha256"] = hashlib.sha256(args.qwen_features.read_bytes()).hexdigest()
    args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    (args.output/"render-manifest.json").write_text(json.dumps({"clips":[{"id":r["id"],"path":r["clip"]} for r in report["results"]]},indent=2),encoding="utf-8")


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prompts",type=Path,default=ROOT/"Tools/MotionAdapter/runtime/teacher-motion-diagnostic/prompts.jsonl")
    parser.add_argument("--history",type=Path,default=ROOT/"Server/ARDY/runtime/generate-diagnostics/animator-idle-history.json")
    parser.add_argument("--feature-url",default="http://127.0.0.1:8080")
    sources=parser.add_mutually_exclusive_group()
    sources.add_argument("--teacher",type=Path)
    sources.add_argument("--qwen-features",type=Path,help="Offline replay of the previously measured live-Qwen four-text NPZ; never contacts a Qwen service")
    parser.add_argument("--output",type=Path,default=ROOT/"Server/ARDY/runtime/generated-condition-qwen-diagnosis")
    parser.add_argument("--report",type=Path,default=ROOT/"Tools/MotionAdapter/reports/generated-motion-qwen-condition-ablation.json")
    asyncio.run(main(parser.parse_args()))
