"""Source-space observations for structured labels; no automatic semantic success."""
from __future__ import annotations

import warnings

import numpy as np
from scipy.spatial.transform import Rotation

from Server.ARDY.compare_condition_diagnostics import event_summary


def describe(values):
    values=np.asarray(values,dtype=np.float64)
    return {"minimum":np.min(values,axis=0).tolist(),"maximum":np.max(values,axis=0).tolist(),
            "mean":np.mean(values,axis=0).tolist(),"peakToPeak":np.ptp(values,axis=0).tolist()}


def measure_frames(arrays,names,fps=20):
    p=np.asarray(arrays["posed_joints"],dtype=np.float64)
    r=np.asarray(arrays["global_rot_mats"],dtype=np.float64)
    if p.ndim!=3 or p.shape[1:]!=(len(names),3) or r.shape!=(*p.shape[:2],3,3) or len(p)<2:
        raise ValueError("Expected a finite multi-frame Core27 pose sequence")
    if not np.isfinite(p).all() or not np.isfinite(r).all():
        raise ValueError("Motion contains non-finite values")
    index={name:names.index(name) for name in ("Hips","Spine3","Head","LeftArm","RightArm","LeftHand","RightHand")}
    wrists=p[:,[index["LeftHand"],index["RightHand"]]]
    # Arm is the humeral shoulder pivot; Shoulder is the medial clavicle pivot in Core27.
    shoulders=p[:,[index["LeftArm"],index["RightArm"]]]
    chest=p[:,index["Spine3"]]
    hips=p[:,index["Hips"]]
    inverse_chest=r[:,index["Spine3"]].swapaxes(-1,-2)
    inverse_hips=r[:,index["Hips"]].swapaxes(-1,-2)
    wrist_chest=(inverse_chest[:,None]@(wrists-chest[:,None])[...,None])[...,0]
    shoulder_hips=(inverse_hips[:,None]@(shoulders-hips[:,None])[...,None])[...,0]
    angular={}
    gimbal=[]
    for key,value in (("head",inverse_chest@r[:,index["Head"]]),("upperTorso",inverse_hips@r[:,index["Spine3"]])):
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            euler=Rotation.from_matrix(value).as_euler("xyz")
        if caught:
            gimbal.append(key)
        angles=np.rad2deg(np.unwrap(euler,axis=0))
        angular[key]={axis:angles[:,i].tolist() for i,axis in enumerate(("pitch","yaw","roll"))}
        step=value[1:]@value[:-1].swapaxes(-1,-2)
        angular[key]["sampledGeodesicSpeedDegreesPerSecond"]=(np.rad2deg(Rotation.from_matrix(step).magnitude())*fps).tolist()
    heights={"shoulder":(wrists[...,1]-shoulders[...,1]).tolist(),
             "head":(wrists[...,1]-p[:,index["Head"],None,1]).tolist(),
             "chest":(wrists[...,1]-chest[:,None,1]).tolist()}
    return {"fps":fps,"frames":len(p),"timesSeconds":(np.arange(len(p))/fps).tolist(),
        "coordinates":{"positions":"Source Core27 RH, +Y up, +Z forward; left/right arrays in that order.",
            "wristRelativeHeight":"World +Y wrist minus selected reference; shoulder reference is Arm (humeral pivot), not medial clavicle.",
            "wristExcursion":"Euclidean displacement from first generated wrist position, measured in each frame's upper-chest coordinates.",
            "shoulderHeightChange":"Humeral shoulder height in Hips coordinates minus its first generated-frame height.",
            "headAngles":"inverse(Spine3 rotation) * Head rotation, scipy extrinsic xyz Euler degrees, temporally unwrapped.",
            "upperTorsoAngles":"inverse(Hips rotation) * Spine3 rotation, same explicit Euler convention.",
            "angularSpeed":"Adjacent geodesic rotation angle times FPS; sampled average interval speed, not continuous peak."},
        "gimbalLockWarnings":gimbal,
        "wristRelativeHeightMeters":heights,
        "wristChestLocalPositionMeters":wrist_chest.tolist(),
        "wristExcursionMeters":np.linalg.norm(wrist_chest-wrist_chest[:1],axis=-1).tolist(),
        "shoulderHeightChangeMeters":(shoulder_hips[...,1]-shoulder_hips[:1,...,1]).tolist(),
        "interWristDistanceMeters":np.linalg.norm(wrists[:,0]-wrists[:,1],axis=-1).tolist(),
        "anglesDegrees":angular,
        "rootPositionMeters":np.asarray(arrays["root_positions"]).tolist()}


def selected_side(values,spec):
    side=spec.get("side")
    if side not in ("left","right","both"):
        raise ValueError("Structured observation needs side left, right or both")
    return np.asarray(values)[:,[0,1] if side=="both" else [0 if side=="left" else 1]]


def observe(spec,frames):
    metric=spec.get("metric")
    result={"specification":spec,"status":"measured","thresholdSatisfied":None}
    if metric in ("event_order","repetition_count"):
        return {**result,"status":"manual_pending","note":"Event recognition is not implemented; requested order/count is retained for review, never marked passed."}
    if metric=="wrist_relative_height_m":
        values=selected_side(frames["wristRelativeHeightMeters"][spec["reference"]],spec)
    elif metric=="wrist_excursion_m":
        values=selected_side(frames["wristExcursionMeters"],spec)
    elif metric=="shoulder_height_change_m":
        if spec.get("reference")!="hips":
            raise ValueError("Shoulder height change is defined only in Hips coordinates")
        values=selected_side(frames["shoulderHeightChangeMeters"],spec)
    elif metric=="inter_wrist_distance_m":
        values=np.asarray(frames["interWristDistanceMeters"])[:,None]
    elif metric in ("head_rotation_excursion_deg","upper_torso_rotation_excursion_deg"):
        target="head" if metric.startswith("head_") else "upperTorso"
        expected_ref="upper_chest" if target=="head" else "hips"
        if spec.get("reference")!=expected_ref or spec.get("axis") not in ("pitch","yaw","roll"):
            raise ValueError("Invalid structured angular observation basis/axis")
        values=np.asarray(frames["anglesDegrees"][target][spec["axis"]])[:,None]
        result["excursionDegrees"]=float(np.ptp(values))
        if target in frames["gimbalLockWarnings"]:
            result["status"]="measured_with_gimbal_lock_warning"
    else:
        return {**result,"status":"unsupported_manual_pending","note":"No implemented observation for this explicit metric; no inference from prompt text."}
    result["statistics"]=describe(values)
    expectation=spec.get("expectation","report_only")
    active=None
    if metric=="wrist_relative_height_m" and expectation=="reach_band":
        band=np.asarray(spec["band_m"],dtype=float)
        if band.shape!=(2,) or not np.isfinite(band).all() or band[0]>band[1]:
            raise ValueError("Invalid explicit height band")
        active=np.all((values>=band[0])&(values<=band[1]),axis=1)
    elif metric=="wrist_relative_height_m" and expectation in ("above","below"):
        threshold=float(spec["threshold_m"])
        if not np.isfinite(threshold):
            raise ValueError("Invalid explicit height threshold")
        active=np.all(values>=threshold if expectation=="above" else values<=threshold,axis=1)
    elif expectation!="report_only":
        result["status"]="measured_criterion_manual_pending"
        result["note"]="This label has no supported explicit numerical gate; low activity, event shape or other intent is not assigned an invented threshold."
    if active is not None:
        result["thresholdSatisfied"]=bool(active.any())
        result["simultaneousEvent"]=event_summary(active,frames["fps"])
        result["note"]="Necessary geometric observation only. For side=both the criterion must hold in the same frame."
    return result


def assess(row,frames):
    labels=row.get("assessment",{})
    if labels.get("automatic_semantic_pass",False):
        raise ValueError("This evaluator never grants automatic overall semantic success")
    return {"semanticPass":None,"semanticStatus":"manual_pending",
        "includeInSuccessSummary":bool(row.get("include_in_success_summary",False)),
        "observations":[observe(spec,frames) for spec in labels.get("machine_observations",[])],
        "manualChecks":labels.get("manual_checks",["No structured acceptance labels supplied; review intent and naturalness manually."]),
        "sourceOnly":True,"targetAvatarCollisionEvaluated":False,"palmOrientationEvaluated":False}
