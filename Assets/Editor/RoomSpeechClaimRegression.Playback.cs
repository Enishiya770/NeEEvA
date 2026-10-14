using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechClaimRegression
{
    private static IEnumerator PlaybackExtractionChecks(ChatQW model, Server server, string directory)
    {
        const string off = "テレビ台のランプは、正常に消灯しました。";
        const string on = "小灯は正常に点灯しています。";
        var eventClaim = new JObject { ["quote"] = off.TrimEnd('。'), ["actor"] = "CHARACTER", ["operation"] = "set-state",
            ["state"] = "off", ["outcome"] = "completed", ["time"] = "historical-event" };
        var contradictory = Claim("on", "正常に消灯しました"); contradictory["eventClaims"] = new JArray(eventClaim);
        Check(!RoomSpeechClaimProtocol.TryReadDetailed(contradictory.ToString(), off, out _, out string failure) &&
            failure == "resultReport:event-state-contradiction-for-same-quote", "The logged gen7 contradiction is an extractor failure, not evidence that the correct speech is false.");
        var distinct = Claim("on", "Now the fan is on.", "en");
        distinct["eventClaims"] = new JArray(new JObject { ["quote"] = "I turned the fan off earlier.", ["actor"] = "CHARACTER",
            ["operation"] = "set-state", ["state"] = "off", ["outcome"] = "completed", ["time"] = "historical-event" });
        Check(RoomSpeechClaimProtocol.TryRead(distinct.ToString(), "I turned the fan off earlier. Now the fan is on.", out _),
            "Different temporal predicates remain compatible; historical off does not contradict current on.");
        Check(RoomSpeechClaimProtocol.TryRead(distinct.ToString(), "I turned the fan off earlier. Now the fan is on. The user turned another fan off.", out _),
            "Unrelated actor/object text cannot supply a current-result contradiction.");
        var rows = new JArray();
        foreach (string test in new[] { "state-self-conflict", "confident-language-error", "exhausted-self-conflict", "true-language-switch" })
        {
            model.CancelRoomTaskPlanningMessage();
            bool done = false, ok = false; string result = null, status = null; int count = server.Count;
            var input = Input(test.Contains("self-conflict") ? off : test == "true-language-switch" ? "小灯已经亮了。" : on,
                test.Contains("self-conflict") ? "off" : "on", "device");
            var firstClaim = test.Contains("self-conflict") ? contradictory : Claim("on", (string)input["publicSpeech"], "zh");
            var first = server.Serve(firstClaim.ToString());
            model.PostRoomTaskMessage(input.ToString(), true, (a,b,c) => { ok=a; result=b; status=c; done=true; });
            yield return Until(() => first.Finished.IsCompleted, test + " first extraction");
            var corrected = (JObject)contradictory.DeepClone(); corrected["resultReport"] = "off";
            string repair = test == "state-self-conflict" ? corrected.ToString() : test == "exhausted-self-conflict" ? contradictory.ToString() :
                new JObject { ["languageReason"] = test == "true-language-switch" ? "已经 and 了 form a Chinese completion clause." : "は and しています form Japanese clause grammar.",
                    ["actualLanguage"] = test == "true-language-switch" ? "zh" : "ja" }.ToString();
            var second = server.Serve(repair);
            yield return Until(() => second.Finished.IsCompleted, test + " bounded extraction recheck");
            var repairWords = JObject.Parse((string)JObject.Parse(second.Request)["messages"][1]["content"]);
            Check(repairWords["publicSpeechLanguage"] == null && repairWords["expectedReport"] == null &&
                !second.Request.Contains("completionEvidence"), "Recovery has no execution or declared-language answer key: " + test);
            if (test == "state-self-conflict" || test == "confident-language-error")
            {
                var review = new JObject { ["verdict"] = "consistent", ["reason"] = "Exact state and target match the current receipt.",
                    ["factScope"] = "scene-action", ["resultReport"] = input["sceneActionFacts"]["resultReportRequirement"]["expectedReport"].DeepClone(),
                    ["actualLanguage"] = "ja", ["targetComparison"] = "Same validated object.", ["targetConsistency"] = "matched",
                    ["otherClaimsComparison"] = "No additional claims.", ["otherClaimsSupport"] = "none",
                    ["taskRelationComparison"] = "Character reports its own result.", ["taskRelation"] = "aligned", ["conflict"] = "none", ["conflictQuote"] = "" };
                if (test == "state-self-conflict") review["eventBindings"] = new JArray(new JObject { ["eventId"] = "owned", ["targetMatch"] = "matched" });
                server.Serve(review.ToString());
                yield return Until(() => done, test + " factual review");
                Check(ok && RoomTaskProtocol.ReviewAllows(result) && server.Count == count + 3,
                    "Correct original wording survives bounded extraction repair and normal factual checks: " + test);
            }
            else {
                yield return Until(() => done, test + " rejection");
                Check(server.Count == count + 2 && (test == "exhausted-self-conflict" ? !ok : ok && status == "speech-claim-language-mismatch"),
                    "A repeated extraction failure or actual language change cannot force acceptance: " + test);
            }
            rows.Add(new JObject { ["name"] = test, ["status"] = status, ["response"] = result, ["httpRequests"] = server.Count-count,
                ["speechRewritten"] = false, ["actionsExecuted"] = false });
        }
        File.WriteAllText(Path.Combine(directory, "playback-extraction.json"), rows.ToString());
        yield return CompoundExtractionChecks(model, server, directory);
    }

    private static IEnumerator CompoundExtractionChecks(ChatQW model, Server server, string directory)
    {
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/Continuation.latest-1238-20261014.json"));
        var recorded = JObject.Parse((string)fixture.SelectToken("compoundExtraction.data.rejectedOutput"));
        var rows = new JArray();
        foreach (string test in new[] { "recorded-two-defects", "unverified-language-after-repair", "nonmovement-quote-and-language" })
        {
            model.CancelRoomTaskPlanningMessage();
            bool done=false,ok=false;string result=null,status=null;int count=server.Count;
            bool generic=test=="nonmovement-quote-and-language";
            string speech=generic?"録音ファイルを保存しました。":"小灯は正常に消灯しました。";
            var input=Input(speech,"off","device");
            var bad=(JObject)recorded.DeepClone();
            if(generic) {
                input["sceneActionFacts"]=null;input["intendedAction"]="none";
                input["requestedUserText"]="录音保存到哪里了？";
                bad=Claim("omitted","","zh");bad["otherClaims"]=new JArray("録音ファイルを削除しました");
                bad["languageReason"]="Chinese completed statement.";
            }
            var first=server.Serve(bad.ToString());
            model.PostRoomTaskMessage(input.ToString(),true,(a,b,c)=>{ok=a;result=b;status=c;done=true;});
            yield return Until(()=>first.Finished.IsCompleted,test+" first");
            var repaired=(JObject)bad.DeepClone();repaired["actualLanguage"]=test=="unverified-language-after-repair"?"zh":"ja";
            repaired["languageReason"]="を and しました are Japanese clause particles and inflection.";
            if(generic) repaired["otherClaims"]=new JArray("録音ファイルを保存しました");else repaired["resultReport"]="off";
            var second=server.Serve(repaired.ToString());
            yield return Until(()=>second.Finished.IsCompleted,test+" repair");
            File.WriteAllText(Path.Combine(directory,test+"-repair-request.json"),second.Request);
            var words=JObject.Parse((string)JObject.Parse(second.Request)["messages"][1]["content"]);
            var repair=(JObject)words["extractionRepair"];
            Check(((JArray)repair["validationIssues"]).Count==2 && repair.ToString().Contains("recheck-substantive-clause-grammar"),
                "One bounded recovery carries both extraction defects, including when the original claim is invalid.");
            Check(repair["rejectedOutput"]==null&&words["publicSpeechLanguage"]==null&&
                !second.Request.Contains("completionEvidence"),"Full repair is not anchored by the wrong label or supplied an execution/language answer.");
            if(test=="unverified-language-after-repair") {
                yield return Until(()=>done,"uncertain extraction terminates");
                Check(!ok&&string.IsNullOrEmpty(result)&&status=="speech-claim-language-unverified"&&server.Count==count+2,
                    "Recovery exhaustion is diagnostic uncertainty, never a fabricated inconsistent verdict or third retry.");
            } else {
                var review=new JObject { ["verdict"]="consistent",["reason"]="Controlled facts support each literal claim.",
                    ["factScope"]="scene-action",["resultReport"]=generic?"omitted":"off",["actualLanguage"]="ja",
                    ["targetComparison"]="Same requested target.",["targetConsistency"]="matched",
                    ["otherClaimsComparison"]="The recorded file-save event supports the stated action.",["otherClaimsSupport"]="supported",
                    ["taskRelationComparison"]="Character reports its result.",["taskRelation"]="aligned",["conflict"]="none",["conflictQuote"]="" };
                if(!generic)review["eventBindings"]=new JArray(new JObject{["eventId"]="owned",["targetMatch"]="matched"});
                server.Serve(review.ToString());yield return Until(()=>done,test+" review");
                Check(ok&&RoomTaskProtocol.ReviewAllows(result)&&server.Count==count+3,"Both defects recover before normal fact validation: "+test);
            }
            rows.Add(new JObject{["name"]=test,["status"]=status,["response"]=result,["requests"]=server.Count-count});
        }
        File.WriteAllText(Path.Combine(directory,"compound-extraction.json"),new JObject{["source"]=fixture["source"],
            ["sourceSha256"]=fixture["sourceSha256"],["cases"]=rows}.ToString());
    }
}
