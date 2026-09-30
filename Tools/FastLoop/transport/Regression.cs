using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
namespace UnityEngine {
 public class TextAsset { public string name, text; }
 public static class Mathf { public static int Max(int a,int b)=>Math.Max(a,b); }
 public static class Debug { public static void Log(object value){} }
}
namespace UnityEngine.Networking {
 public class DownloadHandlerBuffer { public string text; }
 public class UploadHandlerRaw { public byte[] data; public UploadHandlerRaw(byte[] b){data=b;} }
 public class UnityWebRequest:IDisposable {
  public enum Result { Success, ConnectionError }
  public static string NextBody; public static int Sent;
  public UploadHandlerRaw uploadHandler; public DownloadHandlerBuffer downloadHandler;
  public int timeout; public long responseCode=200; public Result result=Result.Success; public bool isDone;
  public UnityWebRequest(string u,string method){}
  public void SetRequestHeader(string n,string v){}
  public object SendWebRequest(){Sent++;downloadHandler.text=NextBody;return null;}
  public void Abort(){isDone=true;}
  public void Dispose(){isDone=true;}
 }
}
public class ConversationTrace {
 public Action<string> Sink;
 public ConversationTrace Child(string name)=>this;
 public void Mark(string name,JObject value=null){Sink?.Invoke(name);}
}
public class LLM { public class SendData { public string role,content,imageDataUrl; public SendData(string r,string c){role=r;content=c;} } }
public partial class ChatQW : LLM {
 public enum BackendType { Local, OpenAICompatible }
 private bool m_EnableRoleDecisionFastLane=true, m_LogRequestStats=false;
 private BackendType m_Backend=BackendType.Local;
 private string api_key="test-secret", url="http://local.test", lan="zh", m_Prompt="persona";
 private UnityEngine.TextAsset[] m_PromptFiles;
 private bool HasPromptFiles=>m_PromptFiles!=null&&m_PromptFiles.Length>0;
 private string CurrentModelName=>"test-qwen";
 private const int k_SlotAuxiliary=1;
 private ConversationTrace TraceContext;
 private int m_StreamRequestGeneration;
 private FirstReplyUserLease m_FirstReplyUserLease;
 private readonly List<SendData> m_DataList=new List<SendData>();
 private sealed class FirstReplyUserLease {
  internal int Generation; internal SendData User; internal bool CommitHistory,Settled,HandoffPending;
 }
 private FirstReplyUserLease BeginFirstReplyUserLease(SendData current,int generation,bool commitHistory){
  if(m_FirstReplyUserLease!=null&&m_FirstReplyUserLease.Generation==generation&&ReferenceEquals(m_FirstReplyUserLease.User,current))return m_FirstReplyUserLease;
  var lease=new FirstReplyUserLease{Generation=generation,User=current,CommitHistory=commitHistory};
  m_FirstReplyUserLease=lease;if(commitHistory)m_DataList.Add(current);return lease;
 }
 public int HistoryCount=>m_DataList.Count;
 public string HistoryImage=>m_FirstReplyUserLease?.User.imageDataUrl;
 public void NewHistoryGeneration(){m_StreamRequestGeneration++;}
 public bool ClaimHistory(){
  var lease=m_FirstReplyUserLease;
  if(lease==null||!lease.CommitHistory||!lease.Settled||!lease.HandoffPending||lease.Generation!=m_StreamRequestGeneration||!m_DataList.Contains(lease.User))return false;
  lease.HandoffPending=false;return true;
 }
 public IEnumerator Pending;
 public Action Diagnostic;
 private void AbortPrewarmIfRunning(){}
 private void StartCoroutine(IEnumerator routine){Pending=routine;routine.MoveNext();}
 private void RaiseRequestDiagnostic(string json){Diagnostic?.Invoke();}
 private string BuildRoleDecisionPersona()=>m_Prompt;
 private string BuildRoleDecisionLanguageRules()=>"legacy <say> protocol";
 private string BuildRoleDecisionBehaviorRules()=>FormalPromptProfile.RolePreferences("Transport fixture role preference.");
 private static void AppendJsonString(StringBuilder sb,string v)=>sb.Append(new JValue(v).ToString(Formatting.None));
 private static void AppendMessage(StringBuilder sb,SendData v)=>sb.Append(new JObject { ["role"]=v.role, ["content"]=v.content }.ToString(Formatting.None));
 private void AppendReasoningControls(StringBuilder sb,bool enabled)=>sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
 private void AppendSlot(StringBuilder sb,int slot)=>sb.Append(",\"id_slot\":").Append(slot);
 private class CompletionPerformance { public void Observe(string s){} public JObject Snapshot()=>new JObject(); }
}
public static class Probe {
 static int count;
 static void Check(bool b,string name){if(!b)throw new Exception(name);count++;}
 static readonly MethodInfo Read=typeof(ChatQW).GetMethod("TryReadFastLoopResponse",BindingFlags.NonPublic|BindingFlags.Static);
 static string Raw(string route="reply",string candidate="none",string evidence="",string speech="你好")=>new JObject {
  ["version"]=FastLoopProtocol.CurrentVersion,["task_control"]="none",["control_target_id"]="",["control_evidence"]="",
  ["route"]=route,["candidate"]=candidate,["evidence"]=evidence,["speech"]=speech,["language"]="zh"}.ToString(Formatting.None);
 static JObject Envelope(string raw)=>new JObject { ["choices"]=new JArray(new JObject {
  ["finish_reason"]="stop",["message"]=new JObject{["role"]="assistant",["content"]=raw}})};
 static bool Accept(string json,string current="抬头"){object[] args={json,current,null,null,null};return (bool)Read.Invoke(null,args);}
 public static void Main(string[] args){
  if(args.Length==2 && args[0]=="--export-repairs") { ExportRepairs(args[1]); return; }
  if(args.Length==3 && args[0]=="--evaluate-repairs") { EvaluateRepairs(args[1],args[2]); return; }
  var good=Envelope(Raw()); var goodText=good.ToString(Formatting.None);
  Check(Accept(goodText),"reply valid");
  var compound=JObject.Parse(Raw("recording_singing",speech:""));
  compound["task_control"]="stop_follow";compound["control_target_id"]="follow-task:3:9";compound["control_evidence"]="不用跟着我了";
  Check(Accept(Envelope(compound.ToString()).ToString(),"不用跟着我了，把刚才录的片段唱给我听。"),"v2 preserves control plus recording route");
  var old=(JObject)compound.DeepClone();old["version"]=1;Check(!Accept(Envelope(old.ToString()).ToString()),"v1 rejected");
  foreach(var name in new[]{"task_control","control_target_id","control_evidence"}){
   var missingField=(JObject)compound.DeepClone();missingField.Remove(name);
   Check(!Accept(Envelope(missingField.ToString()).ToString(),"不用跟着我了"),"missing v2 field rejects: "+name);
  }
  Check(Accept(Envelope(Raw("head","head.raise","抬头","")).ToString()),"head valid");
  Check(!Accept(Envelope(Raw("head","head.raise","抬头","我抬头了")).ToString()),"action speech rejected");
  Check(!Accept(Envelope(Raw("head","head.raise","低头","")).ToString()),"noncurrent evidence rejected");
  foreach(var s in new[]{"<say>你好</say>","&lt;say&gt;你好"})Check(!Accept(Envelope(Raw(speech:s)).ToString()),"tag speech rejected");
  foreach(var f in new[]{"length","tool_calls","content_filter","arbitrary\nsecret"}){var j=Envelope(Raw());j["choices"][0]["finish_reason"]=f;Check(!Accept(j.ToString()),"finish rejected");}
  var missing=Envelope(Raw());((JObject)missing["choices"][0]).Remove("finish_reason");Check(!Accept(missing.ToString()),"missing finish rejected");
  Check(!Accept(goodText.Replace("\"finish_reason\":\"stop\"","\"finish_reason\":\"length\",\"finish_reason\":\"stop\"")),"duplicate envelope rejected");
  Check(!Accept(Envelope(Raw().Replace("\"route\":\"reply\"","\"route\":\"head\",\"route\":\"reply\"")).ToString()),"duplicate decision rejected");
  var tool=Envelope(Raw());tool["choices"][0]["message"]["tool_calls"]=new JArray(new JObject());Check(!Accept(tool.ToString()),"tool channel rejected");
  var refusal=Envelope(Raw());refusal["choices"][0]["message"]["refusal"]="no";Check(!Accept(refusal.ToString()),"refusal rejected");
  var multi=Envelope(Raw());((JArray)multi["choices"]).Add(multi["choices"][0].DeepClone());Check(!Accept(multi.ToString()),"multiple choice rejected");
  var role=Envelope(Raw());role["choices"][0]["message"]["role"]="user";Check(!Accept(role.ToString()),"unexpected role rejected");
  Check(!Accept("{}"),"missing choices rejected");
  Check(!Accept("[]"),"array envelope rejected");
  var backend=new ChatQW(); int calls=0;
  UnityWebRequest.NextBody=goodText;
  backend.PostFastLoopDecision("facts","hello",(d,r)=>{Check(d!=null&&r=="ok","callback validated");calls++;});
  backend.Pending.MoveNext();Check(calls==1,"callback once");
  backend.PostFastLoopDecision("facts","hello",(d,r)=>calls++);var stale=backend.Pending;backend.CancelFastLoopDecision();stale.MoveNext();Check(calls==1,"cancel suppress callback");
  int sent=UnityWebRequest.Sent;backend.Diagnostic=backend.CancelFastLoopDecision;backend.PostFastLoopDecision("facts","hello",(d,r)=>calls++);Check(UnityWebRequest.Sent==sent&&calls==1,"diagnostic cancellation suppress send");
  var build=typeof(ChatQW).GetMethod("BuildFastLoopRequestJson",BindingFlags.NonPublic|BindingFlags.Instance);
  var wire=JObject.Parse((string)build.Invoke(backend,new object[]{"facts","用户\"原话",null}));
  Check((bool)wire["stream"]==false&&(bool)wire["enable_thinking"]==false&&(int)wire["max_tokens"]==256&&(int)wire["id_slot"]==1,"bounded auxiliary request");
  Check(!((string)wire["messages"][0]["content"]).Contains("<say>"),"legacy language excluded");
  Check((string)JObject.Parse((string)wire["messages"][1]["content"])["current_user"]=="用户\"原话","current user preserved");
  Check(!wire.ToString().Contains("test-secret"),"credentials excluded from wire diagnostic");
  PolicyChecks(backend, build);
  var history=new ChatQW();
  history.BeginFastLoopHistory("抬头","image:accepted",true);
  history.CancelFastLoopDecision();
  Check(history.HistoryCount==1&&history.HistoryImage=="image:accepted","accepted input and image survive cancellation");
  history.CompleteFastLoopHistory();
  Check(history.HistoryCount==1&&!history.ClaimHistory(),"own completion closes handoff without dropping input");
  history.NewHistoryGeneration();history.BeginFastLoopHistory("抬头",null,true);
  Check(history.HistoryCount==2,"identical next user is a distinct accepted turn");
  Check(history.ClaimHistory()&&!history.ClaimHistory()&&history.HistoryCount==2,"legacy claims accepted input once");
  history.NewHistoryGeneration();history.BeginFastLoopHistory("抬头",null,true);
  history.NewHistoryGeneration();
  Check(!history.ClaimHistory()&&history.HistoryCount==3,"generation invalidates handoff but retains accepted user");
  history.BeginFastLoopHistory("ephemeral",null,false);history.CompleteFastLoopHistory();
  Check(history.HistoryCount==3&&!history.ClaimHistory(),"ephemeral input does not write history");
  Console.WriteLine("FastLoop backend production source: "+count+" checks passed.");
  TaskControlChecks.Run(args);
 }

 static void PolicyChecks(ChatQW backend,MethodInfo build) {
  string Facts(string id,bool following,bool repair=false)=>new JObject {
   ["roomTask"]=new JObject{["connected"]=true,["activeTaskId"]=id,["followActive"]=following},
   ["taskControlRepair"]=repair?new JObject{["attempt"]=1}:null }.ToString(Formatting.None);
  var none=FastLoopRequestPolicy.FromContext(Facts("",false));
  var follow=FastLoopRequestPolicy.FromContext(Facts("real-follow",true));
  var moving=FastLoopRequestPolicy.FromContext(Facts("real-movement",false));
  var denied=FastLoopRequestPolicy.FromContext(Facts("real-follow",true,true));
  foreach(var item in new[]{none,follow,moving,denied}) {
   var schema=(JArray)item.Constrain(FastLoopProtocol.ResponseFormat())["json_schema"]["schema"]["anyOf"];
   Check(schema.Count==(item.Control=="none"?5:10),"only current control branches exist");
   foreach(JObject branch in schema) {
    var props=branch["properties"];
    var ctl=(string)props["task_control"]["enum"][0];
    Check(ctl=="none"||ctl==item.Control,"no unsupported control kind");
    if(ctl!="none")Check((string)props["control_target_id"]["enum"][0]==item.TargetId,"exact request-local target enum");
   }
  }
  var stop=JObject.Parse(Raw());stop["task_control"]="stop_follow";stop["control_target_id"]="real-follow";stop["control_evidence"]="停止跟随";
  bool Bound(FastLoopRequestPolicy p,JObject body,string user)=>FastLoopProtocol.TryReadBoundResponse(Envelope(body.ToString()).ToString(),user,p,out _,out _,out _);
  Check(Bound(follow,stop,"停止跟随"),"correct live follow control survives");
  Check(!Bound(none,stop,"停止跟随")&&!Bound(denied,stop,"停止跟随")&&!Bound(moving,stop,"停止跟随"),"no active/denied/wrong-domain control rejects outside grammar too");
  stop["control_target_id"]="stale-follow";Check(!Bound(follow,stop,"停止跟随"),"different id cannot pass parser even if provider ignored grammar");
  var repairFacts=new JObject{["dialogueRepair"]=new JObject{["reason"]="long-repeated-draft"}}.ToString();
  var repair=FastLoopRequestPolicy.FromContext(repairFacts);
  var answer=JObject.Parse(Raw());
  Check(!Bound(repair,answer,"请重复刚才的话"),"repair requires response relationship");
  answer["reply_basis"]="requested_repetition";answer["reply_evidence"]="请重复刚才的话";
  Check(Bound(repair,answer,"请重复刚才的话"),"intentional quotation is representable");
  Check(!Bound(repair,answer,"不要重复了，结束测试"),"history evidence cannot authorize repeated output");
  Check(!Bound(none,answer,"请重复刚才的话"),"repair metadata forbidden on ordinary response");
  answer["reply_basis"]="current_input";answer["reply_evidence"]="结束测试";
  Check(Bound(repair,answer,"不要重复了，结束测试"),"new current-focused reply remains available");
  answer["unexpected"]=true;Check(!Bound(repair,answer,"结束测试"),"repair wrapper does not weaken base unknown-field rejection");
  var actual=JObject.Parse((string)build.Invoke(backend,new object[]{Facts("",false),"一直跟着我",null}));
  Check(((JArray)actual["response_format"]["json_schema"]["schema"]["anyOf"]).Count==5,"real backend wire uses context bound grammar");
  backend.Diagnostic=null;UnityWebRequest.NextBody=Envelope(stop.ToString()).ToString();
  bool rejected=false;backend.PostFastLoopDecision(Facts("",false),"停止跟随",(d,r)=>rejected=d==null&&r=="control_outside_current_request");
  backend.Pending.MoveNext();Check(rejected,"real transport rejects provider response violating snapshot");
 }

 static void ExportRepairs(string path) {
  var backend=new ChatQW();
  var flags=BindingFlags.NonPublic|BindingFlags.Instance;
  typeof(ChatQW).GetField("m_Prompt",flags).SetValue(backend,System.IO.File.ReadAllText("Assets/AIChatTookit/Prompts/persona.txt"));
  typeof(ChatQW).GetField("lan",flags).SetValue(backend,"ja");
  var build=typeof(ChatQW).GetMethod("BuildFastLoopRequestJson",flags);
  var cases=JArray.Parse(System.IO.File.ReadAllText("work/runtime-contract-repair-20260926/fast-cases.json"));
  foreach(JObject row in cases) {
   var context=(JObject)row["context"];
   row["request"]=JObject.Parse((string)build.Invoke(backend,new object[]{context.ToString(Formatting.None),(string)row["user"],null}));
   row["request"]["model"]="qwen3.6-35b-a3b";
  }
  System.IO.File.WriteAllText(path,cases.ToString());
  Console.WriteLine("Exported production request factory: "+cases.Count);
 }
 static void EvaluateRepairs(string input,string output) {
  var cases=JArray.Parse(System.IO.File.ReadAllText("work/runtime-contract-repair-20260926/fast-cases.json"));
  var results=JArray.Parse(System.IO.File.ReadAllText(input));var report=new JArray();
  foreach(JObject result in results) {
   JObject source=null;foreach(JObject row in cases)if((string)row["id"]==(string)result["id"])source=row;
   if(source==null)throw new Exception("unknown case");
   var policy=FastLoopRequestPolicy.FromContext(source["context"].ToString(Formatting.None));
   bool ok=FastLoopProtocol.TryReadBoundResponse((string)result["raw"],(string)source["user"],policy,out var d,out var finish,out var reason);
   report.Add(new JObject{["id"]=result["id"],["accepted"]=ok,["reason"]=reason,["finish"]=finish,
    ["route"]=d?.Route,["taskControl"]=d?.TaskControl,["replyBasis"]=d?.ReplyBasis,["speech"]=d?.Speech,["language"]=d?.Language});
  }
  System.IO.File.WriteAllText(output,report.ToString());Console.WriteLine(report.ToString());
 }
}
