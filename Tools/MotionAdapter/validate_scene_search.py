"""Compile real search parsers; optionally replay untouched isolated-Unity wires once."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request

from validate_room_task_closure import ROOT, MONO, PROTOCOL, NEWTONSOFT, strict_json
from validate_intent_consistency import all_slots_idle, request_json

SEARCH = "Assets/AIChatTookit/Scripts/LLM/SceneSearchProtocol.cs"
PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.SceneSearch.cs"


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def compile_probe(output):
    code = r'''using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public static class Probe {
 public static void Main(string[] args) {
  if(args[0]=="contracts") { Console.Write(new JObject { ["searchSchema"]=JObject.Parse(SceneSearchProtocol.Schema),["searchContract"]=SceneSearchProtocol.Contract,["roomSchema"]=JObject.Parse(RoomTaskProtocol.DecisionSchema),["roomContract"]=RoomTaskProtocol.DecisionContract }); return; }
  if(args[0]=="schema") { Console.Write(SceneSearchProtocol.SchemaForInput(File.ReadAllText(args[1]))); return; }
  var results=new JArray();
  foreach(var r in JArray.Parse(File.ReadAllText(args[1]))) {
   bool accepted; string reason=null; object decision;
   if((string)r["kind"]=="room") { accepted=RoomTaskProtocol.TryDecision((string)r["json"],(string)r["user"],false,out var d,out reason); decision=d; }
   else { SceneSearchProtocol.Decision d; accepted=r["input"] is JObject ?
     SceneSearchProtocol.TryDecisionForInput((string)r["json"],r["input"].ToString(Formatting.None),out d,out reason) :
     SceneSearchProtocol.TryDecision((string)r["json"],out d,out reason); decision=d; }
   results.Add(JObject.FromObject(new {id=(string)r["id"],accepted,reason,decision}));
  } Console.Write(results.ToString(Formatting.None));
 }
}'''
    folder = output / "parser"
    folder.mkdir()
    source = folder / "Probe.cs"
    source.write_text(code, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, folder / "Newtonsoft.Json.dll")
    proc = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"),
        "/nologo", "/langversion:9", "/r:" + str(folder / "Newtonsoft.Json.dll"),
        "/out:" + str(folder / "probe.exe"), str(ROOT / PROTOCOL), str(ROOT / SEARCH), str(source)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=40)
    (folder / "compile.log").write_text(proc.stdout + proc.stderr, encoding="utf-8")
    if proc.returncode:
        raise RuntimeError("Production parser compilation failed")


def probe(output, command, path=None):
    args = [str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), command]
    if path:
        args.append(str(path))
    result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20, check=True)
    return strict_json(result.stdout.lstrip("\ufeff"))


def cpu_checks(output):
    rows = []
    def add(name, value, expected, kind="search", user="四处看看，找一下小灯。"):
        rows.append(dict(id=name, json=value if isinstance(value,str) else json.dumps(value,ensure_ascii=False), expected=expected, kind=kind,user=user))
    for choice in ("current-candidate", "historical-object", "historical-view", "explore-region", "next-page", "done"):
        add(choice,dict(choice=choice,referenceId="" if choice=="done" else "offered-id",selectionRole="target" if choice in ("current-candidate","historical-object") else "exploration",reason="依据当前证据"),True)
    base=dict(choice="current-candidate",referenceId="offered-id",selectionRole="target",reason="可见候选")
    add("role-required",{k:v for k,v in base.items() if k!="selectionRole"},False)
    add("landmark-not-current-target",dict(base,selectionRole="landmark"),False)
    add("landmark-memory-is-hint",dict(base,choice="historical-object",selectionRole="landmark"),True)
    add("exploration-not-target-proof",dict(base,choice="explore-region",selectionRole="target"),False)
    add("no-model-found",dict(base,choice="found"),False)
    add("no-device-command",dict(base,objectState="off"),False)
    add("no-free-pixel",dict(base,imagePoint=dict(x=.4,y=.6)),False)
    add("no-empty-candidate",dict(base,referenceId=""),False)
    add("done-cannot-smuggle-id",dict(base,choice="done"),False)
    add("duplicate-fields",'{"choice":"done","referenceId":"","reason":"x","choice":"done"}',False)
    note=dict(referenceId="offered-id",description="亮色小物")
    add("bounded-notes",dict(base,observations=[note]),True)
    add("no-duplicate-notes",dict(base,observations=[note,note]),False)
    add("bounded-description",dict(base,observations=[dict(note,description="x"*121)]),False)
    add("bounded-note-count",dict(base,observations=[dict(note,referenceId=str(i)) for i in range(7)]),False)
    room=dict(intent="search-scene",origin="user",evidence="四处看看",searchTask=dict(mode="survey"))
    add("survey-entry",room,True,"room")
    add("find-entry",dict(room,searchTask=dict(mode="find")),True,"room")
    add("search-needs-mode",{k:v for k,v in room.items() if k!="searchTask"},False,"room")
    add("search-no-free-turn",dict(room,viewChange=dict(captureId="fake",yaw=60,pitch=0)),False,"room")
    add("search-no-goal-rewrite",dict(room,searchTask=dict(mode="find",target="红灯")),False,"room")
    add("search-evidence-bound",dict(room,evidence="虚构的用户命令"),False,"room")
    fixture=output/"parser-cases.json";write(fixture,rows)
    results=probe(output,"parse",fixture)
    for case,result in zip(rows,results):
        result["passed"]=result["accepted"]==case["expected"]
    return results


def check_reference(value, source):
    choice, ref = value["choice"], value["referenceId"]
    if "requestChoices" in source:
        return any(choice==row.get("choice") and ref==row.get("referenceId") for row in source["requestChoices"])
    if source.get("imageRole")=="historical":
        return (choice=="done" and ref=="") or (choice=="historical-view" and ref==source.get("historicalReferenceId"))
    catalog=source.get("visibleCatalog",{})
    pools={"current-candidate":catalog.get("current",{}).get("candidates",[]),
        "next-page":catalog.get("current",{}).get("availablePages",[]),
        "historical-object":catalog.get("memory",[]), "historical-view":catalog.get("historicalViews",[]),
        "explore-region":source.get("explorationRegions",[])}
    if choice=="done": return ref==""
    return any(ref==row.get("referenceId") or ref==row.get("selectionId") for row in pools.get(choice,[]))


def replay(args, output, contracts):
    folder=args.manifest.resolve().parent
    upstream=strict_json((folder/"report.json").read_text(encoding="utf-8-sig"))
    if upstream.get("status")!="passed": raise ValueError("Require passed isolated Unity evidence")
    manifest=strict_json(args.manifest.read_text(encoding="utf-8-sig"))
    if args.case and not any(case["id"]==args.case for case in manifest["cases"]):
        raise ValueError("Requested additional case does not exist in the immutable manifest")
    results=[]
    for case in manifest["cases"]:
        if args.case and case["id"]!=args.case: continue
        raw=(folder/case["request"]).read_bytes()
        wire=strict_json(raw.decode("utf-8-sig"))
        source=strict_json((folder/case["input"]).read_text(encoding="utf-8-sig"))
        kind="search" if source.get("imageRole") else "room"
        if wire.get("stream") is not False or len(wire.get("messages",[])) not in ((2,) if kind=="search" else (2,3)):
            raise ValueError("Unexpected exported production wire")
        if kind=="search" and (wire["response_format"]["schema"]!=probe(output,"schema",folder/case["input"]) or
                not wire["messages"][0]["content"].startswith(contracts["searchContract"])):
            raise ValueError("Search protocol changed after Unity export")
        if kind=="room" and not wire["messages"][0]["content"].startswith(contracts["roomContract"]):
            raise ValueError("Room protocol changed after Unity export")
        row=dict(id=case["id"],kind=kind,requestSha256=hashlib.sha256(raw).hexdigest(),status="prepared",modelRequests=0)
        results.append(row)
        (output/(case["id"]+".request.json")).write_bytes(raw)
        if not args.execute_model: continue
        if not all_slots_idle(request_json(args.url.rstrip("/")+"/slots")):
            row["status"]="deferred-busy";continue
        row["modelRequests"]=1;started=time.perf_counter()
        req=urllib.request.Request(args.url.rstrip("/")+"/v1/chat/completions",raw,{"Content-Type":"application/json"})
        try:
            with urllib.request.urlopen(req,timeout=60) as response: envelope_raw=response.read()
            (output/(case["id"]+".envelope.json")).write_bytes(envelope_raw)
            envelope=strict_json(envelope_raw.decode("utf-8"));choices=envelope.get("choices",[])
            if len(choices)!=1 or choices[0].get("finish_reason")!="stop": raise ValueError("Incomplete completion")
            content=choices[0]["message"]["content"]
            response_path=output/(case["id"]+".response.json");response_path.write_text(content,encoding="utf-8")
            fixture=output/(case["id"]+".parse-input.json")
            parse_case=dict(id=case["id"],kind=kind,json=content,user=source.get("currentUserText",""))
            if kind=="search" and "requestChoices" in source:
                parse_case["input"]=source
            write(fixture,[parse_case])
            row["parser"]=probe(output,"parse",fixture)[0]
            row["decision"]=strict_json(content)
            row["validOfferedReference"]=check_reference(row["decision"],source) if kind=="search" else None
            row["status"]="parsed" if row["parser"]["accepted"] and row["validOfferedReference"] is not False else "failed"
            if kind=="search" and case.get("expected",{}).get("expectedChoice"):
                expected=case["expected"]
                row["semanticTargetSelectionPassed"]=(row["decision"].get("choice")==expected["expectedChoice"] and
                    row["decision"].get("selectionRole")==expected.get("expectedRole") and
                    row["decision"].get("referenceId")==expected.get("targetReference"))
                if not row["semanticTargetSelectionPassed"]: row["status"]="failed-semantic-target-selection"
            if kind=="room" and case.get("expected",{}).get("mode")=="find":
                # These frozen fixture utterances request finding only. This label
                # stays outside the actual request, and never affects execution.
                row["noUnrequestedDeviceGoal"]="deviceGoal" not in row["decision"]
                if not row["noUnrequestedDeviceGoal"]: row["status"]="failed-unrequested-device-goal"
                expected=case["expected"]
                row["expectedRoomRoutePassed"]=(row["decision"].get("intent")==expected.get("intent","search-scene") and
                    row["decision"].get("searchTask",{}).get("mode")=="find")
                if not row["expectedRoomRoutePassed"]: row["status"]="failed-room-find-route"
        except Exception as exc: row.update(status="failed",error=str(exc))
        row["elapsedSeconds"]=round(time.perf_counter()-started,3)
        write(output/"replay-progress.json",results)
    return results


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--manifest",type=Path)
    parser.add_argument("--case",help="Replay one explicitly named additional case; this is not a complete-manifest result")
    parser.add_argument("--execute-model",action="store_true")
    parser.add_argument("--url",default="http://127.0.0.1:8080")
    args=parser.parse_args();output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    compile_probe(output);contracts=probe(output,"contracts");write(output/"contracts.json",contracts)
    report=dict(status="running",sources={f:hashlib.sha256((ROOT/f).read_bytes()).hexdigest() for f in [PROTOCOL,SEARCH,PROVIDER]},
        parserChecks=cpu_checks(output),selectedCase=args.case,scope="Real compiled parsers and optional untouched model wires. A parsed choice is not semantic correctness or executed search success; controlled fixture target labels are not human ground truth.")
    if args.manifest: report["replay"]=replay(args,output,contracts)
    report["status"]="passed" if all(r["passed"] for r in report["parserChecks"]) and all(r["status"] in ("prepared","parsed") for r in report.get("replay",[])) else "failed"
    report["modelRequests"]=sum(r["modelRequests"] for r in report.get("replay",[]))
    write(output/"report.json",report)
    print(json.dumps({k:report[k] for k in ("status","modelRequests")}))
    return 0 if report["status"]=="passed" else 1


if __name__=="__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
