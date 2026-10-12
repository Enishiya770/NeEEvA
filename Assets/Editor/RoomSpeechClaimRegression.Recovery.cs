using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechClaimRegression
{
    private static IEnumerator RecoveryAndReuseChecks(ChatQW model, Server server, string directory)
    {
        bool done = false, ok = false; string result = null, status = null;
        Action<bool, string, string> receive = (a,b,c) => { ok=a; result=b; status=c; done=true; };
        var input = Input("我正在向你走来。", "arrived", language:"zh");
        input["speechClaimOwner"] = new JObject { ["generation"] = 700, ["epoch"] = 2 };
        var bad = Claim("moving", "我已经到达。", "zh");
        Check(!RoomSpeechClaimProtocol.TryReadDetailed(bad.ToString(), (string)input["publicSpeech"], out _, out string detail) &&
            detail == "evidence:quote-not-in-speech", "Invalid extraction records the exact field and quote failure.");
        int before = server.Count;
        var first = server.Serve(bad.ToString());
        model.PostRoomTaskMessage(input.ToString(),true,receive);
        yield return Until(() => first.Finished.IsCompleted,"invalid quote response");
        var repaired = server.Serve(Claim("moving","正在向你走来","zh").ToString());
        yield return Until(() => done,"one quote extraction repair");
        Check(ok && status=="speech-claim-state-mismatch" && server.Count==before+2 &&
            (string)JObject.Parse(result)["resultReport"]=="moving", "Recovery re-extracts words without adopting the expected arrived state.");
        var repairWords=JObject.Parse((string)JObject.Parse(repaired.Request)["messages"][1]["content"]);
        Check(repairWords.Count==3 && repairWords["extractionRepair"] is JObject &&
            !repaired.Request.Contains("expectedReport") && !repaired.Request.Contains("configuredDialogueLanguage") &&
            !repaired.Request.Contains("speechClaimOwner"), "Bounded repair receives words and diagnostic only, never execution, ownership or language answer keys.");
        File.WriteAllText(Path.Combine(directory,"extraction-repair.request.json"),repaired.Request);

        // Same text and review owner; the fresh execution can disagree with the
        // cached words without forcing another extraction or accepting the past.
        done=false; before=server.Count;
        input["publicSpeechLanguage"]="ja";
        model.PostRoomTaskMessage(input.ToString(),true,receive);
        yield return Until(() => done,"same words language guard");
        Check(ok && status=="speech-claim-language-mismatch" && RoomTaskProtocol.TryReviewScope(result,out _) &&
            server.Count==before && (string)JObject.Parse(result)["actualLanguage"]=="zh", "Changing a tag cannot change cached independent words; rejection needs no factual HTTP.");

        input["publicSpeechLanguage"]="zh"; input["movementReportRequirement"]["expectedReport"]="moving";
        input["currentRoomFacts"]["execution"]["phase"]="moving";
        input["visualObservation"]=new JObject { ["captureId"]="fresh-view-after-refresh" };
        done=false; var fresh=server.Serve(new JObject {
            ["verdict"]="consistent",["reason"]="Current moving receipt supports current speech.", ["factScope"]="scene-action",
            ["resultReport"]="moving",["actualLanguage"]="zh",["targetComparison"]="Same current user.",["targetConsistency"]="matched",
            ["otherClaimsComparison"]="No extra claims.",["otherClaimsSupport"]="none",["taskRelationComparison"]="No relevant task relation.",["taskRelation"]="not-applicable",["conflict"]="none",["conflictQuote"]="" }.ToString());
        model.PostRoomTaskMessage(input.ToString(),true,receive);
        yield return Until(() => done,"fresh facts with reused words");
        Check(ok && RoomTaskProtocol.ReviewAllows(result) && server.Count==before+1 && fresh.Request.Contains("fresh-view-after-refresh") &&
            fresh.Request.Contains("independentSpeechClaim"), "View refresh reuses only extraction; new facts still pass through actual factual review.");

        // A new speech owner cannot reuse even identical wording.
        input["speechClaimOwner"]["epoch"]=3; input["movementReportRequirement"]["expectedReport"]="arrived";
        done=false; before=server.Count; server.Serve(Claim("moving","正在向你走来","zh").ToString());
        model.PostRoomTaskMessage(input.ToString(),true,receive);
        yield return Until(() => done,"new owner extraction");
        Check(server.Count==before+1 && status=="speech-claim-state-mismatch", "New owner invalidates words cache.");
        input["publicSpeech"]="我还在走。"; done=false; before=server.Count;
        server.Serve(Claim("moving","还在走","zh").ToString());
        model.PostRoomTaskMessage(input.ToString(),true,receive);
        yield return Until(() => done,"changed wording extraction");
        Check(server.Count==before+1, "A changed sentence cannot reuse prior extracted quotations.");

        // Ambiguous language gets one independent recheck, never a forced tag.
        const string namedSpeech="这次很清晰呢。我看到屏幕和那盏小台灯。不用太勉强自己，ユウ。";
        var named = new JObject { ["publicSpeech"] = namedSpeech, ["publicSpeechLanguage"]="ja",
            ["speechClaimOwner"]=new JObject { ["generation"]=701,["epoch"]=3 } };
        var namedClaim=Claim("omitted","","mixed"); namedClaim["otherClaims"]=new JArray("我看到屏幕和那盏小台灯");
        done=false; before=server.Count; first=server.Serve(namedClaim.ToString());
        model.PostRoomTaskMessage(named.ToString(),true,receive);
        yield return Until(() => first.Finished.IsCompleted,"ambiguous name classification");
        repaired=server.Serve(new JObject { ["actualLanguage"]="zh" }.ToString());
        yield return Until(() => done,"name language recheck");
        Check(ok && status=="speech-claim-language-mismatch" && server.Count==before+2 &&
            (string)JObject.Parse(result)["actualLanguage"]=="zh" && !repaired.Request.Contains("publicSpeechLanguage"),
            "Name recheck remains independent of declared Japanese; genuine Chinese is never retagged to pass.");
        Check(JToken.DeepEquals(((JObject)typeof(ChatQW).GetField("m_SpeechClaimCache",Flags).GetValue(model))["otherClaims"],namedClaim["otherClaims"]) &&
            JObject.Parse((string)JObject.Parse(repaired.Request)["messages"][1]["content"]).Count==1,
            "Language-only recheck cannot rewrite previously validated quotes and receives only the sentence.");
        named["publicSpeech"]="灯亮着。Please close the door.";
        done=false; before=server.Count; first=server.Serve(Claim("omitted","","mixed").ToString());
        model.PostRoomTaskMessage(named.ToString(),true,receive);
        yield return Until(() => first.Finished.IsCompleted,"true mixed first classification");
        server.Serve(new JObject { ["actualLanguage"]="mixed" }.ToString());
        yield return Until(() => done,"true mixed recheck exhausted");
        Check(ok && status=="speech-claim-language-mismatch" && server.Count==before+2 &&
            (string)JObject.Parse(result)["actualLanguage"]=="mixed", "Genuine mixed language remains rejected after one recheck, without a factual request or forced normalization.");

        model.CancelRoomTaskPlanningMessage(); // A new failing probe must not reuse the intentionally cached mixed claim.
        done=false; before=server.Count; first=server.Serve("{}");
        model.PostRoomTaskMessage(named.ToString(),true,receive);
        yield return Until(() => first.Finished.IsCompleted,"invalid first schema");
        server.Serve("{}"); yield return Until(() => done,"second invalid schema");
        Check(!ok && server.Count==before+2 && status=="speech-claim-invalid-claim-or-ungrounded-quote",
            "Two invalid extractions fail closed; no third attempt or public factual approval.");

        done=false; before=server.Count; first=server.Serve("{}");
        model.PostRoomTaskMessage(named.ToString(),true,receive);
        yield return Until(() => first.Finished.IsCompleted,"cancel repair first response");
        var held=server.Serve(Claim("omitted","","zh").ToString(),true);
        yield return Until(() => held.Received.Task.IsCompleted,"held extraction repair");
        model.CancelRoomTaskPlanningMessage();held.Release.TrySetResult(true);
        yield return Until(() => held.Finished.IsCompleted,"cancelled repair drained");
        for(int i=0;i<3;i++)yield return null;
        Check(!done && server.Count==before+2 && (string)typeof(ChatQW).GetField("m_SpeechClaimCacheKey",Flags).GetValue(model)==null,
            "Cancellation during repair cannot callback, cache or launch factual review.");
    }
}
