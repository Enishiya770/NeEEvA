using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class RecordingIntentRegression
{
    static int checks;
    static readonly Dictionary<string, RecordingSingingDecisionContext> CaseContexts = new Dictionary<string, RecordingSingingDecisionContext>();
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    public static RecordingSingingDecisionContext Context(string user, int capture = 19)
    {
        var c = new RecordingSingingDecisionContext { UserText = user,
            CurrentInput = new JObject { ["turn"] = 19, ["capture"] = capture, ["clip"] = capture > 0 ? "clip:session-" + capture : null },
            RequestEvidenceTexts = new[] { "初めてのルーブル。你能把这一段回唱给我听吗？" },
            Facts = new JObject { ["lastPlayback"] = new JObject { ["completed"] = true, ["materials"] = new JArray("clip:session-10") },
                ["activeGoal"] = new JObject { ["refs"] = "clip:session-10", ["outcome"] = "playback_complete_content_unverified" } }.ToString() };
        c.RequestEvidence = new JArray(new JObject { ["turn"] = 10, ["capture"] = 10, ["clip"] = "clip:session-10", ["text"] = c.RequestEvidenceTexts[0] });
        Add(c, 10, "初めてのルーブル。你能把这一段回唱给我听吗？", "初めてのルーブル", true);
        Add(c, 19, capture==19 ? user : "我再唱一句。初めてあなたを見たあの日。止められない喪失の予感。你能把这一段回唱给我听吗？", "初めてあなたを見たあの日。止められない喪失の予感。", false);
        return c;
    }
    public static void Add(RecordingSingingDecisionContext c, int cap, string text, string song, bool ready, bool index = false)
    {
        c.Materials.Add(new RecordingSingingMaterial { Key = "material_" + cap, ClipRef = "clip:session-" + cap,
            CaptureSerial = cap, Revision = ready ? "0" : "pending", PlaybackStatus = ready ? "ready" : "evidence_only",
            HasCleanRange = true, HasExpandedRange = true, RequiresExpansion = index,
            Description = new JObject { ["CaptureSessionSerial"] = cap, ["RecordingTranscript"] = text, ["SingingSegmentText"] = song,
                ["SourceStatus"] = ready ? "confirmed_user" : "pending", ["ContentVerdict"] = "undecided", ["AcousticMode"] = "speech",
                ["PitchTimelineAvailable"] = true, ["RawSeconds"] = 23.97, ["CleanStartSeconds"] = 8.83, ["CleanEndSeconds"] = 19.77,
                ["ExpandedStartSeconds"] = 8.4, ["ExpandedEndSeconds"] = 20.17,
                ["HeadExtraReviewRequired"] = true, ["TailExtraReviewRequired"] = true,
                ["currentPlayback"] = new JObject { ["selected"] = ready, ["Revision"] = ready ? "0" : "pending" },
                ["scope"] = "源和边界说明，不是播放授权" }.ToString() });
    }
    public static JObject Plan(string material, string relation, string quote, string intent = "perform") => new JObject {
        ["decision"] = "perform", ["materials"] = new JArray(material), ["range"] = "clean", ["intent"] = intent,
        ["expected"] = "回唱用户所指的录音", ["user_evidence"] = quote, ["source_evidence"] = quote,
        ["thought"] = "", ["speech"] = new JObject { ["text"] = "", ["language"] = "zh" } };
    public static string QuoteRef(RecordingSingingDecisionContext c,string quote,string origin="current") =>
        (string)RecordingSingingDecisionCompiler.QuoteReferences(c).OfType<JObject>()
            .First(q=>(string)q["origin"]==origin && ((string)q["text"]).Contains(quote))["ref"];
    public static JObject ReferencePlan(RecordingSingingDecisionContext c,string material,string quote,string intent="repeat") => new JObject {
        ["decision"]="perform",["source_ref"]=QuoteRef(c,quote),["request_ref"]="source",
        ["materials"]=new JArray(material),["range"]="clean",["intent"]=intent,
        ["expected"]="回唱所指录音的歌声，仍需独立审核",["thought"]="",["speech"]=new JObject{["text"]="",["language"]="ja"}};
    public static int RunReferenceChecks()
    {
        int before=checks;
        var c=Context("你好。请把刚才那段回唱给我听。");
        var p=ReferencePlan(c,"material_19","请把刚才那段回唱给我听。");
        bool Compile(JObject v,out string output,out string why)=>RecordingSingingDecisionCompiler.TryCompile(v.ToString(),c,out output,out why);
        Check(Compile(p,out var compiled,out var error) && compiled.Contains("<sing_goal ") && compiled.Contains("source_relation=\"current_recording\""),"program quote reference compiles only a goal proposal with bound current capture");
        string exact=(string)RecordingSingingDecisionCompiler.QuoteReferences(c).OfType<JObject>().Single(q=>(string)q["ref"]==(string)p["source_ref"])["text"];
        Check(compiled.Contains("evidence=\""+exact+"\"") && compiled.Contains("source_evidence=\""+exact+"\""),"both quotes come from exact stored span without model rewriting");
        Check(!compiled.Contains((string)p["source_ref"]),"opaque proposal ref is compiled to existing runtime original-text contract");
        p["speech"]["text"]="我现在就唱给你听。";
        Check(!Compile(p,out _,out error) && error=="perform-speech-not-empty" && RecordingSingingDecisionCompiler.RepairHint(error).Contains("speech.text"),"nonempty perform speech has precise repair fact");p["speech"]["text"]="";
        string current=(string)p["source_ref"];
        p["source_ref"]=exact;Check(!Compile(p,out _,out error) && error=="source-ref-not-current","model cannot substitute a prose quote for program reference");
        p["source_ref"]=QuoteRef(c,"你能把这一段回唱", "retained_request");
        Check(!Compile(p,out _,out error) && error=="source-ref-not-current","retained request reference cannot masquerade as current source");p["source_ref"]=current;
        p["request_ref"]=QuoteRef(c,"你能把这一段回唱", "retained_request");
        Check(!Compile(p,out _,out error) && error=="repeat-requires-current-source","repeat cannot acquire present authorization from retained user request");
        p["intent"]="perform";Check(!Compile(p,out compiled,out error) && error=="request-requires-bound-continuation","retained request alone cannot authorize perform without a bound unfinished goal");
        p["request_ref"]="invented";Check(!Compile(p,out _,out error) && error=="request-ref-not-bound","invented retained request identity fails closed");p["request_ref"]="source";p["intent"]="repeat";
        p["source_evidence"]=exact;Check(!Compile(p,out _,out error) && error=="invalid-recording-reference-shape","new identity protocol cannot mix in freely written evidence");p.Remove("source_evidence");
        var currentInput=(JObject)c.CurrentInput.DeepClone();c.CurrentInput["capture"]=20;c.CurrentInput["clip"]="clip:session-20";
        Check(!Compile(p,out _,out error) && error=="source-ref-not-current","same words in a new capture cannot reuse old source reference");c.CurrentInput=currentInput;
        string original=c.UserText;c.UserText+="现在先不要唱。";
        Check(!Compile(p,out _,out error) && error=="source-ref-not-current","same initial quote cannot reuse reference after a later cancellation changes full current input");c.UserText=original;
        p["expected"]="";Check(!Compile(p,out _,out error) && error=="perform-expected-required","empty expected is not mislabeled as unbound user");p["expected"]="回唱歌声";
        p["materials"]=new JArray();Check(!Compile(p,out _,out error) && error=="perform-material-required","missing material has specific repair");p["materials"]=new JArray("missing");
        Check(!Compile(p,out _,out error) && error=="material-not-in-snapshot","reference authorization cannot introduce unknown material");
        var schema=RecordingSingingDecisionCompiler.ResponseFormat(c)["json_schema"]["schema"];
        var branches=((JArray)schema["anyOf"]).OfType<JObject>().ToArray();
        Check(branches.Length==8,"correlate decision/request origin with every supported source/range combination");
        foreach(var branch in branches)
        {
            string decision=(string)branch["properties"]["decision"]["enum"][0];
            Check((bool)branch["additionalProperties"]==false && branch["required"].Count()==9,"each guided branch is a complete closed object");
            Check(branch["properties"]["source_evidence"]==null && branch["properties"]["user_evidence"]==null,"new guided output never asks model to rewrite exact evidence");
            if(decision!="reply") Check((string)branch["properties"]["speech"]["properties"]["text"]["enum"][0]=="","perform and handoff schema force silent submission");
            if(decision=="perform")
            {
                string range=(string)branch["properties"]["range"]["enum"][0];
                var expectedKeys=c.Materials.Where(m=>RecordingSingingDecisionCompiler.ProposalRanges(m).Contains(range)).Select(m=>m.Key);
                Check(branch["properties"]["range"]["enum"].Count()==1 &&
                    ((JArray)branch["properties"]["materials"]["items"]["enum"]).Values<string>().SequenceEqual(expectedKeys),
                    "each range permits every compatible stable source, never pending+current");
            }
            if(decision=="perform" && (string)branch["properties"]["intent"]["enum"][0]=="repeat")
                Check(branch["properties"]["request_ref"]["enum"].Count()==1 && (string)branch["properties"]["request_ref"]["enum"][0]=="source","repeat cannot generate a retained request ref then fail its correlated parser rule");
        }
        p=ReferencePlan(c,"material_19",c.UserText); p["range"]="current";
        Check(!Compile(p,out compiled,out error) && error=="selected-material-range-unavailable" &&
            compiled!=RecordingSingingDecisionCompiler.Handoff,"invalid pending/current stays a bounded domain repair, never legacy handoff");
        foreach(string kind in new[]{"reply","handoff"})
        {
            var v=new JObject{["decision"]=kind,["source_ref"]="none",["request_ref"]="none",["materials"]=new JArray(),["range"]="current",["intent"]="perform",["expected"]="",["thought"]="",["speech"]=new JObject{["text"]=kind=="reply"?"今は歌わないね。":"",["language"]="ja"}};
            Check(Compile(v,out compiled,out error),"nonperform branch remains available: "+kind);
            if(kind=="handoff"){v["speech"]["text"]="现在就开始。";Check(!Compile(v,out _,out error)&&error=="handoff-speech-not-empty","handoff cannot promise an unreviewed action");}
        }
        c=Context("歌词引用‘你能把这一段回唱给我听吗’，但我现在不要你唱。" );
        var quotes=RecordingSingingDecisionCompiler.QuoteReferences(c);
        Check(quotes.OfType<JObject>().Any(q=>(string)q["origin"]=="current"&&(string)q["text"]==c.UserText),"quote selection retains whole short negation/quotation instead of extracting only request words");
        Check(RecordingSingingDecisionCompiler.Prompt(c).Contains(c.UserText),"full negation remains visible for semantic and independent goal review");
        c.UserText="带换行的普通输入。\r\n"+new string('长',2000)+"最后一句仍是完整用户数据。";
        foreach(JObject q in RecordingSingingDecisionCompiler.QuoteReferences(c).Where(q=>(string)q["origin"]=="current"))
            Check((string)q["text"]==c.UserText.Substring((int)q["start"],(int)q["length"]) && ((string)q["text"]).Length<=300,"reference offsets are exact and bounded even with CRLF/long input");
        c=Context("再试一次，把我刚才唱的那一段歌要回唱给我听。",16);Add(c,14,"接下来我要唱一段歌哦。","",false);Add(c,15,"实际歌声","歌声",false);
        p=ReferencePlan(c,"material_15","再试一次");p["materials"]=new JArray("material_14","material_15");p["range"]="expanded";
        p["expected"]="先唱开场白，再唱歌声。";
        Check(Compile(p,out compiled,out error),"compiler validates c16 typed proposal without pretending to perform semantic goal review");
        Check(!string.Join(",",((JArray)p["materials"]).Values<string>()).Equals("material_15") && compiled.Contains("<sing_goal origin=\"user_request\"") && compiled.Contains("clip:session-14,clip:session-15"),"c16 intro-plus-song is explicitly a semantic mismatch requiring existing independent goal review, not counted as correct performance");
        return checks-before;
    }
    public static int RunChecks()
    {
        checks = 0;
        var c = Context("我再唱一句。初めてあなたを見たあの日。止められない喪失の予感。你能把这一段回唱给我听吗？");
        var p = Plan("material_19", "current_recording", "你能把这一段回唱给我听吗？");
        bool Compile(JObject v, out string value) => RecordingSingingDecisionCompiler.TryCompile(v.ToString(), c, out value, out _);
        Check(Compile(p, out var compiled) && compiled.Contains("input_capture=\"19\"") && compiled.Contains("clip:session-19"), "current recording identity compiled from snapshot");
        p["materials"] = new JArray("material_10");
        Check(Compile(p, out compiled) && compiled.Contains("source_relation=\"retained_recording\""), "identical short request quote cannot make earlier recording current");
        c.UserText = "这次别用第二段，还是把第一段再唱一次。";
        p["user_evidence"] = p["source_evidence"] = "把第一段再唱一次"; p["intent"] = "repeat";
        Check(Compile(p, out compiled) && compiled.Contains("clip:session-10"), "explicit repeat of old song remains supported despite newer capture");
        p["materials"] = new JArray("material_19");
        Check(!RecordingSingingDecisionCompiler.ValidateSourceSelection(c,c.Materials.Where(m=>m.CaptureSerial==19),"retained_recording","把第一段再唱一次",out _), "current capture cannot masquerade as retained");
        p["materials"] = new JArray("material_10", "material_19");
        c.UserText = "把两段按先后都唱出来。"; p["user_evidence"] = p["source_evidence"] = c.UserText;
        Check(Compile(p, out compiled) && compiled.Contains("clip:session-10,clip:session-19"), "ordered mixed selection preserves both captures");
        Check(!RecordingSingingDecisionCompiler.ValidateSourceSelection(c,c.Materials,"current_recording",c.UserText,out _), "mixed set cannot claim single current source");
        c = Context("还是没成功，再试一次。", 0); p = Plan("material_10", "retained_recording", "你能把这一段回唱给我听吗？", "repeat");
        Check(!Compile(p, out _), "c12 prior quote rejected before runtime instead of invalid goal");
        p["user_evidence"] = p["source_evidence"] = "再试一次";
        Check(Compile(p, out _), "c17 valid current repeat authorization with old recording");
        p["user_evidence"]="你能把这一段回唱给我听吗？";
        Check(Compile(p,out compiled) && compiled.Contains("evidence=\"再试一次\""),"repeat uses the exact current source quote once; duplicate stale request quote cannot replace it");
        p["intent"] = "perform"; p["user_evidence"] = "你能把这一段回唱给我听吗？";
        Check(!Compile(p, out _), "recovery cannot quote an old perform request without an explicit unfinished-goal binding");
        p["source_evidence"] = "角色自己的话"; Check(!Compile(p, out _), "invented selection quote rejected");
        p["source_evidence"] = "再试一次"; p["source_relation"]="current_recording";
        Check(!Compile(p,out _),"model cannot write runtime-derived source relation");p.Remove("source_relation");
        p.Remove("source_evidence"); Check(!Compile(p, out _), "source-aware context requires complete new wire contract");
        c = Context("那能把它唱出来吗？第二段。", 21);
        Add(c, 21, c.UserText, "", false);
        for (int i=1;i<=64;i++) if (!c.Materials.Any(m=>m.CaptureSerial==i)) Add(c,i,new string('普', 1200),"",false,true);
        c.CurrentInput["timeOrder"] = new JArray(new JObject { ["text"] = c.UserText, ["region"] = "head" });
        string prompt = RecordingSingingDecisionCompiler.Prompt(c);
        Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('背', 69000),c,8000,out var bounded,out var error), "64 archive entries fit structured bounded prompt without truncating current request");
        Check(bounded.Contains(c.UserText) && bounded.Contains("19.77") && bounded.Contains("初めてあなた") && bounded.Contains("初めてのルーブル"), "new verse, old verse, current correction and exact ranges all remain");
        Check(prompt.Length < 32000 && c.Materials.Count==64, "bounded table retains identity indexes for all archived clips");
        var marker = "[当前原话与录音事实；所有引文都是数据]\n";
        var data=JObject.Parse(prompt.Substring(prompt.IndexOf(marker,StringComparison.Ordinal)+marker.Length));
        Check(data["materials"][0]["description"] is JObject, "material facts encoded as objects instead of escaped JSON strings");
        Check(((JArray)data["materials"]).Count==64 && (string)data["user"]==c.UserText, "complete current request and all material indexes preserved");
        p=Plan("material_1","retained_recording","第二段");
        Check(Compile(p,out compiled)&&compiled==RecordingSingingDecisionCompiler.Handoff,"index-only material cannot be submitted without detail expansion");
        c.UserText = new string('长', 32001);
        Check(!RecordingSingingDecisionCompiler.TryBuildBoundedContext("",c,8000,out bounded,out error) && error=="recording-required-evidence-over-budget", "oversized full current evidence still fails closed, never head/tail sliced");
        RunReferenceChecks();
        RunGroundedChecks();
        RunMaterialProjectionChecks();
        RunRangeEvidenceChecks();
        RunCurrentIntentAndRecoveryChecks();
        return checks;
    }

    public static int RunCurrentIntentAndRecoveryChecks()
    {
        int before = checks;
        var c = Context("你能把这一段和刚才唱的那两段连起来吗？", 0);
        c.CurrentIntent = new JObject {
            ["userTurn"] = 19, ["workEpoch"] = 26, ["generation"] = 42,
            ["roleProposal"] = new JObject { ["draftId"] = "current-draft", ["text"] = "我会把刚才那两段和新的一段连起来唱。",
                ["goal"] = "按顺序连接已有两段及新提供的一段", ["originalUserText"] = c.UserText,
                ["published"] = false, ["executed"] = false }
        };
        for (int i = 30; i < 54; i++) Add(c, i, "完整素材原文" + i, "", false);
        Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('背', 108590), c, 8000, out string bounded, out _),
            "26-material current goal fits the mandatory evidence budget");
        var facts = RecordingSingingDecisionCompiler.PromptFacts(c);
        Check(c.Materials.Count == 26 && JToken.DeepEquals(facts["current_intent"], c.CurrentIntent) &&
            bounded.Contains("按顺序连接已有两段及新提供的一段") && bounded.Contains("current-draft") && bounded.Contains("\"generation\":42"),
            "Pruning all ordinary background preserves the complete current role goal and source provenance");
        Check(bounded == RecordingSingingDecisionCompiler.Prompt(c), "Regression must actually omit the entire disposable background");
        var names = facts.Properties().Select(p => p.Name).ToList();
        Check(names.IndexOf("materials") < names.IndexOf("materialProjection") && names.IndexOf("materials") < names.IndexOf("current_intent") && names.Last() == "user",
            "Stable material facts precede current intent and complete current user without dropping either");
        facts["current_intent"]["roleProposal"]["goal"] = "mutated";
        Check((string)c.CurrentIntent["roleProposal"]["goal"] != "mutated", "Protected current intent projection is detached from the owner");

        var p = ReferencePlan(c, "material_10", c.UserText, "perform");
        p["request_ref"] = QuoteRef(c, "你能把这一段回唱", "retained_request");
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out _, out string error) && error == "request-requires-bound-continuation",
            "c26 cannot replace the current combined goal with the retained completed first-song request");
        var performSchemas = ((JArray)RecordingSingingDecisionCompiler.ResponseFormat(c)["json_schema"]["schema"]["anyOf"])
            .OfType<JObject>().Where(b => (string)b["properties"]["decision"]["enum"][0] == "perform" &&
                (string)b["properties"]["intent"]["enum"][0] == "perform").ToArray();
        Check(performSchemas.Length > 0 && performSchemas.All(b => b["properties"]["request_ref"]["enum"].Count() == 1 &&
            (string)b["properties"]["request_ref"]["enum"][0] == "source"),
            "Guided decoding cannot select a completed or unrelated historical request");
        p["request_ref"] = "source"; p["materials"] = new JArray("material_10", "material_19", "material_30");
        Check(RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out string compiled, out _) &&
            compiled.Contains("clip:session-10,clip:session-19,clip:session-30"),
            "Current goal can propose all ordered materials without historical authorization or forced newest-only selection");
        c.UserText = "先别唱了，我只想确认这段有没有保存。";
        var reply = new JObject { ["decision"] = "reply", ["source_ref"] = "none", ["request_ref"] = "none",
            ["materials"] = new JArray(), ["range"] = "current", ["intent"] = "perform", ["expected"] = "", ["thought"] = "",
            ["speech"] = new JObject { ["text"] = "好，我先不唱。", ["language"] = "zh" } };
        Check(RecordingSingingDecisionCompiler.TryCompile(reply.ToString(), c, out compiled, out _) && !compiled.Contains("<sing "),
            "A protected adopted goal does not force execution after the role retracts or user cancels");

        c = Context("刚才还没完成，继续原来的目标。", 0);
        const string originalQuote = "你能把这一段回唱给我听吗？";
        c.Recovery = new RecordingSingingRecoveryBinding { InputTurn = 19, GoalVersion = 7, RequestTurn = 10,
            RequestQuote = originalQuote, Refs = "clip:session-10", Range = "clean", Expected = "完整回唱最初指定的歌声",
            Completion = "user_confirmation", Intent = "perform" };
        var recoveryFacts = RecordingSingingDecisionCompiler.RecoveryFacts(c);
        Check(recoveryFacts != null && (string)recoveryFacts["request_quote"] == originalQuote && (int)recoveryFacts["goal_version"] == 7,
            "Recovery exposes one exact source-turn quote and unfinished goal version, including a request subspan");
        p = ReferencePlan(c, "material_10", c.UserText, "perform"); p["request_ref"] = recoveryFacts["request_ref"].DeepClone();
        p["expected"] = c.Recovery.Expected;
        Check(RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out compiled, out _) &&
            compiled.Contains("transition=\"continue\" base_version=\"7\"") && compiled.Contains("completion=\"user_confirmation\""),
            "Exact unfinished-task recovery preserves target version and its stronger completion condition");
        p["materials"] = new JArray("material_19");
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out _, out error) && error == "current-goal-change-needs-explicit-transition",
            "An old request cannot authorize another recording while claiming continuation");
        p["materials"] = new JArray("material_10"); p["expected"] = "换成更短的目标";
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out _, out error) && error == "current-goal-change-needs-explicit-transition",
            "Continuation cannot silently shrink or rewrite the active success condition");
        p["expected"] = c.Recovery.Expected; c.Recovery.InputTurn = 18;
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out _, out error) && error == "request-requires-bound-continuation",
            "A previous input transaction cannot lend its unfinished-task binding to this input");
        c.Recovery.InputTurn = 19; p["intent"] = "repeat";
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out _, out error) && error == "repeat-requires-current-source",
            "A continuation binding still cannot authorize a new repeat from historical evidence");
        p["request_ref"] = "source"; p["intent"] = "perform";
        Check(RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out compiled, out _) &&
            compiled.Contains("transition=\"continue\" base_version=\"7\"") && compiled.Contains("completion=\"user_confirmation\""),
            "A current-source continuation preserves the unfinished goal version and completion condition too");
        p["materials"] = new JArray("material_19"); p["expected"] = "换为另一份录音的新目标";
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out compiled, out error) &&
            compiled != RecordingSingingDecisionCompiler.Handoff && error == "current-goal-change-needs-explicit-transition",
            "An implicit target change is rejected inside the structured route, not handed off as an executable plan");
        p["goal_transition"] = "replace";
        Check(RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out compiled, out error) &&
            compiled.Contains("transition=\"replace\" base_version=\"7\"") && compiled.Contains("change_quote=\"" + c.UserText + "\"") &&
            compiled.Contains("clip:session-19") && compiled != RecordingSingingDecisionCompiler.Handoff,
            "An explicit pending replacement binds the complete current request and previous version for independent review");
        p["request_ref"] = recoveryFacts["request_ref"].DeepClone();
        Check(!RecordingSingingDecisionCompiler.TryCompile(p.ToString(), c, out _, out error) && error == "goal-transition-requires-complete-current-request",
            "The typed replacement cannot reuse a historical request as new change authority");
        return checks - before;
    }

    public static int RunRangeEvidenceChecks()
    {
        int before = checks;
        var facts = new JObject {
            ["RawSeconds"] = 23.66, ["CleanStartSeconds"] = 6.33, ["CleanEndSeconds"] = 16.87,
            ["ExpandedStartSeconds"] = 0, ["ExpandedEndSeconds"] = 21.97,
            ["HeadExtraText"] = "好，接下来我要唱一首歌。", ["TailExtraText"] = "把我唱的这一段回唱给我听。",
            ["HeadSegments"] = new JArray(new JObject { ["rawStartSeconds"] = 0, ["rawEndSeconds"] = 6.33, ["type"] = "non_melodic" }),
            ["TailSegments"] = new JArray(new JObject { ["rawStartSeconds"] = 16.87, ["rawEndSeconds"] = 21.97, ["type"] = "uncertain" }),
            ["currentPlayback"] = new JObject { ["selected"] = true, ["Revision"] = 1,
                ["CurrentStartSeconds"] = 0, ["CurrentEndSeconds"] = 21.97 }
        };
        string original = facts.ToString();
        var evidence = RecordingSingingDecisionCompiler.RangeEvidence(facts, "expanded");
        Check((double)evidence["ranges"]["clean"]["start"] == 6.33 &&
            ((JArray)evidence["ranges"]["clean"]["boundaryOverlap"]).Count == 0 &&
            ((JArray)evidence["ranges"]["expanded"]["boundaryOverlap"]).Count == 2 &&
            ((JArray)evidence["ranges"]["current"]["boundaryOverlap"]).Count == 2,
            "Range audit must distinguish previous broad playback from the narrower measured candidate without selecting it.");
        Check((string)evidence["boundarySegments"][1]["type"] == "uncertain" &&
            ((string)evidence["scope"]).Contains("not guaranteed perfect") && original == facts.ToString(),
            "Geometric overlap cannot mutate source facts or turn uncertain acoustic evidence into confirmed speech.");
        evidence["boundarySegments"][0]["type"] = "mutated";
        Check((string)facts["HeadSegments"][0]["type"] == "non_melodic", "Returned evidence must be detached.");
        JValue Single(float value) => new JValue((object)value);
        var singleFacts = new JObject {
            ["RawSeconds"] = Single(23.66f), ["CleanStartSeconds"] = Single(6.33f), ["CleanEndSeconds"] = Single(16.87f),
            ["ExpandedStartSeconds"] = Single(0f), ["ExpandedEndSeconds"] = Single(21.97f),
            ["HeadSegments"] = new JArray(new JObject { ["rawStartSeconds"] = Single(0f), ["rawEndSeconds"] = Single(6.33f), ["type"] = "non_melodic" }),
            ["TailSegments"] = new JArray(new JObject { ["rawStartSeconds"] = Single(16.87f), ["rawEndSeconds"] = Single(21.97f), ["type"] = "uncertain" }),
            ["currentPlayback"] = new JObject { ["selected"] = true, ["CurrentStartSeconds"] = Single(0f), ["CurrentEndSeconds"] = Single(21.97f) }
        };
        Check(((JValue)singleFacts["CleanStartSeconds"]).Value is float,
            "Regression must exercise a Single-backed token, as returned by direct runtime fact serialization.");
        var wireFacts = JObject.Parse(singleFacts.ToString(Formatting.None));
        Check(JToken.DeepEquals(RecordingSingingDecisionCompiler.RangeEvidence(singleFacts),
            RecordingSingingDecisionCompiler.RangeEvidence(wireFacts)),
            "Direct Single-backed review facts and JSON-round-tripped proposal facts must yield exactly the same ranges and overlaps.");
        var strictGeometry = RecordingSingingDecisionCompiler.RangeEvidence(singleFacts);
        wireFacts["CleanStartSeconds"] = 6.34;
        Check(!JToken.DeepEquals(strictGeometry["ranges"]["clean"],
            RecordingSingingDecisionCompiler.RangeEvidence(wireFacts)["ranges"]["clean"]),
            "Numeric wire normalization must not hide a real change to measured clean bounds.");
        facts["CleanStartSeconds"] = -1; facts["ExpandedEndSeconds"] = 99;
        var invalid = RecordingSingingDecisionCompiler.RangeEvidence(facts);
        Check(!(bool)invalid["ranges"]["clean"]["available"] && !(bool)invalid["ranges"]["expanded"]["available"],
            "Negative/out-of-recording bounds must not be presented as known selectable geometry.");
        var unknown = RecordingSingingDecisionCompiler.RangeEvidence(new JObject());
        Check(!(bool)unknown["ranges"]["clean"]["available"] && unknown["ranges"]["clean"]["start"].Type == JTokenType.Null,
            "Absent source coordinates stay unknown, never zero-length proven content.");
        var c = Context("把我说话的部分去掉，只唱有旋律的那部分。", 9);
        c.Materials[0].Description = original;
        var table = RecordingSingingDecisionCompiler.PromptFacts(c);
        var projected = table["materials"][0]["description"];
        Check(projected["rangeEvidence"] != null && (string)projected["HeadExtraText"] == "好，接下来我要唱一首歌。",
            "Actual dedicated prompt must retain both boundary semantics and range overlap.");
        var clean = ReferencePlan(c, c.Materials[0].Key, c.UserText);
        Check(RecordingSingingDecisionCompiler.TryCompile(clean.ToString(), c, out string compiled, out _) &&
            compiled.Contains("range=\"clean\"") && compiled.Contains(c.Materials[0].ClipRef),
            "A clean correction remains a source-bound proposal, not a generated clip ID.");
        clean["range"] = "expanded";
        Check(RecordingSingingDecisionCompiler.TryCompile(clean.ToString(), c, out compiled, out _) &&
            compiled.Contains("range=\"expanded\""),
            "Geometry does not silently override semantic range selection; independent review must compare the goal.");
        clean["materials"] = new JArray("clip:made-up");
        Check(!RecordingSingingDecisionCompiler.TryCompile(clean.ToString(), c, out _, out _),
            "Typed trim/repeat cannot bypass material key ownership with an invented clip.");
        return checks - before;
    }

    public static int RunMaterialProjectionChecks()
    {
        int before=checks;
        var c=Context("我已经唱了呀，现在你把它回唱出来呀。",21);
        c.Materials.Clear();
        Add(c,15,"下面开始歌唱测试。初めてのルーブルはなんでことはなかたは私だけ。","",false);
        Add(c,21,c.UserText,"",false);
        var song=c.Materials[0];
        var facts=JObject.Parse(song.Description); facts["SingingProbability"]=.432; song.Description=facts.ToString();
        var projected=RecordingSingingDecisionCompiler.PromptFacts(c);
        Check((string)projected["materials"][0]["description"]["RecordingTranscript"]==(string)facts["RecordingTranscript"] &&
            (string)projected["materials"][0]["description"]["SingingSegmentText"]=="",
            "Missing segment transcript must not erase the whole retained song/intro transcript");
        var p=ReferencePlan(c,song.Key,c.UserText);
        Check(RecordingSingingDecisionCompiler.TryCompile(p.ToString(),c,out var output,out _) &&
            output!=RecordingSingingDecisionCompiler.Handoff && output.Contains("retained_recording") && !RoleOutputChannels.Parse(output).HasSpeech,
            "Low-score retained recording with full facts stays a silent review proposal, not an implicit handoff or performance");
        c.UserText="好吧，现在好像失败了，再来一次，试着把我刚才唱的那一段回唱给我听。";
        c.CurrentInput["capture"]=22; c.CurrentInput["clip"]="clip:session-22";
        Add(c,22,c.UserText,"",false);
        p=ReferencePlan(c,song.Key,c.UserText);
        Check(RecordingSingingDecisionCompiler.TryCompile(p.ToString(),c,out output,out _) && output.Contains("clip:session-15"),
            "A new retry keeps the same selected older source identity without adopting the intervening request recording");
        for(int i=1;i<=64;i++) if(!c.Materials.Any(m=>m.CaptureSerial==i)) Add(c,i,"ordinary explanation "+i,"",false);
        Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext("",c,8000,out var bounded,out var error),
            "64 short full-transcript recordings should fit after lossless column compression: "+error);
        projected=RecordingSingingDecisionCompiler.PromptFacts(c);
        Check(projected["materialFactColumns"] is JArray && projected["materials"].Count()==64 && bounded.Length<=32000,
            "Large archive must compact repeated field names rather than discard evidence or overrun the hard character budget");
        var columns=((JArray)projected["materialFactColumns"]).Values<string>().ToArray();
        foreach(JObject row in (JArray)projected["materials"])
        {
            var original=JObject.Parse(c.Materials.Single(m=>m.Key==(string)row["key"]).Description);
            Check((string)row["description"][Array.IndexOf(columns,"RecordingTranscript")]==(string)original["RecordingTranscript"],
                "Column encoding changed one full retained transcript: "+row["key"]);
        }
        song.Description=new JObject{["RecordingTranscript"]=new string('长',40000),["CaptureSessionSerial"]=15}.ToString();
        Check(!RecordingSingingDecisionCompiler.TryBuildBoundedContext("",c,8000,out _,out error) && error=="recording-required-evidence-over-budget",
            "An individually oversized retained transcript must fail closed rather than be silently shortened or classified away");
        return checks-before;
    }
    public static JArray Cases()
    {
        var rows=new JArray();
        void ExportCase(string id,RecordingSingingDecisionContext c,string expectedMaterial,string expectedRelation,string kind="perform")
        {
            CaseContexts[id]=c;
            rows.Add(new JObject { ["id"]=id,["expectedMaterial"]=expectedMaterial,
                ["expectedRelation"]=expectedRelation,["expectedDecision"]=kind,
                ["expectedIntent"]=id=="explicit-old-repeat" ? "repeat" : null,
                ["scope"]="Production prompt/schema with synthetic material facts; decision and compiler validation only, no audio execution or independent goal approval.",
                ["messages"]=new JArray(new JObject { ["role"]="system",["content"]="你是录音回唱决策器，严格根据实际用户意图填写协议。" },
                    new JObject { ["role"]="user",["content"]=RecordingSingingDecisionCompiler.Prompt(c) }),
                ["response_format"]=RecordingSingingDecisionCompiler.ResponseFormat(c) });
        }
        void Case(string id,string user,int capture,string expectedMaterial,string expectedRelation,string kind="perform")
        {
            var c=Context(user,capture);
            if(capture==21) Add(c,21,user,"",false);
            ExportCase(id,c,expectedMaterial,expectedRelation,kind);
        }
        Case("c19-new-verse","非常棒，这一次唱出来了。那我再唱一句：初めてあなたを見たあの日、止められない喪失の予感。你能把这一段回唱给我听吗？",19,"material_19","current_recording");
        Case("c21-second-correction","不是第一段，我是说最新唱的第二段。那能把它唱出来吗？",21,"material_19","retained_recording");
        Case("explicit-old-repeat","我再唱一句：初めてあなたを見たあの日。止められない喪失の予感。但现在不要回唱第二段，请把第一段再唱一次。",19,"material_10","retained_recording");
        Case("c12-retry","我是说把我刚才唱的第一段回唱给我听，再试一次。",0,"material_10","retained_recording");
        Case("both-ordered","把刚才两段按第一段、第二段的顺序都唱给我听。",19,"material_10,material_19","mixed_recordings");
        Case("ordinary-correction","那能把第二段唱出来吗？",21,"material_19","retained_recording");
        Case("no-performance","我刚唱了第二段，只问你听到没有，现在不要唱。",19,"","","reply");
        Case("quoted-request","歌词里有‘你能把这一段回唱给我听吗’，但这是歌词引用，我没有请你唱，现在不要唱。",19,"","","reply");
        Case("cancel-new-request","原先想让你回唱第二段，但现在取消，不要唱了。",19,"","","reply");
        string initial="没有问题。那接下来进入唱歌测试环节，我要唱一段歌哦。。 ルブルはなんてことはなかたは私だけのもナリがもう特くに出あげたか。 好，现在请你把我唱的这一段回唱给我听。";
        string retry="嗯，奇怪，为什么唱歌又被禁止了呢？再试一次再试一次，把我刚才唱的那一段歌要回唱给我听。";
        string last="好像还是失败。那最后一次再试一次，看看能不能回唱给我听。";
        foreach(var entry in new[]{Tuple.Create(15,initial),Tuple.Create(16,retry),Tuple.Create(17,last)})
        {
            var c=Context(entry.Item2,entry.Item1);c.Materials.Clear();
            c.CurrentInput["turn"]=entry.Item1;
            Add(c,10,"很久以前的一首不同的歌。你能唱这首吗？","很久以前的不同旋律",true);
            Add(c,14,"没有问题。那接下来进入唱歌测试环节，我要唱一段歌哦。","",false);
            Add(c,15,initial,"ルブルはなんてことはなかたは私だけのもナリがもう特くに出あげたか。",false);
            if(entry.Item1!=15)Add(c,entry.Item1,entry.Item2,"",false);
            c.RequestEvidenceTexts=new[]{initial,retry};
            c.RequestEvidence=new JArray(new JObject{["turn"]=15,["capture"]=15,["clip"]="clip:session-15",["text"]=initial});
            if(entry.Item1==17)c.RequestEvidence.Add(new JObject{["turn"]=16,["capture"]=16,["clip"]="clip:session-16",["text"]=retry});
            ExportCase("session1852-c"+entry.Item1,c,"material_15",entry.Item1==15?"current_recording":"retained_recording");
        }
        foreach (string scenario in new[] { "combine", "replacement", "inventory", "role-refusal", "bound-recovery" })
        {
            string user = scenario == "replacement" ? "这次不唱前两段，只把新给的第三段唱给我听。" :
                scenario == "inventory" ? "第三段我刚才已经唱过了，你手上有没有保存它？我现在只问有没有。" :
                scenario == "bound-recovery" ? "刚才还没完成，请继续原来的两段目标。" : "你能把这一段和刚才唱的那两段连起来吗？";
            string oldRequest = "请把最初唱的第一段和第二段按顺序连起来回唱。";
            var c = Context(user, 26); c.Materials.Clear(); c.CurrentInput["turn"] = 26;
            Add(c, 10, "第一段：初めてのルーブル。请把这一段唱给我听。", "初めてのルーブル", true);
            Add(c, 19, "第二段：初めてあなたを見たあの日。请把第二段回唱。", "初めてあなたを見たあの日", true);
            Add(c, 25, "第三段：止められない喪失の予感。这是新给你的第三段。", "止められない喪失の予感", false);
            for (int capture = 1; capture <= 26; capture++)
                if (!c.Materials.Any(m => m.CaptureSerial == capture))
                    Add(c, capture, capture == 26 ? user : "普通讲话，说明环境与测试进度，采集" + capture, "", false);
            c.RequestEvidenceTexts = new[] { oldRequest, "第三段：止められない喪失の予感。这是新给你的第三段。", user };
            c.RequestEvidence = new JArray(new JObject { ["turn"] = 24, ["capture"] = 24,
                ["clip"] = "clip:session-24", ["text"] = oldRequest }, new JObject { ["turn"] = 25, ["capture"] = 25,
                ["clip"] = "clip:session-25", ["text"] = c.RequestEvidenceTexts[1] });
            string goal = scenario == "replacement" ? "仅回唱新提供的第三段" : scenario == "bound-recovery" ? "按原顺序完整回唱第一段和第二段" : "按顺序回唱已有第一段、第二段和新提供的第三段";
            string draft = scenario == "role-refusal" ? "我现在不想唱，想先安静一会儿。" :
                scenario == "inventory" ? "第三段的录音已经保存，我先确认这个事实。" : goal;
            c.CurrentIntent = new JObject { ["userTurn"] = 26, ["workEpoch"] = 26, ["generation"] = 42,
                ["input"] = c.CurrentInput.DeepClone(), ["roleProposal"] = new JObject {
                    ["draftId"] = "synthetic-c26-" + scenario, ["source"] = "unpublished-current-role-draft", ["text"] = draft,
                    ["task"] = scenario == "role-refusal" || scenario == "inventory" ? "none" : "recording_singing",
                    ["goal"] = scenario == "role-refusal" || scenario == "inventory" ? "" : goal,
                    ["originalUserText"] = user, ["published"] = false, ["executed"] = false } };
            c.Facts = new JObject { ["lastPlayback"] = new JObject { ["completed"] = true,
                    ["materials"] = new JArray("clip:session-10", "clip:session-19") },
                ["activeGoal"] = new JObject { ["refs"] = "clip:session-10,clip:session-19", ["range"] = "clean",
                    ["expected"] = "按原顺序完整回唱第一段和第二段", ["requestQuote"] = oldRequest,
                    ["sourceUserTurn"] = 24, ["outcome"] = scenario == "bound-recovery" ? "not_submitted" : "playback_complete_content_unverified" } }.ToString();
            if (scenario == "bound-recovery")
                c.Recovery = new RecordingSingingRecoveryBinding { InputTurn = 26, GoalVersion = 7, RequestTurn = 24,
                    RequestQuote = oldRequest, Refs = "clip:session-10,clip:session-19", Range = "clean", Expected = goal,
                    Completion = "playback", Intent = "perform" };
            bool reply = scenario == "role-refusal" || scenario == "inventory";
            ExportCase("intent-cache-26-" + scenario, c, reply ? "" : scenario == "replacement" ? "material_25" :
                scenario == "bound-recovery" ? "material_10,material_19" : "material_10,material_19,material_25", "retained_recording", reply ? "reply" : "perform");
            rows.Last["scope"] = "Production prompt/schema and complete 26-row synthetic material archive reconstructed for c26 failure modes; not the original production wire. Includes current adopted role intent, old completed goal or explicit unfinished recovery binding. Model decision/compilation only; semantic approval, playback and real audio are not executed.";
        }
        return rows;
    }
    // The quoted user/ASR text comes from the immutable 2026-09-27 1308 session.
    // State is reconstructed, not a captured original wire; no PCM is evaluated.
    public static JArray ConvergenceCases()
    {
        var rows = new JArray();
        const string first = "clip:9558f0f0-6", second = "clip:9558f0f0-11", request = "clip:9558f0f0-15";
        const string firstText = "初めてのルーブルはなんでこはなかたは私だけのモナリザもどこに出あっ？ たか。";
        const string secondText = "初めてあなたを見たあの日をき出した車止められない喪失のよ。 No you can.";
        foreach (var test in new[] {
            Tuple.Create("session1308-second-request", "你把第二段唱给我听呀，我唱的那个第二段，你唱给我听。", second, "perform"),
            Tuple.Create("session1308-lyric-correction", "不是第一段不是第一段，是第二段，是那个唱着。 はめて安卡米塔那一段。", second, "perform"),
            Tuple.Create("session1308-return-to-first", "这次不要第二段了，还是把最开始初めてのルーブル那段再唱给我听。", first, "perform"),
            Tuple.Create("session1308-two-ordered", "把我唱过的两段按原来第一段、第二段的顺序回唱给我听。", first + "," + second, "perform"),
            Tuple.Create("session1308-cancel", "不是第一段不是第一段，我是说第二段。不过不用再试了，现在不要唱。", "", "reply"),
            Tuple.Create("session1308-quotation", "我是在复述刚才的要求‘你把第二段唱给我听’，现在只问你是不是选错了，不让你再唱。", "", "reply") })
        {
            var c = new RecordingSingingDecisionContext { UserText = test.Item2,
                CurrentInput = new JObject { ["turn"] = 15, ["capture"] = 16, ["clip"] = request },
                CurrentIntent = new JObject { ["userTurn"] = 15, ["roleProposal"] = new JObject {
                    ["source"] = "current-user-goal", ["text"] = test.Item2, ["task"] = test.Item4 == "perform" ? "recording_singing" : "none" } },
                RequestEvidence = new JArray(new JObject { ["turn"] = 6, ["capture"] = 7, ["clip"] = first, ["text"] = firstText },
                    new JObject { ["turn"] = 11, ["capture"] = 12, ["clip"] = second, ["text"] = secondText },
                    new JObject { ["turn"] = 14, ["capture"] = 15, ["clip"] = "clip:9558f0f0-14", ["text"] = "不是第一段，我说的是第二段。" }),
                Facts = new JObject { ["lastPlayback"] = new JObject { ["completed"] = true, ["materials"] = new JArray(first) },
                    ["activeGoal"] = new JObject { ["refs"] = first, ["expected"] = "回唱第二段，即初めてあなたを見た那段。",
                        ["outcome"] = "playback_complete_content_unverified" } }.ToString() };
            void Material(string clip, int sequence, int capture, string transcript, bool ready)
            {
                c.Materials.Add(new RecordingSingingMaterial { Key = clip, ClipRef = clip, RecordingSequence = sequence,
                    CaptureSerial = capture, Revision = ready ? "1" : "pending", PlaybackStatus = ready ? "ready" : "evidence_only",
                    HasCleanRange = true, HasExpandedRange = true,
                    Description = new JObject { ["RecordingSequence"] = sequence, ["CaptureSessionSerial"] = capture,
                        ["RecordingTranscript"] = transcript, ["SingingSegmentText"] = "", ["SourceStatus"] = ready ? "confirmed_user" : "pending",
                        ["ContentVerdict"] = "undecided", ["AcousticMode"] = "speech", ["RawSeconds"] = 12.0,
                        ["CleanStartSeconds"] = 0.0, ["CleanEndSeconds"] = clip == second ? 10.9 : 9.87,
                        ["ExpandedStartSeconds"] = 0.0, ["ExpandedEndSeconds"] = 11.4,
                        ["currentPlayback"] = new JObject { ["selected"] = ready, ["Revision"] = ready ? "1" : "pending" } }.ToString() });
            }
            Material(first, 6, 7, firstText, true);
            Material("clip:9558f0f0-10", 10, 11, "很好啊，那接下来先停下来吧。", false);
            Material(second, 11, 12, secondText, false);
            Material(request, 15, 16, test.Item2, false);
            CaseContexts[test.Item1] = c;
            rows.Add(new JObject { ["id"] = test.Item1, ["expectedMaterial"] = test.Item3,
                ["expectedRelation"] = "retained_recording", ["expectedDecision"] = test.Item4,
                ["scope"] = "Immutable 1308-session words with reconstructed stable recording identities/readiness/history. Production recording prompt/schema/compiler; no original final-wire claim, acoustic evaluation or playback.",
                ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = RecordingSingingDecisionCompiler.SourceContract },
                    new JObject { ["role"] = "user", ["content"] = RecordingSingingDecisionCompiler.PromptFacts(c).ToString(Newtonsoft.Json.Formatting.None) }),
                ["response_format"] = RecordingSingingDecisionCompiler.ResponseFormat(c) });
        }
        return rows;
    }
    private static RecordingSingingDecisionContext ImportProductionContext(JObject value)
    {
        // Explicit transport mapping keeps this small .NET runner independent of
        // the Unity Newtonsoft build's reflection/security compatibility shim.
        var c = new RecordingSingingDecisionContext { UserText = (string)value["UserText"], Facts = (string)value["Facts"],
            RequireGroundedResolution=(bool?)value["RequireGroundedResolution"]??false,
            CurrentInput = value["CurrentInput"] as JObject, CurrentIntent = value["CurrentIntent"] as JObject,
            RequestEvidence = value["RequestEvidence"] as JArray ?? new JArray(),
            RequestEvidenceTexts = (value["RequestEvidenceTexts"] as JArray ?? new JArray()).Values<string>().ToArray() };
        foreach(JObject row in (JArray)value["Materials"])
            c.Materials.Add(new RecordingSingingMaterial { Key=(string)row["Key"], ClipRef=(string)row["ClipRef"],
                Revision=(string)row["Revision"], Description=(string)row["Description"], PlaybackStatus=(string)row["PlaybackStatus"],
                CaptureSerial=(int?)row["CaptureSerial"] ?? 0, RecordingSequence=(int?)row["RecordingSequence"] ?? 0,
                HasCleanRange=(bool?)row["HasCleanRange"] ?? false, HasExpandedRange=(bool?)row["HasExpandedRange"] ?? false,
                RequiresExpansion=(bool?)row["RequiresExpansion"] ?? false, LastCompletedPlayback=row["LastCompletedPlayback"] as JObject });
        if(value["Recovery"] is JObject r)
            c.Recovery=new RecordingSingingRecoveryBinding { InputTurn=(int)r["InputTurn"], GoalVersion=(int)r["GoalVersion"],
                RequestTurn=(int)r["RequestTurn"], RequestQuote=(string)r["RequestQuote"], Refs=(string)r["Refs"],
                Range=(string)r["Range"], Expected=(string)r["Expected"], Completion=(string)r["Completion"], Intent=(string)r["Intent"] };
        return c;
    }
    public static int RunGroundedChecks()
    {
        int before=checks;
        var c=Context("先确认第一段还在不在，有的话把三段按顺序连起来唱。",21);
        c.RequireGroundedResolution=true;
        Add(c,20,"もういっぱい忘れよう last kiss","もういっぱい忘れよう last kiss",true);
        c.CurrentIntent=new JObject { ["roleProposal"]=new JObject { ["task"]="recording_singing",["goal"]=c.UserText,
            ["currentFactPrerequisite"]=true,["reviewedCoverage"]="compound" } };
        JObject Proof(string key) { var m=c.Materials.Single(x=>x.Key==key); var f=JObject.Parse(m.Description);
            string text=(string)f["SingingSegmentText"]??(string)f["RecordingTranscript"]??"";
            return new JObject{["key"]=key,["quote"]=text.Substring(0,Math.Min(48,text.Length))}; }
        var p=ReferencePlan(c,"material_10",c.UserText,"perform");
        p["materials"]=new JArray("material_10","material_19","material_20");p["resolution"]="matched";p["evidence_ref"]="none";
        p["selection_evidence"]=new JArray(Proof("material_10"),Proof("material_19"),Proof("material_20"));
        bool Compile(JObject v,out string output,out string why)=>RecordingSingingDecisionCompiler.TryCompile(v.ToString(),c,out output,out why);
        Check(Compile(p,out var output,out var why)&&output.Contains("refs=\"clip:session-10,clip:session-19,clip:session-20\""),"grounded three sources preserve adopted order without another route");
        ((JObject)p["selection_evidence"][0])["quote"]="もういっぱい";
        Check(!Compile(p,out _,out why)&&why=="recording-content-quote-not-in-clip","foreign verse cannot be attached to first source");
        p["selection_evidence"][0]=Proof("material_10"); p["selection_evidence"][1]=Proof("material_20");
        Check(!Compile(p,out _,out _),"proof order cannot disagree with executable order");p["selection_evidence"][1]=Proof("material_19");
        var reply=new JObject { ["decision"]="reply",["source_ref"]="none",["request_ref"]="none",["materials"]=new JArray(),["range"]="current",["intent"]="perform",["expected"]="",["thought"]="",["speech"]=new JObject{["text"]="",["language"]="zh"},["resolution"]="role_declined",["evidence_ref"]="none",["selection_evidence"]=new JArray() };
        Check(!Compile(reply,out _,out why)&&why=="recording-adopted-choice-cannot-be-replaced","planner cannot erase adopted choice with a new refusal");
        reply["resolution"]="source_uncertain";reply["evidence_ref"]=QuoteRef(c,c.UserText);
        Check(Compile(reply,out output,out _)&&!output.Contains("缺")&&!output.Contains("只有")&&!output.Contains("<sing "),"uncertain current mapping does not invent missing recordings or action");
        reply["speech"]["text"]="只有两段，第三段没录到";
        Check(!Compile(reply,out _,out _),"planner authored absent recording facts fail closed");reply["speech"]["text"]="";
        c.CurrentIntent["roleProposal"]=JValue.CreateNull();
        Check(RecordingSingingDecisionCompiler.ResponseFormat(c)!=null,"null role proposal is valid data, not indexed as an object");
        reply["resolution"]="role_declined";reply["evidence_ref"]="none";
        Check(Compile(reply,out output,out _)&&!output.Contains("<sing "),"unadopted role refusal remains available");
        c.CurrentIntent["roleProposal"]=new JObject{["task"]="none",["text"]="今天不想唱，但我想保留自己的说法 & 情绪。"};
        Check(Compile(reply,out output,out _)&&output.Contains("今天不想唱")&&output.Contains("&amp;"),"existing refusal wording and XML data escaping preserved");
        c.CurrentInput["capture"]=0;c.CurrentInput["clip"]=JValue.CreateNull();
        Check(RecordingSingingDecisionCompiler.Prompt(c).StartsWith(RecordingSingingDecisionCompiler.GroundedSourceContract),"text input still uses grounded contract and retained audio");
        var facts=RecordingSingingDecisionCompiler.PromptFacts(c);var first=(JObject)facts["materials"][0];
        Check(first["recorded_content"]["transcript"]!=null&&first["description"]["RecordingTranscript"]==null,"content is complete in one stable-key column, not duplicated");
        Check(!RecordingSingingDecisionCompiler.GroundedSourceContract.StartsWith(RecordingSingingDecisionCompiler.SourceContract),"v3 policy does not concatenate conflicting prior free-speech policy");
        var h=(JObject)reply.DeepClone();h["decision"]="handoff";h["resolution"]="catalog_search";h["evidence_ref"]=QuoteRef(c,c.UserText);
        Check(Compile(h,out output,out _)&&output==RecordingSingingDecisionCompiler.Handoff,"declared handoff is only a proposal for independent boundary review");
        var r=new JObject{["verdict"]="supported_here",["operation"]="catalog_search",["evidence_ref"]="none",["sources"]=new JArray()};
        Check(RecordingCapabilityReview.TryRead(r.ToString(),h.ToString(),c,out var reviewed,out _)&& (string)reviewed["verdict"]=="supported_here","independent reviewer can return false catalog declaration to supported domain");
        Check(!RecordingCapabilityReview.TryConstrainCandidate("<song_search query=\"x\"/>",reviewed,c,out _,out _),"supported-here verdict is never a tool grant");
        r["verdict"]="required";r["evidence_ref"]=QuoteRef(c,c.UserText);
        Check(RecordingCapabilityReview.TryRead(r.ToString(),h.ToString(),c,out reviewed,out _),"typed required request remains explicit boundary evidence");
        Check(RecordingCapabilityReview.TryConstrainCandidate("<lang code=\"zh\"/><speech mode=\"independent\"/><say>我马上播放。</say><song_search query=\"known song\"/>",reviewed,c,out output,out _)&& !output.Contains("马上")&&output.Contains("song_search"),"private catalog adapter retains read-only query, discards unexecuted promise");
        Check(!RecordingCapabilityReview.TryConstrainCandidate("<song_sing song=\"x\"/>",reviewed,c,out _,out _),"catalog read cannot escalate to playback");
        r["operation"]="pitch_change";r["sources"]=new JArray("material_10");
        string goal="<sing_goal origin=\"user_request\" refs=\"clip:session-10\" range=\"clean\" intent=\"perform\" expected=\"first verse\" completion=\"playback\" revisions=\"0\" input_turn=\"19\" input_capture=\"0\" input_clip=\"none\" source_relation=\"retained_recording\" source_evidence=\""+c.UserText+"\" evidence=\""+c.UserText+"\"/>";
        Check(RecordingCapabilityReview.TryConstrainCandidate("<speech mode=\"after_action\"/><silent/>"+goal+"<sing refs=\"clip:session-10\" range=\"clean\" transpose=\"+whole_step\"/>",r,c,out output,out _),"existing pitch executor stays reachable as silent constrained candidate");
        Check(!RecordingCapabilityReview.TryConstrainCandidate("<speech mode=\"after_action\"/><silent/>"+goal+"<sing refs=\"clip:session-19\" range=\"clean\" transpose=\"+whole_step\"/>",r,c,out _,out _),"private adapter cannot switch stable audio source");
        r["operation"]="custom_range";
        Check(RecordingCapabilityReview.TryConstrainCandidate("<speech mode=\"after_action\"/><silent/>"+goal+"<sing refs=\"clip:session-10\" start_seconds=\"1.2\" end_seconds=\"8.1\"/>",r,c,out _,out _),"custom crop entry retained, range still awaits existing independent review");
        Check(!RecordingCapabilityReview.TryConstrainCandidate("<speech mode=\"after_action\"/><silent/>"+goal+"<sing refs=\"clip:session-10\" start_seconds=\"1.2\" end_seconds=\"8.1\"/><motion action=\"body_turn\" degrees=\"90\"/>",r,c,out _,out _),"recording capability cannot silently append independent body action");
        Check(!RecordingCapabilityReview.TryConstrainCandidate("<speech mode=\"after_action\"/><silent/>"+goal+"<sing refs=\"clip:session-10\" start_seconds=\"1.2\" end_seconds=\"8.1\" confirm_user=\"true\"/>",r,c,out _,out _),"capability syntax adapter cannot confirm source identity");
        return checks-before;
    }
    public static void Main(string[] args)
    {
        Console.WriteLine("PASS "+RunChecks()+" recording identity/evidence/budget checks");
        if(args.Length==2 && args[0]=="--export") File.WriteAllText(args[1],Cases().ToString());
        if(args.Length==2 && args[0]=="--export-convergence") File.WriteAllText(args[1],ConvergenceCases().ToString());
        if(args.Length==3 && args[0]=="--validate-production")
        {
            var cases=JArray.Parse(File.ReadAllText(args[1])); var results=JArray.Parse(File.ReadAllText(args[2]));
            foreach(JObject result in results)
            {
                var row=cases.OfType<JObject>().Single(r=>(string)r["id"]==(string)result["id"]);
                var context=ImportProductionContext((JObject)row["context"]);
                bool valid=RecordingSingingDecisionCompiler.TryCompile((string)result["raw"],context,out var compiled,out var reason);
                result["compilerAccepted"]=valid; result["compilerReason"]=reason; result["compiledOutput"]=compiled;
                result["semanticPass"]=false;
                if(valid)
                {
                    var plan=JObject.Parse((string)result["raw"]);
                    var keys=((JArray)plan["materials"]).Values<string>().ToArray();
                    var clips=keys.Select(k=>context.Materials.Single(m=>m.Key==k).ClipRef).ToArray();
                    result["selectedClips"]=new JArray(clips);
                    result["actualCompiledHandoff"]=compiled==RecordingSingingDecisionCompiler.Handoff;
                    result["actualCompiledSingingProposal"]=compiled!=RecordingSingingDecisionCompiler.Handoff && compiled.Contains("<sing_goal ") && compiled.Contains("<sing refs=");
                    var compiledRefs=System.Text.RegularExpressions.Regex.Match(compiled,"<sing refs=\"([^\"]+)\"");
                    var actualClips=compiledRefs.Success?compiledRefs.Groups[1].Value.Split(','):new string[0];
                    result["actualCompiledClips"]=new JArray(actualClips);
                    bool compatible=keys.All(k=>RecordingSingingDecisionCompiler.ProposalRanges(context.Materials.Single(m=>m.Key==k)).Contains((string)plan["range"]));
                    result["selectedRange"]=(string)plan["range"];
                    result["selectedRangeCompatible"]=(string)plan["decision"]=="perform"?new JValue(compatible):JValue.CreateNull();
                    result["semanticPass"]=(string)plan["decision"]==(string)row["expectedDecision"] &&
                        ((string)plan["decision"]!="perform" || (bool)result["actualCompiledSingingProposal"] && compatible && string.Join(",",actualClips)==(string)row["expectedClip"]);
                }
                result["validationScope"]="Actual production compiler and expected ordered clip identity. Exported production wire with reconstructed session/PCM; not independent approval, audio conversion or playback.";
            }
            File.WriteAllText(args[2],results.ToString());
            Console.WriteLine("Production semantic passes "+results.Count(r=>(bool)r["semanticPass"])+"/"+results.Count);
        }
        if(args.Length==2 && (args[0]=="--validate" || args[0]=="--validate-convergence"))
        {
            var results=JArray.Parse(File.ReadAllText(args[1])); var cases=args[0]=="--validate-convergence"?ConvergenceCases():Cases();
            foreach(JObject result in results)
            {
                var row=cases.OfType<JObject>().Single(r=>(string)r["id"]==(string)result["id"]);
                var context=CaseContexts[(string)row["id"]];
                bool valid=RecordingSingingDecisionCompiler.TryCompile((string)result["raw"],context,out var legacy,out var reason);
                result["compilerAccepted"]=valid;result["compilerReason"]=reason;
                result["compiledOutput"]=legacy;
                result["validationScope"]="Production compiler plus expected decision/ordered material/source-relation subset, and explicit repeat intent where specified. Independent semantic approval, actual audio boundaries, conversion and playback are not executed.";
                if(valid)
                {
                    var plan=JObject.Parse((string)result["raw"]);
                    var refs=RecordingSingingDecisionCompiler.QuoteReferences(context).OfType<JObject>().ToArray();
                    string sourceText=(string)refs.FirstOrDefault(q=>(string)q["ref"]==(string)plan["source_ref"])?["text"];
                    result["resolvedSourceQuote"]=sourceText;
                    result["resolvedRequestQuote"]=(string)plan["request_ref"]=="source"?sourceText:
                        (string)refs.FirstOrDefault(q=>(string)q["ref"]==(string)plan["request_ref"])?["text"];
                    result["independentGoalApprovalExecuted"]=false;
                    string relation=RecordingSingingDecisionCompiler.SourceRelation(context,context.Materials.Where(m=>((JArray)plan["materials"]).Any(t=>(string)t==m.Key)));
                    result["computedSourceRelation"]=relation;
                    result["decisionMaterialRelationSubsetPass"]=(string)plan["decision"]==(string)row["expectedDecision"] &&
                        ((string)plan["decision"]!="perform" || string.Join(",",((JArray)plan["materials"]).Select(t=>(string)t))==(string)row["expectedMaterial"] &&
                            relation==(string)row["expectedRelation"]);
                    result["expectedIntent"]=row["expectedIntent"]?.DeepClone();
                    result["intentChecked"]=(string)row["expectedIntent"]!=null;
                    result["intentPass"]=(string)row["expectedIntent"]==null || (string)plan["intent"]==(string)row["expectedIntent"];
                    result["semanticPass"]=(bool)result["decisionMaterialRelationSubsetPass"] && (bool)result["intentPass"];
                }
                else result["semanticPass"]=false;
            }
            File.WriteAllText(args[1],results.ToString());
            Console.WriteLine("Semantic passes "+results.Count(r=>(bool)r["semanticPass"])+"/"+results.Count);
        }
    }
}
