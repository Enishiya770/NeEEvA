using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class AgentSingingGoalRegression
{
    public static void ExportRecordingReviewCases(string output)
    {
        var rows=new JArray();
        foreach (bool ordinaryFollowup in new[] { false,true })
        foreach (bool wrongOld in new[] { false,true })
        using(var f=new Fixture())
        {
            f.Begin("第一段唱完了，你能把这一段回唱给我听吗？");
            f.Propose(f.first,"current"); f.Approve();
            if(!f.Resolve(f.first,"current",out string preparationError)) throw new Exception(preparationError);
            f.Completed(f.first);
            string user="非常棒，这一次唱出来了。那我再唱"+(ordinaryFollowup?"第二段":"一句")+"：初めてあなたを見たあの日。止められない喪失の予感。你能把这一段回唱给我听吗？";
            f.Begin(user); f.StageCompletedEvidence(user,"初めてあなたを見たあの日。止められない喪失の予感。",.4f,2.4f,1.9f,2.3f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
            var context=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            string current=(string)context.CurrentInput["clip"];
            string requested=current;
            string quote="你能把这一段回唱给我听吗？";
            if(ordinaryFollowup)
            {
                quote="那能把第二段唱出来吗？";
                f.Begin(quote); f.StageCompletedEvidence(quote,"",.1f,2.4f,1.9f,2.3f);
                Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
                context=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
                current=(string)context.CurrentInput["clip"];
            }
            var selected=context.Materials.Single(m=>m.ClipRef==(wrongOld?f.first:requested));
            var raw=new JObject { ["decision"]="perform",["materials"]=new JArray(selected.Key),["range"]="clean",["intent"]="repeat",
                ["expected"]=wrongOld?"再唱已播放的第一段public phrase alpha":"回唱新第二段初めてあなたを見たあの日、止められない喪失の予感",
                ["user_evidence"]=quote,["source_evidence"]=quote,
                ["thought"]="",["speech"]=new JObject{["text"]="",["language"]="zh"} };
            if(!CompileRecordingFixture(raw, context,out var compiled,out var error)) throw new Exception(error);
            Call(f.chat,"ExtractSingingGoalTags",new object[]{compiled,true});
            string frame=(string)Call(f.chat,"BuildPerceptionFrame","recording-source-regression");
            string prompt=(string)Call(f.chat,"BuildDedicatedWorkReviewPrompt",frame);
            var provider=f.host.AddComponent<ChatQW>(); provider.m_Backend=ChatQW.BackendType.OpenAICompatible;
            string request=(string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson",Flags).Invoke(provider,new object[]{prompt});
            rows.Add(new JObject{["id"]=(ordinaryFollowup?"ordinary-followup-":"")+(wrongOld?"wrong-old-source":"correct-current-source"),["expectedAllowed"]=!wrongOld,
                ["currentClip"]=current,["requestedClip"]=requested,["selectedClip"]=selected.ClipRef,["wire"]=JObject.Parse(request),
                ["scope"]="Actual BuildPerceptionFrame + BuildDedicatedWorkReviewPrompt + BuildWorkReviewRequestJson on retained synthetic PCM; no scene image, network or playback during export."});
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,rows.ToString());
    }
    // Actual retained PCM and production parser/reviewer comparator, with an
    // injected review verdict. No HTTP, model, conversion, playback or services.
    public static int RunRecordingInputChecks()
    {
        int before = checks;
        using (var f = new Fixture())
        {
            const string request = "我再唱一句，初めてあなたを見たあの日。你能把这一段回唱给我听吗？";
            f.Begin(request); f.StageCompletedEvidence(request, "初めてあなたを見たあの日", .4f, 2.4f, 1.9f, 2.3f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
            var c = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
            string clip = (string)c.CurrentInput["clip"];
            var current = c.Materials.Single(m => m.ClipRef == clip);
            Check((int)c.CurrentInput["capture"] == f.sense.InputCaptureSessionSerial &&
                current.Revision == "pending" && !current.RequiresExpansion,
                "Recording IR did not bind the exact accepted current capture, or promoted its authority.");
            JObject Plan(string key, string relation) => new JObject {
                ["decision"] = "perform", ["materials"] = new JArray(key), ["range"] = "clean", ["intent"] = "perform",
                ["expected"] = "回唱本次第二段", ["user_evidence"] = "你能把这一段回唱给我听吗？",
                ["source_evidence"] = "你能把这一段回唱给我听吗？",
                ["thought"] = "", ["speech"] = new JObject { ["text"] = "", ["language"] = "zh" } };
            var old = c.Materials.Single(m => m.ClipRef == f.first);
            Check(CompileRecordingFixture(Plan(old.Key,"current_recording"), c,out string oldPlan,out _) &&
                oldPlan.Contains("source_relation=\"retained_recording\""),
                "The identical short request could relabel old retained PCM as current capture.");
            Check(CompileRecordingFixture(Plan(current.Key,"current_recording"), c,out string compiled,out _),
                "Current source-aware recording proposal did not compile.");
            Call(f.chat,"ExtractSingingGoalTags",new object[] { compiled,true });
            Check((bool)Call(f.chat,"HasPendingSingingGoalReview") && !(bool)Get(f.chat,"m_SingingRequestSubmittedSinceUserTurn"),
                "Input binding either lost the goal or implicitly submitted audio.");
            var expected = new JObject { ["origin"] = "user_request", ["request_quote"] = "你能把这一段回唱给我听吗？",
                ["refs"] = clip,["range"] = "clean",["source_relation"] = "retained_recording" };
            object[] args={expected,""};
            Check(!(bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",args),
                "Independent review may not approve the wrong current-versus-retained relationship.");
            expected.Remove("source_relation");args=new object[]{expected,""};
            Check(!(bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",args),
                "New IR proposal accepted an old reviewer response without source relation.");
            expected["source_relation"]="current_recording";args=new object[]{expected,""};
            Check((bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",args),
                "Current source relation could not pass the existing independent target/version check.");
            // Merely retaining a candidate is not accepting a new user request.
            f.StageCompletedEvidence(request,"初めてあなたを見たあの日",.4f,2.4f,1.9f,2.3f);
            Call(f.chat,"RetainCompletedRecordingEvidence",f.sense);
            args=new object[]{expected,""};
            Check((bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",args),
                "An unaccepted candidate invalidated the accepted source approval.");
            object oldGoal = Get(f.chat,"m_SingingGoal");
            f.Begin(request);
            Call(f.chat,"BindAcceptedRecordingInput",true);
            args=new object[]{oldGoal,""};
            Check(!(bool)Call(f.chat,"ValidateRecordingGoalInput",args),
                "A genuinely accepted same-text new recording reused the previous user identity.");
            var newer=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            Check((string)newer.CurrentInput["clip"]!=clip && (int)newer.CurrentInput["capture"]!=(int)c.CurrentInput["capture"],
                "Fresh source snapshot silently reused an older capture identity.");
            Call(f.chat,"SetCurrentUserInput",request,request);
            var typed=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            Check((int)typed.CurrentInput["capture"]==0 && typed.CurrentInput["clip"].Type==JTokenType.Null,
                "Typed identical text inherited the preceding microphone recording.");
        }
        using(var f=new Fixture())
        {
            f.Begin("不要唱新录音，还是再唱第一段。");
            f.Propose(f.first,"current");
            var expected=new JObject { ["origin"]="user_request",["request_quote"]=f.user,["refs"]=f.first,["range"]="current" };
            object[] args={expected,""};
            Check((bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",args),
                "Legacy non-IR goal lost its independent-review compatibility.");
        }
        using(var f=new Fixture())
        {
            const string oldRequest="之前请唱第一段。";
            f.Begin(oldRequest);
            f.Begin("我重新唱了第二段，现在请把这段回唱给我听。");
            var c=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            var selected=c.Materials.Single(m=>m.ClipRef==f.first);
            var proposal=new JObject { ["decision"]="perform",["materials"]=new JArray(selected.Key),["range"]="current",["intent"]="repeat",
                ["expected"]="旧曲重唱提案",["user_evidence"]=oldRequest,["source_evidence"]="现在请把这段回唱给我听。",
                ["thought"]="",["speech"]=new JObject { ["text"]="",["language"]="zh" } };
            Check(CompileRecordingFixture(proposal, c,out var compiled,out _),"Repeat quote normalization failed.");
            Call(f.chat,"ExtractSingingGoalTags",new object[]{compiled,true});
            var expected=new JObject { ["origin"]="user_request",["request_quote"]=oldRequest,["refs"]=f.first,
                ["range"]="current",["source_relation"]="retained_recording" };
            object[] args={expected,""};
            Check(!(bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",args) && ((string)args[1]).Contains("历史授权"),
                "Old repeat authorization passed when refs, relation and range matched the mistaken proposal.");
            string review=(string)Call(f.chat,"BuildRecordingInputReviewContext");
            Check(review.Contains(f.user) && review.Contains("current_user_complete_only") && review.Contains("proposed_recording_facts"),
                "Independent source comparison omitted complete current input or the retained-source proposal facts.");
        }
        using(var f=new Fixture())
        {
            f.Begin("我再唱一句：初めてあなたを見たあの日。你能把这一段回唱给我听吗？");
            f.StageCompletedEvidence(f.user,"初めてあなたを見たあの日",.4f,2.4f,1.9f,2.3f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
            Call(f.chat,"BuildRecordingSingingDecisionContext");
            for(int i=0;i<24;i++)
            {
                f.Begin("这只是普通说明，不是新歌声，编号"+i);
                f.StageCompletedEvidence(f.user,"",.1f,2.4f,2.0f,2.3f);
                Call(f.chat,"RetainCompletedRecordingEvidence",f.sense);
            }
            f.Begin("不是第一段，我说的是刚才新唱的第二段。请把第二段回唱给我听，保留这句末尾请求。");
            f.StageCompletedEvidence(f.user,"",.1f,2.4f,2.0f,2.3f);
            var c=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            string prompt=RecordingSingingDecisionCompiler.Prompt(c);
            Check(c.Materials.Count==28 && c.Materials.All(m=>!m.RequiresExpansion),
                "Acoustic speech labels removed retained transcripts from the dedicated proposal table.");
            Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('背',69305),c,8000,out var bounded,out _) &&
                bounded.Contains(f.user) && bounded.Contains("初めてあなたを見たあの日") && bounded.Contains("CleanEndSeconds"),
                "Real retained-audio archive exceeded budget or lost current request/new verse/range evidence.");
            UnityEngine.Debug.Log("[RecordingInput/Budget] materials="+c.Materials.Count+" indexed="+c.Materials.Count(m=>m.RequiresExpansion)+" protectedCharacters="+prompt.Length);
        }
        return checks-before;
    }
}
