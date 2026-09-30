using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public static class RoomPreparationWireRegression
{
    private static int checks;
    private static void Check(bool value, string label)
    { checks++; if (!value) throw new Exception("Room preparation: " + label); }
    private static readonly string Input = new JObject {
        ["currentUserText"] = "今天感觉很好。", ["autonomous"] = false,
        ["recentDialogue"] = new JArray(), ["visualObservation"] = null
    }.ToString();
    private static string Envelope(string content = "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}")
        => new JObject { ["choices"] = new JArray(new JObject {
            ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = content }
        }) }.ToString();
    private static void Complete(IEnumerator routine, UnityWebRequest request, string body = null, bool success = true)
    {
        request.isDone = true; request.responseCode = success ? 200 : 0;
        request.result = success ? UnityWebRequest.Result.Success : UnityWebRequest.Result.ConnectionError;
        request.downloadHandler.text = body ?? Envelope();
        Check(!routine.MoveNext(), "completion exits request coroutine");
    }
    public static void Run()
    {
        checks = 0; Time.realtimeSinceStartup = 0; Debug.OnLog = null; UnityWebRequest.Created.Clear();
        Check(RoomPreparationBudget.BackgroundTimeoutSeconds == 12, "one twelve-second background budget");
        Check(RoomPreparationBudget.JoinDeadline(12, 7) == 7.5f, "formal critical path adds at most .5s");
        Check(RoomPreparationBudget.JoinDeadline(12, 11.9f) == 12, "joining never renews the original deadline");
        Check(RoomPreparationBudget.JoinDeadline(7.5f, 7.3f) == 7.5f, "duplicate join cannot extend its first wait");

        var model = new ChatQW(); int callbacks = 0; bool accepted = false;
        model.PostRoomPreparationMessage(Input, (ok, text, status) => { callbacks++; accepted = ok; });
        var request = UnityWebRequest.Created.Last();
        Check(request.timeout == RoomPreparationBudget.BackgroundTimeoutSeconds, "actual production HTTP request uses shared budget");
        var payload = JObject.Parse(Encoding.UTF8.GetString(request.uploadHandler.Data));
        Check((int)payload["max_tokens"] == 640 && !(bool)payload["stream"] && payload["messages"].Count() == 3,
            "unchanged production planner payload is bounded and nonstreaming");
        Check(((string)payload["messages"][1]["content"]).Contains("今天感觉很好") &&
            !payload.ToString().Contains("image_url"), "preparation preserves exact text and never invents a visual observation");
        Time.realtimeSinceStartup = 6;
        Complete(model.Running.Last(), request);
        Check(callbacks == 1 && accepted && request.Disposed, "a six-second completed reply is delivered, not rejected by former two-second timeout");
        Check(!model.Running.Last().MoveNext() && callbacks == 1, "terminal response cannot publish twice");

        int old = 0, newer = 0;
        model.PostRoomPreparationMessage(Input, (_,__,___) => old++);
        var oldRoutine = model.Running.Last(); var oldRequest = UnityWebRequest.Created.Last();
        model.PostRoomPreparationMessage(Input, (_,__,___) => newer++);
        var newRoutine = model.Running.Last(); var newRequest = UnityWebRequest.Created.Last();
        Check(oldRequest.AbortCount == 1, "replacement aborts only the old preparation");
        Complete(oldRoutine, oldRequest);
        Check(old == 0 && newRequest.AbortCount == 0, "late old success cannot publish or cancel successor");
        Complete(newRoutine, newRequest);
        Check(newer == 1, "successor remains deliverable");

        model.PostRoomPreparationMessage(Input, (_,__,___) => old++);
        oldRoutine = model.Running.Last(); oldRequest = UnityWebRequest.Created.Last();
        Debug.OnLog = _ => { Debug.OnLog = null; model.PostRoomPreparationMessage(Input, (a,b,c) => newer++); };
        Complete(oldRoutine, oldRequest);
        Check(old == 0, "synchronous diagnostic reentry cannot release old callback after replacement");
        Complete(model.Running.Last(), UnityWebRequest.Created.Last());
        Check(newer == 2, "diagnostic replacement retains its own callback");

        int failed = 0;
        model.PostRoomPreparationMessage(Input, (ok,text,status) => {
            Check(!ok && status == "preparation-http-failure", "transport failure remains explicit and cannot become accepted semantics"); failed++;
        });
        Complete(model.Running.Last(), UnityWebRequest.Created.Last(), success:false);
        Check(failed == 1, "transport failure delivered once");

        int before = UnityWebRequest.Created.Count;
        var stale = (IEnumerator)typeof(ChatQW).GetMethod("RequestRoomPreparation", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(model, new object[] { Input, -1, (Action<bool,string,string>)((a,b,c) => old++) });
        Check(!stale.MoveNext() && UnityWebRequest.Created.Count == before, "already-cancelled coroutine cannot start a late duplicate HTTP request");
        Debug.OnLog = null;
        Console.WriteLine("PASS: " + checks + " room preparation budget/cancellation/production-wire checks (no HTTP)");
    }
}
