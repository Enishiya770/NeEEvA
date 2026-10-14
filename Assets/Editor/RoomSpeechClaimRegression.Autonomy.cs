using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechClaimRegression
{
    private static IEnumerator CheckAutonomyPremiseTransport(string directory)
    {
        var host=new GameObject("AutonomyPremiseLoopback");host.SetActive(false);
        var model=host.AddComponent<ChatQW>(); model.enabled=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_PrewarmPrefixOnStart=false;model.m_LogRequestStats=false;
        using(var server=new Server())
        try
        {
            model.m_LocalUrl=server.Url;host.SetActive(true);typeof(LLM).GetField("url",Flags).SetValue(model,server.Url);
            var input=new JObject { ["autonomous"]=true, ["currentUserText"]="", ["autonomousIntention"]="Respond to the user's confession.",
                ["recentDialogue"]=new JArray(new JObject { ["role"]="user",["quotedText"]="Check the microphone and then return here." }) };
            foreach(string verdict in new[]{"unsupported","uncertain","supported","no-factual-premise","missing"})
            {
                var decision=new JObject { ["intent"]="none",["origin"]="none",["evidence"]="" };
                if(verdict!="missing") decision["autonomyPremise"]=new JObject { ["verdict"]=verdict,
                    ["claim"]=verdict=="no-factual-premise"?"":"the user's confession",
                    ["source"]=verdict=="supported"?"dialogue:0":"",["quote"]=verdict=="supported"?"I love you":"",
                    ["reason"]=verdict=="no-factual-premise"?"A prospective topic chosen by the character.":"No supplied user confession." };
                if(verdict=="no-factual-premise")input["autonomousIntention"]="Choose a new topic about this afternoon.";
                else input["autonomousIntention"]="Respond to the user's confession.";
                var exchange=server.Serve(decision.ToString()); bool done=false,ok=false;string output=null,status=null;
                model.PostRoomTaskMessage(input.ToString(),false,(a,b,c)=>{ok=a;output=b;status=c;done=true;});
                yield return Until(()=>done,"autonomous premise verdict");
                Check(ok==(verdict=="no-factual-premise"),"Actual transport rejects absent/uncertain/unsupported/unmatched premise evidence: "+verdict);
                Check(ok ? !output.Contains("autonomyPremise") && RoomTaskProtocol.TryAutonomousDecision(output,(string)input["autonomousIntention"],false,out _,out _) :
                    status==AutonomyPremiseProtocol.Rejected && output=="", "Rejected premise cannot reach action parsing or public writing.");
                Check(exchange.Request.Contains("autonomyPremise") && exchange.Request.Contains("factual presupposition"),"Production native schema and independent planner contract carry the premise gate.");
                File.WriteAllText(Path.Combine(directory,"autonomy-premise-"+verdict+".request.json"),exchange.Request);
            }
            var supported=new JObject { ["intent"]="none",["origin"]="none",["evidence"]="",
                ["autonomyPremise"]=new JObject { ["claim"]="the user's earlier microphone question",["verdict"]="supported",["source"]="dialogue:0",["quote"]="Check the microphone",["reason"]="The recent user asked for a microphone check." } };
            input["autonomousIntention"]="Reflect on the user's earlier microphone question.";
            Check(AutonomyPremiseProtocol.TryResolve(input.ToString(),false,supported.ToString(),out _,out _),"A real quoted non-device premise remains usable.");
            Check(server.Count==5 && model.m_DataList.Count==0,"No extra request, history entry or tool execution is created by checking a premise.");
        }
        finally { model.CancelRoomTaskPlanningMessage();UnityEngine.Object.DestroyImmediate(host); }
    }
}
