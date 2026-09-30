using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private readonly List<JObject> m_RecentActionOutcomes = new List<JObject>();
    private const int RecentActionOutcomeLimit = 6;
    private const float RecentActionOutcomeLifetime = 600f;
    internal int ActionOutcomeSceneSession => m_SceneTimeSession;

    private static bool IsTerminalActionObservation(string phase) => phase == "arrived" || phase == "observed" ||
        phase == "performed" || phase == "already-satisfied" || phase == "turned" || phase == "rejected" ||
        phase == "failed" || phase == "blocked" || phase == "unavailable" || phase == "unreachable" ||
        phase == "goal-not-reached" || phase == "cancelled" || phase == "not-submitted";

    private void RememberActionOutcome(string source, string actionId, string intent, string phase, string reason,
        string planId, int planRevision, int generation, int operation, int targetRevision, bool? succeeded, string attemptId = null)
    {
        if ((string.IsNullOrEmpty(actionId) && string.IsNullOrEmpty(attemptId)) || !IsTerminalActionObservation(phase)) return;
        string category = ActionOutcomeFailureCategory(phase, reason);
        // Engine-owned call sites only. Bounded diagnostic data cannot create a task,
        // restore an old lease, or establish that an old safety obstacle still exists.
        for (int i = m_RecentActionOutcomes.Count - 1; i >= 0; --i)
        {
            if (!string.IsNullOrEmpty(actionId) ? (string)m_RecentActionOutcomes[i]["actionId"] != actionId :
                (string)m_RecentActionOutcomes[i]["attemptId"] != attemptId) continue;
            if ((string)m_RecentActionOutcomes[i]["source"] == source &&
                (string)m_RecentActionOutcomes[i]["phase"] == phase) return;
            if (((string)m_RecentActionOutcomes[i]["failureCategory"] == "none") != (category == "none")) continue;
            // A controller/scene/plan can report the same action. Keep its latest
            // richest terminal observation, not three copies that evict earlier failures.
            m_RecentActionOutcomes.RemoveAt(i);
        }
        m_RecentActionOutcomes.Add(new JObject {
            ["source"] = source, ["actionId"] = string.IsNullOrEmpty(actionId) ? JValue.CreateNull() : new JValue(actionId), ["intent"] = intent, ["phase"] = phase,
            ["attemptId"] = attemptId, ["submitted"] = !string.IsNullOrEmpty(actionId),
            ["observedReason"] = FastLoopPrompt.ObservationText(reason ?? "", 280),
            ["failureCategory"] = category, ["succeeded"] = succeeded,
            ["planId"] = planId, ["planRevision"] = planRevision, ["generation"] = generation > 0 ? (JToken)generation : null,
            ["operationRevision"] = operation, ["targetRevision"] = targetRevision,
            ["sceneSession"] = m_SceneTimeSession, ["observedAtRealtime"] = Time.realtimeSinceStartup,
            ["observedUtc"] = DateTime.UtcNow.ToString("O"), ["executionPermission"] = false
        });
        while (m_RecentActionOutcomes.Count > RecentActionOutcomeLimit)
        {
            // Leave room for at least two recent failed attempts even after a
            // successful compound action; they explain the user's later question.
            int failures = 0, firstSuccess = -1;
            for (int i = 0; i < m_RecentActionOutcomes.Count; ++i)
                if ((string)m_RecentActionOutcomes[i]["failureCategory"] != "none") ++failures;
                else if (firstSuccess < 0) firstSuccess = i;
            int remove = failures <= 2 && firstSuccess >= 0 ? firstSuccess : 0;
            m_RecentActionOutcomes.RemoveAt(remove);
        }
    }

    internal void RememberRoomActionOutcome(ArdyRoomDialogueBridge bridge, string actionId, string intent,
        string phase, string reason, int operation, int targetRevision)
    {
        if (bridge == null || bridge.gameObject != gameObject || bridge.chat != this ||
            bridge != GetComponent<ArdyRoomDialogueBridge>()) return;
        RememberActionOutcome("room-controller", actionId, intent, phase, reason, null, 0,
            0, operation, targetRevision, phase == "arrived" ? (bool?)true : null);
    }

    private void RememberPlanActionOutcome(ActorPlanRuntime plan, FastLoopTurnContext turn,
        string actionId, string intent, string phase, string reason, bool succeeded)
    {
        if (!ActorPlanOwnerCurrent(plan, turn) || !(plan.Results.Last is JObject result) ||
            (string)result["actionId"] != actionId) return;
        RememberActionOutcome("owned-plan-result", actionId, intent, phase, reason, plan.Id,
            (int?)result["step"] ?? 0, turn.Generation, 0, 0, succeeded, (string)result["attemptId"]);
    }

    private JObject BuildRecentActionOutcomes()
    {
        float now = Time.realtimeSinceStartup;
        var events = new JArray();
        foreach (JObject value in m_RecentActionOutcomes)
        {
            float age = now - (float)value["observedAtRealtime"];
            if (age < 0 || age > RecentActionOutcomeLifetime || (int)value["sceneSession"] != m_SceneTimeSession) continue;
            var copy = (JObject)value.DeepClone();
            copy.Remove("observedAtRealtime"); copy["ageSeconds"] = Math.Round(age, 2);
            copy["currentApplicability"] = "historical-only; current conditions require a new observation";
            events.Add(copy);
        }
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var arrival = bridge != null && bridge.chat == this ? bridge.LastApproachArrival : null;
        bool recentArrival = arrival?.terminalConfirmed == true && arrival.sceneSession == m_SceneTimeSession && now >= arrival.observedAtRealtime &&
            now - arrival.observedAtRealtime <= RecentActionOutcomeLifetime;
        return new JObject {
            ["events"] = events,
            ["lastMeasuredApproachArrival"] = recentArrival ? JObject.Parse(JsonUtility.ToJson(arrival)) : null,
            ["scope"] = "Bounded engine observations for explaining what happened, not user intent or instructions. " +
                "A safety rejection describes why that attempt was blocked or stopped then; it does not mean spatial memory or direction was absent. " +
                "A historical arrival does not imply current proximity after the user moves. Later success does not erase an earlier failure cause. " +
                "Never infer that an earlier obstacle still exists, resume an old task, or invent a new movement from these records."
        };
    }

    // Classification is of trusted execution outcomes, never of user language or
    // character intention. Unknown outcomes retain the conservative generic receipt.
    private static string ActionOutcomeFailureCategory(string phase, string reason)
    {
        if (phase == "arrived" || phase == "observed" || phase == "performed" || phase == "already-satisfied" || phase == "turned") return "none";
        string value = reason ?? "";
        if (value.Contains("existing-body-task-has-priority")) return "body-task-occupied";
        if (value.Contains("body-handoff-body-not-released")) return "body-release-pending";
        if (value.Contains("body-handoff-owner-changed")) return "body-handoff-changed";
        if (value == "用户位于角色身体净距内，未开始原地转身。" ||
            value == "用户已进入当前路线的身体净距范围，已停止移动。" || value == "user-clearance") return "user-clearance";
        if (value.Contains("[service-code=stream_quality_rejected]")) return "motion-quality";
        if (value.Contains("目标快照移动") || value.Contains("用户视点绑定已改变") || value.Contains("plan-target-changed")) return "target-changed";
        if (value.Contains("未核验到达") || phase == "goal-not-reached") return "arrival-unverified";
        if (value.Contains("超出可操作距离") || value.Contains("操作距离") || value.Contains("out-of-reach")) return "device-distance";
        if (value.Contains("未在当前") && value.Contains("视") || value.Contains("not-visible") || value.Contains("same-visible-entity")) return "visibility-unverified";
        if (value.Contains("ARDY 服务请求失败") || value.Contains("service-request")) return "motion-service";
        if (phase == "cancelled" || value.Contains("owner-invalidated") || value.Contains("replaced")) return "cancelled";
        if (phase == "unavailable" || value.Contains("room-control-unavailable")) return "unavailable";
        if (phase == "unreachable") return "unreachable";
        return "unverified";
    }

    private static string ActionOutcomeFailureText(string phase, string reason, string language)
    {
        string Text(string zh, string ja, string en) => language == "ja" ? ja : language == "en" ? en : zh;
        switch (ActionOutcomeFailureCategory(phase, reason))
        {
            case "body-task-occupied": return Text("我还在执行之前的身体任务，这次没有让出身体，所以新的动作没有开始。", "前の身体タスクが続いていて、身体を引き継がなかったので、新しい動きは始めていないわ。", "The previous body task still owns the body; I didn't hand it over, so the new action hasn't started.");
            case "body-release-pending": return Text("之前的跟随已经暂停，但身体还没完成姿势交接，所以新动作没有开始。", "ついていく動きは一時停止したけれど、姿勢の引き継ぎが終わらず、新しい動きは始めていないわ。", "Following is paused, but the body has not finished releasing its pose, so the new action hasn't started.");
            case "body-handoff-changed": return Text("身体任务或控制绑定在交接时改变了，这次的新动作没有开始。", "引き継ぎ中に身体タスクか接続が変わったので、新しい動きは始めていないわ。", "The body task or control binding changed during handoff, so the new action hasn't started.");
            case "user-clearance": return Text("刚才检测到你进入了身体安全间距，这一步被安全检查挡住了。", "さっき身体の安全な間隔を保てなかったので、この動きは安全確認で止まったわ。", "The safety check blocked that step because you were inside the body's clearance space at that time.");
            case "motion-quality": return Text("这一步的动作生成未通过质量检查，执行已停止。", "動作の生成が品質チェックを通らなかったので、実行は止まったわ。", "The generated motion failed its quality check, so execution stopped.");
            case "motion-service": return Text("动作服务这次返回了错误，执行已停止。", "今回は動作サービスでエラーが起きたので、実行は止まったわ。", "The motion service returned an error for this attempt, so execution stopped.");
            case "target-changed": return Text("这一步进行时目标位置或绑定发生了变化，原来的执行已停止。", "途中で目標の位置や接続が変わったので、元の動きは止まったわ。", "The target position or binding changed during this step, so the original execution stopped.");
            case "device-distance": return Text("刚才确认的距离超出了这个物体的操作范围，还没有改变它的状态。", "確認した距離がその物の操作範囲を超えていたので、状態は変えていないわ。", "The observed distance was outside the object's operating range, so its state was not changed.");
            case "visibility-unverified": return Text("刚才的新画面还没有确认到同一个目标，这一步没有继续执行。", "新しい画面で同じ対象を確認できなかったので、この動きは続けていないわ。", "The fresh view did not confirm the same target, so this step did not continue.");
            case "arrival-unverified": return Text("动作已经结束，但实际位置和朝向还没有满足这次走近目标。", "動きは終わったけれど、実際の位置と向きは今回の目標を満たしたと確認できていないわ。", "The motion ended, but the measured position and facing did not verify this approach goal.");
            case "cancelled": return Text("这一步已取消或被新目标替换，旧任务没有继续执行。", "この動きは取り消されたか新しい目標に置き換わったので、古いタスクは続けていないわ。", "This step was cancelled or replaced; the old task did not continue.");
            case "unavailable": return Text("这一步所需的控制或观测当时不可用，没有确认完成。", "必要な操作や観測がその時は使えなかったので、完了は確認できていないわ。", "The required control or observation was unavailable at that time, so completion was not confirmed.");
            case "unreachable": return Text("这次没有通过到目标的路线检查，尚未走到目标。", "今回は目標までの経路を確認できなかったので、まだ到着していないわ。", "The route check did not pass for this attempt; arrival was not confirmed.");
            default: return null;
        }
    }
}
