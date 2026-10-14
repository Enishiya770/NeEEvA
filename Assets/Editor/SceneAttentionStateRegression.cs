using System;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class SceneAttentionStateRegression
{
    public static JObject Run()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception("Attention: " + message); }
        var state = new SceneAttentionState();
        var observations = Enumerable.Range(0, 20).Select(i => new JObject {
            ["entityId"] = "seen-" + i, ["captureId"] = "view-" + i, ["observedAt"] = 10,
            ["description"] = i == 19 ? "用户刚才提到的蓝色杯子" : "架子上的书 " + i
        }).ToArray();
        string original = new JArray(observations).ToString();
        state.Update("room-a", "conversation-1", "蓝色杯子", false, false, true, observations, null, 10);
        var first = state.Snapshot("planner", 10);
        Check(first["selectedEntities"].Any(x => (string)x["entityId"] == "seen-19"), "Referenced observed object is recalled.");
        Check((int)first["ordinarySlots"] == 3 && (bool)first["actionAuthorization"] == false, "Conversation stays bounded without granting action.");
        state.Update("room-a", "conversation-1", "蓝色杯子", false, false, true, observations.Reverse(), null, 11);
        var second = state.Snapshot("writer", 11);
        Check((int)second["revision"] == (int)first["revision"], "Consumers and input order do not reset persistent attention.");
        Check(second["selectedEntities"].All(x => (double)x["ageSeconds"] == 1), "Polling does not refresh old observations.");
        state.Update("room-a", "task-2", "检查录音设备", true, false, false, observations, new[] { "seen-1" }, 12);
        var task = state.Snapshot("planner", 12);
        Check((string)task["mode"] == "task" && task["selectedEntities"].Any(x => (string)x["entityId"] == "seen-1"), "New non-lamp task preempts and retains an observed binding.");
        Check((double)task["updateIntervalSeconds"] < (double)first["updateIntervalSeconds"], "Task monitoring adapts frequency.");
        state.Update("room-a", "task-2", "检查录音设备", true, true, false, observations, new[] { "seen-1" }, 13);
        var failure = state.Snapshot("reviewer", 13);
        Check((string)failure["mode"] == "investigate" && (int)failure["ordinarySlots"] > (int)task["ordinarySlots"], "Uncertainty temporarily broadens evidence selection.");
        state.Update("room-a", "task-2", "检查录音设备", false, false, false, observations, null, 16);
        Check((string)state.Snapshot("runtime", 16)["mode"] == "idle", "Completed work releases focused attention.");
        state.Update("room-b", "conversation-3", "蓝色杯子", false, false, true, Array.Empty<JObject>(), new[] { "unseen" }, 17);
        Check(!state.Snapshot("writer", 17)["selectedEntities"].Any(), "Scene replacement and unseen IDs cannot leak old or hidden objects.");
        Check(new JArray(observations).ToString() == original, "Projection does not alter observation memory.");
        var duplicates = new[] {
            new JObject { ["entityId"]="cup", ["captureId"]="older", ["observedAt"]=20, ["description"]="blue cup", ["descriptionCaptureId"]="recognition-19" },
            new JObject { ["entityId"]="cup", ["captureId"]="newer", ["observedAt"]=22, ["description"]="blue cup", ["descriptionCaptureId"]="recognition-19" }
        };
        state.Update("room-b", "cup-task", "cup", true, false, false, duplicates, null, 23);
        var merged = state.Snapshot("planner", 23);
        for (int i=0;i<5;i++) {
            state.Update("room-b", "cup-task", "cup", true, false, false, i%2==0 ? duplicates.Reverse() : duplicates, null, 24+i);
            var stable=state.Snapshot("runtime",24+i);
            Check((int)stable["revision"]==(int)merged["revision"] && (string)stable["selectedEntities"][0]["sourceCaptureId"]=="newer",
                "Duplicate entity captures and reversed enumeration do not manufacture changes or regress the chosen observation.");
        }
        state.Update("room-b", "cup-task", "cup", true, false, false, duplicates.Take(1), null, 30);
        var late=state.Snapshot("writer",30);
        Check((string)late["selectedEntities"][0]["sourceCaptureId"]=="newer" && (double)late["selectedEntities"][0]["ageSeconds"]==8,
            "Late older input cannot move geometry/observation age backwards.");
        Check((string)late["selectedEntities"][0]["descriptionCaptureId"]=="recognition-19", "New geometry never re-dates remembered recognition.");
        var reread=(JObject)duplicates[1].DeepClone();reread["observedAt"]=30;
        state.Update("room-b", "cup-task", "cup", true, false, false, new[]{reread}, null, 30);
        Check((double)state.Snapshot("writer",30)["selectedEntities"][0]["ageSeconds"]==8,
            "Re-reading the same capture cannot reset its age even if the caller restamps the row.");
        var recognized = (JObject)duplicates[1].DeepClone(); recognized["observedAt"] = 30;
        recognized["captureId"] = "recognized-30"; recognized["description"] = "striped blue cup";
        recognized["descriptionCaptureId"] = "recognized-30";
        state.Update("room-b", "cup-task", "cup", true, false, false, new[] { recognized }, null, 30);
        var geometry = (JObject)recognized.DeepClone(); geometry["captureId"] = "geometry-31";
        geometry["observedAt"] = 31; geometry.Remove("description"); geometry.Remove("descriptionCaptureId");
        state.Update("room-b", "cup-task", "cup", true, false, false, new[] { geometry, duplicates[0] }, null, 31);
        Check((string)state.Snapshot("writer",31)["selectedEntities"][0]["descriptionCaptureId"] == "recognized-30",
            "A new geometry-only capture cannot make an old description overwrite newer remembered recognition.");
        state.Update("room-b", "cup-task", "cup", true, false, false, Array.Empty<JObject>(), null, 31);
        Check(!state.Snapshot("runtime",31)["selectedEntities"].Any(),"Expired memory releases attention.");
        return new JObject { ["passed"] = checks, ["conversation"] = first, ["task"] = task, ["investigation"] = failure };
    }
}
