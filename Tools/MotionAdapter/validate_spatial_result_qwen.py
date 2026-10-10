"""Prepare or single-shot replay terminal results through compiled production payload/parser.

No Unity, actions, retry, service startup or endpoint fallback. The JSON fixture
contains real-log-derived unprojected facts, not a captured original wire. Other
outcomes are explicitly derived controls. Responses still need semantic review;
a valid envelope alone is never scored as a correct task result.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, MONO, NEWTONSOFT, PROTOCOL
from validate_standing_goal_planner_qwen import method

POLICY = "Assets/AIChatTookit/Scripts/LLM/SpatialGoalResultContextPolicy.cs"
PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.SpatialGoalResult.cs"
CORE = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs"

def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def compile_probe(output):
    folder = output / "parser"
    folder.mkdir()
    shutil.copy2(ROOT / NEWTONSOFT, folder / "Newtonsoft.Json.dll")
    slot = method((ROOT / CORE).read_text(encoding="utf-8-sig"), "private void AppendSlot(")
    source = r'''
using System; using System.IO; using System.Text; using System.Collections.Generic;
using Newtonsoft.Json; using Newtonsoft.Json.Linq;
namespace UnityEngine { public static class Debug { public static void Log(object x) { Console.Error.WriteLine(x); } public static void LogWarning(object x) { Console.Error.WriteLine(x); } } }
public sealed class TextAsset { public string name, text; }
public partial class ChatQW {
 public sealed class SendData { public string role,content; }
 public enum BackendType { Local, Cloud }
 private BackendType m_Backend=BackendType.Local;
 private TextAsset[] m_PromptFiles; private string m_SystemSetting="",lan="ja";
 private string CurrentModelName="antoneva"; private const int k_SlotAuxiliary=1;
 SLOT
 public string Build(string input,string root) {
  var data=JObject.Parse(File.ReadAllText(input));
  m_PromptFiles=new[]{new TextAsset{name="persona",text=File.ReadAllText(Path.Combine(root,"Assets/AIChatTookit/Prompts/persona.txt"))},new TextAsset{name="language",text=File.ReadAllText(Path.Combine(root,"Assets/AIChatTookit/Prompts/language.txt"))}};
  string context=SpatialGoalResultContextPolicy.BuildContext((string)data["originalUserRequest"],data["facts"].ToString(Formatting.None));
  string wire=BuildSpatialGoalResultRequestJsonForMessages(null,false,context,"",null);
  var facts=(JObject)data["facts"]; var projected=SpatialGoalResultContextPolicy.ProjectResultFacts(facts);
  return new JObject{["wire"]=wire==null?null:JObject.Parse(wire),["expectedReport"]=SpatialGoalResultContextPolicy.RequiredReport(facts),["projectedReport"]=SpatialGoalResultContextPolicy.RequiredReport(projected),["projectedFacts"]=projected,["sourceFactsEstimate"]=SpatialGoalResultContextPolicy.EstimateTextTokens(facts.ToString(Formatting.None)),["projectedFactsEstimate"]=SpatialGoalResultContextPolicy.EstimateTextTokens(projected.ToString(Formatting.None))}.ToString(Formatting.None);
 }
}
public static class Program { public static int Main(string[] args) {
 if(args[0]=="prepare") {Console.Write(new ChatQW().Build(args[1],args[2]));return 0;}
 if(args[0]=="review-contract") {Console.Write(JsonConvert.SerializeObject(new {contract=RoomTaskProtocol.ReviewContract,
   schema=args.Length>1?RoomTaskProtocol.BuildSpeechReviewSchemaForInput(File.ReadAllText(args[1])):RoomTaskProtocol.ReviewSchema}));return 0;}
 if(args[0]=="review") { var facts=JObject.Parse(File.ReadAllText(args[2]));
  Console.Write(JsonConvert.SerializeObject(new {allowed=SpatialGoalResultContextPolicy.CanReleaseGeneratedTerminalReport(File.ReadAllText(args[1]),facts),
    fallback=SpatialGoalResultContextPolicy.BuildTrustedReport(facts,args[3])}));return 0;}
 string normalized,reason;bool valid=SpatialGoalResultContextPolicy.TryValidateOutput(File.ReadAllText(args[1]),out normalized,out reason);
 Console.Write(JsonConvert.SerializeObject(new{valid,normalized,reason}));return 0;
} }
'''.replace(" SLOT", slot)
    adapter = folder / "ResultProbe.cs"
    adapter.write_text(source, encoding="utf-8")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"),
        "/nologo", "/langversion:9", "/r:" + str(folder / "Newtonsoft.Json.dll"),
        "/out:" + str(folder / "probe.exe"), str(ROOT / PROTOCOL), str(ROOT / POLICY),
        str(ROOT / PROVIDER), str(adapter)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (folder / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production result probe compilation failed; see parser/compile.log")

def probe(output, *args):
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *map(str,args)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    with (output / "parser/diagnostics.log").open("a",encoding="utf-8") as log:
        log.write(result.stderr)
    if result.returncode: raise RuntimeError(result.stderr)
    return json.loads(result.stdout.lstrip("\ufeff"))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=ROOT / "Assets/Editor/Fixtures/SpatialGoalResult.multi-step.json")
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--url",default="http://127.0.0.1:8080")
    parser.add_argument("--model")
    parser.add_argument("--execute-model",action="store_true")
    args=parser.parse_args(); output=args.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    fixture=json.loads(args.fixture.read_text(encoding="utf-8-sig"))
    sources={name:digest(ROOT/name) for name in (POLICY,PROVIDER,PROTOCOL,CORE,"Assets/AIChatTookit/Prompts/persona.txt","Assets/AIChatTookit/Prompts/language.txt")}
    report={"status":"preparing","sources":sources,"fixtureSha256":digest(args.fixture),"fixtureProvenance":fixture["provenance"],"modelRequests":0,"cases":[],"scope":"Production whole result policy/provider and AppendSlot body compiled unchanged with host-field stubs. No real scene execution or TTS. Envelope acceptance is not semantic correctness."}
    try:
        compile_probe(output)
        for case in ("real-log-off","derived-on","derived-no-search","derived-cancelled"):
            data=copy.deepcopy(fixture); data.pop("provenance")
            if case=="derived-on":
                data["originalUserRequest"]="请重新打开那盏灯。"; goal=data["facts"]["goal"]
                goal["originalRequest"]=goal["latestUserText"]=data["originalUserRequest"]
                goal["desiredState"]="on"; proof=goal["completionEvidence"]; proof["desiredState"]="on"
                proof["actual"].update(on=True,lightEnabled=True,emissionEnabled=True,lightState="enabled",emissionState="on")
                data["facts"]["lastAction"]["requestedState"]="on"
                # Drop conflicting off histories in this explicitly derived control.
                data["facts"].pop("runner",None); data["facts"].pop("objectContext",None); data["facts"].pop("latestGoalReview",None)
            elif case.startswith("derived-"):
                applied=case=="derived-cancelled"
                review={"relation":"cancel","verdict":"approved" if applied else "rejected","reviewApplied":applied,"cancellationApplied":applied,"reason":"The pending request was cleared." if applied else "Unapplied interpretation; no search has been dispatched."}
                data["facts"]={"goal":{"status":"cancelled" if applied else "waiting_clarification","registered":False,"ownerCurrent":False,"completionEvidenceValid":False,"originalRequest":data["originalUserRequest"],"latestUserText":"先不要管灯。","cancellationApplied":applied,"searchSummary":{"searchAttempted":False,"completedStepCount":0}},"lastAction":None,"latestGoalReview":review}
            input_path=output/(case+".input.json"); write(input_path,data)
            prepared=probe(output,"prepare",input_path,ROOT); wire=prepared.pop("wire")
            if wire is None or prepared["expectedReport"]!=prepared["projectedReport"]:
                raise RuntimeError("Projection/wire preflight failed for "+case)
            if args.model: wire["model"]=args.model
            write(output/(case+".request.json"),wire)
            entry={"id":case,"input":input_path.name,"request":case+".request.json",**prepared}
            if args.execute_model:
                if not all_slots_idle(request_json(args.url.rstrip("/")+"/slots")):
                    raise RuntimeError("Local model is busy; no request sent")
                report["modelRequests"]+=1
                envelope=request_json(args.url.rstrip("/")+"/v1/chat/completions",wire)
                write(output/(case+".response.json"),envelope)
                choice=envelope["choices"][0]; raw=choice.get("message",{}).get("content","")
                raw_path=output/(case+".output.txt"); raw_path.write_text(raw,encoding="utf-8")
                entry["outputValidation"]=probe(output,"parse",raw_path)
                entry["finishReason"]=choice.get("finish_reason")
                entry["envelopeAccepted"]=entry["outputValidation"]["valid"] and entry["finishReason"]=="stop" and raw.strip()!="<silent/>"
                entry["semanticStatus"]="requires-semantic-review-against-projected-facts"
            report["cases"].append(entry)
        report["status"]="prepared" if not args.execute_model else "completed-envelope-check-only"
    except Exception as error:
        report["status"]="failed"; report["error"]=str(error)
    finally:
        report["sourcesUnchanged"]=all(digest(ROOT/name)==sha for name,sha in sources.items())
        write(output/"report.json",report)
    print(json.dumps({"status":report["status"],"modelRequests":report["modelRequests"],"report":str(output/"report.json")},ensure_ascii=False))
    return 1 if report["status"]=="failed" or not report["sourcesUnchanged"] else 0

if __name__=="__main__":
    if hasattr(sys.stdout,"reconfigure"):sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
