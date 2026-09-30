#if UNITY_EDITOR
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// Uses the existing retained-PCM fixture and actual goal parser/comparator.
// Verdicts are controlled; this is not a model review or a playback test.
public static partial class AgentSingingGoalRegression
{
    public static JObject RunRecordingReferenceChecks()
    {
        int before=checks;
        foreach(bool includeUnrequestedIntro in new[]{false,true})
        using(var f=new Fixture())
        {
            const string user="再试一次，把我刚才唱的这一段歌回唱给我听，只要新的歌声，不要前面的开场白。";
            f.Begin(user);f.StageCompletedEvidence(user,"新しい歌の断片",.4f,2.4f,1.9f,2.3f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
            var context=(RecordingSingingDecisionContext)Call(f.chat,"BuildRecordingSingingDecisionContext");
            string clip=(string)context.CurrentInput["clip"];
            var current=context.Materials.Single(m=>m.ClipRef==clip);
            var old=context.Materials.Single(m=>m.ClipRef==f.first);
            string quoteRef=(string)RecordingSingingDecisionCompiler.QuoteReferences(context).OfType<JObject>()
                .First(q=>(string)q["origin"]=="current"&&((string)q["text"]).Contains("不要前面的开场白"))["ref"];
            var raw=new JObject{["decision"]="perform",["source_ref"]=quoteRef,["request_ref"]="source",
                ["materials"]=includeUnrequestedIntro?new JArray(old.Key,current.Key):new JArray(current.Key),
                ["range"]="clean",["intent"]="repeat",["expected"]=includeUnrequestedIntro?"先唱前面的开场白，再唱新歌声":"只回唱新歌声",
                ["thought"]="",["speech"]=new JObject{["text"]="",["language"]="ja"}};
            Check(CompileRecordingFixture(raw, context,out string compiled,out string error),
                "New reference proposal did not compile: "+error);
            Check(!RoleOutputChannels.Parse(compiled).HasSpeech,"A proposal added an unapproved spoken performance promise.");
            Call(f.chat,"ExtractSingingGoalTags",new object[]{compiled,true});
            Check((bool)Call(f.chat,"HasPendingSingingGoalReview") && !(bool)Get(f.chat,"m_SingingRequestSubmittedSinceUserTurn"),
                "Compiling references bypassed the actual independent goal gate or submitted conversion.");
            string proposed=includeUnrequestedIntro?f.first+","+clip:clip;
            Check(!f.Resolve(proposed,"clean",out _),"A schema-valid selection became executable before independent approval.");
            var expected=new JObject{["origin"]="user_request",["request_quote"]=user,["refs"]=clip,
                ["range"]="clean",["source_relation"]="current_recording"};
            object[] compare={expected,""};
            bool matched=(bool)Call(f.chat,"TryMatchReviewedSingingGoalTarget",compare);
            Check(matched!=includeUnrequestedIntro,"Actual independent target comparator failed to distinguish requested song from extra leading material.");
            if(includeUnrequestedIntro)
            {
                Call(f.chat,"ApplySingingGoalReview","revise","受控复核：完整用户只要新歌声，不同意额外开场白。保留这个反例，不把编译通过算为唱对。");
                Check(!f.Resolve(proposed,"clean",out _),"Semantically revised intro-plus-song candidate was still executable.");
            }
            context.CurrentInput["capture"]=(int)context.CurrentInput["capture"]+1;
            Check(!CompileRecordingFixture(raw, context,out _,out error)&&error=="source-ref-not-current",
                "Same words in a different actual capture kept the old quote identity.");
        }
        return new JObject{["passed"]=true,["checks"]=checks-before,
            ["scope"]="Actual retained synthetic PCM, reference compiler, goal parser and independent target comparator; controlled review verdict; no network, conversion or playback."};
    }
}
#endif
