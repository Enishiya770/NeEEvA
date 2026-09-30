using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public static class ActorSemanticReviewRegression
{
    static int checks;
    static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
    public static void Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--classify")
        {
            string raw=File.ReadAllText(args[1]),draft=File.ReadAllText(args[2]);
            bool review=args.Length>3 && args[3]=="review";
            string currentUser=args.Length>4?File.ReadAllText(args[4]):"";
            ActorIntentDecision d=null;string reason;bool ok;
            string status=null;
            if(review){ok=ActorIntentProtocol.TryParseReview(raw,draft,out var rd,out reason);status=rd?.Status;if(ok)ok=ActorIntentProtocol.CompileReview(rd,draft,out d,out reason);}
            else ok=ActorIntentProtocol.TryCompile(raw,draft,out d,out reason);
            bool valid=ok && ActorIntentProtocol.Validate(d,draft,out _);
            Console.WriteLine(new JObject{["parsed"]=ok,["valid"]=valid,["reviewRequired"]=!valid || d?.Kind=="claim" || d?.Kind=="none" && ActorIntentProtocol.NeedsNonActionReview(draft) || ActorIntentProtocol.NeedsKnownTaskConditionReview(d,draft,currentUser),["status"]=status,["reason"]=reason,["decision"]=d==null?null:new JObject{["Domain"]=d.Domain,["Kind"]=d.Kind,["Task"]=d.Task,["Target"]=d.Target,["Action"]=d.Action,["Condition"]=d.Condition,["Coverage"]=d.Coverage,["Goal"]=d.Goal,["Evidence"]=d.Evidence,["RequiresFullReview"]=d.RequiresFullReview}}.ToString(Formatting.None));return;
        }
        string greeting="听到了哦，涅瓦在这里呢。";
        var conflict=new JObject{["version"]=4,["domain"]="perception",["kind"]="act",["action"]="none",["task"]="none",["target"]="none",["condition"]="none",["evidence"]=greeting,["coverage"]="atomic",["goal"]="告诉用户能听见"};
        Check(ActorIntentProtocol.TryCompile(conflict.ToString(),greeting,out var marker,out _),"correlation failure is available for semantic review");
        Check(marker.RequiresFullReview && !ActorIntentProtocol.Validate(marker,greeting,out _),"review marker cannot pass execution validation");
        var runtime=new FastLoopRuntime();
        Check(!runtime.TryApplyActor(marker,new ActorDraft{Id="greeting",Revision=1,Text=greeting},null,0,out _,out _) && runtime.HeadTask==null,"review marker cannot mutate head runtime");
        foreach(string kind in new[]{"none","claim","replan","wait","cancel"})
        {conflict["kind"]=kind;Check(!ActorIntentProtocol.TryCompile(conflict.ToString(),greeting,out marker,out _) || !ActorIntentProtocol.Validate(marker,greeting,out _),"inconsistent enum never grants an action: "+kind);}
        conflict["kind"]="none";conflict["goal"]="";conflict["evidence"]="";
        Check(ActorIntentProtocol.TryCompile(conflict.ToString(),greeting,out marker,out _) && marker.RequiresFullReview,"none with wrong coverage has a non-executable repair path");
        conflict["coverage"]="none";
        Check(ActorIntentProtocol.TryCompile(conflict.ToString(),greeting,out var none,out _) && !none.RequiresFullReview && ActorIntentProtocol.Validate(none,greeting,out _),"valid ordinary listening stays direct and needs no review");
        foreach(string status in new[]{"non_action","adopted_action","reported_action","uncertain"})
        {
            bool action=status=="adopted_action"||status=="reported_action";
            var wire=new JObject{["version"]=2,["status"]=status,["goal"]=action?"关闭灯具":"",["coverage"]=action?"atomic":"none",
                ["task"]=status=="adopted_action"?"other":"none",["target"]="none",["trigger"]="none",["evidence"]="消すね。"};
            Check(ActorIntentProtocol.TryParseReview(wire.ToString(),"消すね。",out var r,out _),"strict review "+status);
            bool compiled=ActorIntentProtocol.CompileReview(r,"消すね。",out var d,out _);
            Check(status=="uncertain"?!compiled:compiled,"uncertain is not silently none");
            if(compiled)Check(status=="non_action"?d.Kind=="none":status=="adopted_action"?d.Kind=="replan"&&d.Task=="other"&&!d.TargetIsProgramBound:d.Kind=="claim"&&d.Task=="none","review grants exactly its semantic scope");
            if(action){wire["evidence"]="消す";Check(!ActorIntentProtocol.TryParseReview(wire.ToString(),"消すね。",out _,out _),"partial draft cannot discard later commitments");}
        }
        foreach(string raw in new[]{"{\"version\":1,\"status\":\"non_action\",\"status\":\"adopted_action\",\"goal\":\"\",\"evidence\":\"\"}","{\"version\":1,\"status\":\"non_action\",\"goal\":null,\"evidence\":\"\"}","{\"version\":1,\"status\":\"non_action\",\"goal\":\"\",\"evidence\":\"\",\"action\":\"head.raise\"}"})
            Check(!ActorIntentProtocol.TryParseReview(raw,greeting,out _,out _),"closed review rejects duplicate/null/extra");
        foreach(string draft in new[]{"嗯，好的。我会一直跟着你的，请多指教了。","嗯，没关系，我们慢慢来。我会再仔细确认一下旋律的边界，这次一定只唱你唱的那部分。","我只提取旋律部分，调整目标。","I'll keep following you.","私はずっとついていくつもりです。"})
            Check(ActorIntentProtocol.NeedsNonActionReview(draft),"observed/future wording admits exactly a semantic check: "+draft);
        foreach(string draft in new[]{greeting,"这幅画很漂亮。","你好，很高兴见到你。","能看见你。","こんばんは。","That is a nice room."})
            Check(!ActorIntentProtocol.NeedsNonActionReview(draft),"ordinary none does not acquire another model request");
        foreach(string task in new[]{"follow","approach","recording_singing"})
        {
            var r=new ActorIntentReviewDecision{Status="adopted_action",Goal="保留完整目标",Evidence="我会按约定继续。",Task=task,Target=task=="recording_singing"?"none":"current_user",Coverage="atomic"};
            Check(ActorIntentProtocol.CompileReview(r,r.Evidence,out var d,out _) && d.Task==task && !d.TargetIsProgramBound,"one bounded review retains typed proposal without binding an actual target");
            r.Coverage="compound";
            if(task=="follow" || task=="approach")
            {
                r.Goal="先靠近并持续跟随用户，随后查看指定物体并反馈结果";
                Check(ActorIntentProtocol.CompileReview(r,r.Evidence,out var compound,out _) &&
                    compound.Task=="other" && compound.Kind=="replan" && compound.Coverage=="compound" &&
                    compound.Goal==r.Goal && compound.Evidence==r.Evidence && !compound.TargetIsProgramBound &&
                    ActorIntentProtocol.Validate(compound,r.Evidence,out _),
                    "adopted compound user task retains every goal and evidence through full planner, without fast-path target binding");
            }
            else Check(!ActorIntentProtocol.CompileReview(r,r.Evidence,out _,out _),"compound recording task still cannot silently narrow to atomic without a supported current-fact contract");
        }
        var deferred=new ActorIntentReviewDecision{Status="adopted_action",Goal="等门打开后跟随用户",Evidence="我会等门打开后再跟着你。",Task="follow",Target="current_user",Coverage="atomic",Trigger="deferred"};
        Check(!ActorIntentProtocol.CompileReview(deferred,deferred.Evidence,out _,out var deferredReason) && deferredReason=="actor_review_deferred_trigger_unavailable", "future trigger cannot be compiled into immediate follow even if other fields say atomic");
        var currentFact=new ActorIntentReviewDecision{Status="adopted_action",Goal="现在检查首段录音，若与用户内容对应则按完整顺序回唱三段，否则澄清",Evidence="我先确认那段还在不在，有的话把三段接起来唱。",Task="recording_singing",Target="none",Coverage="compound",Trigger="current_fact"};
        Check(ActorIntentProtocol.CompileReview(currentFact,currentFact.Evidence,out var factPlan,out _) && factPlan.CurrentFactPrerequisite && factPlan.Task=="recording_singing" && factPlan.Coverage=="compound" && factPlan.Goal==currentFact.Goal && factPlan.Evidence==currentFact.Evidence,"current recording prerequisite preserves compound coverage and complete conditional goal in recording planner, not execution approval");
        currentFact.Task="follow";
        Check(!ActorIntentProtocol.CompileReview(currentFact,currentFact.Evidence,out _,out var currentFactReason) && currentFactReason=="actor_review_current_fact_requires_recording_planner","current_fact cannot grant unimplemented spatial condition scope");
        currentFact.Task="recording_singing";currentFact.Trigger="deferred";
        Check(!ActorIntentProtocol.CompileReview(currentFact,currentFact.Evidence,out _,out _),"future recording trigger is never converted into present execution");
        var reviewV3=new JObject{["version"]=ActorIntentProtocol.ReviewVersion,["status"]="adopted_action",["goal"]="先核对现有录音再决定回唱",["coverage"]="atomic",["task"]="recording_singing",["target"]="none",["trigger"]="current_fact",["evidence"]=currentFact.Evidence};
        Check(ActorIntentProtocol.TryParseReview(reviewV3.ToString(),currentFact.Evidence,out var v3,out _) && ActorIntentProtocol.CompileReview(v3,currentFact.Evidence,out _,out _),"v3 wire/parser preserves present fact branch");
        reviewV3["version"]=2;
        Check(!ActorIntentProtocol.TryParseReview(reviewV3.ToString(),currentFact.Evidence,out _,out _),"v2 wire cannot claim the new present-fact capability");
        var unrelatedTail=new ActorIntentReviewDecision{Status="adopted_action",Goal="确认录音后唱歌，然后转身",Evidence="有录音就唱完再转身。",Task="other",Target="other",Coverage="compound",Trigger="current_fact"};
        Check(!ActorIntentProtocol.CompileReview(unrelatedTail,unrelatedTail.Evidence,out _,out _),"present-fact singing cannot discard an independent spatial tail");
        var futureRecording=new ActorIntentReviewDecision{Status="adopted_action",Goal="确认录音还在，等用户明天说开始后回唱",Evidence="我先确认，等你明天说开始后再唱。",Task="recording_singing",Target="none",Coverage="compound",Trigger="deferred"};
        Check(!ActorIntentProtocol.CompileReview(futureRecording,futureRecording.Evidence,out _,out _),"a current check plus a future signal still cannot execute now");
        var known=new ActorIntentDecision{Domain="other_action",Kind="replan",Task="follow",Target="current_user",Coverage="atomic",Goal="跟随用户",Evidence=deferred.Evidence,Action="none",Condition="none"};
        Check(ActorIntentProtocol.NeedsKnownTaskConditionReview(known,deferred.Evidence,"跟着我。"),"real wrongly atomic conditional follow requests bounded review before short-path binding");
        Check(!ActorIntentProtocol.NeedsKnownTaskConditionReview(known,"我会一直跟着你。","现在跟着我。"),"unconditional follow retains one classifier request");
        var disagreement=new ActorIntentReviewDecision{Status="non_action",Goal="",Evidence="我会一直跟着你。"};
        Check(ActorIntentProtocol.ReviewContradictsTypedCommitment(known,disagreement,disagreement.Evidence),"typed action versus non-action disagreement cannot publish the old promise");
        Check(!ActorIntentProtocol.ReviewContradictsTypedCommitment(none,disagreement,disagreement.Evidence),"non-action reviewer does not acquire action authority or automatically invalidate consistent none");
        const string gazePromise="好，这就把视线移过去。";
        var invalidGaze=new ActorIntentDecision{Domain="head",Kind="act",Task="other",Target="other",Action="head.left",Evidence=gazePromise,RequiresFullReview=true};
        var droppedGaze=new ActorIntentReviewDecision{Status="non_action",Evidence=gazePromise};
        Check(ActorIntentProtocol.ReviewContradictsTypedCommitment(invalidGaze,droppedGaze,gazePromise),
            "Observed head/task conflict followed by non_action cannot release an unexecuted gaze promise");
        Check(!ActorIntentProtocol.ReviewContradictsTypedCommitment(invalidGaze,new ActorIntentReviewDecision{Status="adopted_action",Evidence=gazePromise},gazePromise),
            "Independent repaired action still reaches planning, never promoted by disagreement guard");
        invalidGaze.Evidence="这就把视线移过去";
        Check(ActorIntentProtocol.ReviewContradictsTypedCommitment(invalidGaze,droppedGaze,"嗯，明白了。"+gazePromise),
            "Disagreement cannot disappear because evidence is a substring or an acknowledgement omitted first person");
        foreach(string draft in new[]{"我会跟着你只是一个例句。","我会唱歌，但现在不唱。","假如我会瞬移，就不用走了。","I will not follow you."})
        {
            Check(ActorIntentProtocol.NeedsNonActionReview(draft),"hint conservatively requests review of negation/quotation/ability");
            var r=new ActorIntentReviewDecision{Status="non_action",Goal="",Evidence=draft,Task="none",Target="none",Coverage="none"};
            Check(ActorIntentProtocol.CompileReview(r,draft,out var d,out _) && d.Kind=="none","review hint cannot override a semantic refusal or authorize action");
        }
        if(args.Length==2 && args[0]=="--export")File.WriteAllText(args[1],new JObject{["system"]=ActorIntentProtocol.SystemPrompt,["response_format"]=ActorIntentProtocol.ResponseFormat(),["reviewSystem"]=ActorIntentProtocol.ReviewSystemPrompt,["review_response_format"]=ActorIntentProtocol.ReviewResponseFormat(),["context"]=new JObject{["waitingTask"]=null,["currentPosture"]=null,["inputSequence"]=1}}.ToString(Formatting.Indented));
        Console.WriteLine("Actor semantic permission boundary: "+checks+" checks passed.");
    }
}
