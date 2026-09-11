"""CPU summary of archived traces and the fixed eight live HTTP probes."""
from __future__ import annotations

import numpy as np

from Server.ARDY.diagnose_composition_traces import OUT, ROOT, angle, dump, matrices, read, sha


def angular_coupling(clip):
    r = matrices(clip["frames"])
    names = clip["jointNames"]
    groups = {"upperArmRelativeChest": [], "forearmRelativeUpperArm": [], "handRelativeForearm": []}
    for side in ("Left", "Right"):
        for label, parent, bone in (("upperArmRelativeChest", "Spine3", side+"Arm"),
                                    ("forearmRelativeUpperArm", side+"Arm", side+"ForeArm"),
                                    ("handRelativeForearm", side+"ForeArm", side+"Hand")):
            groups[label].append(r[:, names.index(parent)].swapaxes(-1, -2) @ r[:, names.index(bone)])
    result = []
    for start in (0, 40, 80):
        row = {"startSeconds": start/20, "endSecondsInclusive": (start+39)/20}
        for label, sides in groups.items():
            values = np.stack(sides, axis=1)[start:start+40]
            step = angle(values[1:], values[:-1])
            excursion = angle(values, values[:1])
            row[label] = {"leftRightRotationTravelDegrees": step.sum(0).tolist(),
                          "leftRightMaxDeviationFromWindowFirstDegrees": excursion.max(0).tolist(),
                          "leftRightMaxSampledSpeedDegreesPerSecond": (step*20).max(0).tolist()}
        result.append(row)
    return result


def main():
    trace_report = read(OUT/"report.json")
    http_path = OUT/"http-probes/report.json"
    probes = read(http_path)
    assert probes["status"] == "complete" and len(probes["results"]) == 8
    assert all(len(case["chunks"]) == 3 and all(chunk["httpStatus"] == 200 for chunk in case["chunks"]) and case["cancellation"]["httpStatus"] == 200 for case in probes["results"])
    rows = []
    for case in probes["results"]:
        clip = read(case["clip"])
        coupling = angular_coupling(clip)
        row = {"id": case["id"], "history": case["history"], "seed": case["seed"], "description": case["description"],
               "clip": case["clip"], "clipSha256": sha(case["clip"]), "frameMeasurements": case["frameMeasurements"],
               "initialPoseNEVA": case["metrics"]["NEVA_fullblend_UpperBody"]["initialHistory"]["samples"][-1],
               "lastTwoSecondsNEVA": case["metrics"]["NEVA_fullblend_UpperBody"]["returnedMotion"]["windows"][-1],
               "lastTwoSecondsCore27": case["metrics"]["core27"]["returnedMotion"]["windows"][-1],
               "angularCoupling": coupling, "semanticPass": None}
        rows.append(row)
    result = {"schema": 1, "sourceTraceReport": str(OUT/"report.json"), "sourceTraceReportSha256": sha(OUT/"report.json"),
              "sourceHttpReport": str(http_path), "sourceHttpReportSha256": sha(http_path), "summaryScriptSha256": sha(__file__),
              "scope": "Eight actual archived motion traces plus exactly eight predeclared current-service HTTP trajectories; no additional text or seed search.",
              "targetAssetAssumption": "NEVA.vrm is a concrete asset-based CPU mapping reconstruction. These traces do not identify the actual user scene model, and this analysis does not claim to record current VRM output.",
              "coordinates": trace_report["coordinateBasis"],
              "couplingBasis": "Geodesic relative rotations: chest-to-upper-arm, upper-arm-to-forearm and forearm-to-hand, left/right order. Fixed target-rest conjugations preserve angular distances under the current fully blended UpperBody global-rotation mapping. Travel is the sum of adjacent 20Hz angle increments, not net amplitude or repeated-wave count.",
              "verifiedHttp": {"trajectories": 8, "successfulGenerationResponses": 24, "successfulPrivateRevisionCancellations": 8,
                               "initialHealth": probes["initialHealth"], "finalHealth": probes["finalHealth"]},
              "conclusions": [
                  "The archive distinguishes forward from lateral extension: revision42 lateral reach is mostly X, while revision50 forward reach is mostly +Z. It does not support a simple global axis swap as the cause.",
                  "Revision50 completed 12.781 seconds before revision54 began. Revision54 input wrists were near the captured idle posture, not the previous forward-returned endpoint. Separate turns did not preserve the held pose.",
                  "The generated then-wave clips contain wrist-local motion but substantial elbow/arm motion as well; they are not numerically motionless. Wrist rotational travel alone does not prove gentle side-to-side waving.",
                  "Using exactly the last sixteen returned revision50 frames gives a forward input (no peak selection), but original-text generation still bends/lowers the arms. An initial pose is not an enforced six-second pose constraint.",
                  "With the explicit wrist-only text, both idle-start probes end near arms-down, while both generated-history probes have large arm/forearm motion. These eight cases do not show that wording plus carried history reliably produces stationary straight arms and small wrist motion.",
                  "Maintaining an observed held posture and controlling wrist motion separately is therefore a distinct capability to validate, not something established by the current native ARDY endpoint. No production fix or new model selection was made in this diagnosis."
              ],
              "limitations": ["No overall semantic or naturalness success score.", "No automatic inference of palm orientation, hand-waving direction or exactly two repetitions.", "No Unity render or blend/Animator replay was performed; returned cancelled future frames are not assumed played.", "No production service, model or Player was edited; the HTTP probes used the already running service and retained exact response provenance."],
              "probes": rows}
    output = ROOT/"Tools/MotionAdapter/reports/composition-diagnosis-v1.json"
    dump(output, result)
    print(output)
    for row in rows:
        angles = row["angularCoupling"][-1]
        print(row["id"], {name: {"travel": value["leftRightRotationTravelDegrees"], "maxDeviation": value["leftRightMaxDeviationFromWindowFirstDegrees"]} for name, value in angles.items() if isinstance(value, dict)})


if __name__ == "__main__":
    main()
