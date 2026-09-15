using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

/// <summary>Production trajectory builder export for a fixed saved prefix; no scene loading, playback or HTTP.</summary>
public static class ArdyRoomBrakingTargetExport
{
    [Serializable] private sealed class Pack { public int schema; public SavedCase[] cases; }
    [Serializable] private sealed class SavedCase
    {
        public string caseId, capturePath; public int branchStep, endStep; public float motionScale;
        public SavedRecord[] records; public BuilderContext branchBuilderContext;
    }
    [Serializable] private sealed class BuilderContext { public Quaternion anchorRotation; public Vector3 anchorGround; }
    [Serializable] private sealed class SavedRecord
    {
        public int step; public float submittedAtRealtime, nearbyObservedDesiredSpeed, observationDeltaSeconds;
        public SavedRequest request; public ArdyLocomotionClip response;
    }
    [Serializable] private sealed class SavedRequest { public ArdyLocomotionTarget[] targets; }
    [Serializable] private sealed class Targets { public ArdyLocomotionTarget[] targets; }
    [Serializable] private sealed class CandidateCase
    {
        public string caseId, rejection, sourceCapture;
        public int branchStep, endStep;
        public bool controlRejected, simplifiedToOriginalEndpoint;
        public float worldSpeed, desiredSpeed, pathLength, originalPolylineLength, observationDeltaSeconds;
        public float maximumOriginalTargetDeviation, maximumAllowedSimplificationDeviation;
        public Vector3 sourcePosition;
        public float sourceHeading;
        public string routeScope = "Surrogate world pelvis-centre polyline reconstructed from the recorded request targets, starting at the previous accepted source pelvis. Original NavMesh corners were not recorded. rootOffset=0; numeric callback always true, so this export proves no room obstacle clearance.";
    }

    /// <summary>-ardyBrakingCases cases.json -ardyRoomOutput folder [-ardyBrakingBranchOffset 0].</summary>
    public static void RunBatch() => Run(false);

    /// <summary>A separately declared numerical experiment; at most 2 cm deviation from the saved target polyline.</summary>
    public static void RunLinearBatch() => Run(true);

    private static void Run(bool linear)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only export in the existing isolated validation project with -batchmode.");
        try
        {
            string input = Path.GetFullPath(Argument("-ardyBrakingCases", ""));
            string output = Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-braking-export"));
            int offset = int.Parse(Argument("-ardyBrakingBranchOffset", "0"), CultureInfo.InvariantCulture);
            if (offset > 0 || offset < -4) throw new ArgumentException("Branch offset must be predeclared from -4 through 0.");
            var pack = JsonUtility.FromJson<Pack>(File.ReadAllText(input));
            if (pack == null || pack.schema != 1 || pack.cases == null || pack.cases.Length != 2)
                throw new InvalidOperationException("Expected the two prepared saved capture cases.");
            var cases = new List<string>();
            foreach (var saved in pack.cases)
            {
                int branch = saved.branchStep + offset;
                if (branch <= 0 || branch >= saved.records.Length) throw new InvalidOperationException("Branch lacks a saved preceding source prefix.");
                var previous = saved.records[branch - 1]; var record = saved.records[branch];
                var pose = previous.response.rotationClip.SampleDelta(0, 7f / 20f);
                Vector3 facing = pose * Vector3.forward;
                float heading = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
                Vector3 source = previous.response.frames[7].rootPosition;
                float speed = Vector3.Distance(previous.request.targets[7].rootPosition, previous.request.targets[6].rootPosition) * 20 * saved.motionScale;
                var A = saved.branchBuilderContext.anchorRotation; var ground = saved.branchBuilderContext.anchorGround;
                Vector3 start = ground + A * new Vector3(source.x * saved.motionScale, 0, source.z * saved.motionScale);
                var points = new List<Vector3> { start };
                foreach (var target in record.request.targets)
                {
                    Vector3 p = ground + A * (target.rootPosition * saved.motionScale);
                    if (Vector3.Distance(points[points.Count - 1], p) > .0001f) points.Add(p);
                }
                float length = 0; for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i-1], points[i]);
                float originalLength = length, maximumDeviation = 0;
                if (linear)
                {
                    Vector3 end = ground + A * (record.request.targets[record.request.targets.Length - 1].rootPosition * saved.motionScale), delta = end - start;
                    foreach (var originalTarget in record.request.targets)
                    {
                        Vector3 point = ground + A * (originalTarget.rootPosition * saved.motionScale);
                        float fraction = delta.sqrMagnitude > .000000001f ? Mathf.Clamp01(Vector3.Dot(point - start, delta) / delta.sqrMagnitude) : 0;
                        maximumDeviation = Mathf.Max(maximumDeviation, Vector3.Distance(point, start + delta * fraction));
                    }
                    points = new List<Vector3> { start, end };
                    length = Vector3.Distance(start, end);
                }
                var route = new ArdyRoomRoute();
                typeof(ArdyRoomRoute).GetProperty("Corners", BindingFlags.Instance | BindingFlags.Public).SetValue(route, points.ToArray());
                typeof(ArdyRoomRoute).GetProperty("GroundY", BindingFlags.Instance | BindingFlags.Public).SetValue(route, ground.y);
                typeof(ArdyRoomRoute).GetProperty("Length", BindingFlags.Instance | BindingFlags.Public).SetValue(route, length);
                var result = new CandidateCase { caseId = saved.caseId, branchStep = branch, endStep = branch,
                    worldSpeed = speed, desiredSpeed = record.nearbyObservedDesiredSpeed, pathLength = length,
                    observationDeltaSeconds = record.observationDeltaSeconds, sourcePosition = source, sourceHeading = heading,
                    sourceCapture = saved.capturePath, originalPolylineLength = originalLength,
                    simplifiedToOriginalEndpoint = linear, maximumOriginalTargetDeviation = maximumDeviation,
                    maximumAllowedSimplificationDeviation = linear ? .02f : 0 };
                if (linear)
                    result.routeScope = "Separately declared straight-line control experiment: actual prior accepted source pelvis to the unchanged final saved target. Initial speed, source pose/heading and endpoint are retained. Every original target is within the reported deviation of this segment (fixed 2 cm ceiling). rootOffset=0; numeric callback always true; no original NavMesh or room obstacle clearance claim.";
                ArdyLocomotionTarget[] targets = null;
                try
                {
                    if (linear && maximumDeviation > .02f)
                        throw new InvalidOperationException("control-linear-surrogate-deviation: original targets exceed the predeclared 2 cm simplification limit.");
                    targets = ArdyRoomStreamTrajectory.Build(route, source, heading, speed, result.desiredSpeed,
                        A, ground, saved.motionScale, 40, 0, Vector3.zero, (from, to) => true);
                }
                catch (InvalidOperationException error)
                {
                    result.controlRejected = true; result.rejection = error.Message;
                }
                string header = JsonUtility.ToJson(result);
                string mapping = "{}";
                if (targets != null)
                {
                    string wrapped = JsonUtility.ToJson(new Targets { targets = targets });
                    string array = wrapped.Substring(wrapped.IndexOf(':') + 1);
                    array = array.Substring(0, array.Length - 1);
                    mapping = "{\"" + branch.ToString(CultureInfo.InvariantCulture) + "\":" + array + "}";
                }
                cases.Add(header.Substring(0, header.Length - 1) + ",\"targetsByStep\":" + mapping + "}");
            }
            Directory.CreateDirectory(output);
            string scope = linear ? "Separately declared linearized control experiment; original actual source start and final target, speed, heading and seed retained; maximum original target-to-segment deviation must be <=2 cm. Production ArdyRoomStreamTrajectory.Build supplies the new braking targets." : "Production ArdyRoomStreamTrajectory.Build on recorded target-polyline surrogate; fixed prior accepted pelvis and committed target speed.";
            string manifest = "{\"schema\":1,\"provenance\":\"" + scope + " This is a local numerical braking comparison, not original NavMesh or room clearance reproduction. Branch offset " + offset + ". No seed changes; infeasible control remains rejected.\",\"cases\":[" + string.Join(",", cases) + "]}";
            File.WriteAllText(Path.Combine(output, "candidate-production.json"), manifest, new UTF8Encoding(false));
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static string Argument(string name, string fallback)
    { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
}
