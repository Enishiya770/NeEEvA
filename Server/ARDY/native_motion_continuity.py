"""Unblended source-space continuity proxies, not avatar comfort ratings."""
import numpy as np


UPPER_BODY=("Spine","Spine2","Spine3","Neck","Head","LeftShoulder","LeftArm","LeftForeArm","LeftHand","RightShoulder","RightArm","RightForeArm","RightHand")


def angles(a,b):
    cosine=(np.einsum("...ij,...ij->...",np.asarray(a,dtype=np.float64),np.asarray(b,dtype=np.float64))-1)/2
    return np.degrees(np.arccos(cosine.clip(-1,1)))


def continuity_observations(input_pose,generated,names,fps=20):
    indices=[names.index(name) for name in UPPER_BODY]
    history=np.asarray(input_pose["global_rot_mats"],dtype=np.float64)
    current=np.asarray(generated["global_rot_mats"],dtype=np.float64)
    joined=np.concatenate([history[-1:],current])
    steps=angles(joined[1:,indices],joined[:-1,indices])
    local_jump=angles(generated["local_rot_mats"][0,indices],input_pose["local_rot_mats"][-1,indices])
    wrists=[names.index("LeftHand"),names.index("RightHand")]
    wrist_jump=np.linalg.norm(generated["posed_joints"][0,wrists]-input_pose["posed_joints"][-1,wrists],axis=-1)
    return {"scope":"13 retargeted upper-body source bones, before Unity blend/retargeting; first interval joins the actual input-history endpoint to generated frame zero. No automatic naturalness threshold.",
        "fps":fps,"boneNames":list(UPPER_BODY),
        "firstFrameGlobalRotationJumpDegrees":{"perBone":steps[0].tolist(),"mean":float(steps[0].mean()),"maximum":float(steps[0].max())},
        "firstFrameLocalRotationJumpDegrees":{"perBone":local_jump.tolist(),"mean":float(local_jump.mean()),"maximum":float(local_jump.max())},
        "firstFrameSourceWristDisplacementMetersLeftRight":wrist_jump.tolist(),
        "lastInputHistoryGlobalStepDegrees":angles(history[-1,indices],history[-2,indices]).tolist(),
        "perFrameUpperBodyGlobalStepDegrees":steps.tolist(),
        "perFrameUpperBodyGlobalSpeedDegreesPerSecond":(steps*fps).tolist(),
        "speedSummaryIncludingInitialJoin":{"maximum":float(steps.max()*fps),"p95":float(np.percentile(steps*fps,95))},
        "speedSummaryGeneratedFramesOnly":{"maximum":float(steps[1:].max()*fps),"p95":float(np.percentile(steps[1:]*fps,95))},
        "windowSeams":[{"firstNewFrame":i,"seconds":i/fps,"perBoneStepDegrees":steps[i].tolist(),"maximumStepDegrees":float(steps[i].max())} for i in (40,80) if i<len(current)],
        "actualAvatarContinuityEvaluated":False,"naturalnessPass":None}
