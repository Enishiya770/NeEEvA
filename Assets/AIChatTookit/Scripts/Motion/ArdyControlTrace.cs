using System;
using System.Collections.Generic;
using System.IO;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Bounded local constraint diagnostics; never recorded as ARDY generation.</summary>
    internal sealed class ArdyControlTrace
    {
        [Serializable] private sealed class Record
        {
            public string source = "procedural-constraints-v1";
            public string startedUtc, characterId, turnId, avatarSourcePath, avatarSourceSha256, finishReason;
            public long revision;
            public ArdyControlPlan plan;
            public List<Entry> entries = new List<Entry>();
        }
        [Serializable] private sealed class Entry
        {
            public float seconds;
            public string phase, note;
            public ArdyConstraintObservation observation;
        }
        private readonly Record record;
        private readonly float started;
        private float nextSample, nextSave;
        private string lastPhase;
        private bool ended;
        public string Path { get; private set; }
        public ArdyControlTrace(string character, string turn, long revision, ArdyControlPlan plan, Vrm10Instance avatar)
        {
            started = Time.realtimeSinceStartup;
            var reference = ArdyAvatarRestPose.Find(avatar);
            record = new Record { startedUtc = DateTime.UtcNow.ToString("o"), characterId = character,
                turnId = turn, revision = revision, plan = plan.Copy(),
                avatarSourcePath = reference != null ? reference.sourcePath : "runtime-import-without-baked-identity",
                avatarSourceSha256 = reference != null ? reference.sourceSha256 : "" };
            string directory = Application.isEditor
                ? System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Logs/ardy-control"))
                : System.IO.Path.Combine(Application.persistentDataPath, "ardy-control");
            Path = System.IO.Path.Combine(directory, "ardy-control-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            Save();
        }
        public void Sample(string phase, ArdyConstraintObservation observation)
        {
            float now = Time.realtimeSinceStartup;
            if (ended || record.entries.Count >= 420 || (now < nextSample && phase == lastPhase)) return;
            nextSample = now + 0.1f;
            // Copy mutable measurements; a trace entry must describe this frame.
            var copy = observation == null ? null : JsonUtility.FromJson<ArdyConstraintObservation>(JsonUtility.ToJson(observation));
            record.entries.Add(new Entry { seconds = now - started, phase = phase, observation = copy });
            bool changed = phase != lastPhase;
            lastPhase = phase;
            if (changed || now >= nextSave) { nextSave = now + 1; Save(); }
        }
        public void Event(string phase, string note)
        {
            if (ended || record.entries.Count >= 420) return;
            record.entries.Add(new Entry { seconds = Time.realtimeSinceStartup - started, phase = phase, note = note });
            Save();
        }
        public void Finish(string reason)
        {
            if (ended) return;
            record.finishReason = reason;
            Event("finished", reason);
            ended = true;
            Save();
        }
        private void Save()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path, JsonUtility.ToJson(record, true));
                var old = new List<FileInfo>(new DirectoryInfo(directory).GetFiles("ardy-control-*.json"));
                old.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                while (old.Count > 8) { old[0].Delete(); old.RemoveAt(0); }
            }
            catch (Exception error) { Debug.LogWarning("Constraint trace unavailable: " + error.Message); }
        }
    }
}
