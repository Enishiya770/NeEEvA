#if UNITY_EDITOR
using System.Linq;
using Newtonsoft.Json.Linq;
public static partial class AgentSingingGoalRegression
{
    public static int RunGroundedCapabilityChecks()
    {
        int before=checks;
        foreach(string operation in new[]{"pitch_change","custom_range"})
        using(var f=new Fixture()) {
            f.Begin(operation=="pitch_change"?"把第一段升高一个全音再唱。":"只唱第一段原录音0.2秒到2.3秒的范围。");
            var context=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            Check(CompileRecordingFixture(MaterialPlan(context,f.first),context,out string original,out string error),error);
            var goalMatch=System.Text.RegularExpressions.Regex.Match(original,@"<sing_goal\b[^>]*>");
            var goal=System.Xml.Linq.XElement.Parse(goalMatch.Value);
            if(operation=="custom_range") { goal.SetAttributeValue("range","window");goal.SetAttributeValue("start_seconds","0.2");goal.SetAttributeValue("end_seconds","2.3"); }
            string sing=operation=="pitch_change"?$"<sing refs=\"{f.first}\" range=\"clean\" transpose=\"+whole_step\"/>":$"<sing refs=\"{f.first}\" start_seconds=\"0.2\" end_seconds=\"2.3\"/>";
            string candidate="<speech mode=\"after_action\"/><silent/>"+goal.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)+sing;
            var review=new JObject{["verdict"]="required",["operation"]=operation,["sources"]=new JArray(context.Materials.Single(m=>m.ClipRef==f.first).Key)};
            Check(RecordingCapabilityReview.TryConstrainCandidate(candidate,review,context,out string compiled,out error),error);
            var withoutSource=System.Text.RegularExpressions.Regex.Replace(candidate," source_relation=\"[^\"]*\"","");
            Check(!RecordingCapabilityReview.TryConstrainCandidate(withoutSource,review,context,out _,out _),"Typed candidate must preserve current-input source transaction.");
            string broadened=candidate.Replace("<sing refs=",operation=="pitch_change"?"<sing start_seconds=\"0.2\" end_seconds=\"2.3\" refs=":"<sing transpose=\"+whole_step\" refs=");
            Check(!RecordingCapabilityReview.TryConstrainCandidate(broadened,review,context,out _,out _),"Single reviewed operation cannot add another audio operation.");
            object[] register={compiled,true};Call(f.chat,"ExtractSingingGoalTags",register);
            Check((bool)Call(f.chat,"HasPendingSingingGoalReview"),"Constrained candidate did not register a real pending goal.");
            var target=new JObject{["origin"]="user_request",["request_quote"]=f.user,["refs"]=f.first,["range"]=operation=="custom_range"?"window":"clean",["source_relation"]="retained_recording"};
            if(operation=="custom_range") { target["start_seconds"]=.2;target["end_seconds"]=2.3; }
            object[] matching={target,""};Check((bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",matching),"Actual target review mismatch: "+matching[1]);f.Approve();
            object[] extract={sing};object request=Call(f.chat,"ExtractHumBackTag",extract);
            string validation=(string)request.GetType().GetField("ValidationError",Flags).GetValue(request);
            Check(string.IsNullOrEmpty(validation),"Production pitch/range parser rejected candidate: "+validation);
            object[] resolve={request,""};Check((bool)Call(f.chat,"TryResolveUnifiedSing",resolve),"Production source/range resolution rejected: "+resolve[1]);
        }
        return checks-before;
    }

    public static void ExportFollowthroughRecordingCases(string output,string repositoryRoot)
    {
        var input=JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(repositoryRoot,"work/runtime-followthrough-20260928/recording-log-fixture.json")));
        var rows=new JArray();
        foreach(JObject item in (JArray)input["cases"])
        using(var f=new Fixture()) {
            var context=new RecordingSingingDecisionContext { RequireGroundedResolution=true,UserText=(string)item["user"],
                CurrentInput=new JObject{["turn"]=30,["capture"]=0,["clip"]=JValue.CreateNull()},
                CurrentIntent=new JObject{["roleProposal"]=item["roleProposal"].DeepClone()},Facts="{}" };
            foreach(JObject row in (JArray)input["materials"]) context.Materials.Add(row.ToObject<RecordingSingingMaterial>());
            ExportMaterialRow(rows,f,context,(string)item["id"],"perform",(string)item["expectedRefs"],repositoryRoot);
            ((JObject)rows.Last)["fixtureProvenance"]=input["provenance"].DeepClone();
            ((JObject)rows.Last)["scope"]=input["provenance"]["scope"].DeepClone();
            ((JObject)rows.Last)["expectedClips"]=new JArray(((string)item["expectedRefs"]).Split(','));
        }
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output)));
        System.IO.File.WriteAllText(output,rows.ToString());
    }
    // Fixture-only migration: source identity/error cases keep their original
    // proposal, with the v3 content proof derived from that selected recording.
    // Actual HTTP response validators never call this helper.
    private static bool CompileRecordingFixture(JObject raw,RecordingSingingDecisionContext context,out string compiled,out string reason)
    {
        raw=(JObject)raw.DeepClone();
        if(context.RequireGroundedResolution) {
            raw["resolution"]=(string)raw["decision"]=="perform"?"matched":"source_uncertain";
            raw["evidence_ref"]="none";
            var proofs=new JArray();
            foreach(string key in ((JArray)raw["materials"]).Values<string>()) {
                var material=context.Materials.FirstOrDefault(m=>m.Key==key);
                var facts=material==null?new JObject():JObject.Parse(material.Description);
                string quote=(string)facts["SingingSegmentText"];
                if(string.IsNullOrEmpty(quote))quote=(string)facts["RecordingTranscript"]??"";
                proofs.Add(new JObject{["key"]=key,["quote"]=quote.Substring(0,System.Math.Min(48,quote.Length))});
            }
            raw["selection_evidence"]=proofs;
        }
        return RecordingSingingDecisionCompiler.TryCompile(raw.ToString(),context,out compiled,out reason);
    }
}
#endif
