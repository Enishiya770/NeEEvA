"""CPU-only comparison of the fixed four-text Qwen/teacher motion experiment."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from Server.ARDY.motion_service.backend import ROOT


def event_summary(active, fps=20):
    active = np.asarray(active, dtype=bool)
    indices = np.flatnonzero(active)
    longest = current = 0
    for value in active:
        current = current + 1 if value else 0
        longest = max(longest, current)
    return {"frameFraction":float(active.mean()),
            "firstObservedSeconds":float(indices[0]/fps) if len(indices) else None,
            "longestContinuousSeconds":longest/fps}


def trajectory_measurements(row):
    path = Path(row["clip"])
    clip = json.loads(path.read_text(encoding="utf-8"))
    names = clip["jointNames"]
    with np.load(path.with_suffix(".npz"),allow_pickle=False) as data:
        positions = data["posed_joints"].astype(np.float64)
        rotations = data["global_rot_mats"].astype(np.float64)
    chest = names.index("Spine3")
    inverse_chest = rotations[:,chest].swapaxes(-1,-2)
    wrists = positions[:,[names.index("LeftHand"),names.index("RightHand")]]
    height_margin = wrists[...,1]-positions[:,names.index("Head"),None,1]
    both_margin = height_margin.min(axis=1)
    result = {"overheadNecessaryEvent":{
        "applicable":row["promptId"].startswith("raise-"),
        "bothWristsAboveHead":event_summary(both_margin>0),
        "bothWristsAtLeast5CmAboveHead":event_summary(both_margin>.05),
        "maximumSimultaneousHeightMarginMeters":float(both_margin.max()),
        "note":"Source skeleton necessary height event only; not full palm/posture/naturalness success."},
        "forearms":{},"palmOrientationEvaluated":False,"naturalnessEvaluated":False}
    for side in ("Left","Right"):
        shoulder,elbow,wrist=[positions[:,names.index(side+name)] for name in ("Arm","ForeArm","Hand")]
        forearm=wrist-elbow
        forearm=forearm/np.linalg.norm(forearm,axis=1,keepdims=True)
        toward_shoulder=shoulder-elbow
        toward_shoulder=toward_shoulder/np.linalg.norm(toward_shoulder,axis=1,keepdims=True)
        flexion=180-np.degrees(np.arccos(np.einsum("ti,ti->t",forearm,toward_shoulder).clip(-1,1)))
        direction=(inverse_chest@forearm[...,None])[...,0]
        wrist_local=(inverse_chest@(wrist-positions[:,chest])[...,None])[...,0]
        result["forearms"][side.lower()]={
            "elbowFlexionMinMedianMaxDegrees":[float(flexion.min()),float(np.median(flexion)),float(flexion.max())],
            "chestLocalForwardCosineMinMedianMax":[float(direction[:,2].min()),float(np.median(direction[:,2])),float(direction[:,2].max())],
            "wristChestLocalZMinMedianMaxMeters":[float(wrist_local[:,2].min()),float(np.median(wrist_local[:,2])),float(wrist_local[:,2].max())],
            "assessment":"Descriptive geometry only. Neither forward wrist position nor elbow flexion establishes a palms-up explanatory gesture."}
    return result


def main(args):
    qwen=json.loads(args.qwen.read_text(encoding="utf-8"))
    teacher=json.loads(args.teacher.read_text(encoding="utf-8"))
    for key in ("prompts","seeds","historySha256"):
        if qwen[key]!=teacher[key]:
            raise ValueError(f"Unpaired experiment field: {key}")
    for key in ("ardyRevision","coreRevision","adapterSha256"):
        if qwen["backend"][key]!=teacher["backend"][key]:
            raise ValueError(f"Experiment backend changed: {key}")
    key=lambda r:(r["promptId"],r["historyKind"],r["effectiveSeed"])
    qmap={key(r):r for r in qwen["results"]}
    tmap={key(r):r for r in teacher["results"]}
    if len(qmap)!=16 or set(qmap)!=set(tmap):
        raise ValueError("Expected the exact same sixteen diagnostic cases")
    report={"schema":1,"method":"Fixed text, explicit seed, initial history and pinned ARDY; only the text condition changes. Reuses unchanged previously measured live-Qwen trajectories and saved features; CPU-only postprocessing.",
        "inputs":{str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in (args.qwen,args.teacher,args.qwen_features)},
        "teacherControlChecks":teacher["teacherControlChecks"],"pairs":[],"featureErrors":[],
        "languageModelsLoaded":False,"gpuUsedForThisComparison":False}
    for identity,qrow in qmap.items():
        trow=tmap[identity]
        report["pairs"].append({"promptId":identity[0],"historyKind":identity[1],"seed":identity[2],
            "qwen":{"clip":qrow["clip"],"spineTiltMaxDegrees":qrow["bones"]["Spine3"]["global"]["tiltMaxDegrees"],**trajectory_measurements(qrow)},
            "teacher":{"clip":trow["clip"],"spineTiltMaxDegrees":trow["bones"]["Spine3"]["global"]["tiltMaxDegrees"],**trajectory_measurements(trow)}})
    # Only the small adapter is evaluated on CPU; teacher vectors were exported earlier.
    import torch
    from Tools.MotionAdapter.adapter import load_adapter
    from Tools.MotionAdapter.data import read_bundle
    adapter,_=load_adapter(ROOT/"Tools/MotionAdapter/runtime/mlp-seed1.pt",device="cpu")
    with np.load(args.qwen_features,allow_pickle=False) as data:
        if data["ids"].tolist()!=[r["id"] for r in qwen["prompts"]] or data["texts"].tolist()!=[r["text"] for r in qwen["prompts"]]:
            raise ValueError("Frozen Qwen texts/IDs do not match the compared experiment")
        raw=data["features"].astype(np.float32)
    with np.load(teacher["teacherFeatures"],allow_pickle=False) as data:
        ids=data["ids"].tolist()
        expected=np.stack([data["features"][ids.index(r["id"])] for r in qwen["prompts"]])
    with torch.inference_mode():
        predicted=adapter(torch.from_numpy(raw)).numpy()
    for row,pred,goal in zip(qwen["prompts"],predicted,expected):
        report["featureErrors"].append({"promptId":row["id"],
            "cosine":float(pred@goal/(np.linalg.norm(pred)*np.linalg.norm(goal))),
            "relativeL2":float(np.linalg.norm(pred-goal)/np.linalg.norm(goal)),
            "predictedToTeacherNormRatio":float(np.linalg.norm(pred)/np.linalg.norm(goal))})
    overhead=[p for p in report["pairs"] if p["promptId"].startswith("raise-")]
    report["overheadSummary"]={"caseCount":len(overhead),**{kind+"CasesWithBothWristsAboveHead":sum(p[kind]["overheadNecessaryEvent"]["bothWristsAboveHead"]["frameFraction"]>0 for p in overhead) for kind in ("qwen","teacher")}}
    report["interpretation"]=[
        "The conditioned feature path is a material bottleneck for these overhead prompts: direct teacher conditioning unlocks a capability missed by the current Qwen-adapter output.",
        "This does not prove that the small MLP alone, rather than its training coverage or Qwen representation plus mapping, causes every failure.",
        "Teacher conditioning still fails one actual-prompt/grounded-history seed. History and stochastic generation remain relevant.",
        "Explanatory motions receive forearm/elbow descriptors only; palm orientation and overall semantics remain unscored pending visual assessment.",
        "Four prompts, two seeds and one captured idle history do not establish broad action generalization."]
    args.output.write_text(json.dumps(report,indent=2),encoding="utf-8")
    choices=[("raise-actual","grounded-unity-idle16",0),("raise-actual","grounded-unity-idle16",1),
             ("raise-style","grounded-unity-idle16",1),("explain-actual","grounded-unity-idle16",0)]
    clips=[]
    for identity in choices:
        for kind,mapping in (("qwen",qmap),("teacher",tmap)):
            row=mapping[identity]
            clips.append({"id":f"{identity[0]}-{kind}-grounded-seed{identity[2]}","path":row["clip"]})
    args.manifest.write_text(json.dumps({"clips":clips,"purpose":"Matched successful and failed overhead cases plus an explanatory case requiring visual assessment; no baseline substitution."},indent=2),encoding="utf-8")
    print(json.dumps({"pairs":len(report["pairs"]),"overheadSummary":report["overheadSummary"],"featureErrors":report["featureErrors"],"manifest":str(args.manifest)},indent=2))


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    reports=ROOT/"Tools/MotionAdapter/reports"
    parser.add_argument("--qwen",type=Path,default=reports/"generated-motion-qwen-condition-ablation.json")
    parser.add_argument("--teacher",type=Path,default=reports/"generated-motion-teacher-condition-ablation.json")
    parser.add_argument("--qwen-features",type=Path,default=ROOT/"Server/ARDY/runtime/generated-condition-qwen-diagnosis/qwen-features.npz")
    parser.add_argument("--output",type=Path,default=reports/"generated-motion-paired-condition-comparison.json")
    parser.add_argument("--manifest",type=Path,default=ROOT/"Server/ARDY/runtime/generated-condition-teacher-diagnosis/paired-render-manifest.json")
    main(parser.parse_args())
