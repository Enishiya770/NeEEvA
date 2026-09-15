using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Controlled review verdicts exercise production ownership and permission commits.
// Public model cases are exported separately; controlled verdicts are not semantic accuracy evidence.
public static class AgentSingingPolicyRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }
    private const string BatchKey = "NeEEvA.SingingPolicyRegression.Running";
    private static readonly Stack<IEnumerator> batchStack = new Stack<IEnumerator>();
    private static GameObject batchHost;
    private static string batchOutput;

    public static void RunBatch()
    {
        if (Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("RunBatch is restricted to the existing isolated Unity validation project.");
        batchOutput = Environment.GetEnvironmentVariable("NEEEVA_POLICY_REGRESSION_OUTPUT");
        if (string.IsNullOrWhiteSpace(batchOutput)) batchOutput = Path.GetFullPath("Tools/MotionAdapter/reports/singing-policy");
        SessionState.SetString(BatchKey + ".Output", batchOutput); SessionState.SetBool(BatchKey, true);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.update -= StepBatch; EditorApplication.update += StepBatch;
        EditorApplication.EnterPlaymode();
    }

    [InitializeOnLoadMethod]
    private static void ResumeBatch()
    {
        if (!SessionState.GetBool(BatchKey, false)) return;
        batchOutput = SessionState.GetString(BatchKey + ".Output", "");
        EditorApplication.update -= StepBatch; EditorApplication.update += StepBatch;
    }

    private static void StepBatch()
    {
        if (!SessionState.GetBool(BatchKey, false) || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
        try
        {
            if (batchHost == null)
            {
                batchHost = new GameObject("Isolated singing policy regression"); batchHost.SetActive(false);
                var chat = batchHost.AddComponent<ChatSample>();
                var button = new GameObject("Unpressed send button").AddComponent<UnityEngine.UI.Button>(); button.transform.SetParent(batchHost.transform);
                var model = batchHost.AddComponent<SingingPolicyFixtureModel>();
                Set(chat, "m_CommitMsgBtn", button); Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
                Set(chat, "m_PersistSubtitleSettings", false); Set(chat, "m_EnableSubtitleTranslation", false);
                batchHost.SetActive(true);
                var exportHost = new GameObject("Inactive production policy payload exporter");
                exportHost.SetActive(false); exportHost.transform.SetParent(batchHost.transform);
                var productionModel = exportHost.AddComponent<ChatQW>();
                productionModel.m_Backend = ChatQW.BackendType.Local;
                productionModel.m_LocalModelName = "antoneva";
                ExportPublicModelCases(productionModel, batchOutput);
                batchStack.Push(RunChecks(chat, batchOutput));
            }
            while (batchStack.Count > 0)
            {
                var current = batchStack.Peek();
                if (!current.MoveNext()) { batchStack.Pop(); continue; }
                if (current.Current is IEnumerator nested) { batchStack.Push(nested); continue; }
                return;
            }
            FinishBatch(null);
        }
        catch (Exception error) { FinishBatch(error); }
    }

    private static void FinishBatch(Exception error)
    {
        EditorApplication.update -= StepBatch; SessionState.SetBool(BatchKey, false);
        if (error != null)
        {
            LastReport = new JObject { ["status"] = "failed", ["checks"] = checks, ["error"] = error.ToString() };
            Directory.CreateDirectory(batchOutput); File.WriteAllText(Path.Combine(batchOutput, "singing-policy-regression.json"), LastReport.ToString());
            Debug.LogException(error);
        }
        if (batchHost != null) UnityEngine.Object.DestroyImmediate(batchHost);
        EditorApplication.Exit(error == null ? 0 : 1);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags).GetValue(owner);
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags).SetValue(owner, value);
    private static object Call(object owner, string name, params object[] values) => owner.GetType().GetMethod(name, Flags).Invoke(owner, values);
    private static SingingPolicyReviewResult Decision(string user, string candidate, bool grant, string quote = null) =>
        SingingPolicyReviewProtocol.ReadDecision(new JObject {
            ["decision"] = grant ? "grant" : "not-grant", ["candidate"] = candidate,
            ["user_quote"] = grant ? quote ?? user : "", ["evidence"] = grant ? "当前用户明确要求对应权限变化。" : "当前用户仅反馈没听到，不要求改变歌唱权限。"
        }.ToString(), new SingingPolicyReviewRequest { UserText = user, Candidate = candidate });

    public static int RunProtocolChecks()
    {
        int before = checks;
        var request = new SingingPolicyReviewRequest { UserText = "请禁用唱歌能力，之后也不要唱了。", Candidate = "disable", CurrentAccess = "Available" };
        string Json(string disposition, string candidate, string quote, string evidence = "用户明确要求禁用。") =>
            new JObject { ["decision"] = disposition, ["candidate"] = candidate, ["user_quote"] = quote, ["evidence"] = evidence }.ToString();
        string good = Json("grant", "disable", "请禁用唱歌能力");
        Check(SingingPolicyReviewProtocol.ReadDecision(good, request).Granted, "Exact current-user evidence failed.");
        foreach (string reason in new[] { "length", "content_filter", "", "tool_calls" })
            Check(!SingingPolicyReviewProtocol.ReadDecision(good, request, reason).Granted, "Non-final review granted a permission.");
        foreach (string json in new[] { "", "[]", "{", "{\"decision\":\"grant\"}", "```json\n" + good + "\n```",
            Json("maybe", "disable", request.UserText), Json("grant", "delete", request.UserText),
            Json("grant", "disable", ""), Json("grant", "disable", "用户说要禁用"), Json("grant", "enable", request.UserText),
            Json("grant", "disable", request.UserText, ""), Json("grant", "disable", request.UserText, new string('a', 257)),
            good.TrimEnd().TrimEnd('}') + ",\"extra\":true}",
            "{\"decision\":\"not-grant\",\"decision\":\"grant\",\"candidate\":\"disable\",\"user_quote\":\"请禁用唱歌能力\",\"evidence\":\"x\"}" })
            Check(!SingingPolicyReviewProtocol.ReadDecision(json, request).Granted, "Invalid schema/evidence granted a permission: " + json);
        var rejected = SingingPolicyReviewProtocol.ReadDecision(Json("not-grant", "disable", ""), request);
        Check(rejected.Valid && !rejected.Granted, "Valid refusal was mistaken for an invalid transport result.");
        string envelope = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = "stop", ["message"] = new JObject { ["content"] = good } }) }.ToString();
        Check(SingingPolicyReviewProtocol.ReadResponse(envelope, request).Granted, "Completed wire review was not accepted.");
        Check(!SingingPolicyReviewProtocol.ReadResponse(envelope.Replace("\"stop\"", "\"length\""), request).Granted, "Truncated HTTP-200 review granted policy.");
        Check(!SingingPolicyReviewProtocol.ReadResponse("{}", request).Granted, "Missing choice granted policy.");
        return checks - before;
    }

    public static IEnumerator RunChecks(ChatSample chat, string output)
    {
        checks = 0;
        var cases = new JArray();
        RunProtocolChecks();
        using (var f = new Fixture(chat))
        {
            f.Arm("没听到啊。");
            Check(f.Queue("suppress_autonomy"), "A policy proposal was not accepted for private review.");
            Check(f.Pending && f.Access == "Available" && f.Model.Requests.Count == 1, "Pending proposal changed permission or did not retain ownership.");
            Check((bool)Call(chat, "IsSingingPolicyActionDeferredThisRound"), "Submitting a policy allowed its same-response actions.");
            Check((int)Get(chat, "m_SingingRejectedSpeechRound") == (int)Get(chat, "m_CurrentSpeechRoundId"), "Unreviewed promises were not withheld.");
            var first = f.Model.Requests[0];
            Check(first.Request.UserText == "没听到啊。" && first.Request.Candidate == "suppress_autonomy", "Review did not receive the actual current user/proposed value.");
            first.Callback(Decision(first.Request.UserText, first.Request.Candidate, false));
            Check(!f.Pending && f.Access == "Available" && f.Fact.Contains("not-granted") && f.Fact.Contains("\"applied\":false"), "Negative review changed policy or lost its fact.");
            first.Callback(Decision(first.Request.UserText, first.Request.Candidate, true));
            Check(f.Access == "Available", "A duplicate callback after refusal granted policy.");
            Check(f.Queue("suppress_autonomy") && f.Model.Requests.Count == 1, "Same-turn retry caused an extra semantic request.");
            Check(!f.Queue("disable") && f.Model.Requests.Count == 1, "Changing candidate in the same turn bypassed the review budget.");
            cases.Add("feedback-not-granted-idempotent-and-no-permission-side-effect");

            foreach (var tuple in new[] {
                new[] { "请禁用唱歌能力，以后我点歌也不要唱。", "disable", "UserDisabled" },
                new[] { "把唱歌能力恢复开启吧。", "enable", "Available" },
                new[] { "先别主动唱，但我让你唱时可以唱。", "suppress_autonomy", "SoftSuppressed" }
            })
            {
                f.Arm(tuple[0]); Check(f.Queue(tuple[1]) && f.Pending, "Explicit permission proposal did not enter review.");
                var pending = f.Model.Requests.Last();
                pending.Callback(Decision(tuple[0], tuple[1], true));
                Check(f.Access == tuple[2] && !f.Pending && f.Fact.Contains("\"applied\":true"), "Reviewed permission was not committed once.");
                Check((string)Get(f.State, "LastPolicyUserText") == tuple[0], "Applied policy lost the exact authorizing user turn.");
                Check((bool)Call(chat, "IsSingingPolicyActionDeferredThisRound"), "Immediate policy completion released old accompanying actions.");
            }
            cases.Add("explicit-disable-enable-suppress-commit-only-after-review");

            f.Arm("停止当前歌曲，并且禁用歌唱能力。", "Available");
            // A controlled live transaction uses the real stop/cleanup path, with no network request or PCM source.
            Set(chat, "m_HumBackPending", true); Set(chat, "m_HumBackPlaying", true);
            Check((bool)Call(chat, "IsSingingRuntimeActivityActive"), "Stop/policy fixture did not establish an active singing transaction.");
            Check(f.Queue("disable") && f.Pending && f.Access == "Available", "Disable proposal changed policy before its independent review.");
            object stopPolicyTicket = Get(chat, "m_PendingSingingPolicyReview");
            var stopPolicyRequest = f.Model.Requests.Single();
            int cancellationsBeforeStop = f.Model.Cancellations;
            Call(chat, "CancelSkillRuntimeActivity", "singing", "singing-stop-tool");
            Check(!(bool)Call(chat, "IsSingingRuntimeActivityActive"), "singing_stop failed to stop the current transaction immediately.");
            Check(f.Pending && f.Access == "Available" && ReferenceEquals(stopPolicyTicket, Get(chat, "m_PendingSingingPolicyReview")) &&
                f.Model.Cancellations == cancellationsBeforeStop,
                "Stopping the current song cancelled the same response's independent policy review or changed policy.");
            Check(f.Queue("disable") && f.Model.Requests.Count == 1 && ReferenceEquals(stopPolicyTicket, Get(chat, "m_PendingSingingPolicyReview")),
                "Repeated policy proposal after singing_stop replaced its existing review ticket.");
            stopPolicyRequest.Callback(Decision(stopPolicyRequest.Request.UserText, "disable", true));
            Check(!f.Pending && f.Access == "UserDisabled" && f.Fact.Contains("\"applied\":true"),
                "Stopping the current song prevented a later valid independent policy grant.");
            cases.Add("same-response-current-song-stop-preserves-pending-policy-review");

            foreach (string staleBy in new[] { "new-user", "work-epoch", "goal-turn", "model-replaced", "policy-revision", "input-active", "shutdown", "disabled" })
            {
                f.Arm("请禁用唱歌能力。", "Available");
                Check(f.Queue("disable"), "Stale-case proposal failed to queue.");
                var pending = f.Model.Requests.Last();
                if (staleBy == "new-user") { Call(chat, "ResetSingingPolicyReview", "new-user"); Set(chat, "m_LastUserMsg", "不用改权限。"); }
                if (staleBy == "work-epoch") Set(chat, "m_WorkEpoch", (int)Get(chat, "m_WorkEpoch") + 1);
                if (staleBy == "goal-turn") Set(chat, "m_SingingGoalTurn", (int)Get(chat, "m_SingingGoalTurn") + 1);
                if (staleBy == "model-replaced") f.Settings.m_ChatModel = f.Other;
                if (staleBy == "policy-revision") Set(f.State, "LastPolicyChangedAt", Time.realtimeSinceStartup + 1f);
                if (staleBy == "input-active") Set(chat, "m_UserSpeechActiveForAutonomy", true);
                if (staleBy == "shutdown") Set(chat, "m_AgentGracefulShutdownPending", true);
                if (staleBy == "disabled") chat.enabled = false;
                string factBefore = f.Fact;
                pending.Callback(Decision(pending.Request.UserText, "disable", true));
                Check(f.Access == "Available" && !f.Pending && f.Fact == factBefore, "A stale callback changed permission or published a current fact: " + staleBy);
                cases.Add("stale-" + staleBy + "-no-side-effects");
            }

            foreach (string failure in new[] { "network", "invalid-schema", "wrong-candidate", "foreign-quote", "incomplete" })
            {
                f.Arm("请禁用唱歌能力。", "Available"); Check(f.Queue("disable"), "Failure-case queue failed.");
                var pending = f.Model.Requests.Last();
                var response = Decision(pending.Request.UserText, "disable", true);
                if (failure == "network") response = SingingPolicyReviewResult.Failed("policy-review-http-failure");
                if (failure == "invalid-schema") response.Valid = false;
                if (failure == "wrong-candidate") response.Candidate = "enable";
                if (failure == "foreign-quote") response.UserQuote = "别人的授权";
                if (failure == "incomplete") response.FinishReason = "length";
                pending.Callback(response);
                Check(!f.Pending && f.Access == "Available" && f.Fact.Contains("review-failed"), "Review failure granted policy or stayed pending: " + failure);
            }
            cases.Add("failed-invalid-mismatched-review-keeps-current-permission");

            f.Arm("请禁用唱歌能力。", "Available");
            f.Model.Synchronous = request => Decision(request.UserText, request.Candidate, true);
            Check(f.Queue("disable") && !f.Pending && f.Access == "UserDisabled", "Synchronous provider raced owner setup or left a pending ticket.");
            Check(f.Fact.Contains("\"outcome\":\"applied\"") && (bool)Call(chat, "IsSingingPolicyActionDeferredThisRound"), "Synchronous grant released the old response or lost the applied fact.");
            f.Arm("没听到啊。", "Available");
            f.Model.Synchronous = request => Decision(request.UserText, request.Candidate, false);
            Check(f.Queue("suppress_autonomy") && !f.Pending && f.Access == "Available", "Synchronous refusal changed policy.");
            f.Arm("请禁用唱歌能力。", "Available"); f.Model.ThrowOnRequest = true;
            Check(f.Queue("disable") && !f.Pending && f.Access == "Available" && f.Fact.Contains("dispatch-failed"), "Thrown provider left a pending review or changed policy.");
            cases.Add("synchronous-grant-refusal-and-throw-settle-without-old-action-release");

            f.Arm("请禁用唱歌能力。", "Available"); Set(chat, "m_AgentCurrentRoundIsTick", true);
            Check(!f.Queue("disable") && f.Model.Requests.Count == 0 && f.Access == "Available", "Autonomy changed the user's policy or requested authorization for itself.");
            cases.Add("autonomy-cannot-authorize-user-policy");

            foreach (string invalidContext in new[] { "invalid-value", "autonomy", "missing-model", "missing-user" })
            {
                f.Arm("请禁用唱歌能力。", "Available");
                string candidate = invalidContext == "invalid-value" ? "invented-policy" : "disable";
                Set(chat, "m_AgentRunning", true);
                // Establish a real after_action speech dependency with a declared action. Otherwise the
                // missing-action fallback could conceal a failure to mark this policy proposal rejected.
                Call(chat, "ObserveSingingSpeechPhase", new SpeechText("已经关闭唱歌能力了。", actionDependency: SpeechActionDependency.AfterAction));
                Set(chat, "m_SingingDeclaredActionRound", (int)Get(chat, "m_CurrentSpeechRoundId"));
                if (invalidContext == "autonomy") Set(chat, "m_AgentCurrentRoundIsTick", true);
                if (invalidContext == "missing-model") f.Settings.m_ChatModel = null;
                if (invalidContext == "missing-user") Set(chat, "m_LastUserMsg", "");
                Check(!f.Queue(candidate) && f.Model.Requests.Count == 0, "Invalid policy context reached semantic transport: " + invalidContext);
                object[] publicSpeech = { "已经关闭唱歌能力了。" };
                Call(chat, "ResolveSingingSpeech", publicSpeech);
                Check((string)publicSpeech[0] == "" && (bool)Get(chat, "m_SingingSpeechWithheld") && f.Access == "Available",
                    "Invalid policy's after_action promise escaped the real speech gate: " + invalidContext);
                Set(chat, "m_AgentRunning", false);
                cases.Add("invalid-policy-" + invalidContext + "-after-action-promise-withheld");
            }

            foreach (string directPolicyContext in new[] { "pending-policy", "invalid-policy" })
            {
                f.Arm("请禁用唱歌能力。", "Available");
                Set(chat, "m_AgentRunning", false);
                Call(chat, "ObserveSingingSpeechPhase", new SpeechText("已经关闭唱歌能力了。", actionDependency: SpeechActionDependency.AfterAction));
                Set(chat, "m_SingingDeclaredActionRound", (int)Get(chat, "m_CurrentSpeechRoundId"));
                bool pendingPolicy = directPolicyContext == "pending-policy";
                bool queued = f.Queue(pendingPolicy ? "disable" : "invented-policy");
                Check(queued == pendingPolicy && f.Pending == pendingPolicy && f.Model.Requests.Count == (pendingPolicy ? 1 : 0),
                    "Direct-conversation policy fixture did not reach the intended pending/rejected state: " + directPolicyContext);
                object[] directSpeech = { "已经关闭唱歌能力了。" };
                Call(chat, "ResolveSingingSpeech", directSpeech);
                Check((string)directSpeech[0] == "" && (bool)Get(chat, "m_SingingSpeechWithheld") && f.Access == "Available",
                    "Direct conversation released a policy-dependent promise without review: " + directPolicyContext);
                cases.Add("non-agent-" + directPolicyContext + "-after-action-promise-withheld");
            }
            f.Arm("今天天气如何？", "Available");
            Set(chat, "m_AgentRunning", false);
            Call(chat, "ObserveSingingSpeechPhase", new SpeechText("我们可以先聊聊今天。", actionDependency: SpeechActionDependency.Independent));
            object[] ordinarySpeech = { "我们可以先聊聊今天。" };
            Call(chat, "ResolveSingingSpeech", ordinarySpeech);
            Check((string)ordinarySpeech[0] == "我们可以先聊聊今天。" && !(bool)Get(chat, "m_SingingSpeechWithheld") && !f.Pending && f.Model.Requests.Count == 0,
                "Direct ordinary independent speech was withheld by a previous policy review.");
            cases.Add("non-agent-ordinary-independent-speech-remains-deliverable");

            f.Arm("为什么还没唱出来？", "Available");
            var goalType = typeof(ChatSample).GetNestedType("SingingGoal", Flags);
            object active = Activator.CreateInstance(goalType, true);
            Set(active, "outcome", "not_submitted"); Set(active, "review", "approved"); Set(active, "completion", "playback");
            Set(chat, "m_ActiveSingingGoal", active);
            Call(chat, "AcquireSkillExecutionLease", "singing", "regression-unfinished-plan", false);
            IDictionary leases = (IDictionary)Get(chat, "m_SkillExecutionLeases");
            object lease = leases["singing"]; int rounds = (int)Get(lease, "RoundsRemaining"); float expires = (float)Get(lease, "ExpiresAt");
            for (int i = 0; i < 3; i++) Call(chat, "UpdateSingingPlanningLeaseAfterDecision", false);
            Check(leases.Contains("singing") && (int)Get(lease, "RoundsRemaining") == rounds && (float)Get(lease, "ExpiresAt") == expires,
                "Unfinished objective lost its planning lease or extended it indefinitely.");
            Check(ReferenceEquals(Get(chat, "m_ActiveSingingGoal"), active) && f.Access == "Available", "Planning closure mutated durable target or permission.");
            Check(!(bool)Call(chat, "HasSkillExecutionLease", "singing", expires + 1f), "Unfinished target bypassed the bounded lease expiry.");
            Check(ReferenceEquals(Get(chat, "m_ActiveSingingGoal"), active), "Expiry discarded the unfinished target.");
            Set(active, "outcome", "playback_complete_content_unverified");
            Call(chat, "AcquireSkillExecutionLease", "singing", "regression-completed", false);
            Call(chat, "UpdateSingingPlanningLeaseAfterDecision", false);
            Check(!leases.Contains("singing") && f.Access == "Available", "Completed plan did not release temporary resources independently of permission.");
            cases.Add("durable-goal-and-bounded-planning-lease-independent-lifetimes");
            // Let settled watchdog coroutines naturally exit before restoring the live fixture.
            yield return null;
        }
        LastReport = new JObject { ["status"] = "passed", ["checks"] = checks, ["cases"] = cases,
            ["scope"] = "Production policy queue, commit, stale ownership, synchronous callbacks and lease lifecycle; review verdicts controlled, no network/audio. Separate exported real-model cases establish semantic accuracy." };
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "singing-policy-regression.json"), LastReport.ToString());
        Debug.Log("[AgentSingingPolicyRegression] passed checks=" + checks);
    }

    public static string ExportPublicModelCases(ChatQW model, string output)
    {
        var cases = new JArray();
        void Add(string id, string user, string candidate, bool expected, string current = "Available", string reference = "")
        {
            var request = new SingingPolicyReviewRequest { UserText = user, Candidate = candidate, CurrentAccess = current, ReferencedSkill = reference };
            cases.Add(new JObject { ["id"] = id, ["expectedGrant"] = expected, ["observation"] = JObject.FromObject(request),
                ["request"] = JObject.Parse((string)Call(model, "BuildSingingPolicyReviewRequestJson", request)) });
        }
        Add("did-not-hear", "没听到啊。", "suppress_autonomy", false);
        Add("why-no-song", "为什么还是没唱出来？再试一下。", "disable", false);
        Add("status-question", "你的唱歌能力被关掉了吗？", "enable", false, "UserDisabled");
        Add("only-stop-current", "停下这首歌吧。", "disable", false);
        Add("quoted-other-request", "他刚才说‘禁用唱歌能力’，但我只是在转述他的话。", "disable", false);
        Add("ordinary-song-request", "请把刚才我唱的那一段回唱给我听。", "enable", false);
        Add("explicit-disable", "请禁用唱歌能力，以后我点歌也不要唱了。", "disable", true);
        Add("explicit-enable", "请恢复唱歌能力，我以后还是会点歌的。", "enable", true, "UserDisabled");
        Add("explicit-suppress", "先别主动唱，但我让你唱时可以唱。", "suppress_autonomy", true);
        Add("current-only-japanese", "今歌っているこの曲だけ止めて。歌う機能はそのままでいい。", "disable", false);
        Add("suppress-english", "Please don't start singing on your own, but do sing when I request a song.", "suppress_autonomy", true);
        Add("wrong-direction", "歌唱能力重新启用吧。", "disable", false, "UserDisabled");
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "singing-policy-public-model-cases.json"); File.WriteAllText(path, cases.ToString());
        return path;
    }

    public static JObject ValidatePublicModelResponses(string fixturePath, string responsesPath)
    {
        var fixtures = JArray.Parse(File.ReadAllText(fixturePath)); var responses = JArray.Parse(File.ReadAllText(responsesPath));
        var results = new JArray(); bool passed = true;
        foreach (JObject item in fixtures)
        {
            string id = (string)item["id"]; var matches = responses.OfType<JObject>().Where(r => (string)r["id"] == id).ToArray();
            var review = matches.Length == 1 ? SingingPolicyReviewProtocol.ReadResponse((string)matches[0]["envelope"], item["observation"].ToObject<SingingPolicyReviewRequest>()) : SingingPolicyReviewResult.Failed("missing-or-duplicate-result");
            bool ok = review.Valid && review.Granted == (bool)item["expectedGrant"]; passed &= ok;
            results.Add(new JObject { ["id"] = id, ["passed"] = ok, ["review"] = JObject.FromObject(review) });
        }
        return new JObject { ["passed"] = passed, ["count"] = fixtures.Count, ["cases"] = results };
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ChatSample Chat;
        public readonly ChatSetting Settings;
        public readonly SingingPolicyFixtureModel Model, Other;
        private readonly GameObject host;
        private readonly LLM priorModel;
        private readonly bool priorEnabled;
        private readonly List<KeyValuePair<FieldInfo, object>> saved = new List<KeyValuePair<FieldInfo, object>>();
        private int epoch = 778000, round = 778000;
        public Fixture(ChatSample chat)
        {
            Chat = chat; priorEnabled = chat.enabled;
            foreach (var field in typeof(ChatSample).GetFields(Flags).Where(f => !f.IsStatic))
            {
                object value = field.GetValue(chat); saved.Add(new KeyValuePair<FieldInfo, object>(field, value));
                if (value is StringBuilder sb) field.SetValue(chat, new StringBuilder(sb.ToString()));
                else if (value is Delegate) field.SetValue(chat, null);
                else if (value != null && value.GetType().IsGenericType && value.GetType().Namespace == "System.Collections.Generic")
                {
                    var empty = value.GetType().GetConstructor(Type.EmptyTypes);
                    if (empty != null) field.SetValue(chat, empty.Invoke(null));
                }
            }
            Settings = (ChatSetting)Get(chat, "m_ChatSettings"); priorModel = Settings.m_ChatModel;
            host = new GameObject("Singing policy review zero-network models");
            Model = host.AddComponent<SingingPolicyFixtureModel>(); Other = host.AddComponent<SingingPolicyFixtureModel>();
            Settings.m_ChatModel = Model;
            Set(chat, "m_AgentRunning", false); Set(chat, "m_SceneTimePaused", true); chat.enabled = true;
        }
        public void Arm(string user, string access = null)
        {
            Call(Chat, "ResetSingingPolicyReview", "fixture-next-turn");
            epoch++; round++; Settings.m_ChatModel = Model; Model.Requests.Clear(); Model.Synchronous = null; Model.ThrowOnRequest = false;
            Set(Chat, "m_WorkEpoch", epoch); Set(Chat, "m_SingingGoalTurn", epoch); Set(Chat, "m_CurrentSpeechRoundId", round);
            Set(Chat, "m_LastUserMsg", user); Set(Chat, "m_PendingSkillStatusFrame", ""); Set(Chat, "m_SingingRejectedSpeechRound", -1);
            Set(Chat, "m_AgentCurrentRoundIsTick", false); Set(Chat, "m_UserSpeechActiveForAutonomy", false); Set(Chat, "m_AgentGracefulShutdownPending", false);
            Set(Chat, "m_WorkNoProgress", false); Set(Chat, "m_SingingGoal", null); Set(Chat, "m_ActiveSingingGoal", null);
            Set(Chat, "m_LastReferencedSkillName", ""); Chat.enabled = true;
            if (access != null) Set(State, "Access", Enum.Parse(Get(State, "Access").GetType(), access));
        }
        public object State => Call(Chat, "GetSkillRouteState", "singing");
        public string Access => Get(State, "Access").ToString();
        public bool Pending => (bool)Call(Chat, "HasPendingSingingPolicyReview");
        public string Fact => (string)Get(Chat, "m_PendingSkillStatusFrame");
        public bool Queue(string candidate)
        {
            object proposal = Activator.CreateInstance(typeof(ChatSample).GetNestedType("AgentSingingPolicyRequest", Flags), true);
            Set(proposal, "Value", candidate); object[] args = { proposal, "" };
            return (bool)Call(Chat, "QueueSingingPolicyReview", args);
        }
        public void Dispose()
        {
            Call(Chat, "ResetSingingPolicyReview", "fixture-complete");
            Settings.m_ChatModel = priorModel;
            foreach (var pair in saved) pair.Key.SetValue(Chat, pair.Value);
            Chat.enabled = priorEnabled; UnityEngine.Object.DestroyImmediate(host);
        }
    }
}

public sealed class SingingPolicyFixtureModel : LLM
{
    public sealed class Pending { public SingingPolicyReviewRequest Request; public Action<SingingPolicyReviewResult> Callback; }
    public readonly List<Pending> Requests = new List<Pending>();
    public Func<SingingPolicyReviewRequest, SingingPolicyReviewResult> Synchronous;
    public bool ThrowOnRequest;
    public int Cancellations;
    public override void PostSingingPolicyReview(SingingPolicyReviewRequest observation, Action<SingingPolicyReviewResult> callback)
    {
        if (ThrowOnRequest) throw new InvalidOperationException("controlled dispatch failure");
        Requests.Add(new Pending { Request = observation, Callback = callback });
        if (Synchronous != null) callback(Synchronous(observation));
    }
    public override void CancelSingingPolicyReview() { Cancellations++; }
}
