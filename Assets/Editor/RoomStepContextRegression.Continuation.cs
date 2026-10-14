using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomStepContextRegression
{
    private static int CheckGenericContinuationFocus(ChatQW model, string directory)
    {
        int checks=0;var rows=new JArray();var modelCases=new JArray();
        void Check(bool value,string reason){++checks;if(!value)throw new Exception("Continuation attention: "+reason);}
        var source=(JObject)JArray.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomStepContext.latest-1238-20261014.json"))[0]["planningInput"];
        foreach(string kind in new[]{"observation","other"}) {
            var input=(JObject)source.DeepClone();
            string first=kind=="observation"?"检查桌上的物件":"保存录音", answer=kind=="observation"?"告诉我检查结果":"告诉我保存位置";
            string request=first+"，然后跟着我走，再"+answer+"。";
            input["currentUserText"]=request;input["latestUserText"]=request;
            input["workContinuation"]["originalRequest"]=request;input["taskSequence"]["request"]=request;
            var steps=(JArray)input["taskSequence"]["steps"];
            steps[0]["kind"]=kind;steps[0]["desiredState"]=null;steps[0]["quote"]=first;
            var receipt=new JObject { ["actionId"]="generic-completed",["actor"]="CHARACTER",["phase"]="completed",
                ["observedUtc"]="2026-10-14T03:41:24Z",["result"]="answer retained",["futureConstraint"]=new JObject{["allowOverwrite"]=false} };
            steps[0]["receipt"]=receipt;steps[1]["kind"]="follow";steps[1]["quote"]="跟着我走";
            steps.Add(new JObject{["id"]="s3",["kind"]="other",["quote"]=answer,["desiredState"]=null,["after"]=new JArray("s2"),["status"]="pending"});
            input["answerRequired"]=true; input["unknownConstraint"]="不得改动原始文件";
            string intact=input.ToString(Formatting.None);
            string wire=(string)Call(model,"BuildRoomTaskRequestJsonWithImage",intact,"data:image/png;base64,AA==",false);
            var payload=JObject.Parse(wire);object[] find={payload,null,null};
            Check((bool)Call(null,"TryFindRoomTaskInput",find),"wire fact packet exists");var sent=(JObject)find[2];
            Check((string)sent.SelectToken("planningEvidenceScope.phase")=="approved-continuation","Non-device predecessor admits focused current body step");
            Check(JToken.DeepEquals(sent.SelectToken("taskSequence.steps[0].receipt"),receipt)&&
                (string)sent.SelectToken("taskSequence.steps[2].status")=="pending"&&(bool?)sent["answerRequired"]==true&&
                (string)sent["unknownConstraint"]=="不得改动原始文件"&&input.ToString(Formatting.None)==intact,
                "Generic receipt, pending independent answer and unknown constraints survive without changing memory");
            object[] budget={wire,false,null,null};
            Check((bool)Call(null,"TryBudgetRoomDecisionRequest",budget),"Generic continuation fits final unchanged request budget");
            modelCases.Add(new JObject{["name"]="generic-"+kind,["utterance"]=request,["expectedStep"]="s2",["expectedIntent"]="follow",["request"]=JObject.Parse((string)budget[2])});
            steps[0]["status"]="failed";
            string denied=(string)Call(model,"BuildRoomTaskRequestJsonWithImage",input.ToString(Formatting.None),"data:image/png;base64,AA==",false);
            object[] blocked={JObject.Parse(denied),null,null};Call(null,"TryFindRoomTaskInput",blocked);
            Check(((JObject)blocked[2])["planningEvidenceScope"]==null,"Failed predecessor cannot use continuation shortcut");
            rows.Add(new JObject{["kind"]=kind,["passed"]=true,["wireCharacters"]=wire.Length});
        }
        File.WriteAllText(Path.Combine(directory,"generic-continuation.json"),rows.ToString());
        File.WriteAllText(Path.Combine(directory,"generic-continuation-model-cases.json"),modelCases.ToString());
        return checks;
    }
}
