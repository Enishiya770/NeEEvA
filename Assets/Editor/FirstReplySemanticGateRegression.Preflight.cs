using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class FirstReplySemanticGateRegression
{
    private static IEnumerator RunPreflightChecks(ChatQW model, Server server, string directory, JArray cases)
    {
        server.ConversationPreflight = false;
        try {
            foreach (string route in new[] { "tools", "evidence" }) {
                model.m_DataList.Clear(); int before = server.Count, done = 0, speech = 0;
                FirstReplyCompletion result = null;
                var review = server.Serve(Verdict(route), false, true);
                string user = route == "tools" ? "把书收好，然后回到我身边。" : "你刚才为什么没有找到杯子？";
                model.PostFirstReplyStream(user, _ => ++speech, value => { result = value; ++done; });
                yield return Await(review.Received.Task, "preflight received");
                var wire = JObject.Parse(review.RequestJson);
                var input = JObject.Parse((string)wire["messages"][1]["content"]);
                Check((bool?)input["routingPreflight"] == true && ((JArray)input["candidateSpeech"]).Count == 0 &&
                    (string)input["currentUtterance"] == user && !review.RequestJson.Contains("image_url") && speech == 0 && done == 0,
                    "First exchange classifies intact words before candidate prose or public effects.");
                review.Release.TrySetResult(true); yield return WaitUntil(() => done == 1, "preflight handoff");
                Check(server.Count == before + 1 && speech == 0 && result.Disposition ==
                    (route == "tools" ? FirstReplyDisposition.Tools : FirstReplyDisposition.Evidence) &&
                    !model.m_DataList.Exists(row => row.role == "assistant"), "Approved route omits the large candidate request.");
                var userHandoff = (LLM.SendData)typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags).Invoke(model, null);
                Check(userHandoff?.content == user && typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags).Invoke(model, null) == null,
                    "Classifier preserves one exact accepted-user handoff.");
                cases.Add(new JObject { ["id"] = "preflight-" + route, ["requests"] = 1, ["publicDeltas"] = speech });
                File.WriteAllText(Path.Combine(directory, "preflight-" + route + ".json"), wire.ToString());
            }
            foreach (bool cancel in new[] { false, true }) {
                model.m_DataList.Clear(); int controlDone = 0, done = 0, deltas = 0;
                var stop = server.Serve("{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", false, true);
                model.PostRoomPreparationMessage(RoomControlReviewProtocol.BodyInput("先停下。", "active-follow", 2, "follow-waiting", true, false).ToString(),
                    (_, __, ___) => ++controlDone);
                yield return Await(stop.Received.Task, "control submitted before main");
                int before = server.Count;
                var main = server.Serve(Verdict("tools"), false, true);
                model.PostFirstReplyStream("先停下，然后来我身边。", _ => ++deltas, _ => ++done);
                yield return new WaitForSecondsRealtime(.15f);
                Check(server.Count == before && !main.Received.Task.IsCompleted && done == 0 && deltas == 0,
                    "Control remains sole in-flight request until its bounded response; no overlapping cold main prefill.");
                if (cancel) {
                    model.CancelActiveResponse(); model.CancelRoomPreparationMessage();
                    stop.Release.TrySetResult(true); yield return Await(stop.Finished, "cancelled control socket");
                    yield return new WaitForSecondsRealtime(.1f);
                    Check(done == 0 && controlDone == 0 && !main.Received.Task.IsCompleted, "Cancellation while priority waits releases no stale requests or callbacks.");
                    // Consume the deliberately pending server exchange with a
                    // fresh generation, proving only that new owner may proceed.
                    model.PostFirstReplyStream("现在来我身边。", _ => ++deltas, _ => ++done);
                } else stop.Release.TrySetResult(true);
                yield return Await(main.Received.Task, "current preflight after control");
                main.Release.TrySetResult(true); yield return WaitUntil(() => done == 1, "current completion");
                Check(deltas == 0 && controlDone == (cancel ? 0 : 1), "Priority changes order, not stop authority or public output.");
                cases.Add(new JObject { ["id"] = "control-priority-" + (cancel ? "cancel" : "complete"), ["passed"] = true });
            }
            model.m_DataList.Clear(); int cancelledDone = 0;
            var held = server.Serve(Verdict("tools"), false, true);
            model.PostFirstReplyStream("回来。", _ => ++cancelledDone, _ => ++cancelledDone);
            yield return Await(held.Received.Task, "held preflight"); model.CancelActiveResponse();
            held.Release.TrySetResult(true); yield return Await(held.Finished, "late cancelled preflight"); yield return null;
            Check(cancelledDone == 0 && !model.m_DataList.Exists(row => row.role == "assistant"), "Late classifier response cannot revive cancelled turn.");
            cases.Add(new JObject { ["id"] = "preflight-cancel-held", ["passed"] = true });
        }
        finally { server.ConversationPreflight = true; model.CancelRoomPreparationMessage(); }
    }
}
