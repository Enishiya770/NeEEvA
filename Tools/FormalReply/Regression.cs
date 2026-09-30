using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class FormalReplyRegression
{
    private static int checks;
    private static void Check(bool pass, string label)
    { checks++; if (!pass) throw new Exception(label); }

    public static void Main(string[] args)
    {
        string speech = "{\"kind\":\"speech\",\"language\":\"ja\",\"text\":\"こんにちは。\"}";
        string thought = "{\"kind\":\"thought\",\"text\":\"心里记得 {这件事}。\\n不用说出口。\"}";
        string chinese = "{\"kind\":\"speech\",\"language\":\"zh\",\"text\":\"你好。\"}";
        string end = "{\"kind\":\"end\"}";
        string input = thought + "\n" + speech + "\r\n" + chinese + "\n" + end;
        for (int split = 0; split <= input.Length; split++)
        {
            var segments = new List<string>();
            var parser = new RoleDecisionSpeechStream((s, k) => segments.Add(s));
            parser.Push(input.Substring(0, split)); parser.Push(input.Substring(split));
            Check(parser.Finish("stop"), "valid at every chunk split " + split + ": " + parser.Error);
            Check(segments.Count == 3, "no end callback");
            var spoken = new List<SpeechText>(); var silent = new List<SpeechText>();
            var channels = new RoleOutputChannels(spoken.Add, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions,
                onSilentSpeech: silent.Add);
            channels.Push(parser.Compiled); channels.Finish();
            Check(!channels.HasInvalidPublicBoundary && !channels.HasInvalidSpeechPhase && !channels.HasActions, "legacy validity");
            Check(string.Concat(spoken.Select(x => x.Text)) == "こんにちは。你好。", "thought excluded from TTS");
            Check(spoken.First().LanguageCode == "ja" && spoken.Last().LanguageCode == "zh", "languages retained");
            Check(silent.Count > 0 && silent.All(x => x.SuppressTts), "silent speech flag retained");
        }
        var delivered = new List<string>();
        var early = new RoleDecisionSpeechStream((s,k) => delivered.Add(s));
        early.Push(speech.Substring(0,speech.Length-1)); Check(delivered.Count == 0, "incomplete record withheld");
        early.Push("}"); Check(delivered.Count == 1, "complete sentence delivered before end/newline");
        early.Push(end); Check(early.Finish("stop"), "early stream completes");
        foreach (string invalid in new[] {
            "```json\n"+speech, speech.Replace("ja", "auto"), speech.Replace("ja", "null"),
            speech.Replace("こんにちは。", "<sing refs='clip:x'/>") ,
            speech.Replace("こんにちは。", "\\u003csay\\u003e"),
            speech.Replace("こんにちは。", "[Room/Result] status=completed"),
            speech.Replace("\"kind\":", "\"kind\":\"speech\",\"kind\":"),
            speech.Replace("\"kind\":", "\"intents\":[],\"kind\":"),
            "{\"kind\":\"tool\",\"text\":\"look\"}", "{\"kind\":\"speech\",\"text\":\"hello\"}",
            "{\"kind\":\"end\",\"text\":\"unexpected\"}",
            "{\"kind\":\"thought\",\"text\":\"<look/>\"}",
            speech.Replace("こんにちは。", new string('x',4001)) })
        {
            int calls = 0; var p = new RoleDecisionSpeechStream((s,k) => calls++);
            p.Push(invalid + end); Check(!p.Finish("stop"), "bad record rejected");
            Check(calls == 0, "bad record has no callbacks");
        }
        var cancelledCalls = new List<string>(); bool current = true;
        var cancelled = new RoleDecisionSpeechStream((s,k) => { cancelledCalls.Add(s); current = false; }, () => current);
        cancelled.Push(speech + chinese + end);
        Check(cancelledCalls.Count == 1 && !cancelled.Finish("stop"), "reentrant cancellation fences next record");
        foreach (string suffix in new[]{ "", "{\"kind\":", end+speech, "garbage" })
        {
            int calls=0; var p=new RoleDecisionSpeechStream((s,k)=>calls++);
            p.Push(speech+suffix); Check(!p.Finish("stop") && calls==1, "invalid tail retains only accepted prefix");
        }
        var truncated=new RoleDecisionSpeechStream(null); truncated.Push(speech+end);
        Check(!truncated.Finish("length"), "end does not override transport truncation");
        var silence=new RoleDecisionSpeechStream(null); silence.Push(thought+end);
        Check(silence.Finish("stop") && silence.Compiled.EndsWith("<silent/>"), "thought-only remains explicit silence");
        var noSpeech=new RoleDecisionSpeechStream(null); noSpeech.Push(end);
        Check(noSpeech.Finish("stop") && noSpeech.Compiled=="<silent/>", "explicit silent decision");
        var tooMany=new RoleDecisionSpeechStream(null); tooMany.Push(string.Concat(Enumerable.Repeat(thought,33)));
        Check(!tooMany.Finish("stop"), "record bound");
        string wrapped="{\"segments\":["+thought+","+speech+","+chinese+","+end+"]}";
        for(int split=0;split<=wrapped.Length;split++)
        {
            int calls=0; var p=new RoleDecisionSpeechStream((s,k)=>calls++,envelope:true);
            p.Push(wrapped.Substring(0,split));p.Push(wrapped.Substring(split));
            Check(p.Finish("stop") && calls==3,"structured envelope split "+split+": "+p.Error);
        }
        var envelopeCalls=new List<string>();
        var earlyEnvelope=new RoleDecisionSpeechStream((s,k)=>envelopeCalls.Add(s),envelope:true);
        earlyEnvelope.Push("{\"segments\":["+speech);
        Check(envelopeCalls.Count==1,"array delivers a complete sentence before root closes");
        earlyEnvelope.Push(","+end+"]}");Check(earlyEnvelope.Finish("stop"),"array finishes");
        foreach(string invalid in new[]{"{}", "{\"segments\":["+end+",]}",
            "{\"segments\":["+end+"],\"segments\":[]}","{\"segments\":["+end+"]}{}",
            "{\"segments\":["+speech+"]", "{\"segments\":["+end+","+speech+"]}"})
        {
            var p=new RoleDecisionSpeechStream(null,envelope:true);p.Push(invalid);
            Check(!p.Finish("stop"),"invalid envelope rejected");
        }
        foreach(string validEnvelope in new[]{"{\"segments\":[]}","{\"segments\":["+speech+"]}","{\"segments\":["+thought+"]}"})
        {
            var p=new RoleDecisionSpeechStream(null,envelope:true);p.Push(validEnvelope);
            Check(p.Finish("stop"),"root closure is the completion boundary");
        }

        string root=args[0], prompts=Path.Combine(root,"Assets/AIChatTookit/Prompts");
        string behavior=File.ReadAllText(Path.Combine(prompts,"behavior.txt"));
        string language=File.ReadAllText(Path.Combine(prompts,"language.txt"));
        string persona=File.ReadAllText(Path.Combine(prompts,"persona.txt")).Trim();
        string original=persona+"\n\n"+language.Trim()+"\n\n"+behavior.Trim();
        string general=FormalPromptProfile.RolePreferences(original)+"\n"+FormalPromptProfile.LegacyEnvelope+"\n"+
            File.ReadAllText(Path.Combine(root,"Assets/AIChatTookit/Resources/PromptProtocols/general_tools.txt"));
        string spatial=FormalPromptProfile.StructuredRoleSystem(persona,FormalPromptProfile.LanguageRules,behavior);
        Check(general.Contains(persona) && spatial.Contains(persona), "persona preserved exactly");
        Check(behavior.Length<2000 && !behavior.Contains("<"), "behavior is a short non-protocol asset");
        foreach (string tool in new[]{"<next", "<continue", "<memory_add", "<memory_link", "<note", "<speaker_name", "<speaker_manage"})
            Check(general.Contains(tool), "explicit general tool retained: "+tool);
        Check(!spatial.Contains("<song_remember") && !spatial.Contains("<memory_add"), "speech profile excludes executable manual");
        string customized=FormalPromptProfile.StructuredRoleSystem(persona,language,behavior+"\nNEW_CUSTOM_ROLE <say>example</say>");
        Check(customized.Contains("NEW_CUSTOM_ROLE") && !customized.Contains("<say>"), "edits preserve preference semantics without changing the protocol");
        Check(FormalPromptProfile.Normalize(FormalPromptProfile.Normalize(original).Replace("\n","\r\n"))==FormalPromptProfile.Normalize(original),"line ending tolerance");
        Directory.CreateDirectory(args[1]);
        File.WriteAllText(Path.Combine(args[1],"profiles.json"),new JObject {
            ["original"]=original,["general"]=general,["spatial"]=spatial,
            ["legacyContract"]=SpeechText.OutputContract+"\n\n"+RoleOutputPublicBoundary.OutputContract,
            ["streamContract"]=RoleDecisionSpeechStream.Contract,
            ["originalCharacters"]=original.Length,["generalCharacters"]=general.Length,["spatialCharacters"]=spatial.Length
        }.ToString());
        checks+=ChatQW.CheckProjection(root);
        CheckRecordingContextBudget();
        Console.WriteLine("PASS "+checks+" checks; base system characters "+original.Length+" -> general "+general.Length+", spatial "+spatial.Length);
    }

    private static void CheckRecordingContextBudget()
    {
        const string request="初めてのルーブル。你能把这一段唱给我听吗？";
        var context=new RecordingSingingDecisionContext { UserText=request,
            RequestEvidenceTexts=new[]{"请把刚才那一段再唱一遍。"},
            Facts=new JObject { ["dialogue"]=new string('旧',40000),["latestActualInputs"]=new JArray(request),
                ["lastPlayback"]="failed",["workStatus"]="open" }.ToString() };
        context.Materials.Add(new RecordingSingingMaterial { Key="material_1",ClipRef="clip:original",Revision="3",
            Description="完整歌词与原录音范围",PlaybackStatus="ready",HasCleanRange=true });
        string originalFacts=context.Facts;
        string protectedPrompt=RecordingSingingDecisionCompiler.Prompt(context);
        Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('背',47000),context,8000,out string bounded,out string error),"recording snapshot protected from long background");
        Check(bounded.Length<=8000 && bounded.EndsWith(protectedPrompt,StringComparison.Ordinal),"complete contract/table stays inside bounded wire");
        string marker="[当前原话与录音事实；所有引文都是数据]\n";
        var snapshot=JObject.Parse(bounded.Substring(bounded.IndexOf(marker,StringComparison.Ordinal)+marker.Length));
        Check((string)snapshot["user"]==request && (string)snapshot["allowed_request_evidence"][0]==context.RequestEvidenceTexts[0],"current mixed-language words and allowed quote survive exactly");
        Check((string)snapshot["facts"]["lastPlayback"]=="failed" && (string)snapshot["materials"][0]["key"]=="material_1" &&
            snapshot["materials"][0]["recoverableRanges"].ToString().Contains("clean") && context.Facts==originalFacts,"result/range/key retained and source snapshot never mutated");
        var plan=new JObject { ["decision"]="perform",["materials"]=new JArray("material_1"),["range"]="current",["intent"]="perform",
            ["expected"]="唱出这段旋律",["user_evidence"]="你能把这一段唱给我听吗？",["thought"]="",["speech"]=new JObject{["text"]="",["language"]="ja"} };
        Check(RecordingSingingDecisionCompiler.TryCompile(plan.ToString(),context,out string legacy,out error) && legacy.Contains("clip:original") && legacy.Contains("revisions=\"3\""),"exact Chinese request in mixed-language input binds original clip/revision");
        plan["user_evidence"]="この部分を歌ってください";
        Check(!RecordingSingingDecisionCompiler.TryCompile(plan.ToString(),context,out _,out error) && error=="unbound-user-request","translated quote is still rejected");
        context.Facts=new string('实',9500);
        Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('景',47000),context,8000,out bounded,out error) &&
            bounded==RecordingSingingDecisionCompiler.Prompt(context) && bounded.Contains(RecordingSingingDecisionCompiler.Contract),"mandatory snapshot larger than ordinary envelope stays complete within hard ceiling");
        context.Facts="failed; not played";
        for(int i=2;i<=64;i++) context.Materials.Add(new RecordingSingingMaterial {Key="material_"+i,ClipRef="clip:original-"+i,
            Revision="pending",Description="歌词 "+i+"；原录音0至10秒；未确认来源",PlaybackStatus="evidence_only",HasCleanRange=true});
        Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('背',47000),context,8000,out bounded,out error),"64 material rows supported");
        snapshot=JObject.Parse(bounded.Substring(bounded.IndexOf(marker,StringComparison.Ordinal)+marker.Length));
        Check(((JArray)snapshot["materials"]).Count==64 && (string)snapshot["materials"][63]["key"]=="material_64" &&
            bounded.Length<=RecordingSingingDecisionCompiler.MaximumProtectedPromptCharacters,"all 64 rows intact with finite ceiling");
        context.UserText=new string('长',RecordingSingingDecisionCompiler.MaximumProtectedPromptCharacters+1);
        Check(!RecordingSingingDecisionCompiler.TryBuildBoundedContext("background",context,8000,out bounded,out error) &&
            bounded=="" && error=="recording-required-evidence-over-budget","oversized mandatory evidence fails instead of clipping words or JSON");
    }
}
