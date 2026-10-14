using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class SceneReviewQueueRegression
{
    private static IEnumerator SpeechHandoffChecks(Context c)
    {
        var latest=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/FollowHandoff.latest-1348-20261014.json"));
        var fixture=new JObject{["history"]=latest["history"].DeepClone(),
            ["goal"]=new JObject{["originalRequest"]=latest["turns"].Last["fullRequest"].DeepClone()},
            ["receipt"]=latest["turns"].Last["receipts"].Last["data"].DeepClone()};
        var responses=(Queue<string>)Get(c.chat,"m_WorkResponses");var saved=responses.ToArray();
        var settings=(ChatSetting)Get(c.chat,"m_ChatSettings");settings.m_TextToSpeech=c.directTts;
        int generation=(int)Get(c.chat,"m_FormalResponseGeneration");
        var rows=new JArray();LastReport["actualSpeechHandoff"]=rows;
        try {
            foreach(string variant in new[]{"complete","independent-answer","new-input","failed-audio"})
            {
                var ledger=new NeEEvA.Motion.ArdyActionConstraintLedger();
                foreach(JObject message in fixture["history"]) if((string)message["role"]=="user") ledger.RecordUserTurn((string)message["content"]);
                if(variant=="independent-answer") ledger.RecordUserTurn("检查输入设备，再告诉我采样率。");
                c.Set(c.chat,"m_ActionConstraints",ledger);
                var recordedInputs=(IList)Activator.CreateInstance(Get(c.chat,"m_SingingUserInputs").GetType());
                var inputType=recordedInputs.GetType().GetGenericArguments()[0];int inputTurn=0;
                foreach(JObject message in fixture["history"]) if((string)message["role"]=="user") {
                    var entry=Activator.CreateInstance(inputType,true);
                    inputType.GetField("turn").SetValue(entry,++inputTurn);inputType.GetField("text").SetValue(entry,(string)message["content"]);
                    recordedInputs.Add(entry);
                }
                if(variant=="independent-answer") {
                    var entry=Activator.CreateInstance(inputType,true);inputType.GetField("turn").SetValue(entry,++inputTurn);
                    inputType.GetField("text").SetValue(entry,"检查输入设备，再告诉我采样率。");recordedInputs.Add(entry);
                }
                c.Set(c.chat,"m_SingingUserInputs",recordedInputs);c.Set(c.chat,"m_SingingGoalTurn",inputTurn);
                c.Arm("speech-handoff-"+variant);c.audio.Stop();c.Set(c.chat,"m_SceneFinalPlayback",null);
                c.Set(c.chat,"m_SpatialDeviceGoal",null);
                c.Set(c.chat,"m_WorkReviewOpen",true);c.Set(c.chat,"m_WorkStatus","substep-result-pending");
                c.Set(c.chat,"m_WorkSubstepReviewPending",true);
                c.Set(c.chat,"m_WorkNoProgress",false);
                c.Set(c.chat,"m_LastUserMsg",variant=="independent-answer" ? "检查输入设备，再告诉我采样率。" : (string)fixture["goal"]["originalRequest"]);
                c.Set(c.chat,"m_TTSSenderDone",false);c.Set(c.chat,"m_StreamComplete",false);
                c.Set(c.chat,"<IsAISpeaking>k__BackingField",false);c.Set(c.chat,"m_RealFirstAudioStarted",true);
                c.Set(c.chat,"m_LatencyFillerPlayed",false);
                c.Set(c.chat,"m_AutonomyReviewMinimumSpacing",0f);
                c.model.m_DataList.Clear();foreach(JObject m in fixture["history"])
                    c.model.m_DataList.Add(new LLM.SendData((string)m["role"],(string)m["content"]));
                string prefix="この機器が今オンになっていることを確認したわ。",final="さっきあなたのそばに着いたわ。";
                if(variant=="independent-answer") {
                    prefix="入力機器を確認しました。";final="入力は正常です。";
                    c.model.m_DataList.Add(new LLM.SendData("user","检查输入设备，再告诉我采样率。"));
                    c.Set(c.chat,"m_LastSelfInspection","Completed input-device inspection: input works; sample rate is 48000 Hz. The sample-rate question still needs an answer.");
                } else c.Set(c.chat,"m_LastSelfInspection","");
                c.Set(c.chat,"m_PendingSelfInspection","");
                responses.Clear();responses.Enqueue(prefix+final);
                var receipt=(JObject)fixture["receipt"].DeepClone();
                receipt["generation"]=generation;receipt["userTurn"]=ledger.CurrentUserTurn;
                receipt["completedPlaybackText"]=prefix;receipt["publicText"]=prefix+final;
                c.Set(c.chat,"m_LastFormalReplyReceipt",receipt);c.Set(c.chat,"m_LastFormalReplyBridge",c.bridge);
                c.Set(c.chat,"m_LastFormalReplyAvatar",c.bridge.avatar);c.Set(c.chat,"m_LastFormalReplyEnvironment",c.bridge.environmentRoot);
                c.Set(c.chat,"m_LastFormalReplySession",Get(c.chat,"m_AgentSessionStartTime"));
                // Earlier test buffers do not belong to this output. Production
                // callback writes the owner-bound receipt only on actual drain.
                c.Set(c.chat,"m_AssistantHeardText",new System.Text.StringBuilder(prefix));
                c.Set(c.chat,"m_DeliveryGeneration",-1);c.Set(c.chat,"m_RoomSpeechPlayback",null);
                var queue=Get(c.chat,"m_PendingChunks");var type=typeof(ChatSample).GetNestedType("PendingSpeechChunk",BindingFlags.NonPublic);
                var chunk=Activator.CreateInstance(type,true);type.GetField("RoundId").SetValue(chunk,190002);
                type.GetField("Text").SetValue(chunk,final);type.GetField("LanguageCode").SetValue(chunk,"ja");
                queue.GetType().GetMethod("Enqueue").Invoke(queue,new[]{chunk});
                c.directTts.SynthesisComplete=false;c.directTts.Started=false;int count=c.model.probes.Count;
                c.directPlayer=c.chat.StartCoroutine((IEnumerator)Call(c.chat,"StreamDirectTTSPlayer",generation));
                yield return Until(()=>c.directTts.Started,2,"handoff PCM starts");
                c.directTts.SynthesisComplete=true;c.Set(c.chat,"m_StreamComplete",true);
                yield return Until(()=>c.model.probes.Count==count+1,5,"work review overlaps actual final PCM");
                var old=c.model.probes[count];
                old.done("{\"proceed\":true,\"work_status\":\"continue\",\"next_intent_source\":\"current-work\",\"next_step_id\":\"\",\"planning_focus\":\"other\",\"intent\":\"补充播报到达后的回应\",\"work_evidence\":\"语音仅播报了前半部分，后半部分未播放。\",\"wait_seconds\":30}");
                int handed=(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff"),publicBefore=c.model.publicRequests;
                if(variant=="failed-audio") {
                    c.audio.Stop();
                    yield return Until(()=>Get(c.chat,"m_ScenePlaybackReview")==null,4,"failed audio revokes candidate");
                    c.Check((string)receipt["completedPlaybackText"]==prefix && c.model.publicRequests==publicBefore,
                        "Generated/synthesized but interrupted audio is never counted as spoken or permitted to hand off.");
                    rows.Add(new JObject{["variant"]=variant,["passed"]=true,["completedText"]=receipt["completedPlaybackText"].DeepClone()});
                    continue;
                }
                c.audio.time=SceneReviewQueuePcmTTS.PcmDuration-.08f;
                yield return Until(()=>c.model.probes.Count==count+2,6,"changed actual playback gets bounded private recheck");
                var fresh=c.model.probes[count+1];
                c.Check(fresh.prompt.Contains("实际已播进度发生改变") && !fresh.prompt.Contains("visibleObjectCatalog") && string.IsNullOrEmpty(fresh.image),
                    "Delivery-only handoff uses the current obligations/results/played words instead of reloading the scene catalog.");
                c.Check(fresh.prompt.Contains("[Historical interrupted work; reference only]") &&
                    fresh.prompt.Contains("\"createsCurrentObligation\":false") && !fresh.prompt.Contains("\"previousUnfinishedWork\":"),
                    "Interrupted history remains reference data outside the current outstanding-work list.");
                rows.Add(new JObject{["variant"]=variant+"-review-scope",["beforeCharacters"]=old.prompt.Length,["afterCharacters"]=fresh.prompt.Length,
                    ["completeRequestRetained"]=true,["actualPlaybackRetained"]=true,["prompt"]=fresh.prompt,
                    ["history"]=new JArray(c.model.m_DataList.Select(m=>new JObject{["role"]=m.role,["content"]=m.content}))});
                c.Check(c.model.publicRequests==publicBefore && (int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff")==handed &&
                    (string)receipt["completedPlaybackText"]==prefix+final,
                    "An outdated unspoken premise cannot start a public turn after the production player records real completion.");
                c.Check(fresh.prompt.Contains(prefix+final) && fresh.prompt.Contains("音频交接复核") &&
                    !fresh.prompt.Contains("后半部分未播放。"),"Recheck receives current completed words without recycling the stale model premise.");
                if(variant=="independent-answer") c.Check(fresh.prompt.Contains("48000 Hz") && fresh.prompt.Contains("检查输入设备，再告诉我采样率"),
                    "Non-device/non-movement pending answers remain in the current task review despite completed speech.");
                if(variant=="new-input") {
                    c.Set(c.chat,"m_InputAudioRevision",(int)Get(c.chat,"m_InputAudioRevision")+1);
                    Call(c.chat,"ValidateSceneReviewQueue");fresh.done("{\"proceed\":true,\"work_status\":\"continue\",\"intent\":\"stale\"}");
                    c.Check(Get(c.chat,"m_ScenePlaybackReview")==null && c.model.publicRequests==publicBefore,"New input revokes a pending playback recheck and its late callback.");
                } else if(variant=="independent-answer") {
                    fresh.done("{\"proceed\":true,\"work_status\":\"continue\",\"next_intent_source\":\"current-work\",\"next_step_id\":\"\",\"planning_focus\":\"other\",\"intent\":\"回答采样率为48000Hz\",\"work_evidence\":\"输入正常已播出，但采样率尚未回答。\"}");
                    c.Check(Get(c.chat,"m_ScenePlaybackReview")!=null && c.model.publicRequests==publicBefore,
                        "A genuinely missing answer retains its newly reviewed candidate, without premature public/action authority.");
                    Call(c.chat,"CancelAutonomyIntentProbe");
                } else {
                    fresh.done("{\"proceed\":false,\"work_status\":\"closed\",\"next_intent_source\":\"none\",\"next_step_id\":\"\",\"planning_focus\":\"other\",\"work_evidence\":\"开灯和到达均已完成且实际播完。\",\"wait_seconds\":30}");
                    Call(c.chat,"StepUrge");
                    yield return Until(()=>(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff")==handed+1,5,"newly reviewed closed candidate handoff");
                    c.Check(c.model.publicRequests==publicBefore && c.model.probes.Count==count+2 && !(bool)Get(c.chat,"m_WorkReviewOpen"),
                        "Only one private recheck closes delivered work; no gen12, duplicate writer or repeated action.");
                }
                c.directPlayer=null;
                rows.Add(new JObject{["variant"]=variant,["passed"]=true,["completedText"]=receipt["completedPlaybackText"].DeepClone(),
                    ["initialPrompt"]=old.prompt,["refreshedPrompt"]=fresh.prompt,["newPublicRequests"]=c.model.publicRequests-publicBefore});
            }
        } finally {responses.Clear();foreach(string s in saved)responses.Enqueue(s);}
        Add("latest full dialogue / actual production PCM completion / stale candidate / pending independent answer / new input / failed audio");
    }
}
