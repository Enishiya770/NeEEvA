"""Fixed validation-only 4/8-frame initial-history ablation; production stays 16."""
from __future__ import annotations

import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np

from Server.ARDY.diagnose_generated_motion import generate,history_diagnostics
from Server.ARDY.evaluate_native_motion import dump,grouped_summary,reserve_output,sha
from Server.ARDY.motion_service.backend import ArdyBackend,ROOT
from Server.ARDY.motion_service.protocol import InitialHistory
from Server.ARDY.native_motion_continuity import continuity_observations
from Server.ARDY.native_motion_measurements import assess,measure_frames
from Tools.MotionAdapter.data import read_bundle,read_records


def main(args):
    baseline=json.loads(args.baseline_report.read_text(encoding="utf-8"))
    original=baseline["plan"]
    if baseline["status"]!="complete" or original["split"]!="val" or original["finalTest"]:
        raise ValueError("This ablation requires completed development validation, never test data")
    ids=original["recordIds"]
    if len(ids)!=40 or original["seeds"]!=[0,1]:
        raise ValueError("Expected the fixed forty validation IDs and seeds 0/1")
    rows=read_records(original["dataset"])
    qwen,qmeta=read_bundle(original["qwenBundle"],rows,"qwen")
    mapping={row["id"]:(i,row) for i,row in enumerate(rows)}
    if any(mapping[identifier][1]["split"]!="val" for identifier in ids):
        raise ValueError("Validation split changed")
    selection=json.loads(args.selection.read_text(encoding="utf-8"))
    if selection["checkpoint_sha256"]!=original["adapters"]["candidate"]["sha256"]:
        raise ValueError("Candidate changed after the fixed validation")
    history_path=Path(original["histories"]["grounded"]["path"])
    if sha(history_path)!=original["histories"]["grounded"]["sha256"]:
        raise ValueError("Actual history capture changed")
    plan={"schema":1,"purpose":"Development-validation response/continuity ablation; no production change or final-test use.",
        "baselineReport":str(args.baseline_report.resolve()),"baselineReportSha256":sha(args.baseline_report),
        "datasetSha256":sha(original["dataset"]),"qwenBundleSha256":sha(original["qwenBundle"]),
        "recordIds":ids,"seeds":[0,1],"initialHistoryFrames":[4,8],"reuseBaselineInitial16":True,
        "initialHistorySource":{"path":str(history_path),"sha256":sha(history_path),"operation":"Project the exact captured history once, then pass projected_tensor[:, -4:] or [:, -8:] directly; do not repad."},
        "selection":str(args.selection.resolve()),"selectionSha256":sha(args.selection),"candidateSha256":selection["checkpoint_sha256"],
        "windows":3,"newFramesPerWindow":40,"continuationHistoryFrames":16,"fps":20,"trajectories":160,
        "rngPolicy":"Same explicit seeds and exact CPU candidate-condition bytes as fixed validation. Official initial noise and sampling act on the fixed forty generated frames, independent of history length.",
        "assessmentPolicy":"Necessary labeled goals, first-reach/hold timing and unblended 13-bone source continuity proxies; no automatic naturalness approval. Lower delay with worse discontinuity is not automatically preferred.",
        "sourceScripts":{name:sha(Path(__file__).parent/name) for name in ("validate_initial_history_length.py","native_motion_continuity.py","native_motion_measurements.py","diagnose_generated_motion.py","motion_service/backend.py","motion_service/adapter_selection.py")}}
    args.output=args.output.resolve()
    plan_hash=reserve_output(args.output,plan)
    if args.plan_only:
        print(json.dumps({"plan":str(args.output/"plan.json"),"trajectories":160,"gpuUsed":False},indent=2))
        return
    started=time.perf_counter()
    report={"schema":1,"status":"running","plan":plan,"planSha256":plan_hash,"results":[],"groups":[],
        "languageModelsLoaded":False,"servicesStarted":False,"productionHistoryChanged":False,"semanticSuccessRate":None}
    try:
        backend=ArdyBackend(adapter_selection=args.selection.resolve())
        backend.load()
        torch=backend.torch
        report["backend"]=backend.info
        # Match the CPU projection used by the original fixed validation exactly.
        cpu_adapter=backend.adapter.cpu()
        value=json.loads(history_path.read_text(encoding="utf-8-sig"))
        projected,projection=backend.project_history(InitialHistory(**value.get("initialHistory",value)))
        report["projection"]=projection
        with torch.inference_mode():
            pose=backend.model.motion_rep.inverse(projected,is_normalized=True)
            input_pose={key:tensor[0].cpu().numpy() for key,tensor in pose.items() if torch.is_tensor(tensor)}
        np.savez(args.output/"projected-full16-source-pose.npz",**input_pose)
        histories={length:projected[:,-length:].clone() for length in (4,8)}
        if any(length%backend.model.num_frames_per_token for length in histories):
            raise ValueError("Requested initial length is incompatible with the pinned tokenizer")
        report["numFramesPerToken"]=backend.model.num_frames_per_token
        report["historyDiagnostics"]={str(length):history_diagnostics(backend,history) for length,history in histories.items()}
        for identifier in ids:
            index,row=mapping[identifier]
            with torch.inference_mode():
                vector=cpu_adapter(torch.tensor(qwen[index:index+1],dtype=torch.float32))[0].numpy()
            condition_hash=hashlib.sha256(np.asarray(vector,dtype=np.float32).tobytes()).hexdigest()
            expected={case["conditionSha256"] for case in baseline["results"] if case["recordId"]==identifier and case["condition"]=="candidate"}
            if expected!={condition_hash}:
                raise ValueError("Candidate condition bytes differ from the existing paired validation")
            condition=torch.tensor(vector,device=backend.device).reshape(1,1,4096)
            for length,history in histories.items():
                for seed in (0,1):
                    clip_id=f"{identifier}--candidate--initial{length}--seed{seed}"
                    result=generate(backend,condition,history,seed,args.output,clip_id,description=row["text"],condition_source=f"locked-candidate-validation-initial-history-{length}")
                    if [window["historyFrames"] for window in result["windows"]]!=[length,16,16]:
                        raise ValueError("Continuation history no longer equals sixteen")
                    with np.load(args.output/(clip_id+".npz"),allow_pickle=False) as arrays:
                        frames=measure_frames(arrays,backend.names)
                        continuity=continuity_observations(input_pose,arrays,backend.names)
                    frame_path=args.output/(clip_id+".measurements.json")
                    continuity_path=args.output/(clip_id+".continuity.json")
                    dump(frame_path,frames)
                    dump(continuity_path,continuity)
                    report["results"].append({"recordId":identifier,"semanticGroup":row["semantic_group"],"family":row["family"],"evaluationTrack":row["evaluation_track"],
                        "condition":"candidate","history":f"initial{length}","initialHistoryFrames":length,"seed":seed,
                        "conditionSha256":condition_hash,"conditionMatchesFixedValidation":True,
                        "clip":result["clip"],"npz":str(args.output/(clip_id+".npz")),"frameMeasurements":str(frame_path),"continuityFile":str(continuity_path),
                        "assessment":assess(row,frames),"windows":result["windows"],
                        "continuitySummary":{key:continuity[key] for key in ("firstFrameGlobalRotationJumpDegrees","firstFrameLocalRotationJumpDegrees","speedSummaryIncludingInitialJoin","speedSummaryGeneratedFramesOnly","windowSeams")}})
                    report["groups"]=grouped_summary(report["results"])
                    dump(args.output/"report.json",report)
                    print(clip_id,"generated, continuation history 16; naturalness unknown",flush=True)
        report["status"]="complete"
        dump(args.output/"render-manifest.json",{"clips":[{"id":Path(case["clip"]).stem,"path":case["clip"]} for case in report["results"]]})
    except BaseException as error:
        report["status"]="failed"
        report["error"]={"type":type(error).__name__,"message":str(error)}
        raise
    finally:
        report["totalWallSeconds"]=time.perf_counter()-started
        dump(args.output/"report.json",report)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-report",type=Path,default=ROOT/"Server/ARDY/runtime/generalization-validation-v1/report.json")
    parser.add_argument("--selection",type=Path,default=ROOT/"Tools/MotionAdapter/runtime/generalization-v1/candidates/selection.json")
    parser.add_argument("--output",type=Path,default=ROOT/"Server/ARDY/runtime/history-length-validation-v1")
    parser.add_argument("--plan-only",action="store_true")
    main(parser.parse_args())
