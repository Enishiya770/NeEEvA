"""Bounded paired history ablation; never changes the production service or baselines."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import time
from pathlib import Path

import httpx
import numpy as np

from Server.ARDY.motion_service.backend import ArdyBackend, ROOT
from Server.ARDY.motion_service.protocol import GenerateRequest, InitialHistory
from Server.ARDY.motion_service.service import LiveFeatureProvider

PROMPTS = {
    "both-arms-overhead": "Raise both arms straight up above the head, palms facing forward",
    "explanatory-palms-up": "Extend both forearms slightly forward with palms facing up, in an explanatory gesture",
}


def rotation_stats(rotations):
    up = rotations[..., 1]
    forward = rotations[..., 2]
    tilt = np.degrees(np.arccos(up[..., 1].clip(-1,1)))
    pitch = np.degrees(np.arctan2(-forward[..., 1], np.hypot(forward[..., 0],forward[..., 2])))
    roll = np.degrees(np.arctan2(-up[...,0],up[...,1]))
    yaw = np.degrees(np.unwrap(np.arctan2(forward[...,0],forward[...,2])))
    return {"tiltMeanDegrees":float(tilt.mean()),"tiltMaxDegrees":float(tilt.max()),
            "pitchMinMaxDegrees":[float(pitch.min()),float(pitch.max())],
            "rollMinMaxDegrees":[float(roll.min()),float(roll.max())],
            "yawMinMaxDegrees":[float(yaw.min()),float(yaw.max())]}


def diagnostics(arrays, names):
    rotations, positions = arrays["global_rot_mats"].astype(np.float64), arrays["posed_joints"].astype(np.float64)
    hips = rotations[:, names.index("Hips")]
    chest = rotations[:, names.index("Spine3")]
    result = {"frames":len(rotations),"bones":{}}
    for name in ("Hips","Spine","Spine3","Neck","Head"):
        value = rotations[:, names.index(name)]
        result["bones"][name] = {"global":rotation_stats(value),"relativeHips":rotation_stats(hips.swapaxes(-1,-2) @ value)}
    wrists = positions[:,[names.index("LeftHand"),names.index("RightHand")]]
    head = positions[:,names.index("Head")]
    over_head = wrists[...,1]-head[:,None,1]
    chest_position = positions[:,names.index("Spine3")]
    in_chest = (chest[:,None].swapaxes(-1,-2) @ (wrists-chest_position[:,None])[...,None])[...,0]
    result["hands"] = {"aboveHeadMinMaxMeters": [[float(v.min()),float(v.max())] for v in over_head.T],
        "bothAboveHeadFraction":float(np.mean(np.min(over_head,axis=1)>0)),
        "leftAcrossChestMidlineFraction":float(np.mean(in_chest[:,0,0]<0)),
        "rightAcrossChestMidlineFraction":float(np.mean(in_chest[:,1,0]>0)),
        "minimumWristSeparationMeters":float(np.linalg.norm(wrists[:,0]-wrists[:,1],axis=-1).min()),
        "chestRelativeRangeXYZMeters":np.ptp(in_chest,axis=0).tolist(),
        "chestLocalForwardZMinMaxMeters":[[float(v.min()),float(v.max())] for v in in_chest[...,2].T],
        "chestLocalForwardDisplacementFromFirstFrameMinMaxMeters":[
            [float(v.min()),float(v.max())] for v in (in_chest[...,2]-in_chest[:1,...,2]).T],
        "forwardMeasurementConvention":"Left, right order; Core27 chest-local +Z is forward; displacement is relative to the first generated frame, not the input history."}
    result["rootRangeXYZMeters"] = np.ptp(arrays["root_positions"],axis=0).tolist()
    result["limitations"] = "Source Core27 geometry; hand-side and height diagnostics are not human naturalness or target-avatar collision judgments."
    return result


def semantic_evaluation(result, prompt_id):
    """A height diagnostic is relevant only to the explicit overhead task."""
    overhead = prompt_id in ("both-arms-overhead", "raise-actual", "raise-style")
    return {
        "overheadCriterionApplicable":overhead,
        "bothWristsAboveHeadObserved":bool(result["hands"]["bothAboveHeadFraction"] > 0) if overhead else None,
        "assessment":("Both wrists above the head is a necessary diagnostic for this command; it does not establish full pose, palm orientation or naturalness."
                      if overhead else "Explanatory gesture semantics require visual assessment of both forearms and palm orientation. Above-head fraction is not a success criterion; chest-local forward measurements are descriptive only."),
    }


def project_global_history(global_ardy):
    from Tools.MotionAdapter.export_unity import quaternion_objects, unity_rotations
    global_ardy = global_ardy.copy()
    global_ardy[:,[0,*range(19,27)]] = np.eye(3)
    rotations = quaternion_objects(unity_rotations(global_ardy),continuous=True)
    return InitialHistory(kind="unity-upper-body-projection-v1",frames=[{"globalRotations":q} for q in rotations])


def legacy_projected_history(backend, value):
    """Reproduce the diagnosed pre-fix Y=0 bug locally, even after production is fixed."""
    projected, metadata = backend.project_history(value)
    with backend.torch.inference_mode():
        pose = backend.model.motion_rep.inverse(projected,is_normalized=True)
        roots = pose["root_positions"].clone()
        roots[...,1] = 0
        result = backend.model.motion_rep(pose["local_rot_mats"],roots,to_normalize=True,to_canonicalize=False)
    return result, metadata


def history_diagnostics(backend, history):
    torch, model = backend.torch, backend.model
    history = history.to(backend.device)
    length = history.shape[1]
    with torch.inference_mode():
        pad = torch.ones((1,length),device=backend.device,dtype=torch.bool)
        lengths = torch.tensor([length],device=backend.device)
        hybrid,_ = model.hybrid.get_hybrid_motion_from_explicit(history,lengths,pad)
        reconstruction = model.hybrid.get_explicit_motion_from_hybrid(hybrid,pad,lengths)
        original_pose = model.motion_rep.inverse(history,is_normalized=True)
        decoded_pose = model.motion_rep.inverse(reconstruction,is_normalized=True)
    original_rot = original_pose["global_rot_mats"][0].cpu().numpy()
    decoded_rot = decoded_pose["global_rot_mats"][0].cpu().numpy()
    angles = np.degrees(np.arccos(((np.einsum("...ij,...ij->...",original_rot,decoded_rot)-1)/2).clip(-1,1)))
    normalized = history[0].cpu().numpy()
    heading = model.motion_rep.get_root_heading_angle(model.motion_rep.unnormalize(history))[0].cpu().numpy()
    canonical = model.motion_rep.canonicalize(history,normalized=True)
    return {"headingDegrees":np.degrees(heading).tolist(),
        "rootWorldYMinMax": [float(original_pose["root_positions"][...,1].min()),float(original_pose["root_positions"][...,1].max())],
        "feetWorldYMinMax": {name:[float(original_pose["posed_joints"][...,backend.names.index(name),1].min()),
                                   float(original_pose["posed_joints"][...,backend.names.index(name),1].max())]
                             for name in ("LeftFoot","LeftToeBase","RightFoot","RightToeBase")},
        "canonicalizedNormalizedMaxAbsDifference":float((canonical-history).abs().max()),
        "normalizedBlocks": {key:{"absP95":float(np.percentile(np.abs(normalized[:,sl]),95)),
                                      "absMax":float(np.abs(normalized[:,sl]).max())}
                             for key,sl in model.motion_rep.slice_dict.items()},
        "autoencoderReconstruction": {"globalRotationMeanDegrees":float(angles.mean()),
            "globalRotationMaxDegrees":float(angles.max()),
            "perBoneMaxDegrees":dict(zip(backend.names,map(float,angles.max(axis=0)))),
            "positionsMeanMeters":float(np.linalg.norm((original_pose["posed_joints"]-decoded_pose["posed_joints"])[0].cpu().numpy(),axis=-1).mean())}}


def generate(backend, condition, initial, seed, output, identifier, description=None, condition_source="qwen-adapter"):
    """Explicit seed is paired across histories; no character/revision hashing here."""
    from Tools.MotionAdapter.export_unity import build_clip
    torch, model = backend.torch, backend.model
    history = None if initial is None else initial.clone().to(backend.device)
    pieces, normalized, windows = [],[],[]
    with torch.random.fork_rng(devices=[torch.cuda.current_device()]):
        torch.random.default_generator.manual_seed(seed)
        torch.cuda.default_generators[torch.cuda.current_device()].manual_seed(seed)
        for window in range(3):
            count = 0 if history is None else history.shape[1]
            torch.cuda.synchronize()
            start = time.perf_counter()
            with torch.inference_mode():
                motion = model.autoregressive_step(num_frames=count+40,
                    num_denoising_steps=int(model.diffusion.num_base_steps),motion_mask=None,observed_motion=None,
                    cfg_weight=(2.,2.),text_feat=condition,text_pad_mask=torch.ones((1,1),device=backend.device,dtype=torch.bool),
                    init_history_sequence=history)
                decoded = model.motion_rep.inverse(motion,is_normalized=True)
            torch.cuda.synchronize()
            arrays = {k:v[0,count:].float().cpu().numpy() for k,v in decoded.items() if torch.is_tensor(v)}
            pieces.append(arrays)
            normalized.append(motion[0,count:].float().cpu().numpy())
            windows.append({"window":window,"historyFrames":count,"milliseconds":(time.perf_counter()-start)*1000})
            history = motion[:,-16:].clone()
    arrays = {k:np.concatenate([p[k] for p in pieces]) for k in pieces[0]}
    clip = build_clip({**arrays,"fps":np.asarray(20),"text":np.asarray(description or identifier),
                      "joint_names":np.asarray(backend.names),"joint_parents":backend.parents},
        clip_id=identifier,expected_names=backend.names,expected_parents=backend.parents,neutral_joints=backend.neutral,
        source={"mode":"diagnostic-native-ardy-paired-history-ablation","effectiveSeed":seed,
                "conditionSource":condition_source,
                "languageModelsLoaded":False,"naturalnessAccepted":False})
    clip_path = output/(identifier+".json")
    clip_path.write_text(json.dumps(clip,separators=(",", ":")),encoding="utf-8")
    np.savez(output/(identifier+".npz"),**arrays,normalized_motion=np.concatenate(normalized))
    return {"id":identifier,"clip":str(clip_path),"windows":windows,**diagnostics(arrays,backend.names)}


async def main_async(args):
    output = args.output
    output.mkdir(parents=True,exist_ok=True)
    provider = LiveFeatureProvider(args.feature_url)
    feature_data = {}
    for name, text in PROMPTS.items():
        request = GenerateRequest(characterId="diagnostic",turnId=name,revision=1,requestId="diagnostic-"+name,
                                  chunkIndex=0,description=text)
        if args.reuse_features:
            feature = {"embedding":np.load(args.reuse_features/(name+"-qwen-live.npy"),allow_pickle=False),
                       "source":"frozen-previously-measured-live-feature-for-paired-ablation",
                       "modelSha256":"071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4"}
        else:
            feature = await provider.fetch(request,20)
        feature_data[name] = feature
        np.save(output/(name+"-qwen-live.npy"),feature["embedding"])
    backend = ArdyBackend()
    backend.load()
    torch = backend.torch
    histories = {"cold":None}
    neutral = project_global_history(np.tile(np.eye(3),(16,27,1,1)))
    histories["neutral16"], _ = legacy_projected_history(backend,neutral)
    if args.history:
        value = json.loads(args.history.read_text(encoding="utf-8-sig"))
        if "initialHistory" in value:
            value = value["initialHistory"]
        histories["unity-idle16"], _ = legacy_projected_history(backend,InitialHistory(**value))
    if args.history_hips_relative:
        value = json.loads(args.history_hips_relative.read_text(encoding="utf-8-sig"))
        if "initialHistory" in value:
            value = value["initialHistory"]
        histories["unity-idle-hips-relative16"], _ = legacy_projected_history(backend,InitialHistory(**value))
    approved = np.load(ROOT/"Server/ARDY/runtime/unity-preview/left-wave.npz",allow_pickle=False)
    histories["native-left-tail16"] = torch.tensor(approved["normalized_motion"][-16:][None],device=backend.device)
    histories["projected-left-tail16"], _ = legacy_projected_history(backend,project_global_history(approved["global_rot_mats"][-16:]))
    grounded_height = float(-backend.neutral[:,1].min())
    if args.grounded_only:
        grounded = {}
        for name,history in histories.items():
            if name in ("cold","native-left-tail16"):
                continue
            with torch.inference_mode():
                pose = backend.model.motion_rep.inverse(history,is_normalized=True)
                roots = pose["root_positions"].clone()
                roots[...,1] = grounded_height
                grounded[name+"-grounded"] = backend.model.motion_rep(pose["local_rot_mats"],roots,to_normalize=True,to_canonicalize=False)
        histories = grounded
    report = {"schema":1,"purpose":"Two reported prompts, paired history ablation at effective seeds 0 and 1; no production mutation",
        "prompts":PROMPTS,"seeds":[0,1],"historyFile":str(args.history) if args.history else None,
        "sourceLimits":["No exact replay of the user's failed request: old service did not save request/history/response.",
            "Input/output rotation roundtrip proves representation consistency, not history likelihood under ARDY's learned tokenizer.",
            "Both tested bilateral combinations are outside the pilot adapter's single-arm raise/extend training templates."],
        "backend":backend.info,"histories":{},"results":[],"featureDiagnostics":{},
        "sourceNeutralHips":backend.neutral[0].tolist(),"groundedSourcePelvisHeightMeters":grounded_height,
        "heightAblation":bool(args.grounded_only),"frozenFeaturesDirectory":str(args.reuse_features) if args.reuse_features else None}
    for name,history in histories.items():
        if history is not None:
            report["histories"][name] = history_diagnostics(backend,history)
    rows = [json.loads(line) for line in (ROOT/"Tools/MotionAdapter/runtime/prompts.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    with np.load(ROOT/"Tools/MotionAdapter/runtime/qwen-2000.npz",allow_pickle=False) as data:
        cache = data["features"].astype(np.float32)
    with np.load(ROOT/"Tools/MotionAdapter/runtime/teacher-2000.npz",allow_pickle=False) as data:
        teacher = data["features"].astype(np.float32)
    for name, feature in feature_data.items():
        with torch.inference_mode():
            condition = backend.adapter(torch.tensor(feature["embedding"][None],device=backend.device)).reshape(1,1,4096)
        qwen = feature["embedding"]
        adapted = condition[0,0].cpu().numpy()
        def nearest(matrix,vector):
            scores = matrix @ vector/(np.linalg.norm(matrix,axis=1)*np.linalg.norm(vector))
            return [{"id":rows[i]["id"],"text":rows[i]["text"],"family":rows[i]["family"],"split":rows[i]["split"],"cosine":float(scores[i])}
                    for i in np.argsort(-scores)[:5]]
        report["featureDiagnostics"][name] = {"featureSource":feature["source"],"modelSha256":feature["modelSha256"],
            "qwenNorm":float(np.linalg.norm(qwen)),"adaptedNorm":float(np.linalg.norm(adapted)),
            "nearestCachedQwen":nearest(cache,qwen),"nearestCachedTeacherToAdapterOutput":nearest(teacher,adapted),
            "limitation":"Nearest template proximity does not identify the correct unseen teacher feature or prove adapter causality."}
        for variant, history in histories.items():
            for seed in (0,1):
                identifier=f"{name}-{variant}-seed{seed}"
                result=generate(backend,condition,history,seed,output,identifier,description=PROMPTS[name])
                result.update(prompt=name,history=variant,effectiveSeed=seed)
                result["semanticEvaluation"] = semantic_evaluation(result,name)
                report["results"].append(result)
                print(identifier,"Hips tilt",round(result["bones"]["Hips"]["global"]["tiltMaxDegrees"],2),
                      "Spine3 tilt",round(result["bones"]["Spine3"]["global"]["tiltMaxDegrees"],2),
                      "relative",round(result["bones"]["Spine3"]["relativeHips"]["tiltMaxDegrees"],2),flush=True)
                args.report.parent.mkdir(parents=True,exist_ok=True)
                args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    report["complete"] = True
    report["scriptSha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    (output/"render-manifest.json").write_text(json.dumps({"clips":[{"id":r["id"],"path":r["clip"]} for r in report["results"]]},indent=2),encoding="utf-8")


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--feature-url",default="http://127.0.0.1:8080")
    parser.add_argument("--history",type=Path)
    parser.add_argument("--history-hips-relative",type=Path)
    parser.add_argument("--grounded-only",action="store_true")
    parser.add_argument("--reuse-features",type=Path)
    parser.add_argument("--output",type=Path,default=ROOT/"Server/ARDY/runtime/generated-motion-diagnosis")
    parser.add_argument("--report",type=Path,default=ROOT/"Tools/MotionAdapter/reports/generated-motion-history-ablation.json")
    asyncio.run(main_async(parser.parse_args()))
