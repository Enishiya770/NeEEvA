using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real room RGB/readback and production review lifecycle, with retained controlled
/// review callbacks. No model HTTP, native movement, fabricated images, or audible TTS.</summary>
public static class RoomSpeechViewPreparationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Candidate = "今の視界を確認しているわ。";
    public static JObject LastReport { get; private set; }
    private static int checks;

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Speech view checks require the existing isolated original-room fixture.");
        checks = 0;
        LastReport = new JObject { ["status"]="running", ["cases"]=new JArray(),
            ["scope"]="Actual original-room character RGB and asynchronous readback; production speech-review owner and fact fences with controlled verdict callbacks. No model/TTS quality or new executed action claim." };
        float oldAge = (float)Get(chat, "m_SceneObservationMaxAgeSeconds");
        Vector3 root = bridge.avatar.transform.position;
        int operation = bridge.Controller.OperationRevision;
        try
        {
            yield return Prepare(chat, model);
            int generation = Generation(chat), before = model.Decisions.Count, history = model.m_DataList.Count;
            var original = Bound(chat);
            Complete(chat);
            Require(model.Decisions.Count == before + 1 && Review(model).Image == original.ImageDataUrl,
                "A still-current formal image is audited immediately, without another capture or request.");
            Require((int)JObject.Parse(Review(model).Input)["reviewAttempt"] == 0, "The first useful audit remains attempt zero.");
            Review(model).Complete(true, Verdict(), "controlled-current-source");
            Require(model.m_DataList.Count == history + 1 && model.m_DataList.Last().content == Candidate,
                "The current-view statement enters history only after its audit.");
            Add("current-formal-image-no-extra-capture", original, original);

            yield return Prepare(chat, model);
            original = Bound(chat); Set(chat, "m_SceneObservationMaxAgeSeconds", 2f);
            yield return Wait(() => Time.realtimeSinceStartup - original.CapturedAtRealtime > 1.1f, "Still-fresh view nearing expiry");
            Require(Fresh(chat, original), "Review-window fixture is still fresh; it has not expired yet.");
            before = model.Decisions.Count; history = model.m_DataList.Count;
            Complete(chat);
            Require(model.Decisions.Count == before, "A likely-to-expire view is refreshed before paying for its first audit.");
            yield return Wait(() => model.Decisions.Count > before, "Review with renewed validity window");
            var renewed = Bound(chat);
            Require(renewed.CaptureId != original.CaptureId && (int)JObject.Parse(Review(model).Input)["reviewAttempt"] == 0,
                "Preparation refresh obtains real new pixels without spending a factual retry.");
            Review(model).Complete(true, Verdict(), "controlled-review-window");
            Require(model.Decisions.Count == before + 1 && model.m_DataList.Count == history + 1,
                "A renewed view permits exactly one independently reviewed publication.");
            Add("near-expiry-refresh-before-first-review", original, renewed);

            yield return Prepare(chat, model);
            original = Bound(chat);
            yield return Expire(chat, original);
            SceneVisionObservation ready = null;
            yield return Capture(chat, "regression-existing-current-frame", x => ready=x);
            Require(Bound(chat).CaptureId == original.CaptureId && !Fresh(chat, original) && Fresh(chat, ready),
                "The formal image really expired while an independent actual current capture is already available.");
            before = model.Decisions.Count; history = model.m_DataList.Count;
            Complete(chat);
            Require(model.Decisions.Count == before + 1 && Review(model).Image == ready.ImageDataUrl && Bound(chat).CaptureId == ready.CaptureId,
                "An existing compatible newer frame replaces expired formal pixels before the first audit.");
            yield return null;
            Require(chat.SceneVisionCapture.LatestObservation.CaptureId == ready.CaptureId && model.Decisions.Count == before + 1,
                "Reusing the existing frame does not enqueue a redundant GPU capture or HTTP review.");
            Review(model).Complete(true, Verdict(), "controlled-existing-current-source");
            Require(model.m_DataList.Count == history + 1, "Reused current pixels permit exactly one audited publication.");
            Add("expired-formal-reuses-existing-current-image", original, ready);

            yield return Prepare(chat, model);
            original = Bound(chat); yield return Expire(chat, original);
            before = model.Decisions.Count; history = model.m_DataList.Count;
            Complete(chat);
            Require(model.Decisions.Count == before && Guard(chat) == Generation(chat),
                "Known expired pixels never receive a first review; capture preparation retains the formal owner.");
            yield return Wait(() => model.Decisions.Count > before, "First prepared review");
            ready = Bound(chat);
            Require(Fresh(chat, ready) && ready.CaptureId != original.CaptureId && Review(model).Image == ready.ImageDataUrl &&
                (int)JObject.Parse(Review(model).Input)["reviewAttempt"] == 0,
                "The first review receives a genuinely new compatible image, still at attempt zero.");
            Review(model).Complete(true, Verdict(), "controlled-captured-before-first-audit");
            Require(model.Decisions.Count == before + 1 && model.m_DataList.Count == history + 1,
                "Initial image preparation does not spend a wasteful audit attempt.");
            Add("expired-formal-captures-before-first-review", original, ready);

            yield return Prepare(chat, model);
            original = Bound(chat); Complete(chat);
            var oldReview = Review(model); before = model.Decisions.Count; history = model.m_DataList.Count;
            yield return Expire(chat, original);
            yield return Capture(chat, "regression-changed-during-review", x => ready=x);
            oldReview.Complete(true, Verdict(), "controlled-stale-verdict");
            Require(model.Decisions.Count == before + 1 && model.m_DataList.Count == history && Review(model).Image == ready.ImageDataUrl,
                "A view expiring during review is still fenced and re-audited with the already available new frame.");
            var second = Review(model);
            Require((int)JObject.Parse(second.Input)["reviewAttempt"] == 1, "The only factual retry is attempt one.");
            oldReview.Complete(true, Verdict(), "duplicate-old-verdict-during-new-review");
            Require(model.Decisions.Count == before + 1 && model.m_DataList.Count == history, "An old duplicate cannot consume the replacement review.");
            yield return Expire(chat, ready);
            second.Complete(true, Verdict(), "controlled-second-expired-view");
            Require(model.Decisions.Count == before + 1 && model.m_DataList.Count == history + 1 &&
                model.m_DataList.Last().content != Candidate && Guard(chat) == -1,
                "A second view change falls back and releases ownership without a third audit or unverified visual claim.");
            Add("post-review-recheck-remains-bounded", original, ready);

            // A generic latest cache alone cannot authorize another round's image.
            yield return Prepare(chat, model);
            original = Bound(chat); Call(chat, "BindFormalSceneObservation", null, Generation(chat));
            before = model.Decisions.Count; Complete(chat);
            Require(model.Decisions.Count == before, "A fresh but unbound cache frame is not borrowed across formal rounds.");
            yield return Wait(() => model.Decisions.Count > before, "Round-owned prepared image");
            ready = Bound(chat);
            Require(ready.CaptureId != original.CaptureId && Review(model).Image == ready.ImageDataUrl,
                "Missing formal provenance requires a new identified capture before audit.");
            Review(model).Complete(true, Verdict(), "controlled-round-owned-source");
            Add("cache-without-current-round-provenance-is-not-reused", original, ready);

            yield return Prepare(chat, model); original=Bound(chat); yield return Expire(chat, original);
            before=model.Decisions.Count; history=model.m_DataList.Count; Complete(chat);
            Require(model.Decisions.Count==before && Guard(chat)==Generation(chat), "Unavailable-readback case begins before any stale image audit.");
            chat.SceneVisionCapture.CancelPending("controlled-readback-unavailable");
            Require(model.Decisions.Count==before && model.m_DataList.Count==history+1 &&
                model.m_DataList.Last().content!=Candidate && Guard(chat)==-1,
                "An unavailable first refresh uses a safe fallback without auditing old pixels or recursively capturing.");
            for(int i=0;i<3;i++) yield return null;
            Require(model.Decisions.Count==before, "An unavailable preparation never schedules another audit behind its fallback.");
            Add("unavailable-first-capture-falls-back-without-stale-audit",original,null);

            foreach (string boundary in new[] { "new-user", "pause", "eyes-closed", "disable" })
            {
                yield return Prepare(chat, model); original=Bound(chat); yield return Expire(chat, original);
                before=model.Decisions.Count; history=model.m_DataList.Count; generation=Generation(chat);
                Complete(chat);
                Require(model.Decisions.Count == before && Guard(chat) == generation, boundary + ": a real initial readback is pending.");
                if (boundary == "new-user") Call(chat, "InvalidateFormalResponse", "new-user-during-speech-preparation");
                else if (boundary == "pause") Call(chat, "OnApplicationPause", true);
                else if (boundary == "eyes-closed") Call(chat, "ResetSceneVision", "eyes-closed-during-speech-preparation", true);
                else chat.enabled=false;
                Require(Guard(chat) == -1 && (int)Get(chat,"m_PendingSceneFormalCaptureGeneration") == -1 &&
                    !(bool)Get(chat,"m_FormalResponseInFlight"), boundary + ": the exact review owner is released immediately.");
                for (int i=0;i<5;i++) yield return null;
                Require(model.Decisions.Count == before && model.m_DataList.Count == history,
                    boundary + ": a late GPU completion cannot dispatch an audit or publish history.");
                if (boundary == "pause") { Call(chat,"OnApplicationPause",false); Call(chat,"ClearScheduledAgentWake"); }
                chat.enabled=true;
                Add("initial-capture-cancelled-by-" + boundary, original, null);
            }

            // The controlled transport deliberately retains cancelled callbacks.
            yield return Prepare(chat, model); Complete(chat); oldReview=Review(model);
            Call(chat,"InvalidateFormalResponse","replace-pending-audit");
            yield return Prepare(chat, model); Complete(chat); second=Review(model);
            generation=Generation(chat); before=model.Decisions.Count; history=model.m_DataList.Count;
            oldReview.Complete(true,Verdict(),"late-replaced-audit");
            Require(Guard(chat)==generation && Generation(chat)==generation && model.Decisions.Count==before && model.m_DataList.Count==history,
                "A previous owner's callback cannot publish or release a newer formal review.");
            second.Complete(true,Verdict(),"controlled-current-owner");
            Require(model.m_DataList.Count==history+1 && Guard(chat)==-1,
                "The replacement review remains independently publishable once.");
            Add("late-audit-cannot-release-new-owner",null,Bound(chat));

            yield return Prepare(chat, model); Complete(chat); oldReview=Review(model); history=model.m_DataList.Count;
            var settings=(ChatSetting)Get(chat,"m_ChatSettings");
            // No replacement provider is run. A binding change alone must revoke this callback.
            var savedModel=settings.m_ChatModel;
            try
            {
                settings.m_ChatModel=null;
                oldReview.Complete(true,Verdict(),"late-rebound-model");
                yield return null;
                Require(model.m_DataList.Count==history && Guard(chat)==-1 && !(bool)Get(chat,"m_FormalResponseInFlight"),
                    "An exact model binding mismatch revokes the pending review instead of appending to its old history.");
            }
            finally { settings.m_ChatModel=savedModel; }
            Add("model-rebinding-invalidates-owner",null,null);

            yield return Prepare(chat,model);
            history=model.m_DataList.Count; before=model.Decisions.Count; generation=Generation(chat);
            var savedAvatar=bridge.avatar;
            try
            {
                bridge.avatar=null;
                // Both early exits used to precede the watchdog. An invalid
                // candidate must not leave an owner when its binding is gone.
                Call(chat,"CompleteRoomSpeech",new SpeechTextBuffer(),"",generation,true,null);
                Require(Guard(chat)==-1 && (int)Get(chat,"m_PendingSceneFormalCaptureGeneration")==-1 &&
                    !(bool)Get(chat,"m_FormalResponseInFlight"),
                    "An unavailable avatar and invalid public speech release the newly registered owner synchronously.");
                Require(model.Decisions.Count==before && model.m_DataList.Count==history && Generation(chat)>generation,
                    "The invalid initial binding neither audits nor publishes a candidate, and revokes its formal generation.");
            }
            finally { bridge.avatar=savedAvatar; }
            Add("invalid-speech-and-missing-initial-avatar-do-not-leak-owner",null,null);

            yield return Prepare(chat,model);
            history=model.m_DataList.Count; before=model.Decisions.Count; generation=Generation(chat);
            savedAvatar=bridge.avatar;
            try
            {
                Set(chat,"m_AgentCurrentRoundIsTick",true); Set(chat,"m_RoomTaskIntent","look-at");
                bridge.avatar=null;
                Call(chat,"CompleteRoomSpeech",new SpeechTextBuffer(),"<silent/>",generation,true,null);
                Require(Guard(chat)==-1 && (int)Get(chat,"m_PendingSceneFormalCaptureGeneration")==-1 &&
                    !(bool)Get(chat,"m_FormalResponseInFlight") && model.Decisions.Count==before && model.m_DataList.Count==history,
                    "The autonomous silent early exit also releases an unavailable initial avatar owner without publication.");
            }
            finally { bridge.avatar=savedAvatar; Set(chat,"m_AgentCurrentRoundIsTick",false); }
            Add("silent-reply-and-missing-initial-avatar-do-not-leak-owner",null,null);
            Require(Vector3.Distance(root,bridge.avatar.transform.position)<.0001f && bridge.Controller.OperationRevision==operation && !bridge.ReservesBody,
                "All review cases preserve the real room controller operation and actor root.");
            LastReport["status"]="passed";
        }
        finally
        {
            chat.enabled=true; Call(chat,"OnApplicationPause",false);
            Call(chat,"InvalidateFormalResponse","speech-preparation-regression-complete"); chat.StopAllCoroutines(); Call(chat,"ClearScheduledAgentWake");
            Set(chat,"m_SceneObservationMaxAgeSeconds",oldAge);
            LastReport["checks"]=checks;
            File.WriteAllText(Path.Combine(output,"room-speech-view-preparation.json"),LastReport.ToString()+"\n");
        }
    }

    private static IEnumerator Prepare(ChatSample chat,NevaSceneVisionIntegrationModel model)
    {
        Call(chat,"InvalidateFormalResponse","speech-preparation-fixture-next"); chat.StopAllCoroutines(); Call(chat,"ClearScheduledAgentWake");
        foreach (string field in new[] { "m_AgentRunning","m_AgentEyesOpen","m_EnableScreenVision","m_StreamComplete" }) Set(chat,field,true);
        foreach (string field in new[] { "m_AgentCurrentRoundIsTick","m_AgentRoundInFlight","m_FormalResponseInFlight","m_UserSpeechActiveForAutonomy",
            "m_AgentGracefulShutdownPending","m_WorkReviewOpen","m_RoomTaskFormatError" }) Set(chat,field,false);
        Set(chat,"m_PendingTickCo",null); Set(chat,"m_SceneObservationMaxAgeSeconds",12f);
        foreach (string field in new[] { "m_AudioSource","m_HumStreamSecondaryAudioSource" })
        { var source=(AudioSource)Get(chat,field); if(source!=null) { source.Stop(); source.clip=null; } }
        const string request="请看看当前情况。";
        Call(chat,"RecordAcceptedMotionUserTurn",request);
        SceneVisionObservation image=null; yield return Capture(chat,"speech-preparation-fixture",x=>image=x);
        Call(chat,"StartStreaming",request,false,null,false,false,false,null,false,image,true);
        model.Decisions.Last().Complete(true,new JObject { ["intent"]="inspect",["origin"]="user",["evidence"]=request }.ToString(),"controlled-speech-preparation-fixture");
        Require(Bound(chat)?.CaptureId==image.CaptureId && model.Speech.Last().Image==image.ImageDataUrl,
            "The production formal dispatch starts from the actual captured character image.");
    }
    private static IEnumerator Expire(ChatSample chat,SceneVisionObservation image)
    {
        Set(chat,"m_SceneObservationMaxAgeSeconds",2f);
        yield return Wait(()=>!Fresh(chat,image),"Actual image age expiration",4f);
        // Real captures retain a two-second validity window. Only the age limit,
        // never the image pixels, capture timestamp, or camera pose, is controlled.
    }
    private static IEnumerator Capture(ChatSample chat,string purpose,Action<SceneVisionObservation> callback)
    {
        SceneVisionObservation image=null; Call(chat,"RequestSceneObservation",purpose,new Action<SceneVisionObservation>(x=>image=x));
        yield return Wait(()=>image!=null,"Actual review preparation RGB");
        Require(Fresh(chat,image),"The renderer returns genuinely current character pixels."); callback(image);
    }
    private static void Complete(ChatSample chat)
    { var buffer=new SpeechTextBuffer(); buffer.Append(new SpeechText(Candidate,"ja",actionDependency:SpeechActionDependency.Independent)); Call(chat,"CompleteRoomSpeech",buffer,Candidate,Generation(chat),true,null); }
    private static NevaSceneVisionIntegrationModel.Request Review(NevaSceneVisionIntegrationModel model)=>model.Decisions.Last(x=>x.Kind=="room-review");
    private static SceneVisionObservation Bound(ChatSample chat)=>(SceneVisionObservation)Call(chat,"GetFormalSceneObservation",Generation(chat));
    private static bool Fresh(ChatSample chat,SceneVisionObservation image)=>(bool)Call(chat,"IsFreshSceneObservation",image);
    private static int Generation(ChatSample chat)=>(int)Get(chat,"m_FormalResponseGeneration");
    private static int Guard(ChatSample chat)=>(int)Get(chat,"m_RoomSpeechGuardGeneration");
    private static string Verdict()=>new JObject { ["verdict"]="consistent",["reason"]="受控当前视野事实审核",["factScope"]="visual-observation" }.ToString();
    private static void Add(string name,SceneVisionObservation before,SceneVisionObservation after)=>((JArray)LastReport["cases"]).Add(new JObject { ["case"]=name,["beforeCaptureId"]=before?.CaptureId,["reviewCaptureId"]=after?.CaptureId });
    private static IEnumerator Wait(Func<bool> ready,string label,float seconds=8f)
    { float end=Time.realtimeSinceStartup+seconds; while(!ready()) { if(Time.realtimeSinceStartup>end) throw new TimeoutException(label); yield return null; } }
    private static object Call(object target,string name,params object[] args)=>NevaSceneVisionIntegrationRegression.Call(target,name,args);
    private static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
    private static void Require(bool value,string reason) { checks++; if(!value) throw new InvalidOperationException(reason); }
}
