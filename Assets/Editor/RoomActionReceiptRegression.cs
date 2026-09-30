using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual public delivery gates with a connected local fixture and controlled facts; no model HTTP.</summary>
public static class RoomActionReceiptRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        checks = 0;
        if (!Application.isPlaying || !Application.isBatchMode || !chat.isActiveAndEnabled || !bridge.IsConnected)
            throw new InvalidOperationException("Use the isolated enabled camera/room fixture.");
        object priorFact = Get(chat, "m_SceneActionFact");
        try
        {
            Prepare(chat, "look-at", "rejected: object-target-expired");
            string action = (string)Get(chat, "m_RoomTaskActionId");
            Set(chat, "m_SceneActionFact", Fact(action, "look-at", "rejected"));
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            int reviewCount = model.Decisions.Count, historyCount = model.m_DataList.Count;
            const string rejectedClaim = "こうすれば、画面の全体が見えているでしょう。";
            var speech = new SpeechTextBuffer(); speech.Append(new SpeechText(rejectedClaim, "ja", actionDependency: SpeechActionDependency.Independent));
            Call(chat, "CompleteRoomSpeech", speech, rejectedClaim, generation, true, null);
            Require(model.Decisions.Count == reviewCount, "A rejected action cannot be relabelled factScope=none by an extra model review.");
            Require(model.m_DataList.Count == historyCount + 1 && model.m_DataList.Last().content != rejectedClaim &&
                model.m_DataList.Last().content.Contains("まだできていない"), "The false completion sentence is replaced by the current failed-action receipt.");

            Prepare(chat, "look-at", "observed");
            action = (string)Get(chat, "m_RoomTaskActionId");
            Set(chat, "m_SceneActionFact", Fact(action, "look-at", "observed"));
            foreach (string language in new[] { "ja", "zh", "en" })
                Require(!string.IsNullOrWhiteSpace((string)Call(chat, "CurrentRoomActionReceipt", language)), "Observed action has a supported-language bounded receipt.");
            Require(Call(chat, "CurrentRoomActionReceipt", "auto") == null, "Receipt language cannot silently fall through to a different language.");
            generation = (int)Get(chat, "m_FormalResponseGeneration");
            var decision = new RoomTaskProtocol.Decision { intent="look-at", origin="user", replyMode="receipt", replyLanguage="ja" };
            int formalCount = model.Speech.Count; reviewCount = model.Decisions.Count; historyCount = model.m_DataList.Count;
            Require(!(bool)Call(chat, "TryDeliverRoomActionReceipt", decision, generation - 1, true), "Stale generation cannot deliver a receipt.");
            Set(chat, "m_UserSpeechActiveForAutonomy", true);
            Require(!(bool)Call(chat, "TryDeliverRoomActionReceipt", decision, generation, true), "A new input candidate revokes pending receipt delivery.");
            Set(chat, "m_UserSpeechActiveForAutonomy", false);
            decision.replyMode = null;
            Require(!(bool)Call(chat, "TryDeliverRoomActionReceipt", decision, generation, true), "Explanations without receipt selection keep the full role response.");
            decision.replyMode = "receipt";
            Require((bool)Call(chat, "TryDeliverRoomActionReceipt", decision, generation, true), "Current observed action can deliver the model-selected bounded receipt.");
            Require(model.Speech.Count == formalCount && model.Decisions.Count == reviewCount && model.m_DataList.Count == historyCount + 1,
                "Bounded delivery eliminates formal-generation and free-speech-review calls, while committing one factual response.");
            Require(!(bool)Call(chat, "TryDeliverRoomActionReceipt", decision, generation, true) && model.m_DataList.Count == historyCount + 1,
                "Duplicate planner callbacks never publish the same receipt twice.");

            Prepare(chat, "reposition-view", "view-position-attempt-submitted");
            action = (string)Get(chat, "m_RoomTaskActionId");
            Set(chat, "m_SceneActionFact", Fact(action, "reposition-view", "submitted"));
            Set(chat, "m_SceneActionActive", true);
            string receipt = (string)Call(chat, "CurrentRoomActionReceipt", "en");
            Require(receipt != null && receipt.Contains("try") && !receipt.Contains("whole"), "An accepted reposition receipt promises neither actual arrival nor seeing the whole screen.");
            Set(chat, "m_SceneActionActive", false);
            object partial = Fact(action, "reposition-view", "arrived-object-unconfirmed");
            Set(partial, "groundArrived", true); Set(partial, "postArrivalObservationStatus", "gaze-hold-not-established");
            Set(chat, "m_SceneActionFact", partial);
            Require((bool)Call(chat, "HasDefinitiveRoomActionFailure"), "A failed arrival gaze handoff blocks completion claims.");
            receipt = (string)Call(chat, "CurrentRoomActionReceipt", "en");
            Require(receipt.Contains("reached") && receipt.Contains("haven't been able to hold"), "Partial failure preserves actual arrival and explicitly reports the missing gaze.");
            Set(chat, "m_SceneActionFact", Fact("another-action", "reposition-view", "observed"));
            Require(Call(chat, "CurrentRoomActionReceipt", "en") == null, "Another action's result cannot authorize the current receipt.");
        }
        finally
        {
            Call(chat, "InvalidateFormalResponse", "receipt-regression-finished"); chat.StopAllCoroutines();
            Set(chat, "m_SceneActionFact", priorFact); Set(chat, "m_SceneActionActive", false);
            File.WriteAllText(Path.Combine(output,"room-action-receipt.json"), new JObject {
                ["checks"] = checks, ["scope"] = "controlled real public delivery gates; no model semantic-quality or physical audio claim"
            }.ToString());
        }
        yield return null;
    }
    private static void Prepare(ChatSample chat, string intent, string dispatch)
    {
        Call(chat, "InvalidateFormalResponse", "receipt-regression-next"); chat.StopAllCoroutines(); Call(chat,"ClearScheduledAgentWake");
        Set(chat,"m_AgentRunning",false); Set(chat,"m_AgentCurrentRoundIsTick",false); Set(chat,"m_AgentRoundInFlight",false);
        Set(chat,"m_UserSpeechActiveForAutonomy",false); Set(chat,"m_SceneTimePaused",false); Set(chat,"m_WorkReviewOpen",false);
        Set(chat,"m_FormalResponseInFlight",true); Set(chat,"m_RoomTaskFormatError",false); Set(chat,"m_SceneActionActive",false);
        Set(chat,"m_RoomTaskIntent",intent); Set(chat,"m_RoomTaskDispatch",dispatch);
        Set(chat,"m_RoomTaskActionId","receipt-fixture-"+Guid.NewGuid().ToString("N"));
        Set(chat,"m_RoomTaskGeneration",Get(chat,"m_FormalResponseGeneration")); Set(chat,"m_FormalTransportDispatchGeneration",-1);
    }
    private static object Fact(string action, string intent, string phase)
    {
        Type type=typeof(ChatSample).GetNestedType("SceneActionFact",BindingFlags.NonPublic);
        object fact=Activator.CreateInstance(type,true);
        Set(fact,"actionId",action);Set(fact,"intent",intent);Set(fact,"phase",phase);Set(fact,"reason",phase=="rejected"?"object-target-expired":"");
        return fact;
    }
    private static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
    private static object Call(object target,string name,params object[] args)=>NevaSceneVisionIntegrationRegression.Call(target,name,args);
    private static void Require(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
}
