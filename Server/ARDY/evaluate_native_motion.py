"""Offline paired native-ARDY evaluation with frozen IDs, seeds and histories.

Validation is the default. A final test requires an explicit flag and one-use
audit marker; no automatic overall semantic success or checkpoint selection.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from Server.ARDY.diagnose_generated_motion import generate, history_diagnostics
from Server.ARDY.motion_service.backend import ArdyBackend, ROOT
from Server.ARDY.motion_service.protocol import InitialHistory
from Server.ARDY.native_motion_measurements import assess, measure_frames
from Tools.MotionAdapter.data import read_bundle, read_records


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def dump(path,value):
    Path(path).write_text(json.dumps(value,indent=2,allow_nan=False),encoding="utf-8")


def select_records(rows,ids,split,final_test):
    if split not in ("val","test"):
        raise ValueError("Only validation or explicit final test may be evaluated")
    if (split=="test")!=bool(final_test):
        raise ValueError("Test requires --final-test; that flag is invalid for validation")
    if not ids or len(ids)!=len(set(ids)):
        raise ValueError("Provide an explicit nonempty unique fixed record-ID list")
    mapping={r["id"]:r for r in rows}
    if any(identifier not in mapping for identifier in ids):
        raise ValueError("An explicit record ID is absent from the dataset")
    selected=[mapping[identifier] for identifier in ids]
    if any(r["split"]!=split for r in selected):
        raise ValueError("Requested record IDs cross the selected split")
    return selected


def named_paths(values,reserved=()):
    result={}
    for entry in values:
        name,separator,path=entry.partition("=")
        if not separator or not re.fullmatch(r"[A-Za-z0-9_.-]+",name) or name in result or name in reserved:
            raise ValueError("Expected distinct safe name=path entries")
        resolved=Path(path).resolve()
        if not resolved.is_file():
            raise ValueError(f"Missing input file: {resolved}")
        result[name]=resolved
    return result


def build_plan(args):
    rows=read_records(args.dataset)
    ids=[value.strip() for value in args.record_ids.split(",") if value.strip()]
    selected=select_records(rows,ids,args.split,args.final_test)
    if not args.seeds or len(args.seeds)!=len(set(args.seeds)) or any(s<0 or s>2147483647 for s in args.seeds):
        raise ValueError("Seeds must be distinct integers in [0, 2147483647]")
    adapters=named_paths(args.adapter,reserved=("teacher",))
    raw_histories=args.history or ["cold"]
    if raw_histories.count("cold")>1:
        raise ValueError("Duplicate cold history")
    histories=named_paths([v for v in raw_histories if v!="cold"],reserved=("cold",))
    # Bundle hashes bind full manifest text, split and labels before filtering.
    qwen,qmeta=read_bundle(args.qwen_bundle,rows,"qwen")
    teacher,tmeta=read_bundle(args.teacher_bundle,rows,"teacher")
    plan={"schema":1,"split":args.split,"finalTest":args.final_test,"recordIds":ids,
        "dataset":str(args.dataset.resolve()),"datasetSha256":sha(args.dataset),
        "qwenBundle":str(args.qwen_bundle.resolve()),"qwenBundleSha256":sha(args.qwen_bundle),
        "teacherBundle":str(args.teacher_bundle.resolve()),"teacherBundleSha256":sha(args.teacher_bundle),
        "featureMetadata":{"qwen":qmeta,"teacher":tmeta},"seeds":args.seeds,
        "adapters":{name:{"path":str(path),"sha256":sha(path)} for name,path in adapters.items()},
        "histories":({"cold":None} if "cold" in raw_histories else {})|{name:{"path":str(path),"sha256":sha(path)} for name,path in histories.items()},
        "windows":3,"newFramesPerWindow":40,"fps":20,"continuationHistoryFrames":16,
        "pairing":"Every selected record, candidate condition and teacher uses the same explicit seeds and exact initial histories; each trajectory has its own RNG context.",
        "evaluationPolicy":"Necessary observations plus manual review; no automatic semantic success, ranking or candidate selection. Boundary records are excluded from success denominators.",
        "sourceScripts":{name:sha(Path(__file__).parent/name) for name in ("evaluate_native_motion.py","native_motion_measurements.py","diagnose_generated_motion.py","motion_service/backend.py")}}
    return plan,rows,selected,qwen,teacher,adapters,histories


def reserve_output(directory,plan):
    directory=Path(directory)
    encoded=json.dumps(plan,sort_keys=True,allow_nan=False)
    if directory.exists():
        existing=list(directory.iterdir())
        if existing and (len(existing)!=1 or existing[0].name!="plan.json" or json.loads(existing[0].read_text())!=plan):
            raise ValueError("Output must be new, empty, or contain only this identical plan.json; previous results are never overwritten")
    directory.mkdir(parents=True,exist_ok=True)
    dump(directory/"plan.json",plan)
    return hashlib.sha256(encoded.encode()).hexdigest()


def reserve_test_audit(path,plan_hash,plan,output):
    path=Path(path)
    path.parent.mkdir(parents=True,exist_ok=True)
    with path.open("x",encoding="utf-8") as stream:
        json.dump({"schema":1,"status":"reserved_before_generation","planSha256":plan_hash,
                   "datasetSha256":plan["datasetSha256"],"output":str(Path(output).resolve()),
                   "policy":"One frozen final-test audit. This marker is not silently replaced after inspecting results."},stream,indent=2)


def grouped_summary(results):
    groups={}
    for result in results:
        key=(result["condition"],result["family"],result["evaluationTrack"],result["semanticGroup"])
        group=groups.setdefault(key,{"condition":key[0],"family":key[1],"evaluationTrack":key[2],"semanticGroup":key[3],
            "cases":0,"includedCases":0,"semanticSuccessCount":None,"manualPendingCases":0,
            "necessaryObservationsWithGate":0,"necessaryObservationsSatisfied":0,"observationsManualOrUngated":0})
        group["cases"]+=1
        group["includedCases"]+=int(result["assessment"]["includeInSuccessSummary"])
        group["manualPendingCases"]+=1
        for observation in result["assessment"]["observations"]:
            value=observation["thresholdSatisfied"]
            if value is None:
                group["observationsManualOrUngated"]+=1
            else:
                group["necessaryObservationsWithGate"]+=1
                group["necessaryObservationsSatisfied"]+=int(value)
    return list(groups.values())


def run(args):
    started=time.perf_counter()
    args.output=args.output.resolve()
    plan,rows,selected,qwen,teacher,adapters,history_paths=build_plan(args)
    plan_hash=reserve_output(args.output,plan)
    if args.plan_only:
        print(json.dumps({"planOnly":True,"selectedRecords":len(selected),"trajectories":len(selected)*(len(adapters)+1)*len(args.seeds)*len(plan["histories"]),"output":str(args.output)},indent=2))
        return
    marker=args.test_audit_marker or ROOT/"Tools/MotionAdapter/reports"/f"native-motion-final-test-{plan['datasetSha256'][:16]}.json"
    if args.final_test:
        reserve_test_audit(marker,plan_hash,plan,args.output)
    report={"schema":1,"status":"running","planSha256":plan_hash,"plan":plan,"results":[],"groups":[],
        "languageModelsLoaded":False,"servicesStarted":False,"semanticSuccessRate":None,
        "startedUtc":datetime.now(timezone.utc).isoformat(),
        "timingScope":"Offline frozen condition vectors. Window timings include ARDY sampling and decode; they exclude live Qwen encoding, network queueing, Unity playback and TTS. This is not end-to-end live latency.",
        "limitations":["Native source skeleton observations are not actual target-avatar collision or naturalness judgments.",
            "Projection history has real sampled upper-body rotations but synthetic stationary legs and source foot contacts.",
            "Validation is for development. Final-test output is descriptive and must not be used for further candidate tuning."]}
    try:
        from Tools.MotionAdapter.adapter import load_adapter
        loaded={name:load_adapter(path,device="cpu",expected_contract=plan["featureMetadata"]["qwen"]["feature_contract"])[0] for name,path in adapters.items()}
        backend=ArdyBackend()
        backend_start=time.perf_counter()
        backend.load()
        report["backendLoadSeconds"]=time.perf_counter()-backend_start
        torch=backend.torch
        report["backend"]=backend.info
        histories={"cold":None} if "cold" in plan["histories"] else {}
        report["historyDiagnostics"]={}
        for name,path in history_paths.items():
            value=json.loads(path.read_text(encoding="utf-8-sig"))
            history,projection=backend.project_history(InitialHistory(**value.get("initialHistory",value)))
            histories[name]=history
            report["historyDiagnostics"][name]={"projection":projection,**history_diagnostics(backend,history)}
        index={row["id"]:i for i,row in enumerate(rows)}
        for row in selected:
            idx=index[row["id"]]
            with torch.inference_mode():
                raw=torch.tensor(qwen[idx:idx+1],dtype=torch.float32)
                vectors={"teacher":teacher[idx]}
                condition_cpu_times={"teacher":None}
                for name,model in loaded.items():
                    condition_start=time.perf_counter()
                    vectors[name]=model(raw)[0].numpy()
                    condition_cpu_times[name]=(time.perf_counter()-condition_start)*1000
            for name,vector in vectors.items():
                condition=torch.tensor(vector,device=backend.device,dtype=torch.float32).reshape(1,1,4096)
                for history_name,history in histories.items():
                    for seed in args.seeds:
                        identifier=f"{row['id']}--{name}--{history_name}--seed{seed}"
                        result=generate(backend,condition,history,seed,args.output,identifier,
                            description=row["text"],condition_source="offline-native-eval-"+name)
                        with np.load(args.output/(identifier+".npz"),allow_pickle=False) as arrays:
                            frames=measure_frames(arrays,backend.names,20)
                        measurement_path=args.output/(identifier+".measurements.json")
                        dump(measurement_path,frames)
                        assessment=assess(row,frames)
                        entry={"recordId":row["id"],"semanticGroup":row["semantic_group"],"family":row.get("family","unspecified"),
                            "evaluationTrack":row.get("evaluation_track","unlabeled"),"condition":name,
                            "history":history_name,"seed":seed,"clip":result["clip"],
                            "npz":str(args.output/(identifier+".npz")),"frameMeasurements":str(measurement_path),
                            "conditionSha256":hashlib.sha256(np.asarray(vector,dtype=np.float32).tobytes()).hexdigest(),
                            "conditionProvenance":{"source":"offline-verified-teacher-feature" if name=="teacher" else "offline-frozen-qwen-plus-adapter",
                                "featureBundleSha256":plan["teacherBundleSha256"] if name=="teacher" else plan["qwenBundleSha256"],
                                "featureContract":plan["featureMetadata"]["teacher" if name=="teacher" else "qwen"]["feature_contract"],
                                "adapterSha256":None if name=="teacher" else plan["adapters"][name]["sha256"],
                                "adapterProjectionCpuMilliseconds":condition_cpu_times[name],
                                "liveFeatureRequestPerformed":False},
                            "windows":result["windows"],"assessment":assessment,"sourceDiagnostics":result}
                        report["results"].append(entry)
                        report["groups"]=grouped_summary(report["results"])
                        dump(args.output/"report.json",report)
                        print(identifier,"generated; semantic status manual_pending",flush=True)
        report["status"]="complete"
        dump(args.output/"render-manifest.json",{"clips":[{"id":Path(r["clip"]).stem,"path":r["clip"]} for r in report["results"]]})
    except BaseException as error:
        report["status"]="failed"
        report["error"]={"type":type(error).__name__,"message":str(error)}
        raise
    finally:
        report["totalWallSeconds"]=time.perf_counter()-started
        report["finishedUtc"]=datetime.now(timezone.utc).isoformat()
        window_times=[window["milliseconds"] for row in report["results"] for window in row["windows"]]
        if window_times:
            report["generationWindowMilliseconds"]={"count":len(window_times),"mean":float(np.mean(window_times)),
                "median":float(np.median(window_times)),"p95":float(np.percentile(window_times,95)),"maximum":float(np.max(window_times))}
        dump(args.output/"report.json",report)
        if args.final_test:
            marker_value=json.loads(Path(marker).read_text())
            marker_value.update(status=report["status"],completedTrajectories=len(report["results"]),report=str((args.output/"report.json").resolve()))
            dump(marker,marker_value)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset",type=Path,required=True)
    parser.add_argument("--qwen-bundle",type=Path,required=True)
    parser.add_argument("--teacher-bundle",type=Path,required=True)
    parser.add_argument("--adapter",action="append",required=True,help="Repeat name=checkpoint.pt for each frozen old/new candidate; direct teacher is always included")
    parser.add_argument("--record-ids",required=True,help="Explicit comma-separated fixed IDs")
    parser.add_argument("--seeds",nargs="+",type=int,default=[0,1])
    parser.add_argument("--history",action="append",help="Repeat cold and/or name=actual-Unity-history.json; default cold")
    parser.add_argument("--split",choices=("val","test"),default="val")
    parser.add_argument("--final-test",action="store_true")
    parser.add_argument("--test-audit-marker",type=Path)
    parser.add_argument("--plan-only",action="store_true",help="Verify contracts and record a plan without loading any network or reserving final-test execution")
    parser.add_argument("--output",type=Path,required=True)
    run(parser.parse_args())
