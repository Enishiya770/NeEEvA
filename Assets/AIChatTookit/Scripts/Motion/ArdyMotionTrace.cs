using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Eight local motion-only traces, including exact history and responses for reproducible failures.</summary>
    internal sealed class ArdyMotionTrace
    {
        [Serializable] private sealed class Record
        {
            public string startedUtc, characterId, turnId, description, serviceUrl, finishReason, actionId, intentGoalJson;
            public long revision;
            public int seed;
            public List<Entry> entries = new List<Entry>();
        }
        [Serializable] private sealed class Entry
        {
            public float elapsedSeconds;
            public string kind, json;
            public long httpStatus;
        }
        private readonly Record record;
        private readonly string path;
        private readonly float started;
        private bool ended;
        public string Path => path;

        public ArdyMotionTrace(string character, string turn, long revision, string description, int seed, string serviceUrl,
            string actionId = null, ArdyMotionGoal goal = null)
        {
            started = Time.realtimeSinceStartup;
            record = new Record { startedUtc = DateTime.UtcNow.ToString("o"), characterId = character,
                turnId = turn, revision = revision, description = description, seed = seed, serviceUrl = serviceUrl,
                actionId = actionId, intentGoalJson = goal == null ? "null" : JsonUtility.ToJson(goal) };
            string directory = Application.isEditor
                ? System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Logs/ardy-live"))
                : System.IO.Path.Combine(Application.persistentDataPath, "ardy-live");
            path = System.IO.Path.Combine(directory, "ardy-motion-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Directory.CreateDirectory(directory);
                Save();
            }
            catch (Exception error) { Debug.LogWarning("ARDY motion trace unavailable: " + error.Message); }
        }

        public void Add(string kind, string json, long status = 0)
        {
            if (ended || record.entries.Count >= 8) return;
            if (json != null && json.Length > 512 * 1024)
            { kind += "-body-omitted"; json = "Response exceeded diagnostic size limit."; }
            record.entries.Add(new Entry { elapsedSeconds = Time.realtimeSinceStartup - started,
                kind = kind, json = json, httpStatus = status });
            Save();
        }

        public void Finish(string reason)
        {
            if (ended) return;
            record.finishReason = reason;
            Add("finished", reason);
            ended = true;
            Save();
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(record, true));
                var old = new List<FileInfo>(new DirectoryInfo(System.IO.Path.GetDirectoryName(path)).GetFiles("ardy-motion-*.json"));
                old.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                while (old.Count > 8) { old[0].Delete(); old.RemoveAt(0); }
            }
            catch (Exception error) { Debug.LogWarning("ARDY motion trace could not be saved: " + error.Message); }
        }
    }
}
