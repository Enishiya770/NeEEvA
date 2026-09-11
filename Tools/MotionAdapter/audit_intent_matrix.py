"""Read-only CPU provenance and independent necessary-geometry audit of the fixed eight rows.

Never executes motion or a model. The original report and every failure stay unchanged.
"""
from __future__ import annotations
import argparse
import difflib
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess

from validate_intent_consistency import ROOT, MONO, exclusive_json, utc


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def verify(condition, message):
    if not condition:
        raise ValueError(message)


def vector(value):
    return [value[key] for key in ("x", "y", "z")]


def minus(left, right):
    return [a - b for a, b in zip(vector(left), vector(right))]


def magnitude(value):
    return math.sqrt(sum(x*x for x in value))


def measured_inside(actual, side, goal):
    arm = actual[side]
    verify(actual["available"] and arm["available"], "Unavailable actual arm")
    length = magnitude(minus(arm["elbowCharacter"], arm["shoulderCharacter"])) + magnitude(minus(arm["wristCharacter"], arm["elbowCharacter"]))
    verify(abs(length - arm["armLengthMeters"]) < 1e-5, "Actual arm length mismatch")
    if goal == "near-head":
        verify(actual["headAvailable"], "Unavailable actual head")
        delta = [x / length for x in minus(arm["wristCharacter"], actual["headCharacter"])]
        return magnitude(delta) <= .55 and -.30 <= delta[1] <= .55 and delta[0] * (-1 if side == "left" else 1) >= .08
    verify(goal == "forward", "This frozen matrix only assesses near-head/forward")
    delta = minus(arm["wristCharacter"], arm["shoulderCharacter"])
    distance = magnitude(delta)
    angle = math.degrees(math.acos(max(-1, min(1, delta[2] / distance))))
    return distance / length >= .55 and angle <= 30


def exact_json_hashes(trace_paths):
    # Use the same JSON number serialization library solely for hash reproduction.
    # Goal classification below is separately recomputed from stored actual positions.
    build = ROOT / "Server/ARDY/runtime/intent-consistency-cpu/matrix-audit"
    build.mkdir(parents=True, exist_ok=True)
    source = build / "HashTrace.cs"
    source.write_text('''using System; using System.IO; using System.Text; using System.Security.Cryptography;
using Newtonsoft.Json; using Newtonsoft.Json.Linq;
class HashTrace {
static string H(JToken v) { using(var s=SHA256.Create()) return BitConverter.ToString(s.ComputeHash(Encoding.UTF8.GetBytes(v.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant(); }
static void Main(string[] args) { var result=new JArray(); foreach(var path in args) {
var t=JObject.Parse(File.ReadAllText(path)); var output=new JObject(); var frames=new JArray(); var windows=new JArray();
foreach(JObject e in (JArray)t["entries"]) { var kind=(string)e["kind"];
if(kind=="request") { var q=JObject.Parse((string)e["json"]); if((int)q["chunkIndex"]==0) output["initialHistory"]=H(q["initialHistory"]); }
if(kind=="response") { var r=JObject.Parse((string)e["json"]); var f=(JArray)r["clip"]["frames"]; windows.Add(H(f)); foreach(var x in f) frames.Add(x.DeepClone()); } }
output["sourceFrames"]=H(frames); output["windows"]=windows; result.Add(output); } Console.Write(result.ToString(Formatting.None)); }
}''', encoding="utf-8")
    dll = ROOT / "Server/ARDY/runtime/unity-naturalness-validation/Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll"
    exe = build / "HashTrace.exe"
    subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/out:" + str(exe), "/r:" + str(dll), str(source)], check=True, capture_output=True)
    env = dict(os.environ, MONO_PATH=str(dll.parent))
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(exe), *[str(p) for p in trace_paths]], env=env, check=True, capture_output=True, encoding="utf-8")
    return json.loads(result.stdout.lstrip("\ufeff"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report_hash = digest(args.report)
    report = json.loads(args.report.read_text(encoding="utf-8"))
    plan = json.loads(Path(report["planPath"]).read_text(encoding="utf-8"))
    frozen = json.loads((ROOT / "Tools/MotionAdapter/data/intent-generalization-matrix-v1.json").read_text(encoding="utf-8"))
    verify(digest(report["planPath"]) == report["planSha256"], "Plan file hash mismatch")
    verify(plan["matrixSignatureSha256"] == frozen["matrixSignatureSha256"], "Frozen matrix changed")
    verify(len(report["rows"]) == len(frozen["rows"]) == 8, "Expected all eight rows")
    isolated = ROOT / "Server/ARDY/runtime/unity-naturalness-validation"
    snapshot = ROOT / "Server/ARDY/runtime/intent-feedback-v1/final-source-snapshot"
    compiled = snapshot / "ArdyIntentGeneralizationRegression.compiled.cs"
    verify(digest(compiled) == plan["sourceSha256"], "Actual compiled harness differs from plan")
    source_checks = []
    for item in plan["sources"]:
        compiled_runtime = snapshot / "ArdyLiveMotionController.matrix-compiled.cs"
        evidence_path = compiled_runtime if item["path"].endswith("/ArdyLiveMotionController.cs") else isolated / item["path"]
        actual = digest(evidence_path)
        source_checks.append({**item, "evidencePath": str(evidence_path), "evidenceSha256": actual,
                              "currentIsolatedSha256": digest(isolated / item["path"]), "matches": actual == item["sha256"]})
        verify(actual == item["sha256"], "Compiled runtime evidence changed: " + item["path"])
    import re
    compiled_text = compiled.read_text(encoding="utf-8-sig")
    resource_checks = []
    for field, symbol in (("avatarSha256", "AvatarPath"), ("animatorSha256", "AnimatorPath")):
        relative = re.search(r'const string ' + symbol + r'\s*=\s*"([^"]+)"', compiled_text)[1]
        actual = digest(isolated / relative)
        verify(actual == plan[field], "Isolated asset mismatch: " + relative)
        resource_checks.append({"path": relative, "sha256": actual})
    wave_path = "Assets/AIChatTookit/Resources/ARDY/left-wave.json"
    verify(digest(isolated / wave_path) == plan["leftWaveSha256"], "Actual wave asset changed")
    resource_checks.append({"path": wave_path, "sha256": plan["leftWaveSha256"]})
    hashes = exact_json_hashes([Path(row["archivedTracePath"]) for row in report["rows"]])
    rows = []
    for row, expected, hash_row in zip(report["rows"], frozen["rows"], hashes):
        for key in ("id", "description", "history", "seed"):
            verify(row["planned"][key] == expected[key], "Frozen row mismatch: " + key)
        verify(digest(row["archivedTracePath"]) == row["archivedTraceSha256"], "Trace file changed")
        verify(digest(row["actualSamplesPath"]) == row["actualSamplesSha256"], "Actual samples changed")
        verify(hash_row["initialHistory"] == row["initialHistorySha256"], "Initial history hash mismatch")
        verify(hash_row["sourceFrames"] == row["sourceFramesSha256"], "Source frame hash mismatch")
        verify(hash_row["windows"] == [w["frameSha256"] for w in row["windows"]], "Window hash mismatch")
        trace = json.loads(Path(row["archivedTracePath"]).read_text(encoding="utf-8"))
        feedback = row["finalFeedback"]
        verify(trace["actionId"] == feedback["actionId"] == row["actionId"], "Action identity mismatch")
        verify(feedback["responseGeneration"] == row["responseGeneration"] + 1, "Generation offset mismatch")
        verify(trace["turnId"] == "response-" + str(feedback["responseGeneration"]), "Trace turn differs from generated action")
        samples = json.loads(Path(row["actualSamplesPath"]).read_text(encoding="utf-8"))["observations"]
        verify(len(samples) == row["recordedActualSamples"], "Sample count mismatch")
        stable_start = previous = None
        max_stable = 0.0
        for sample in samples:
            verify(sample["actionId"] == row["actionId"], "Unbound actual sample")
            actual = sample["actual"]
            when = actual["timeSeconds"]
            verify(previous is None or 0 < when - previous <= .15, "Non-continuous actual samples")
            inside = []
            for side in ("left", "right"):
                flag = measured_inside(actual, side, expected["goal"][side + "Goal"])
                verify(flag == actual[side]["withinGoal"], "Independent arm classification disagrees")
                inside.append(flag)
            if all(inside):
                if stable_start is None:
                    stable_start = when
                max_stable = max(max_stable, when - stable_start)
            else:
                stable_start = None
            previous = when
        result = "reached" if max_stable + 1e-7 >= .2 else "not-reached"
        verify(result == row["necessaryGoalStatus"], "Independent duration classification disagrees")
        verify(abs(max_stable - row["finalObservation"]["maximumStableSeconds"]) < 1e-5, "Stable duration mismatch")
        is_wave = expected["history"] == "left-wave-at-0.7s"
        if is_wave:
            verify(.7 <= row["actualWaveElapsedAtHandoff"] < .734, "Wave handoff is outside realtime frame quantization")
            verify(abs(row["actualWaveElapsedAtHandoff"] - row["actualWaveTimeAtHandoff"]) < 1e-5 and row["actualPoseAtHandoff"]["available"], "Wave handoff not backed by actual pose/playback")
        rows.append({"id": expected["id"], "necessaryGoalStatus": result, "independentMaximumStableSeconds": max_stable,
                     "actualSamples": len(samples), "traceAndSourceAndHistoryHashesMatch": True,
                     "actualWaveTimeAtHandoff": row["actualWaveTimeAtHandoff"], "firstWindowSeconds": row["firstWindowSeconds"],
                     "generatedResponseGeneration": feedback["responseGeneration"], "fullSemanticSuccess": None, "naturalness": None})
    current = ROOT / "Assets/Editor/ArdyIntentGeneralizationRegression.cs"
    diff = "".join(difflib.unified_diff(compiled_text.splitlines(True), current.read_text(encoding="utf-8-sig").splitlines(True), fromfile="actual-compiled-pass3", tofile="current-source"))
    output = {"status": "read-only-audit-passed", "auditedAtUtc": utc(), "sourceReportSha256": report_hash,
              "matrixSignatureSha256": plan["matrixSignatureSha256"], "compiledHarnessSha256": digest(compiled),
              "runtimeSourceChecks": source_checks, "resourceChecks": resource_checks, "harnessSourceDiff": diff,
              "rows": rows, "necessaryReached": sum(r["necessaryGoalStatus"] == "reached" for r in rows),
              "limitations": ["The eight histories are real and individually hashed, not byte-identical histories across seeds.",
                  "Sixteen frames at20Hz span0.75s. The wave handoff at0.7000-0.7167s therefore includes a small idle-to-wave entry portion; per-history absolute sample timestamps are not present in the wire format.",
                  "row.responseGeneration names the preparation/base generation; the actual generated generation is that value+1, bound by terminal feedback and trace.turnId.",
                  "Old HashFile resolved relative plan source/asset paths against process CWD. Actual compiled source snapshots and other isolated runtime/assets match the emitted hashes, so the possible CWD ambiguity does not change this run's attribution. Controller has since changed; its preserved matrix-compiled snapshot is used. Absolute trace/report/sample hashes are unaffected.",
                  "Post-VRM necessary wrist-region classification was independently recomputed from actual shoulder/elbow/wrist/head positions. This still does not establish complete semantics, fingers, sustained final holding or naturalness.",
                  "No correction request, model chat or motion rerun was executed by this audit."],
              "fullSemanticSuccess": None, "naturalness": None, "sourceReportUnchanged": digest(args.report) == report_hash}
    exclusive_json(args.output, output)
    print(json.dumps({"output": str(args.output), "status": output["status"], "necessaryReached": output["necessaryReached"], "rows": len(rows)}))


if __name__ == "__main__":
    main()
