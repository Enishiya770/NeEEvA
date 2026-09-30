#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SingingSessionRegression
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const string Prefix = "clip:5538d650-";
    static int checks;
    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Isolated edit mode only.");
        checks = 0;
        var state = new SingingSessionState();
        Apply(state, 6, "observe_take", 6, "第一份演唱");
        Apply(state, 7, "request_take", 6, "把这一段唱出来");
        Check((string)state.Facts(Input(7), null, true)["binding"]["clip"] == Prefix + 6, "First performance is explicitly bound.");
        state.PlaybackOutcome(Prefix + 6, 7, true);
        Check(state.Facts(Input(7), null, true)["binding"] == null, "Actual completion closes the current request permission, preserving focus.");
        Apply(state, 8, "expect_take", 0, "我接下来唱第二份");
        Apply(state, 9, "observe_and_request_take", 9, "新演唱已录完，请回唱这份");
        Apply(state, 13, "delete_take", 9, "把这份排除");
        Check(!state.Allows(Prefix + 9, Input(13), out _), "Logical deletion excludes the identified take and does not choose another source.");
        Apply(state, 14, "expect_take", 0, "我会重新演唱");
        Apply(state, 15, "observe_take", 15, "新演唱已经录完");
        var c16 = state.Facts(Input(16), Source(16, "我刚才唱过了"), true);
        Check((string)c16["focus"]["clip"] == Prefix + 15 && c16["binding"] == null,
            "A later ordinary recording cannot replace the last semantic performance or authorize singing.");
        Apply(state, 17, "request_take", 15, "把它唱出来");
        Check(state.Allows(Prefix + 15, Input(17), out _) && !state.Allows(Prefix + 6, Input(17), out _),
            "Explicit current request binds the new performance; an older playable first verse cannot substitute.");
        var context = Context(state, 17);
        context.CurrentInput["timeOrder"] = new JArray(new JObject { ["start"] = 3.23f, ["end"] = 11.87f });
        var archive = RecordingArchiveSnapshot.Create(context);
        Check(archive.TrySessionCandidates(context, out var selected, out _) && selected.Materials.Count == 1 && selected.Materials[0].ClipRef == Prefix + 15,
            "Production archive expands one already-bound take, without archive retrieval or last-playback filtering.");
        Check((string)archive.CandidateReviewFacts(selected)["singing_session"]["binding"]["clip"] == Prefix + 15,
            "The exact session binding reaches independent-review facts.");
        Check(SingingSessionProtocol.ValidateSources(selected.SingingSession, selected.CurrentInput, new[] { Prefix + 15 }, out _) &&
            !SingingSessionProtocol.ValidateSources(selected.SingingSession, selected.CurrentInput, new[] { Prefix + 6 }, out _),
            "IR/review source validation cannot silently change the explicit take.");
        context.ArchiveExpansionRequested = true;
        Check(!archive.TrySessionCandidates(context, out _, out var expansionError) && expansionError == null,
            "Explicit expansion can still reach the complete immutable archive.");
        Apply(state, 18, "request_take", 6, "请用原来的第一份");
        Check((string)state.Facts(Input(18), null, true)["binding"]["clip"] == Prefix + 6,
            "An explicit fresh semantic request may select the original take; focus is not a latest-source rule.");
        Check(!state.RejectBinding(Input(17), Prefix + 6, "wrong source") &&
            !state.RejectBinding(Input(18), Prefix + 15, "wrong source"),
            "Independent rejection cannot revoke another input or a different proposed source.");
        Check(state.RejectBinding(Input(18), Prefix + 6, "本轮指向新演唱，旧段不符"),
            "The current independent rejection releases only the mistaken binding.");
        var rejected = state.Facts(Input(18), null, true);
        Check(rejected["binding"] == null && (string)rejected["pending"]["kind"] == "rejected_request" &&
            (string)rejected["focus"]["clip"] == Prefix + 6 && state.Allows(Prefix + 15, Input(18), out _) &&
            !state.Allows(Prefix + 9, Input(18), out _),
            "Rejection keeps the user's unfinished request and focus visible, permits full source reconsideration, and preserves deletion constraints.");
        var stale = new SingingSessionUpdate { Operation = "request_take", Take = Prefix + 15, Evidence = "改用新份",
            RequestFacts = state.Facts(Input(19), null, true) };
        Apply(state, 19, "cancel", 0, "取消歌唱待办");
        Check(!state.Apply(stale, Input(19), "改用新份", out _) && state.Facts(Input(19), null, true)["binding"] == null,
            "A concurrent cancellation invalidates an older session revision.");
        var facts = state.Facts(Input(20), Source(20, "普通讲话"), true);
        var bad = new SingingSessionUpdate { Operation = "observe_take", Take = Prefix + 6, Evidence = "普通讲话", RequestFacts = facts };
        Check(!state.Apply(bad, Input(20), "普通讲话", out _), "Observe cannot bind an older capture, even if the model names a valid historical take.");
        Check(!state.Apply(new SingingSessionUpdate { Operation = "request_take", Take = Prefix + 15, Evidence = "补写的证据", RequestFacts = facts },
            Input(20), "普通讲话", out _), "Evidence must be an exact current-input span, not a generated quotation.");
        var noAudio = state.Facts(Input(21), null, true);
        Check(SingingSessionProtocol.TakeIds(noAudio).Contains(Prefix + 6), "JSON-null currentSource preserves historical take candidates safely.");
        var policy = FastLoopRequestPolicy.FromContext(new JObject { ["singingSession"] = noAudio }.ToString());
        var response = Response("none", "", "", "reply", "听到了。");
        Check(FastLoopProtocol.TryReadBoundResponse(Envelope(response), "听到了吗", policy, out _, out _, out _),
            "The production request-bound parser accepts the conditional three-field protocol.");
        var currentSpeech = state.Facts(Input(22), Source(22, "把它唱出来"), true);
        var speechPolicy = FastLoopRequestPolicy.FromContext(new JObject { ["singingSession"] = currentSpeech }.ToString());
        Check(!FastLoopProtocol.TryReadBoundResponse(Envelope(Response("request_take", Prefix + 22, "把它唱出来", "recording_singing", "")),
            "把它唱出来", speechPolicy, out _, out _, out _),
            "A retained ordinary request capture cannot be selected as an existing take.");
        var branches = (JArray)speechPolicy.Constrain(FastLoopProtocol.ResponseFormat())["json_schema"]["schema"]["anyOf"];
        Check(branches.OfType<JObject>().Where(b => (string)b["properties"]["session_update"]["enum"][0] == "request_take")
            .All(b => !b["properties"]["session_take"]["enum"].Values<string>().Contains(Prefix + 22)) &&
            branches.OfType<JObject>().All(b => ((JObject)b["properties"]).Properties().First().Name == "session_update"),
            "The actual grammar enforces registered/current source roles and resolves session events before dialogue routing.");
        response["session_update"] = "delete_take"; response["session_take"] = Prefix + 6; response["session_evidence"] = "删除"; response["route"] = "legacy"; response["speech"] = "";
        Check(!FastLoopProtocol.TryReadBoundResponse(Envelope(response), "删除", policy, out _, out _, out _),
            "A compound legacy route cannot claim a partial logical deletion and silently drop other goals.");
        var ordinary = FastLoopProtocol.ResponseFormatForContext("{}");
        Check(((JArray)ordinary["json_schema"]["schema"]["anyOf"]).All(b => b["properties"]["session_update"] == null),
            "Ordinary requests keep the nine-field schema.");
        var eventSchema = (JObject)SingingSessionEventProtocol.ResponseFormat(currentSpeech)["json_schema"]["schema"];
        Check(eventSchema["anyOf"] == null && ((JObject)eventSchema["properties"]).Properties().First().Name == "session_evidence" &&
            (int)eventSchema["properties"]["session_evidence"]["maxLength"] == 96,
            "The independent event wire starts with bounded current evidence before its operation enum.");
        var eventValue = new JObject { ["session_evidence"] = "把它唱出来", ["session_update"] = "request_take", ["session_take"] = Prefix + 22 };
        Check(!SingingSessionEventProtocol.TryReadResponse(Envelope(eventValue), currentSpeech, "把它唱出来", out _, out _),
            "Flat wire ID eligibility does not authorize a spoken current capture as an existing take.");
        eventValue["session_update"] = "observe_take"; eventValue["session_take"] = Prefix + 6;
        Check(!SingingSessionEventProtocol.TryReadResponse(Envelope(eventValue), currentSpeech, "把它唱出来", out _, out _),
            "The independent parser still rejects observing a historical take as the current performance.");
        eventValue["session_evidence"] = new string('字', 97); eventValue["session_update"] = "request_take"; eventValue["session_take"] = Prefix + 15;
        Check(!SingingSessionEventProtocol.TryReadResponse(Envelope(eventValue), currentSpeech, new string('字', 97), out _, out _),
            "The independent parser enforces its wire evidence bound even on a server response that ignores schema.");
        eventValue["session_evidence"] = "把它唱出来"; eventValue["session_update"] = "none"; eventValue["session_take"] = "";
        Check(SingingSessionEventProtocol.TryReadResponse(Envelope(eventValue), currentSpeech, "把它唱出来", out var noEvent, out _) &&
            noEvent.Operation == "none" && noEvent.Take == "" && noEvent.Evidence == "",
            "A verified current quote supporting no event normalizes to the existing authority-free no-op.");
        eventValue["session_evidence"] = "模型编造的原话";
        Check(!SingingSessionEventProtocol.TryReadResponse(Envelope(eventValue), currentSpeech, "把它唱出来", out _, out _),
            "The no-op quote adapter still rejects evidence invented outside the current input.");
        RuntimeChecks();
        return checks;
    }

    static void RuntimeChecks()
    {
        object fixture = Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
        try
        {
            var chat = (ChatSample)Get(fixture, "Chat");
            var state = new SingingSessionState(); Apply(state, 15, "observe_take", 15, "实际新演唱");
            Set(chat, "m_SingingSession", state);
            const string user = "把它唱出来";
            Call(fixture, "Arm", user, false); Call(chat, "SetCurrentUserInput", user, user);
            var pending = (FastLoopIntegrationModel.PendingRequest)Call(fixture, "Dispatch", new object[] { null });
            var runtimeSession = (JObject)JObject.Parse(pending.Context)["singingSession"];
            var policy = FastLoopRequestPolicy.FromContext(pending.Context);
            Check(policy.SingingSession == null && (string)runtimeSession["focus"]["clip"] == Prefix + 15,
                "Production Fast context keeps a take across an accepted user turn.");
            var raw = Response("request_take", Prefix + 15, user, "reply", "我先记住这份。");
            var eventRaw = new JObject { ["session_update"] = raw["session_update"], ["session_take"] = raw["session_take"], ["session_evidence"] = raw["session_evidence"] };
            raw.Remove("session_update"); raw.Remove("session_take"); raw.Remove("session_evidence");
            Check(FastLoopProtocol.TryReadBoundResponse(Envelope(raw), user, policy, out var decision, out _, out _) &&
                SingingSessionEventProtocol.TryReadResponse(Envelope(eventRaw), runtimeSession, user, out var parsedEvent, out _),
                "The independent actual role and event parsers create their bounded results.");
            SingingSessionEventProtocol.TryReadResponse(Envelope(eventRaw), runtimeSession, user, out var eventUpdate, out _);
            decision.SessionUpdate = eventUpdate;
            pending.Answer(decision, "controlled-session-test");
            var bound = (JObject)Call(chat, "BuildSingingSessionBinding", Call(chat, "RecordingInputIdentity"));
            Check((string)bound["binding"]["clip"] == Prefix + 15,
                "The actual Fast callback applies the chosen take without requiring a singing execution route.");
            Check(Get(chat, "m_SingingGoal") == null, "Registering a request via reply does not create a singing goal or performance.");
            const string remove = "把这份从本次候选中移除";
            Call(fixture, "Arm", remove, false); Call(chat, "SetCurrentUserInput", remove, remove);
            pending = (FastLoopIntegrationModel.PendingRequest)Call(fixture, "Dispatch", new object[] { null });
            policy = FastLoopRequestPolicy.FromContext(pending.Context);
            runtimeSession = (JObject)JObject.Parse(pending.Context)["singingSession"];
            raw = Response("delete_take", Prefix + 15, remove, "reply", "已经物理删除了所有录音");
            eventRaw = new JObject { ["session_update"] = raw["session_update"], ["session_take"] = raw["session_take"], ["session_evidence"] = raw["session_evidence"] };
            raw.Remove("session_update"); raw.Remove("session_take"); raw.Remove("session_evidence");
            Check(FastLoopProtocol.TryReadBoundResponse(Envelope(raw), remove, policy, out decision, out _, out _) &&
                SingingSessionEventProtocol.TryReadResponse(Envelope(eventRaw), runtimeSession, remove, out parsedEvent, out _), "Deletion is a bounded session operation.");
            SingingSessionEventProtocol.TryReadResponse(Envelope(eventRaw), runtimeSession, remove, out eventUpdate, out _);
            decision.SessionUpdate = eventUpdate;
            pending.Answer(decision, "controlled-session-test");
            string queued = (string)Call(fixture, "Queued");
            Check(queued.Contains("原始音频仍保留") && !queued.Contains("物理删除"),
                "Actual callback replaces an inaccurate deletion draft with an executed logical-state receipt.");
            Check(!(bool)Call(chat, "ValidateSingingSessionSources", new object[] { Prefix + 15, null }),
                "The playback/preparation gate rejects a logically deleted take.");
        }
        finally { ((IDisposable)fixture).Dispose(); }
    }

    public static JObject Export(string root)
    {
        string folder = Path.Combine(root, "work/singing-subsystem-20260929/session");
        var inputs = JArray.Parse(File.ReadAllText(Path.Combine(folder, "actual-inputs.json"))).OfType<JObject>().ToArray();
        var history = JObject.Parse(File.ReadAllText(Path.Combine(root, "work/singing-subsystem-20260929/actual-dialogue-inputs.json")));
        var state = new SingingSessionState(); var cases = new JArray();
        var host = new GameObject("Inactive singing session model export"); host.SetActive(false);
        var assets = new List<TextAsset>();
        try
        {
            var q = host.AddComponent<ChatQW>(); q.m_Backend = ChatQW.BackendType.OpenAICompatible;
            q.m_OpenAICompatibleModelName = "qwen3.6-35b-a3b"; q.m_LogRequestStats = false;
            // The Qwen component in chatSample.unity binds exactly these three
            // prompt assets; singing is a separately loaded skill, not persona.
            typeof(LLM).GetField("lan", F).SetValue(q, "日语");
            foreach (string name in new[] { "persona", "language", "behavior" })
            { var asset = new TextAsset(File.ReadAllText(Path.Combine(root, "Assets/AIChatTookit/Prompts/" + name + ".txt"))) { name = name }; assets.Add(asset); }
            typeof(LLM).GetField("m_PromptFiles", F).SetValue(q, assets.ToArray());
            foreach (var row in inputs.Where(r => (int)r["capture"] >= 6 && (int)r["capture"] <= 18))
            {
                int capture = (int)row["capture"]; string user = (string)row["transcript"];
                string operation = capture == 6 ? "observe_take" : capture == 9 || capture == 15 ? "observe_and_request_take" : capture == 7 || capture == 17 || capture == 18 ? "request_take"
                    : capture == 8 || capture == 14 ? "expect_take" : capture == 13 ? "delete_take" : "none";
                int take = capture == 7 ? 6 : capture == 13 ? 9 : capture >= 17 ? 15 : capture;
                JObject source = Source(capture, user); source["timeOrder"] = FastLoopPrompt.TimeOrderedObservation(row["segments"] as JArray);
                var session = state.Facts(Input(capture), source, true);
                var context = new JObject { ["actorDraftCompiler"] = true, ["actorDraftCapabilities"] = FastLoopProtocol.ActorDraftCapabilities(true),
                    ["singingSession"] = session, ["inputObservation"] = new JObject { ["acceptedInputBound"] = true, ["source"] = "accepted-final-audio",
                        ["acoustic"] = new JObject { ["clipRef"] = row["clip"], ["captureSessionSerial"] = capture, ["timeOrder"] = source["timeOrder"]?.DeepClone() } },
                    ["voiceCapabilities"] = new JObject { ["singing"] = new JObject { ["enabled"] = true, ["svcEnabled"] = true,
                        ["paths"] = new JObject { ["recordingSingBack"] = new JObject { ["registered"] = true, ["handoffRoute"] = "recording_singing" } } } },
                    ["recentDialogue"] = FastLoopPrompt.Dialogue(new JArray(inputs.Select(r => new JObject { ["turn"] = r["capture"], ["text"] = r["transcript"] })),
                        new JArray(((JArray)history["completedSpeech"]).OfType<JObject>().Select(r => new JObject { ["userTurn"] = r["capture"], ["text"] = r["text"] })), capture) };
                if (new[] { 6, 7, 9, 13, 14, 15, 16, 17, 18 }.Contains(capture))
                    cases.Add(new JObject { ["id"] = "c" + capture, ["capture"] = capture, ["lf"] = row["lf"], ["currentUser"] = user,
                        ["originalAsrEvidence"] = row["asrText"], ["context"] = context, ["expectedOperation"] = operation,
                        ["expectedTake"] = operation == "none" || operation == "expect_take" ? "" : Prefix + take,
                        ["wire"] = JObject.Parse((string)Call(q, "BuildFastLoopRequestJson", context.ToString(Formatting.None), user, null)),
                        ["scope"] = "Current production Qwen factory and session reducer; original final-ASR transcript/time-order and completed speech replay. Earlier accepted semantic operations are controlled seeds, not claims that historical or current model chose them. No endpoint/audio execution." });
                if (operation != "none") Apply(state, capture, operation, operation == "expect_take" ? 0 : take, user, source);
                if (capture == 7) state.PlaybackOutcome(Prefix + 6, 7, true);
            }
            string explicitUser = "请把原来的第一段再唱一遍，不是刚重新唱的第二段。";
            var last = (JObject)cases.Last.DeepClone(); last["id"] = "explicit-first-verse"; last["capture"] = 19; last["lf"] = null;
            last["currentUser"] = explicitUser; last["originalAsrEvidence"] = null;
            last["context"]["singingSession"] = state.Facts(Input(19), null, true);
            last["context"]["inputObservation"] = new JObject { ["acceptedInputBound"] = true,
                ["source"] = "accepted-text", ["acoustic"] = null };
            last["expectedOperation"] = "request_take"; last["expectedTake"] = Prefix + 6;
            last["wire"] = JObject.Parse((string)Call(q, "BuildFastLoopRequestJson", last["context"].ToString(Formatting.None), explicitUser, null));
            last["scope"] = "Counterfactual explicit-old-source control, current production factory; not historical input.";
            cases.Add(last);
            File.WriteAllText(Path.Combine(folder, "model-cases.json"), cases.ToString());
            return new JObject { ["cases"] = cases.Count, ["path"] = Path.Combine(folder, "model-cases.json"), ["networkRequests"] = 0 };
        }
        finally { UnityEngine.Object.DestroyImmediate(host); foreach (var asset in assets) UnityEngine.Object.DestroyImmediate(asset); }
    }

    static RecordingSingingDecisionContext Context(SingingSessionState state, int turn)
    {
        var input = Input(turn); input.Remove("acceptedRevision");
        var context = new RecordingSingingDecisionContext { UserText = "把它唱出来", CurrentInput = input,
            SingingSession = state.Facts(Input(turn), null, true) };
        foreach (int capture in new[] { 6, 9, 15 }) context.Materials.Add(new RecordingSingingMaterial { Key = Prefix + capture,
            ClipRef = Prefix + capture, CaptureSerial = capture, RecordingSequence = capture, Revision = "pending",
            Description = "{}", PlaybackStatus = "evidence_only", HasCleanRange = true, HasExpandedRange = true });
        context.ArchiveInputEvents.Add(new JObject { ["turn"] = 15, ["capture"] = 15, ["clip"] = Prefix + 15, ["text"] = "新演唱已经录完" });
        return context;
    }
    static void Apply(SingingSessionState state, int capture, string operation, int take, string user, JObject source = null)
    {
        var facts = state.Facts(Input(capture), source ?? Source(capture, user), true);
        var update = new SingingSessionUpdate { Operation = operation, Take = take == 0 ? "" : Prefix + take, Evidence = user.Length > 300 ? user.Substring(0, 300) : user, RequestFacts = facts };
        if (!state.Apply(update, Input(capture), user, out var receipt)) throw new InvalidOperationException("Controlled session seed failed: " + receipt);
    }
    static JObject Input(int capture) => new JObject { ["turn"] = capture, ["capture"] = capture, ["clip"] = Prefix + capture, ["acceptedRevision"] = capture };
    static JObject Source(int capture, string text) => new JObject { ["clip"] = Prefix + capture, ["capture"] = capture, ["transcript"] = text };
    static JObject Response(string operation, string take, string evidence, string route, string speech) => new JObject {
        ["version"] = 2, ["task_control"] = "none", ["control_target_id"] = "", ["control_evidence"] = "", ["route"] = route,
        ["candidate"] = "none", ["evidence"] = "", ["language"] = "zh", ["speech"] = speech,
        ["session_update"] = operation, ["session_take"] = take, ["session_evidence"] = evidence };
    static string Envelope(JObject response) => new JObject { ["choices"] = new JArray(new JObject {
        ["finish_reason"] = "stop", ["message"] = new JObject { ["content"] = response.ToString(Formatting.None) } }) }.ToString();
    static object Get(object v, string name) => v.GetType().GetField(name, F).GetValue(v);
    static void Set(object v, string name, object value) => v.GetType().GetField(name, F).SetValue(v, value);
    static object Call(object v, string name, params object[] args) => v.GetType().GetMethods(F).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(v, args);
    static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
}
#endif
