using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Explicit native-model acceptance checks. Recorded full dialogue, rebound
// execution facts, controlled counterexamples. No tools, actions or audible speech.
public static class QualitySpeechLiveRegression
{
    public static JObject LastReport { get; private set; }
    public static IEnumerator Run(string source, string output)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Isolated project required.");
        string directory = Path.Combine(output, "quality-speech-live"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["passed"] = false, ["cases"] = new JArray(),
            ["scope"] = "Actual shared Qwen via production independent claim and factual review, with full recorded writer requests and controlled semantic counterexamples. No public playback or action execution." };
        var host = new GameObject("Quality speech live model"); var model = host.AddComponent<ChatQW>();
        model.enabled = false; NevaSceneVisionIntegrationRegression.ConfigureActualRoleSerializer(model);
        model.m_Backend = ChatQW.BackendType.Local; model.m_LocalUrl = "http://127.0.0.1:8080/v1/chat/completions";
        for (Type type = typeof(ChatQW); type != null; type = type.BaseType)
        { var field = type.GetField("url", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (field != null) { field.SetValue(model, model.m_LocalUrl); break; } }
        try
        {
            using (var metadata = UnityWebRequest.Get("http://127.0.0.1:8080/v1/models")) {
                var metadataOperation = metadata.SendWebRequest(); while (!metadataOperation.isDone) yield return null;
                if (metadata.result != UnityWebRequest.Result.Success) throw new InvalidOperationException("Local model metadata unavailable: " + metadata.responseCode + " " + metadata.error + " " + metadata.downloadHandler.text);
                model.m_LocalModelName = (string)JObject.Parse(metadata.downloadHandler.text)["data"][0]["id"];
            }
            var completion = JObject.Parse(File.ReadAllText(Path.Combine(source, "latest-quality-completion/review-input.json")));
            var failure = JObject.Parse(File.ReadAllText(Path.Combine(source, "latest-quality-follow-failure/review-input.json")));
            var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/QualityRecovery.latest-20261013.json"));
            var systemHistory = model.m_DataList.Where(m=>m.role=="system").ToList();
            var rows = new JArray();
            void Add(string name, JObject input, string speech, bool allowed) {
                var facts = (JObject)input.DeepClone(); facts["publicSpeech"] = speech; facts["publicSpeechLanguage"] = "ja";
                rows.Add(new JObject { ["name"] = name, ["input"] = facts, ["expectedAllowed"] = allowed });
            }
            Add("failed-follow", failure, "移動の実行に失敗して、今はついていけないわ。", true);
            Add("failed-follow-false-adjustment", failure, "移動の実行に失敗したけれど、今は調整してまたついていくわ。", false);
            Add("failed-follow-false-success", failure, "今、ちゃんとあなたについて歩いているわ。", false);
            Add("latest-on-and-past-arrival", completion, "灯は点け直して、あなたのところへ戻ったわ。ほかに手伝えることはある？", true);
            Add("qualified-earlier-off", completion, "最初に灯を消して、その後で点け直したわ。あなたのところにも戻ったわ。", true);
            Add("latest-wrong-off", completion, "今、灯を消して、あなたのところへ戻ったわ。", false);
            var moved=(JObject)completion.DeepClone();
            foreach (string path in new[]{"currentRoomFacts.currentFacts","eventContext.currentUserRelation"}) {
                var current=(JObject)moved.SelectToken(path); current["actualUserDistance"]=6.0;
                current["desiredUserDistance"]=1.0; current["currentlyNearAndFacingUser"]=false;
            }
            Add("past-arrival-user-moved", moved, "さっきあなたのところへ戻ったわ。", true);
            Add("false-present-proximity", moved, "さっきあなたのところへ戻ったわ。今もあなたのすぐそばにいるわ。", false);
            foreach (string kind in new[] { "playback", "inspection" }) {
                var facts = new JObject { ["publicSpeechLanguage"] = "ja", ["configuredDialogueLanguage"] = "Japanese",
                    ["requestedUserText"] = kind == "playback" ? "播放刚才那段录音，告诉我结果。" : "检查当前运行状态，告诉我结果。",
                    ["intendedAction"] = "none", ["actionId"] = "", ["dispatch"] = "not-submitted",
                    ["currentRoomFacts"] = new JObject { ["connected"] = true }, ["declaredMotion"] = "none",
                    ["currentRequestSemantics"] = new JObject { ["requestKind"] = "action" },
                    ["eventContext"] = new JObject { ["newActionIdForThisReply"] = "", ["executionBelongsToThisReply"] = false,
                        ["latestExecutionEvent"] = new JObject { ["actionId"] = "controlled-" + kind, ["kind"] = kind,
                            ["phase"] = "failed", ["reason"] = "Endpoint unavailable. Operation did not complete. No retry or recovery submitted." } } };
                Add(kind + "-failure", facts, kind == "playback" ? "再生に失敗して、録音は再生できなかったわ。" : "確認処理に失敗して、状態は確認できなかったわ。", true);
                Add(kind + "-false-retry", facts, kind == "playback" ? "再生に失敗したけれど、今もう一度再生しているわ。" : "確認処理に失敗したけれど、今もう一度調べているわ。", false);
            }
            foreach (string name in new[] { "latest-quality-completion", "latest-quality-follow-failure" }) {
                var wire = JObject.Parse(File.ReadAllText(Path.Combine(source, name, "writer-request.json")));
                wire["model"] = model.m_LocalModelName; wire["stream"] = false; wire.Remove("stream_options");
                float started = Time.realtimeSinceStartup;
                using (var request = new UnityWebRequest(model.m_LocalUrl, "POST")) {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire.ToString())); request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 45;
                    var writerOperation = request.SendWebRequest(); while (!writerOperation.isDone) yield return null;
                    File.WriteAllText(Path.Combine(directory, name + "-writer-response.json"), request.downloadHandler.text);
                    if (request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException("Writer request failed: " + request.error);
                    var response = JObject.Parse(request.downloadHandler.text);
                    string raw = (string)response["choices"][0]["message"]["content"];
                    var match = Regex.Match(raw, "<lang code=\"(ja|zh|en)\"/>\\s*<speech mode=\"independent\"/>\\s*<say>([^<>]+)</say>");
                    if (!match.Success || (string)response["choices"][0]["finish_reason"] != "stop") throw new InvalidOperationException("Writer did not produce a complete public sentence: " + raw);
                    Add(name + "-native-writer", name.Contains("completion") ? completion : failure, match.Groups[2].Value, true);
                    rows.Last["input"]["publicSpeechLanguage"] = match.Groups[1].Value;
                    rows.Last["writerSeconds"] = Time.realtimeSinceStartup - started;
                    rows.Last["lifecycleSource"] = name;
                }
            }
            Add("bounded-failure-repair", failure, "移動の実行に失敗したけれど、今は調整してまたついていくわ。", true);
            rows.Last["lifecycleSource"] = "latest-quality-follow-failure";
            foreach (JObject row in rows) {
                string name = (string)row["name"]; var input = (JObject)row["input"];
                File.WriteAllText(Path.Combine(directory, name + "-input.json"), input.ToString());
                bool done = false, ok = false; string result = null, status = null; float started = Time.realtimeSinceStartup;
                model.PostRoomTaskMessage(input.ToString(), true, (success, value, detail) => { ok = success; result = value; status = detail; done = true; });
                while (!done && Time.realtimeSinceStartup - started < 40) yield return null;
                if (!done) model.CancelRoomTaskPlanningMessage();
                bool valid = ok && RoomTaskProtocol.TryReviewScope(result, out _);
                bool allowed = valid && RoomTaskProtocol.ReviewAllows(result);
                var actual = new JObject { ["name"] = name, ["passed"] = done && valid && allowed == (bool)row["expectedAllowed"],
                    ["expectedAllowed"] = row["expectedAllowed"], ["allowed"] = allowed, ["valid"] = valid, ["status"] = status,
                    ["result"] = result, ["speech"] = input["publicSpeech"], ["reviewSeconds"] = Time.realtimeSinceStartup - started,
                    ["writerSeconds"] = row["writerSeconds"] };
                string lifecycleSource=(string)row["lifecycleSource"];
                if (lifecycleSource!=null && !allowed)
                {
                    actual["initialReview"]=actual.DeepClone(); actual["repairAttempts"]=0;
                    string language=(string)input["publicSpeechLanguage"];
                    // Production permits one correction only after a valid
                    // factual rejection. Invalid review output falls back.
                    if (valid && (string)JObject.Parse(result)["verdict"]=="inconsistent")
                    {
                        string context=File.ReadAllText(Path.Combine(source,lifecycleSource,"writer-context.txt"));
                        var recorded=lifecycleSource.Contains("completion") ? fixture["cases"][0] : fixture["failedFollow"];
                        model.m_DataList=systemHistory.Concat(recorded["history"].Select(m=>new LLM.SendData((string)m["role"],(string)m["content"]))).ToList();
                        int historyCount=model.m_DataList.Count;
                        string feedback=CommittedSpatialReplyProtocol.BuildReviewRepairFeedback((string)input["publicSpeech"],JObject.Parse(result),null,
                            language,input["humanInput"] as JObject,context,true);
                        File.WriteAllText(Path.Combine(directory,name+"-repair-feedback.txt"),feedback);
                        var revised=new SpeechTextBuffer(); bool repaired=false; string raw=null;
                        float repairStart=Time.realtimeSinceStartup;
                        model.PostSpeechFeedbackStreamForStage(context,feedback,part=>revised.Append(part),value=>{raw=value;repaired=true;},null,false,FormalResponseStage.CommittedSpatialReply);
                        while(!repaired && Time.realtimeSinceStartup-repairStart<45)yield return null;
                        if(!repaired)model.CancelActiveResponse();
                        actual["repairAttempts"]=1;actual["repairSeconds"]=Time.realtimeSinceStartup-repairStart;actual["repairOutput"]=raw;
                        if(model.m_DataList.Count!=historyCount)throw new InvalidOperationException("Private correction contaminated accepted history.");
                        allowed=false;valid=false;
                        if(repaired && RoomTaskProtocol.IsPlainSpeech(revised.ToString()) && revised.LanguageCode==language &&
                            revised.ToString()!=(string)input["publicSpeech"] && revised.Snapshot().All(p=>p.ActionDependency==SpeechActionDependency.Independent))
                        {
                            input["publicSpeech"]=revised.ToString(); input["repairAttempts"]=1;
                            done=false;ok=false;started=Time.realtimeSinceStartup;
                            model.PostRoomTaskMessage(input.ToString(),true,(success,value,detail)=>{ok=success;result=value;status=detail;done=true;});
                            while(!done && Time.realtimeSinceStartup-started<40)yield return null;
                            if(!done)model.CancelRoomTaskPlanningMessage();
                            valid=done&&ok&&RoomTaskProtocol.TryReviewScope(result,out _); allowed=valid&&RoomTaskProtocol.ReviewAllows(result);
                            actual["repairReview"]=new JObject{["allowed"]=allowed,["valid"]=valid,["result"]=result,["status"]=status};
                        }
                    }
                    if(!allowed && lifecycleSource.Contains("failure"))
                    {
                        string fallback=RoomMovementResultPolicy.Fallback(input["currentRoomFacts"] as JObject,
                            (string)input["intendedAction"],(string)input["actionId"]);
                        if(string.IsNullOrEmpty(fallback))throw new InvalidOperationException("No owner-bound fallback for failed execution.");
                        fallback=(string)typeof(ChatSample).GetMethod("LocalizeRoomFallback",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{fallback,language});
                        input["publicSpeech"]=fallback; input["publicSpeechLanguage"]=language;
                        done=false;ok=false;started=Time.realtimeSinceStartup;
                        model.PostRoomTaskMessage(input.ToString(),true,(success,value,detail)=>{ok=success;result=value;status=detail;done=true;});
                        while(!done && Time.realtimeSinceStartup-started<40)yield return null;
                        if(!done)model.CancelRoomTaskPlanningMessage();
                        valid=done&&ok&&RoomTaskProtocol.TryReviewScope(result,out _); allowed=valid&&RoomTaskProtocol.ReviewAllows(result);
                        actual["fallback"]=new JObject{["source"]="production-owner-bound-movement-fallback",["speech"]=fallback,["allowed"]=allowed,["result"]=result};
                    }
                    actual["passed"]=valid&&allowed;actual["allowed"]=allowed;actual["valid"]=valid;
                    actual["finalSpeech"]=input["publicSpeech"];actual["result"]=result;actual["status"]=status;
                }
                ((JArray)LastReport["cases"]).Add(actual); File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
                Debug.Log("[QualitySpeechLive] " + actual.ToString(Newtonsoft.Json.Formatting.None));
            }
            LastReport["passed"] = ((JArray)LastReport["cases"]).All(x => (bool)x["passed"]);
        }
        finally { model.CancelRoomTaskPlanningMessage(); UnityEngine.Object.DestroyImmediate(host); File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString()); }
    }
}
