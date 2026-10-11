using System;
using Newtonsoft.Json.Linq;

/// <summary>Current owner-bound movement outcome; dispatch is never completion.</summary>
public static class RoomMovementResultPolicy
{
    public static string Fallback(JObject room, string intent, string actionId)
    {
        if (intent != "follow" && intent != "approach") return null;
        var execution = room?["execution"] as JObject;
        if (string.IsNullOrEmpty(actionId) || (string)execution?["actionId"] != actionId ||
            (string)execution["targetKind"] != "user") return null;
        switch ((string)execution["phase"])
        {
            case "preparing": case "generating": return intent == "follow"
                ? "我已经接到跟随要求，正在准备行走动作，还没有开始走。"
                : "走近的请求已经提交，正在准备行走动作，还没有开始走。";
            case "follow-recovering": return "跟随任务仍然开启，行走暂时中断，系统正在尝试恢复，还没有恢复正常移动。";
            case "nav-recovering": return "当前位置没有通过导航检查，正在尝试有限的位置恢复，还没有完成原来的移动任务。";
            case "follow-replanning": return "跟随仍然开启，正在重新检查当前位置和路线，还没有恢复行走。";
            case "follow-paused": return "现在的路线检查没有通过，我先停在这里，等位置变化后再确认。";
            case "follow-waiting": return "我现在停着等待，跟随仍然开启，会根据你的位置继续调整。";
            case "returning": return "这段移动正在收尾并恢复姿态，还不能据此确认整个任务完成。";
            case "moving": return intent == "follow" ? "我正在跟着你移动。" : "我正在尝试走近你。";
            case "cancelled": return "这次移动已经取消。";
            case "blocked": case "unreachable": return "这次移动没有完成，路线或空间检查没有通过。";
            case "failed":
                if (intent == "follow" && ((string)execution["reason"] ?? "").IndexOf("504", StringComparison.Ordinal) >= 0)
                    return "这次跟随请求已提交，但动作服务等待超时，跟随没有完成。";
                return "这次移动请求已提交，但执行失败了，还没有完成。";
            default: return null;
        }
    }

    public static JObject Requirement(JObject room, string intent, string actionId)
    {
        var execution = room?["execution"] as JObject;
        var current = room?["currentFacts"] as JObject;
        bool arrived = intent == "approach" && !string.IsNullOrEmpty(actionId) &&
            (string)execution?["actionId"] == actionId && (string)execution?["targetKind"] == "user" &&
            (string)execution?["phase"] == "arrived" && (bool?)current?["currentlyNearAndFacingUser"] == true;
        string expected = arrived ? "arrived" : null;
        if ((intent == "approach" || intent == "follow") && !string.IsNullOrEmpty(actionId) &&
            (string)execution?["actionId"] == actionId && (string)execution["targetKind"] == "user")
            switch ((string)execution["phase"])
            {
                case "preparing": case "generating": expected = "preparing"; break;
                case "moving": expected = "moving"; break;
                case "follow-waiting": expected = "waiting"; break;
                case "follow-paused": expected = "paused"; break;
                case "follow-recovering": case "nav-recovering": expected = "recovering"; break;
                case "follow-replanning": expected = "replanning"; break;
                case "returning": expected = "returning"; break;
                case "failed": case "blocked": case "unreachable": expected = "incomplete"; break;
                case "cancelled": expected = "cancelled"; break;
            }
        return new JObject { ["required"] = expected != null, ["expectedReport"] = expected,
            ["actionId"] = actionId, ["execution"] = execution?.DeepClone(),
            ["scope"] = "Latest runtime outcome of this action only. When required, report this execution state. For review, classify resultReport from the actual PUBLIC WORDS, never copy expectedReport: preparing means not yet walking (I am ready / following you does NOT report preparing); recovering is an interruption with recovery in progress, not normal following; waiting means resting nearby with follow still active; paused/replanning/returning retain their literal phase; moving means walking now; arrived requires actual arrival. Future promises or generic reassurance omit the state. A different or omitted state is inconsistent. Never fill missing speech from the receipt, or repeat an action to verify it. Other requested work may remain unfinished." };
    }

    public static bool ReviewReports(string review, JObject requirement)
    {
        if ((bool?)requirement?["required"] != true) return true;
        try { return (string)JObject.Parse(review)["resultReport"] == (string)requirement["expectedReport"]; }
        catch (Exception) { return false; }
    }
}
