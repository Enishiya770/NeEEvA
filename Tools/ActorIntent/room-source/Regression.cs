using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Minimal Unity doubles for production request factories and deterministic
// coroutine transport checks. SendWebRequest only yields a controllable token;
// it never opens a socket or sends a model request.
namespace UnityEngine
{
    public static class Time { public static float realtimeSinceStartup; }
    public static class Debug { public static Action<object> OnLog; public static void Log(object v) { OnLog?.Invoke(v); } public static void LogWarning(object v) { } }
}
namespace UnityEngine.Networking
{
    public class DownloadHandlerBuffer { public string text; }
    public class UploadHandlerRaw { public byte[] Data; public UploadHandlerRaw(byte[] v) { Data = v; } }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success, ConnectionError }
        public bool isDone; public long responseCode; public int timeout; public string error;
        public Result result; public UploadHandlerRaw uploadHandler; public DownloadHandlerBuffer downloadHandler;
        public static readonly System.Collections.Generic.List<UnityWebRequest> Created = new System.Collections.Generic.List<UnityWebRequest>();
        public int AbortCount; public bool Disposed;
        public UnityWebRequest(string u, string m) { Created.Add(this); }
        public void SetRequestHeader(string k, string v) { } public void Abort() { AbortCount++; } public void Dispose() { Disposed = true; }
        public object SendWebRequest() => new object();
    }
}
public class LLM
{
    public virtual bool SupportsRoomTaskMessages => false;
    public virtual bool SupportsRoomPreparationMessages => false;
    public virtual void PostRoomPreparationMessage(string i, Action<bool,string,string> c) { }
    public virtual void CancelRoomPreparationMessage() { }
    public virtual void PostRoomTaskMessage(string i, bool r, Action<bool,string,string> c) { }
    public virtual void PostRoomTaskMessage(string i, string image, bool r, Action<bool,string,string> c) { }
}
public class ConversationTrace
{
    public ConversationTrace Child(string n) => this;
    public void Mark(string n, JObject data) { }
}
public partial class ChatQW : LLM
{
    private enum BackendType { Local, Cloud, OpenAICompatible }
    private BackendType m_Backend = BackendType.Local;
    private string url = "http://never.test", api_key = "fixture";
    private const int k_SlotAuxiliary = 1;
    private string CurrentModelName => "fixture";
    private ConversationTrace TraceContext;
    public readonly System.Collections.Generic.List<IEnumerator> Running = new System.Collections.Generic.List<IEnumerator>();
    private void StartCoroutine(IEnumerator r) { Running.Add(r); r.MoveNext(); }
    private static void AppendJsonString(StringBuilder b, string v) => b.Append(JsonConvert.SerializeObject(v));
    private static void AppendMessage(StringBuilder b, SendData d) => b.Append(new JObject { ["role"] = d.role, ["content"] = d.content }.ToString(Formatting.None));
    private static void AppendReasoningControls(StringBuilder b, bool thinking) { }
    private static void AppendSlot(StringBuilder b, int slot) { }
    private class SendData { public string role, content, imageDataUrl; public SendData(string r, string c) { role = r; content = c; } }
}

public static class Regression
{
    private static int checks;
    private static void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
    private static RoomTaskProtocol.ActorProposalSource Source(string id = "current-draft", int rev = 2, string text = "我想走到你身边，然后看看窗边。", string purpose = "plan")
        => new RoomTaskProtocol.ActorProposalSource { Id = id, Revision = rev, Text = text, Purpose = purpose };
    private static JObject Proposal(RoomTaskProtocol.ActorProposalSource s) => new JObject {
        ["source"] = "unpublished-current-role-draft", ["draftId"] = s.Id, ["draftRevision"] = s.Revision,
        ["text"] = s.Text, ["purpose"] = s.Purpose, ["target"] = s.Target, ["coverage"] = s.Coverage, ["executed"] = false, ["published"] = false };
    private static JObject Actor(string intent = "approach", string evidence = "走到你身边") => new JObject {
        ["intent"] = intent, ["origin"] = "actor_draft", ["evidence"] = evidence,
        ["actorDraftId"] = "current-draft", ["actorDraftRevision"] = 2 };
    private static bool Parse(JObject wire, RoomTaskProtocol.ActorProposalSource s, out RoomTaskProtocol.Decision decision, bool autonomous = false)
        => RoomTaskProtocol.TryDecision(wire.ToString(Formatting.None), "嗯，我听着。", autonomous, s, out decision, out _);
    private static string Factory(string name, params object[] args) => (string)typeof(ChatQW)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    private static bool Fits(JToken schema, JToken value)
    {
        if (schema["type"] != null)
        {
            string type = (string)schema["type"];
            if (type == "object" && value.Type != JTokenType.Object || type == "string" && value.Type != JTokenType.String ||
                type == "integer" && value.Type != JTokenType.Integer || type == "number" && value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                return false;
        }
        if (schema["enum"] is JArray choices && !choices.Any(x => JToken.DeepEquals(x, value))) return false;
        if (value is JObject obj)
        {
            var props = schema["properties"] as JObject;
            if (schema["required"] is JArray required && required.Any(k => obj.Property((string)k) == null)) return false;
            foreach (var p in obj.Properties())
                if (props?[p.Name] == null ? (bool?)schema["additionalProperties"] == false : !Fits(props[p.Name], p.Value)) return false;
        }
        if (schema["oneOf"] is JArray alternatives && alternatives.Count(x => Fits(x, value)) != 1) return false;
        if (schema["anyOf"] is JArray any && !any.Any(x => Fits(x, value))) return false;
        return true;
    }

    private static JArray ModelCases()
    {
        var cases = new JArray();
        void Add(string id, string user, string draft, string[] intents, string[] origins, bool stopped = false, bool completedClaim = false, bool plan = false, string target = "current_user", string coverage = "atomic")
        {
            var source = draft == null ? null : Source(id + "-draft", 1, draft, completedClaim ? "verify" : "plan");
            if (source != null) { source.Target=target; source.Coverage=coverage; }
            var input = new JObject {
                ["currentUserText"] = user, ["autonomous"] = false, ["actorProposal"] = source == null ? null : Proposal(source),
                ["recentDialogue"] = new JArray(new JObject { ["role"]="assistant", ["quotedText"]="我刚才提到过想陪你聊一会儿。" }),
                ["currentFacts"] = new JObject { ["connected"]=true, ["userAnchorValid"]=true, ["userSupportVerified"]=true,
                    ["bodyReserved"]=false, ["followActive"]=false, ["currentlyNearAndFacingUser"]=false,
                    ["actualUserDistance"]=3.0, ["currentTargetRevision"]=8, ["lastAttemptAppliesToCurrentTarget"]=false,
                    ["assessment"]=new JObject { ["canPlanRoute"]=true, ["executionGuaranteed"]=false } },
                ["lastActionResults"] = new JObject { ["phase"]=stopped ? "stopped" : "none", ["reason"]=stopped ? "explicit-user-stop" : "no-current-action" },
                ["execution"] = new JObject { ["matchesRequestedAction"]=false, ["phase"]="idle" },
                ["visualObservation"]=null, ["visibleKnownObjects"]=new JArray(), ["pendingObservationGoal"]=null,
                ["sceneActionFacts"]=new JObject { ["enabled"]=true, ["active"]=false, ["lastAction"]=null },
                ["activeExploration"]=new JObject { ["active"]=false, ["autonomousRestartSuppressed"]=stopped },
                ["decisionContractFacts"]=new JObject { ["autonomousExplorationStep"]=false, ["allowedObservationGoalIds"]=new JArray(),
                    ["autonomousTranslationSuppressed"]=stopped,
                    ["autonomousTranslationRule"]="When suppressed, no self-initiated (autonomous or actor_draft) explore, move-to-ground, approach-object, reposition-view, approach or follow. Looking/inspection and a new explicit user movement request remain available; explicit user explore can begin a new continuing activity." },
                ["scope"]="Synthetic current Unity facts for protocol evaluation; no submitted or completed action and no new image. The explicit user anchor is known; no other person's or object's position is established."
            };
            if (plan && input["actorProposal"] is JObject proposal)
                proposal["actorPlan"] = new JObject { ["id"]="plan-owned", ["revision"]=1,
                    ["originalGoal"]=draft, ["remainingGoal"]=draft, ["state"]="planning", ["results"]=new JArray() };
            string schema = Factory("BuildRoomDecisionSchemaForInput", input.ToString(Formatting.None));
            var provider = new ChatQW();
            var backendField = typeof(ChatQW).GetField("m_Backend", BindingFlags.Instance | BindingFlags.NonPublic);
            backendField.SetValue(provider, Enum.Parse(backendField.FieldType, "OpenAICompatible"));
            var request = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildRoomTaskRequestJson", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(provider, new object[] { input.ToString(Formatting.None), false }));
            cases.Add(new JObject { ["id"]=id, ["input"]=input,
                ["allowedIntents"]=new JArray(intents), ["allowedOrigins"]=new JArray(origins),
                ["messages"]=request["messages"], ["response_format"]=request["response_format"], ["schema"]=JObject.Parse(schema) });
        }
        Add("room01", "嗯，我听着。", "我想走到你身边陪你聊一会儿。", new[]{"approach"}, new[]{"actor_draft"});
        Add("room02", "嗯，我听着。", null, new[]{"none"}, new[]{"none"});
        Add("room03", "请走到我身边。", null, new[]{"approach"}, new[]{"user"});
        Add("room04", "好了吗？", "我已经走到你身边了。", new[]{"none","inspect"}, new[]{"none","actor_draft"}, completedClaim:true);
        Add("room05", "嗯，继续聊。", "我想再走到你身边陪你。", new[]{"none"}, new[]{"none"}, stopped:true);
        Add("room06", "嗯。", "我想走到5番身边。", new[]{"none"}, new[]{"none"}, target:"other");
        Add("room07", "你走过来吧。", "我现在走到你身边。", new[]{"approach"}, new[]{"actor_draft"}, plan:true);
        Add("room08", "跟着我走吧。", "我会一直跟着你。", new[]{"follow"}, new[]{"actor_draft"}, plan:true);
        Add("room09", "过来，然后唱刚才那段。", "我先走到你身边，再唱刚才那段。", new[]{"approach"}, new[]{"actor_draft"}, plan:true, target:"other", coverage:"compound");
        return cases;
    }

    private static bool Evaluate(string path, bool preparationOnly = false)
    {
        var cases = ModelCases().OfType<JObject>().Where(x => !preparationOnly || (string)x["id"] == "room02" ||
            (string)x["id"] == "room03").ToDictionary(x=>(string)x["id"]);
        var reports = new JArray();
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = JObject.Parse(line); var item = cases[(string)response["id"]];
            var input = item["input"]; var source = RoomTaskProtocol.ReadActorProposal(input["actorProposal"]);
            string envelopePath = Path.Combine(Path.GetDirectoryName(path), (string)response["id"] + ".response.json");
            var transportArgs = new object[] { File.Exists(envelopePath) ? File.ReadAllText(envelopePath) : "", "", "" };
            bool complete = (bool)typeof(ChatQW).GetMethod("TryReadRoomTaskCompletion", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, transportArgs);
            string reason = complete ? null : (string)transportArgs[2]; RoomTaskProtocol.Decision d = null;
            bool valid = complete && RoomTaskProtocol.TryDecision((string)transportArgs[1], (string)input["currentUserText"], false, source, out d, out reason, (bool?)input["decisionContractFacts"]?["autonomousTranslationSuppressed"] == true);
            bool expected = valid && ((JArray)item["allowedIntents"]).Values<string>().Contains(d.intent) &&
                ((JArray)item["allowedOrigins"]).Values<string>().Contains(d.origin);
            reports.Add(new JObject { ["id"]=response["id"], ["wireComplete"]=complete, ["protocolValid"]=valid, ["expected"]=expected,
                ["reason"]=reason, ["intent"]=d?.intent, ["origin"]=d?.origin, ["evidence"]=d?.evidence,
                ["elapsedMs"]=response["elapsedMs"], ["error"]=response["error"] });
        }
        bool passed = reports.Count == cases.Count && reports.All(x=>(bool)x["expected"]);
        var summary = new JObject { ["passed"]=passed, ["count"]=reports.Count, ["results"]=reports,
            ["scope"]=preparationOnly
                ? "Two nonvisual preparations validated with actual production envelope parser and RoomTaskProtocol, including its none-origin normalization; real HTTP model responses only, not Unity adoption."
                : "Nine fixed nonvisual room cases with compiled production contract/request-local schema/current-turn factory; real scene OpenAICompatible branch. Model only; no Unity execution or locomotion." };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path),"summary.json"),summary.ToString(Formatting.Indented));
        Console.WriteLine(summary.ToString(Formatting.Indented)); return passed;
    }

    public static int Main(string[] args)
    {
        try
        {
            RoomPreparationWireRegression.Run();
            var source = Source(); var actor = Actor();
            Check(Parse(actor, source, out var decision), "Bound actor proposal accepted without user action quote");
            Check(decision.origin == "actor_draft" && decision.IsSelfInitiated && decision.SourceText == source.Text &&
                decision.actorDraftId == source.Id && decision.actorDraftRevision == 2, "Source survives without promotion to user");
            var sourceProperty = typeof(RoomTaskProtocol.Decision).GetProperty("SourceText");
            Check(sourceProperty.IsDefined(typeof(JsonIgnoreAttribute), true) && !sourceProperty.GetSetMethod(true).IsPublic,
                "Full source is program-assigned and excluded from implicit JSON");
            Check(!RoomTaskProtocol.TryDecision(actor.ToString(), "走到你身边", false, out _, out _), "Old entry cannot mint actor source");
            Check(!Parse(actor, null, out _), "Missing source rejected");
            Check(!Parse(actor, source, out _, true), "Actor cannot enter autonomous tick");
            foreach (var other in new[] { Source("old"), Source(rev:1), Source(text:"这是不同的草稿。"), Source(rev:0), Source(text:"") })
                Check(!Parse(actor, other, out _), "Wrong or invalid source rejected");
            Check(!RoomTaskProtocol.SameActorProposal(source, Source(text:source.Text + "改写")), "Source text mutation revokes snapshot");
            Check(!RoomTaskProtocol.SameActorProposal(source, Source(purpose:"verify")), "Purpose change revokes snapshot");
            Check(RoomTaskProtocol.SameActorProposal(source, Source()), "Same immutable content matches");
            Check(!RoomTaskProtocol.SameActorProposal(source, null), "Cancelled proposal cannot match");
            foreach (string key in new[] { "actorDraftId", "actorDraftRevision" })
            {
                var missing = (JObject)actor.DeepClone(); missing.Remove(key);
                Check(!Parse(missing, source, out _), "Missing actor identity rejected");
                var wrong = (JObject)actor.DeepClone(); wrong[key] = JValue.CreateNull();
                Check(!Parse(wrong, source, out _), "Null actor identity rejected");
            }
            foreach (var evidence in new[] { "", " ", "我听着", "走去你身边", "历史旧动作", new string('我',161) })
            { var bad = (JObject)actor.DeepClone(); bad["evidence"] = evidence; Check(!Parse(bad, source, out _), "Exact short draft evidence required"); }
            var receipt = (JObject)actor.DeepClone(); receipt["replyMode"] = "receipt"; receipt["replyLanguage"] = "zh";
            Check(!Parse(receipt, source, out _), "Actor cannot inherit explicit-user receipt");
            var noAction = Actor("none"); Check(!Parse(noAction, source, out _), "Actor no-action source forbidden");
            foreach (string origin in new[] { "none", "user", "autonomous" })
            { var forged = (JObject)actor.DeepClone(); forged["origin"] = origin; Check(!Parse(forged, source, out _), "Actor fields cannot cross origin"); }
            var extra = (JObject)actor.DeepClone(); extra["SourceText"] = source.Text;
            Check(!Parse(extra, source, out _), "Wire cannot assign program SourceText");
            string duplicate = actor.ToString(Formatting.None).TrimEnd('}') + ",\"actorDraftRevision\":2}";
            Check(!RoomTaskProtocol.TryDecision(duplicate, "嗯", false, source, out _, out _), "Duplicate actor field rejected");
            Check(RoomTaskProtocol.TryDecision("{\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"过来\"}", "请过来", false, out var user, out _) && !user.IsSelfInitiated, "Old user contract unchanged");
            foreach (string quote in new[] { "嗯，我听着。", "好了吗？", "请过来" })
                Check(!RoomTaskProtocol.TryDecision(new JObject { ["intent"]="approach", ["origin"]="user", ["evidence"]=quote }.ToString(),
                    quote, false, source, out _, out string scopeReason) && scopeReason == "invalid-actor-handoff-origin",
                    "Bound actor handoff cannot promote even exact user evidence to user authority");
            Check(RoomTaskProtocol.TryDecision("{\"intent\":\"approach\",\"origin\":\"autonomous\",\"evidence\":\"陪伴\"}", "", true, out var auto, out _) && auto.IsSelfInitiated, "Old autonomous contract unchanged");
            Check(RoomTaskProtocol.TryDecision("{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "嗯", false, source, out _, out _), "Planner may decline proposal");
            Check(!RoomTaskProtocol.TryDecision("{\"intent\":\"none\",\"origin\":\"user\",\"evidence\":\"嗯\"}", "嗯", false, source, out _, out _), "Bound handoff no-action has no user authority either");
            var visual = Actor("look-at", "看看窗边"); visual["visualTarget"] = new JObject { ["captureId"]="new-image", ["x"]=.5, ["y"]=.5, ["label"]="窗边" };
            Check(Parse(visual, source, out _), "Actor still uses typed visual target");
            visual["visualTarget"]["x"] = 5; Check(!Parse(visual, source, out _), "Actor cannot bypass image coordinate constraints");

            var proposal = Proposal(source);
            Check(RoomTaskProtocol.SameActorProposal(source, RoomTaskProtocol.ReadActorProposal(proposal)), "Current unpublished handoff accepted");
            foreach (string key in new[] { "executed", "published" })
            { var bad = (JObject)proposal.DeepClone(); bad[key] = true; Check(RoomTaskProtocol.ReadActorProposal(bad) == null, "Executed/public text is not proposal"); }
            foreach (JToken rev in new JToken[] { new JValue("2"), new JValue(2.0), new JValue(-1), new JValue(long.MaxValue) })
            { var bad = (JObject)proposal.DeepClone(); bad["draftRevision"] = rev; Check(RoomTaskProtocol.ReadActorProposal(bad) == null, "Invalid source revision rejected safely"); }
            var history = (JObject)proposal.DeepClone(); history["source"] = "history";
            Check(RoomTaskProtocol.ReadActorProposal(history) == null, "Historical draft never source");
            var verifySource = Source(purpose:"verify");
            var inspect = Actor("inspect");
            var plainNone = new JObject { ["intent"]="none", ["origin"]="none", ["evidence"]="" };
            Check(Parse(inspect, verifySource, out _), "Verify can only inspect facts with bound draft source");
            Check(Parse(plainNone, verifySource, out _), "Verify may decline without executing");
            foreach (string intent in new[] { "approach", "follow", "stop-moving", "stop-looking", "explore", "move-to-ground", "look-at",
                "approach-object", "orient-view", "turn-in-place", "inspect-object", "set-object-state", "reposition-view" })
                Check(!Parse(Actor(intent), verifySource, out _), "Verify cannot submit execution intent " + intent);
            var forgedPurpose = (JObject)inspect.DeepClone(); forgedPurpose["purpose"] = "plan";
            Check(!Parse(forgedPurpose, verifySource, out _), "Model cannot upgrade program purpose");
            var legacyProposal = (JObject)proposal.DeepClone(); legacyProposal.Remove("purpose");
            var legacySource = RoomTaskProtocol.ReadActorProposal(legacyProposal);
            Check(legacySource?.Purpose == "verify" && !Parse(actor, legacySource, out _) && Parse(inspect, legacySource, out _),
                "Legacy missing purpose is verify-only, never planning");
            foreach (JToken badPurpose in new JToken[] { JValue.CreateNull(), new JValue("execute"), new JValue(1), new JObject() })
            { var malformed = (JObject)proposal.DeepClone(); malformed["purpose"] = badPurpose;
                var scoped = RoomTaskProtocol.ReadActorProposal(malformed);
                Check(scoped?.Purpose == "verify" && !Parse(actor, scoped, out _), "Unknown purpose never becomes plan or missing proposal"); }
            Check(!Parse(actor, Source(purpose:null), out _), "Direct old DTO without purpose cannot execute");

            var legacySchema = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new[] {"goal-current"}));
            var actorSchema = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new[] {"goal-current"}, source));
            Check(!Fits(legacySchema, actor) && Fits(actorSchema, actor), "Grammar opens actor only with current source");
            Check(!Fits(actorSchema, receipt), "Grammar forbids actor receipt");
            foreach (string origin in new[] { "user", "autonomous" })
            { var promoted = new JObject { ["intent"]="approach", ["origin"]=origin, ["evidence"]="嗯，我听着。" };
                Check(!Fits(actorSchema,promoted), "Handoff grammar has no user/autonomous execution branch"); }
            Check(Fits(actorSchema, plainNone), "Handoff grammar retains exactly the none authority path");
            var verifySchema = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, verifySource));
            Check(Fits(verifySchema, inspect) && Fits(verifySchema, plainNone) && !Fits(verifySchema, actor), "Verify schema permits read-only inspection and no action");
            Check(((JArray)verifySchema["properties"]["intent"]["enum"]).Values<string>().SequenceEqual(new[]{"none","inspect"}) &&
                verifySchema["properties"]["visualTarget"] == null && verifySchema["properties"]["replyMode"] == null,
                "Verify field guide cannot advertise physical targets or receipt");
            Check(!Fits(JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, Source(purpose:null))), actor),
                "Invalid direct source schema fails closed");
            foreach (var mutate in new Action<JObject>[] { x=>x["actorDraftId"]="old", x=>x["actorDraftRevision"]=1, x=>x["origin"]="user", x=>x["SourceText"]="forged" })
            { var bad = (JObject)actor.DeepClone(); mutate(bad); Check(!Fits(actorSchema,bad), "Grammar rejects source mutation"); }
            foreach (var branch in ((JArray)actorSchema["oneOf"]).OfType<JObject>().Where(b=>((JArray)b["properties"]["origin"]["enum"]).Values<string>().Contains("actor_draft")))
                Check((bool)branch["additionalProperties"] == false && ((JArray)branch["required"]).Values<string>().Contains("actorDraftId") &&
                    ((JArray)branch["required"]).Values<string>().Contains("actorDraftRevision") && branch["properties"]["replyMode"] == null,
                    "Every actor branch is closed and source bound");
            Check(!Fits(JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForExploration(null)), actor), "Autonomous exploration grammar unchanged");
            var input = new JObject { ["autonomous"] = false, ["currentUserText"] = "嗯，我听着。", ["actorProposal"] = proposal,
                ["recentDialogue"] = new JArray(), ["decisionContractFacts"] = new JObject { ["allowedObservationGoalIds"] = new JArray("goal-current") } };
            Check(Fits(JObject.Parse(Factory("BuildRoomDecisionSchemaForInput", input.ToString())), actor), "Production Qwen factory exposes bound source");
            var verifyInput = (JObject)input.DeepClone(); verifyInput["actorProposal"] = Proposal(verifySource);
            Check(!Fits(JObject.Parse(Factory("BuildRoomDecisionSchemaForInput", verifyInput.ToString())), actor) &&
                Fits(JObject.Parse(Factory("BuildRoomDecisionSchemaForInput", verifyInput.ToString())), inspect), "Production Qwen factory preserves verify scope");
            string turn = Factory("BuildRoomDecisionCurrentTurnMessage", input.ToString());
            Check(turn.Contains("嗯，我听着。") && turn.Contains("current-draft") && turn.Contains("actor_draft"), "Last user message preserves user and separate proposal");
            input["autonomous"] = true;
            Check(!Fits(JObject.Parse(Factory("BuildRoomDecisionSchemaForInput", input.ToString())), actor), "Transport autonomous input cannot acquire actor origin");
            input["autonomous"] = false; input.Remove("actorProposal");
            Check(!Fits(JObject.Parse(Factory("BuildRoomDecisionSchemaForInput", input.ToString())), actor), "Transport no-proposal input cannot acquire actor origin");
            var scopedSource = Source(); scopedSource.PlanId="owned-plan"; scopedSource.PlanRevision=1;
            var transportFormat = RoomTaskProtocol.StructuredResponseFormat(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, scopedSource));
            var wireSchema = transportFormat["json_schema"]["schema"];
            Check((string)transportFormat["type"]=="json_schema" && wireSchema["oneOf"]==null && wireSchema["anyOf"] is JArray,
                "Compatible transport places closed production alternatives on the actual wire.");
            var bounded=Actor(); bounded["source_ref"]=RoomTaskProtocol.PlanSourceReference(scopedSource); bounded["evidence"]=""; bounded["planId"]="owned-plan";bounded["planRevision"]=1;bounded["planRemaining"]="";
            Check(Fits(wireSchema,bounded),"Plan approach is admitted by the real compatible schema.");
            bounded["planContinuation"]="ongoing";
            Check(!Fits(wireSchema,bounded) && !RoomTaskProtocol.TryDecision(bounded.ToString(),"嗯",false,scopedSource,out _,out _),
                "The observed approach-plus-follow-continuation defect is rejected both during decoding and by the runtime parser.");
            bounded["intent"]="follow";
            Check(Fits(wireSchema,bounded) && RoomTaskProtocol.TryDecision(bounded.ToString(),"嗯",false,scopedSource,out _,out _),
                "Valid ongoing follow remains admitted by both wire and runtime.");
            bounded["actorDraftId"]="previous-draft";
            Check(!Fits(wireSchema,bounded),"A model cannot select a previous source id on the actual wire.");
            var otherSource=Source(); otherSource.Target="other";otherSource.Coverage="atomic";
            var otherSchema=JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,otherSource));
            Check(!Fits(otherSchema,actor) && !Parse(actor,otherSource,out _),
                "An atomic other-target proposal cannot acquire the current-user approach capability in grammar or parser.");
            otherSource.Coverage="compound";
            Check(Fits(JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,otherSource)),actor) && Parse(actor,otherSource,out _),
                "Compound other-target scope retains a valid current-user prerequisite instead of losing the tail.");
            var stoppedSchema=JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,source,true));
            Check(!Fits(stoppedSchema,actor) && !RoomTaskProtocol.TryDecision(actor.ToString(),"嗯",false,source,out _,out _,true),
                "Existing stop suppression excludes self-initiated translation at decoding and parsing.");
            Check(Fits(stoppedSchema,new JObject{["intent"]="none",["origin"]="none",["evidence"]=""}),
                "Suppressed movement retains a valid non-executing response.");
            var explicitMove=new JObject{["intent"]="approach",["origin"]="user",["evidence"]="过来"};
            Check(Fits(JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,null,true)),explicitMove) &&
                RoomTaskProtocol.TryDecision(explicitMove.ToString(),"过来",false,null,out _,out _,true),
                "Existing autonomous suppression does not forbid a new explicit user movement request.");
            var sourceWithScope=Source();sourceWithScope.Target="other";sourceWithScope.Coverage="atomic";
            Check(!RoomTaskProtocol.SameActorProposal(source,sourceWithScope),"Changing target scope invalidates an asynchronous source lease.");
            Console.WriteLine(new JObject { ["passed"] = true, ["checks"] = checks, ["networkRequests"] = 0,
                ["scope"] = "Production room source parser and Qwen request factories; no Unity, no physical action, no semantic model inference." }.ToString(Formatting.Indented));
            if (args.Length == 2 && args[0] == "--export-model")
                File.WriteAllText(args[1], new JObject { ["cases"]=ModelCases(), ["productionBackend"]="OpenAICompatible", ["backendValue"]=2,
                    ["preparationTimeoutSeconds"]=RoomPreparationBudget.BackgroundTimeoutSeconds }.ToString(Formatting.Indented));
            if (args.Length == 2 && args[0] == "--evaluate-model") return Evaluate(args[1]) ? 0 : 1;
            if (args.Length == 2 && args[0] == "--evaluate-preparation-model") return Evaluate(args[1], true) ? 0 : 1;
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
