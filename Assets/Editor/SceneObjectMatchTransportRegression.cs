using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real production serializer and completion/cancellation logic on an inactive provider; no HTTP.</summary>
public static class SceneObjectMatchTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static int RunChecks()
    {
        checks = 0;
        LastReport = new JObject { ["status"]="running",
            ["scope"]="Production matcher wire serialization and envelope/generation boundaries on an inactive provider. Synthetic image strings test attachment ownership only; no HTTP, real-image interpretation or scene action is claimed.", ["httpRequests"]=0 };
        var host = new GameObject("InactiveSceneObjectMatchTransportFixture"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.ActiveSkillContext="PRIVATE_MATCH_SKILL"; model.TrailingContext="PRIVATE_MATCH_TRAILING";
            model.SpokenPrefix="PRIVATE_MATCH_SPOKEN"; model.RequestContext="PRIVATE_MATCH_REQUEST_CONTEXT";
            var history = new LLM.SendData("assistant", "PRIVATE_MATCH_HISTORY") { imageDataUrl="data:image/jpeg;base64,UFJJVkFURV9PTERfSU1BR0U=" };
            model.m_DataList.Add(history); int historyCount = model.m_DataList.Count;
            Require(model.SupportsSceneObjectMatchMessages, "The actual provider exposes independent object matching.");
            const string image = "data:image/jpeg;base64,Q1VSUkVOVF9NQVRDSF9JTUFHRQ==";
            const string input = "{\"currentUserText\":\"靠近电视柜，看清小灯\\n不选旧物件\",\"targetPurpose\":\"observe-closer\",\"candidates\":[{\"candidateId\":\"c1\"}]}";
            foreach (ChatQW.BackendType backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
            {
                model.m_Backend=backend; model.m_LocalModelName="public-local-model"; model.m_ChatModelName="public-cloud-model";
                string roomBefore = (string)Call(model, "BuildRoomTaskRequestJsonWithImage", input, image, false);
                string wire = (string)Call(model, "BuildSceneObjectMatchRequestJson", input, image);
                var request = JObject.Parse(wire); var messages = (JArray)request["messages"];
                Require((string)request["model"] == (backend == ChatQW.BackendType.Local ? "public-local-model" : "public-cloud-model") &&
                    (bool?)request["stream"] == false && (bool?)request["enable_thinking"] == false &&
                    (int?)request["max_tokens"] == 240 && (double?)request["temperature"] == 0,
                    "Matcher retains the configured model and bounded nonthinking output parameters.");
                Require(messages.Count == 2 && (string)messages[0]["role"] == "system" &&
                    (string)messages[0]["content"] == RoomTaskProtocol.ObjectMatchContract + "\n" + RoomTaskProtocol.ObjectMatchSchema &&
                    (string)messages[1]["role"] == "user", "Matcher uses its own exact contract and current request, not a decision/review prompt.");
                Require(request.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] { image }) &&
                    ((JArray)messages[1]["content"]).Count == 2 && ((string)messages[1]["content"][1]["text"]).EndsWith(input, StringComparison.Ordinal),
                    "One current image and verbatim quoted input survive the actual provenance wrapper.");
                Require(!wire.Contains("PRIVATE_MATCH_") && !wire.Contains("本次发声输出协议"),
                    "Matcher receives no private history, previous images, skill context or speech instructions.");
                Require((string)request["response_format"]["type"] == "json_object", "Matcher requests structured output.");
                if (backend == ChatQW.BackendType.Local)
                    Require((int?)request["id_slot"] == 1 && (bool?)request["chat_template_kwargs"]?["enable_thinking"] == false &&
                        JToken.DeepEquals(request["response_format"]["schema"], JObject.Parse(RoomTaskProtocol.ObjectMatchSchema)),
                        "Local matcher uses the auxiliary slot and exact short-candidate schema.");
                else Require(request["id_slot"] == null && request["chat_template_kwargs"] == null && request["response_format"]["schema"] == null,
                    "Cloud serialization contains no local-only schema or slot options.");
                Require(roomBefore == (string)Call(model, "BuildRoomTaskRequestJsonWithImage", input, image, false),
                    "Serializing a matcher does not alter existing room decision wire bytes.");
            }

            int calls=0; bool accepted=false; string output=null, status=null;
            Action<bool,string,string> callback=(ok,text,state) => { calls++; accepted=ok; output=text; status=state; };
            int roomGeneration = Field(model, "m_RoomTaskRequestGeneration"), streamGeneration = Field(model, "m_StreamRequestGeneration");
            model.PostSceneObjectMatchMessage(input, null, callback);
            Require(calls == 1 && !accepted && output == "" && status == "scene-object-match-current-image-required" &&
                typeof(ChatQW).GetField("m_SceneObjectMatchRequest", Flags).GetValue(model) == null,
                "A missing current image fails synchronously without launching a coroutine or network request.");
            Require(Field(model, "m_RoomTaskRequestGeneration") == roomGeneration && Field(model, "m_StreamRequestGeneration") == streamGeneration,
                "Matcher-only cancellation does not invalidate the separate room planner or formal stream.");
            string result = "{\"verdict\":\"matched\",\"candidateId\":\"c1\",\"candidateRole\":\"target\",\"reason\":\"候选符合观察参照\"}";
            string complete = Envelope(result).ToString(); int generation = Field(model, "m_SceneObjectMatchGeneration");
            Call(model, "PublishSceneObjectMatchCompletion", generation, true, 200L, complete, 0f, callback);
            Require(calls == 2 && accepted && output == result && status == "received", "A complete current matcher response reaches its owner verbatim.");
            model.CancelSceneObjectMatchMessage();
            Require(Field(model, "m_SceneObjectMatchGeneration") > generation &&
                Field(model, "m_RoomTaskRequestGeneration") == roomGeneration, "Matcher cancellation has independent generation ownership.");
            Call(model, "PublishSceneObjectMatchCompletion", generation, true, 200L, complete, 0f, callback);
            Call(model, "PublishSceneObjectMatchCompletion", generation, false, 0L, "", 0f, callback);
            Require(calls == 2, "Late success and failure from a cancelled matcher cannot revive its scene action.");
            generation=Field(model, "m_SceneObjectMatchGeneration");
            Call(model, "CancelRoomTaskMessage");
            Require(Field(model, "m_SceneObjectMatchGeneration") == generation, "Room cancellation alone does not rewrite matcher request identity.");
            Call(model, "PublishSceneObjectMatchCompletion", generation, false, 200L, complete, 0f, callback);
            Require(calls == 3 && !accepted && output == "", "Transport failure cannot release a plausible semantic JSON result.");

            foreach (string finish in new[] { "length", "tool_calls", "content_filter", "Stop" })
            {
                var body=Envelope(result); body["choices"][0]["finish_reason"]=finish;
                Call(model, "PublishSceneObjectMatchCompletion", generation, true, 200L, body.ToString(), 0f, callback);
                Require(!accepted && output == "", "Non-final matcher completion fails closed: " + finish);
            }
            foreach (string field in new[] { "tool_calls", "function_call", "refusal" })
            {
                var body=Envelope(result); body["choices"][0]["message"][field]="unexpected";
                Call(model, "PublishSceneObjectMatchCompletion", generation, true, 200L, body.ToString(), 0f, callback);
                Require(!accepted && output == "", "Tool/refusal envelopes cannot become object matching results.");
            }
            string duplicate=complete.Replace("\"finish_reason\": \"stop\"", "\"finish_reason\": \"stop\",\"finish_reason\": \"length\"");
            Require(duplicate != complete, "Duplicate-key fixture actually changes the completion envelope.");
            foreach (string body in new[] { "", "[]", "{}", duplicate, new string('x',200001) })
            {
                Call(model, "PublishSceneObjectMatchCompletion", generation, true, 200L, body, 0f, callback);
                Require(!accepted && output == "", "Malformed, ambiguous or oversized envelopes fail without releasing text.");
            }
            int beforeCancellation=calls; generation=Field(model, "m_SceneObjectMatchGeneration");
            model.CancelActiveResponse();
            Require(Field(model, "m_SceneObjectMatchGeneration") > generation, "Cancelling a formal response also cancels its semantic matcher.");
            Call(model, "PublishSceneObjectMatchCompletion", generation, true, 200L, complete, 0f, callback);
            Require(calls == beforeCancellation, "A late matcher cannot outlive formal cancellation.");
            Require(model.m_DataList.Count == historyCount && ReferenceEquals(model.m_DataList[historyCount-1],history) &&
                history.content == "PRIVATE_MATCH_HISTORY" && history.imageDataUrl == "data:image/jpeg;base64,UFJJVkFURV9PTERfSU1BR0U=",
                "Serializer, rejection and cancellation leave accepted dialogue history and archived pixels untouched.");
            LastReport["status"]="passed"; return checks;
        }
        catch (Exception error) { LastReport["status"]="failed"; LastReport["error"]=error.ToString(); throw; }
        finally { LastReport["checks"]=checks; UnityEngine.Object.DestroyImmediate(host); }
    }
    private static JObject Envelope(string content) => new JObject { ["choices"]=new JArray(new JObject {
        ["finish_reason"]="stop", ["message"]=new JObject { ["role"]="assistant", ["content"]=content } }) };
    private static object Call(object model,string name,params object[] args) => typeof(ChatQW).GetMethod(name,Flags).Invoke(model,args);
    private static int Field(object model,string name) => (int)typeof(ChatQW).GetField(name,Flags).GetValue(model);
    private static void Require(bool valid,string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
}
