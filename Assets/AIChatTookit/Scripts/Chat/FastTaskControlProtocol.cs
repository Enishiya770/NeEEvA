using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>A separate semantic check of the user's current body-task control.
/// It receives no candidate decision, character persona or prior assistant text.</summary>
public static class FastTaskControlProtocol
{
    public const string SystemPrompt =
        "你只判断当前用户是否要求现在结束正在执行的身体任务，不扮演角色、不回复对话。" +
        "只输出一个JSON字段control，值为none、stop_follow或stop_movement。" +
        "roomTask是当前程序事实，current_user是完整本轮原话。没有连接或activeTaskId为空时只能none。" +
        "用户明确要求现在结束持续跟随，且followActive=true时选stop_follow；要求现在结束其他正在移动的任务，且followActive=false时选stop_movement。" +
        "当前正在跟随时，明确表示可以停下、不用再跟随或纠正先前要求停止却仍在跟随，也是在要求现在结束。" +
        "判断完整语义及否定作用范围。要求继续跟随、拒绝停止、引用/解释/讨论命令、问任务怎样结束、夸奖本身、未来条件下再停均选none。" +
        "停止发声、不唱歌、解除头部姿势不等于终止身体任务；暂停位置但仍保留跟随也选none。" +
        "同一输入另有其他目标不影响明确的当前身体终止要求；但不得把其他目标的否定当成终止跟随。范围不清选none。";

    public static JObject BuildUser(string context, string currentUser)
    {
        JObject room = null;
        try { room = JObject.Parse(context ?? "")["roomTask"] as JObject; }
        catch (JsonException) { }
        // Do not copy arbitrary task prose, history, persona or a prior proposal.
        var facts = new JObject {
            ["connected"] = Boolean(room, "connected"),
            ["activeTaskId"] = Text(room, "activeTaskId", 128),
            ["followActive"] = Boolean(room, "followActive"),
            ["taskMode"] = Text(room, "taskMode", 40),
            ["bodyReserved"] = Boolean(room, "bodyReserved"),
            ["phase"] = Text(room, "phase", 40)
        };
        return new JObject { ["roomTask"] = facts, ["current_user"] = currentUser ?? "" };
    }

    private static bool Boolean(JObject value, string key) => value?[key]?.Type == JTokenType.Boolean && (bool)value[key];
    private static string Text(JObject value, string key, int maximum)
    {
        string text = value?[key]?.Type == JTokenType.String ? (string)value[key] : "";
        return text.Length <= maximum ? text : "";
    }

    public static JObject ResponseFormat() => new JObject {
        ["type"] = "json_schema", ["json_schema"] = new JObject {
            ["name"] = "fast_task_control", ["strict"] = true,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("control"), ["properties"] = new JObject {
                    ["control"] = new JObject { ["type"] = "string", ["enum"] = new JArray("none", "stop_follow", "stop_movement") }
                }
            }
        }
    };

    public static bool TryParse(string raw, out string control, out string reason)
    {
        control = null; reason = "invalid_control_json";
        if (string.IsNullOrEmpty(raw) || raw.Length > 256) return false;
        // A scalar-only fixed vocabulary allows an exact JSON grammar: no
        // duplicate fields, comments, tags, JS extensions or extra documents.
        var match = Regex.Match(raw, "\\A[ \\t\\r\\n]*\\{[ \\t\\r\\n]*\"control\"[ \\t\\r\\n]*:[ \\t\\r\\n]*\"(none|stop_follow|stop_movement)\"[ \\t\\r\\n]*\\}[ \\t\\r\\n]*\\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        control = match.Groups[1].Value; reason = "ok"; return true;
    }

    public static bool TryReadResponse(string envelope, out string control, out string finishReason, out string reason)
    {
        control = null; finishReason = ""; reason = "invalid_envelope";
        if (string.IsNullOrWhiteSpace(envelope) || envelope.Length > 65536) return false;
        try {
            var root = JObject.Parse(envelope, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (!(root["choices"] is JArray choices) || choices.Count != 1 ||
                !(choices[0] is JObject choice) || !(choice["message"] is JObject message)) return false;
            if (choice["finish_reason"]?.Type != JTokenType.String) { reason = "missing_finish"; return false; }
            string finish = (string)choice["finish_reason"];
            finishReason = finish == "stop" || finish == "length" || finish == "tool_calls" || finish == "content_filter" ? finish : "other";
            if (finish != "stop") { reason = "finish_not_stop"; return false; }
            if (message["content"]?.Type != JTokenType.String) { reason = "missing_message_content"; return false; }
            if (message["role"] != null && (message["role"].Type != JTokenType.String || (string)message["role"] != "assistant"))
            { reason = "unexpected_message_role"; return false; }
            var calls = message["tool_calls"]; var function = message["function_call"]; var refusal = message["refusal"];
            if (calls != null && calls.Type != JTokenType.Null && !(calls is JArray empty && empty.Count == 0) ||
                function != null && function.Type != JTokenType.Null ||
                refusal != null && refusal.Type != JTokenType.Null && !(refusal.Type == JTokenType.String && (string)refusal == ""))
            { reason = "unexpected_message_channel"; return false; }
            return TryParse((string)message["content"], out control, out reason);
        }
        catch (JsonException) { reason = "invalid_json_envelope"; }
        catch (ArgumentException) { reason = "invalid_envelope_argument"; }
        catch (InvalidOperationException) { reason = "invalid_envelope_operation"; }
        return false;
    }
}
