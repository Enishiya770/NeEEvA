"""Offline production C# compaction regression and frozen-wire size comparison. No HTTP."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
MONO = Path("E:/Unity_Data/2022.3.22f1/Editor/Data/MonoBleedingEdge")
SOURCES = ["Assets/AIChatTookit/Scripts/LLM/QW/SceneFactContextCompactor.cs",
           "Assets/AIChatTookit/Scripts/LLM/QW/ChatRequestWireTiming.cs",
           "Assets/Editor/SceneFactCompactionRegression.cs",
           "Assets/AIChatTookit/Scripts/LLM/FormalResponseStage.cs",
           "Assets/Editor/FormalResponseStageRegression.cs"]
HARNESS = r'''
using System; using System.IO; using System.Collections.Generic; using System.Reflection; using System.Text;
using Newtonsoft.Json; using Newtonsoft.Json.Linq;
public static class SceneContextProbe {
 private static string ExpandSaved(string text) {
  // Audit-only reversal of an existing production reference encoding. It is never
  // submitted as a request and never alters stored history or the original wire.
  const string label="[Scene fact block "; var blocks=new Dictionary<string,JObject>();
  var ranges=new List<Tuple<int,int,int,JObject>>(); int cursor=0;
  var flags=BindingFlags.NonPublic|BindingFlags.Static;
  while((cursor=text.IndexOf(label,cursor,StringComparison.Ordinal))>=0) {
   int endLabel=text.IndexOf(']',cursor); string id=text.Substring(cursor+label.Length,endLabel-cursor-label.Length);
   int start=text.IndexOf('{',endLabel); if(start<0) throw new InvalidOperationException("saved-block-missing-json");
   int end=(int)typeof(SceneFactContextCompactor).GetMethod("JsonObjectEnd",flags).Invoke(null,new object[]{text,start,text.Length});
   JObject value; using(var reader=new JsonTextReader(new StringReader(text.Substring(start,end-start)))) {
    reader.DateParseHandling=DateParseHandling.None; reader.FloatParseHandling=FloatParseHandling.Decimal;
    value=JObject.Load(reader,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error });
   }
   typeof(SceneFactContextCompactor).GetMethod("VerifyNumericValues",flags).Invoke(null,new object[]{text.Substring(start,end-start),value.ToString(Formatting.None)});
   blocks.Add(id,value); ranges.Add(Tuple.Create(cursor,start,end,value)); cursor=end;
  }
  if(ranges.Count==0) return text;
  var result=new StringBuilder(); cursor=0;
  foreach(var range in ranges) {
   result.Append(text,cursor,range.Item1-cursor);
   int afterLabel=text.IndexOf('\n',range.Item1)+1;
   result.Append(text,afterLabel,range.Item2-afterLabel);
   var expanded=(JToken)typeof(SceneFactContextCompactor).GetMethod("Expand",flags).Invoke(null,new object[]{range.Item4,blocks,0});
   result.Append(expanded.ToString(Formatting.None)); cursor=range.Item3;
  }
  result.Append(text,cursor,text.Length-cursor); string output=result.ToString();
  int intro=output.IndexOf("[Scene fact sharing v1]",StringComparison.Ordinal);
  if(intro>=0) { int end=output.IndexOf('\n',intro); if(end<0) throw new InvalidOperationException("saved-intro-end"); output=output.Remove(intro,end-intro+1); }
  return output;
 }
 public static int Main(string[] args) {
  try {
   if(args[0]=="test") {
    int checks=SceneFactCompactionRegression.RunChecks();
    checks+=FormalResponseStageRegression.RunChecks(File.ReadAllText("Assets/AIChatTookit/Prompts/Skills/singing.txt"));
    Console.WriteLine(new JObject { ["passed"]=true,["checks"]=checks,["sceneFacts"]=SceneFactCompactionRegression.LastReport,["responseStage"]=FormalResponseStageRegression.LastReport }.ToString(Formatting.None)); return 0;
   }
   string text=File.ReadAllText(args[1]); if(args[0]=="recompact") text=ExpandSaved(text);
   var output=SceneFactContextCompactor.Compact(text,out var report);
   File.WriteAllText(args[2],output,new System.Text.UTF8Encoding(false)); Console.WriteLine(JsonConvert.SerializeObject(report)); return 0;
  } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
 }
}
'''

def run(command: list[str], timeout: int = 45) -> subprocess.CompletedProcess:
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--output", required=True, type=Path)
    ap.add_argument("--wire", action="append", default=[], type=Path, help="Frozen production formal wire; system messages only, never user/persona history")
    ap.add_argument("--reexpand-sharing", action="store_true", help="Audit old encoded wire by exactly expanding its references before current production compaction; never send")
    args = ap.parse_args()
    out = args.output.resolve(); out.mkdir(parents=True, exist_ok=False)
    parser = out / "parser"; parser.mkdir()
    (parser / "SceneContextProbe.cs").write_text(HARNESS, encoding="utf-8")
    shutil.copyfile(ROOT / "Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll", parser / "Newtonsoft.Json.dll")
    exe = parser / "scene-context-probe.exe"
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9", "/r:" + str(parser / "Newtonsoft.Json.dll"), "/out:" + str(exe), *[str(ROOT / s) for s in SOURCES], str(parser / "SceneContextProbe.cs")]
    result = run(command); (parser / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    prefix = [str(MONO / "bin/mono.exe"), str(exe)]
    tests = json.loads(run(prefix + ["test"]).stdout.lstrip("\ufeff"))
    report = {"scope": "Offline actual C# helper compilation, regression and frozen production wire text-byte comparison. No model/HTTP/Unity, no actual tokenizer or latency claim.", "savedReferencesExpandedBeforeCompaction": args.reexpand_sharing, "networkRequests": 0, "regression": tests, "sources": {s: hashlib.sha256((ROOT/s).read_bytes()).hexdigest() for s in SOURCES}, "wires": []}
    for ordinal, wire in enumerate(args.wire, 1):
        raw = wire.read_bytes(); source = json.loads(raw.decode("utf-8-sig")); modified = json.loads(raw.decode("utf-8-sig"))
        cases = []; before_text = after_text = 0
        for index, message in enumerate(source["messages"]):
            content = message.get("content")
            if not isinstance(content, str):
                continue
            before_text += len(content.encode("utf-8")); selected = content
            # Only dynamic scene blocks in a system message can be touched.
            if message.get("role") == "system" and ("[视觉关注与行动事实]" in content or "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]" in content):
                stem = f"wire-{ordinal:02d}-system-{index:02d}"
                old = out / (stem + ".before.txt"); new = out / (stem + ".after.txt")
                old.write_text(content, encoding="utf-8")
                metric = json.loads(run(prefix + ["recompact" if args.reexpand_sharing else "compact", str(old), str(new)]).stdout.lstrip("\ufeff"))
                selected = new.read_text(encoding="utf-8-sig"); cases.append({"message": index, **metric})
            after_text += len(selected.encode("utf-8")); modified["messages"][index]["content"] = selected
        for before, after in zip(source["messages"], modified["messages"]):
            if before.get("role") != "system": assert before == after, "User/assistant/image payload modified"
        # Freeze both complete requests for root-controlled comprehension/performance A/B, never send here.
        (out/f"wire-{ordinal:02d}.before.request.json").write_bytes(raw)
        compact_raw = json.dumps(modified, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        (out/f"wire-{ordinal:02d}.after.request.json").write_bytes(compact_raw)
        normalized_before = json.dumps(source, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        report["wires"].append({"source": str(wire.resolve()), "sourceSha256": hashlib.sha256(raw).hexdigest(), "beforeNormalizedBytes": len(normalized_before), "afterNormalizedBytes": len(compact_raw), "beforeStringContentBytes": before_text, "afterStringContentBytes": after_text, "textByteReductionPercent": round(100 * (before_text-after_text)/max(1,before_text),2), "dynamicBlocks": cases})
    (out/"report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": tests["passed"], "checks": tests["checks"], "wires": report["wires"], "networkRequests": 0}, ensure_ascii=True))

if __name__ == "__main__": main()
