using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyLiveMotionController
    {
        // This is the same fixed registry accepted by RequestMotion, never a model-provided path.
        private static readonly string[] BasicMotionNames = { "left-wave", "right-wave", "nod", "shake-head" };

        public string InspectCapabilities()
        {
            var motions = new JArray();
            foreach (string name in BasicMotionNames)
            {
                var entry = new JObject { ["name"] = name, ["available"] = false };
                var asset = Resources.Load<TextAsset>("ARDY/" + name);
                entry["resourcePresent"] = asset != null;
                if (asset != null)
                {
                    try
                    {
                        var clip = ArdyMotionClip.Parse(asset.text);
                        entry["resourceValid"] = true;
                        entry["durationSeconds"] = clip.Duration;
                        entry["available"] = IsBound;
                    }
                    catch (Exception) { entry["resourceValid"] = false; }
                }
                motions.Add(entry);
            }
            string current = DescribeMotionContext();
            return new JObject {
                ["source"] = "bound-ardy-controller-and-registered-resources",
                ["bound"] = IsBound,
                ["basicMotions"] = motions,
                ["generatedMotionEnabled"] = allowGeneratedMotion,
                ["generationServiceHealth"] = "not-checked",
                ["currentState"] = string.IsNullOrEmpty(current) ? JValue.CreateNull() : JObject.Parse(current),
                ["limit"] = "Availability checks binding and clip validity, not a physical demonstration. No independent finger control or whole-project asset inventory."
            }.ToString(Formatting.None);
        }
    }
}
