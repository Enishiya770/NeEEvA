using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Current owner-bound movement outcome; dispatch is never completion.</summary>
public static class RoomMovementResultPolicy
{
    public static bool IsTerminalFailure(string phase) =>
        new[] { "failed", "blocked", "cancelled", "unavailable", "unreachable", "goal-not-reached", "disconnected" }.Contains(phase);

    public static string ArrivalRelation(JObject room)
    {
        var facts = room?["currentFacts"] as JObject;
        if ((bool?)facts?["currentlyNearAndFacingUser"] == true) return "near-facing";
        if ((bool?)facts?["userTrackingValid"] == false) return "tracking-unavailable";
        if ((bool?)facts?["userSupportVerified"] == false) return "support-unverified";
        double? distance = (double?)facts?["actualUserDistance"], desired = (double?)facts?["desiredUserDistance"];
        if (distance >= 0 && desired > 0 && Math.Abs(distance.Value - desired.Value) > .25) return "distance-changed";
        if ((double?)facts?["actualFacingErrorDegrees"] > 15) return "facing-changed";
        return "current-unverified";
    }

    public static string Fallback(JObject room, string intent, string actionId)
    {
        if (intent != "follow" && intent != "approach") return null;
        var execution = room?["execution"] as JObject;
        if (string.IsNullOrEmpty(actionId) || (string)execution?["actionId"] != actionId ||
            (string)execution["targetKind"] != "user") return null;
        if (IsTerminalFailure((string)execution["phase"]))
        {
            if ((string)execution["approachRetargetStatus"] == "exhausted")
                return "这次有限的路线调整已用完，距离或朝向仍未满足。我先停下，这次走近还没有完成。";
            if ((string)execution["approachRetargetStatus"] == "route-rejected")
                return "调整后的路线检查没有通过。我先停下，这次还没走到你身边。";
        }
        switch ((string)execution["phase"])
        {
            case "preparing": case "generating": return intent == "follow"
                ? "我已经接到跟随要求，正在准备行走动作，还没有开始走。"
                : "走近的请求已经提交，正在准备行走动作，还没有开始走。";
            case "follow-recovering": return "跟随任务仍然开启，行走暂时中断，系统正在尝试恢复，还没有恢复正常移动。";
            case "nav-recovering": return "当前位置没有通过导航检查，正在尝试有限的位置恢复，还没有完成原来的移动任务。";
            case "follow-replanning": return "跟随仍然开启，正在重新检查当前位置和路线，还没有恢复行走。";
            case "approach-replanning": return "距离或朝向还需要调整，我正在重新检查路线，这次走近尚未完成。";
            case "follow-paused": return "现在的路线检查没有通过，我先停在这里，等位置变化后再确认。";
            case "follow-waiting": return "我现在停着等待，跟随仍然开启，会根据你的位置继续调整。";
            case "returning": return "这段移动正在收尾并恢复姿态，还不能据此确认整个任务完成。";
            case "moving": return intent == "follow" ? "我正在跟着你移动。" : "我正在尝试走近你。";
            case "cancelled": return "这次移动已经取消。";
            case "goal-not-reached": return "这段行走已经结束，但没有达到要求的距离或朝向，这次走近还没有完成。";
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
        // Arrival is a committed event. Later user movement changes the live
        // relation, not the completion of this owned action.
        bool arrived = intent == "approach" && !string.IsNullOrEmpty(actionId) &&
            (string)execution?["actionId"] == actionId && (string)execution?["targetKind"] == "user" &&
            (string)execution?["phase"] == "arrived";
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
                case "follow-replanning": case "approach-replanning": expected = "replanning"; break;
                case "returning": expected = "returning"; break;
                case "failed": case "blocked": case "unreachable": case "goal-not-reached": expected = "incomplete"; break;
                case "cancelled": expected = "cancelled"; break;
            }
        return new JObject { ["required"] = expected != null, ["expectedReport"] = expected,
            ["arrivalRelation"] = arrived ? ArrivalRelation(room) : null,
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
