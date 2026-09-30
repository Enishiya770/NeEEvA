using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class ActorIntentRegression
{
    static int checks;
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    static string Option(string[] args, string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    static JObject Intent(string kind, string action = "none", string condition = "none", string evidence = "", string domain = null, string task = null, string target = "none")
        => new JObject { ["version"] = ActorIntentProtocol.IntentVersion,
            ["domain"] = domain ?? (kind == "none" ? "dialogue" : "head"),
            ["kind"] = kind, ["task"] = task ?? (domain == "other_action" && kind == "replan" ? "other" : "none"),
            ["goal"] = kind == "none" ? "" : evidence, ["coverage"] = kind == "none" ? "none" : "atomic",
            ["target"] = target, ["action"] = action, ["condition"] = condition, ["evidence"] = evidence,
            ["motionScope"] = domain == "other_action" || kind == "none" ? "none" : "head_preset",
            ["degrees"] = null, ["deviceState"] = "none", ["targetEntityId"] = "" };
    static ActorDraft Draft(string id, int revision, string text) => new ActorDraft { Id = id, Revision = revision, Text = text };
    static ActorIntentDecision Parse(JObject value, string draft)
    {
        Check(ActorIntentProtocol.TryParse(value.ToString(Formatting.None), draft, out var decision, out var reason), "provided actor fixture rejected: " + reason);
        return decision;
    }
    static void Reject(JObject value, string draft, string label)
        => Check(!ActorIntentProtocol.TryParse(value.ToString(Formatting.None), draft, out _, out _), label);

    static void ProtocolChecks()
    {
        CacheLayoutChecks();
        string draft = "了解，这就把视线移向右边。";
        JObject valid = Intent("act", "head.right", evidence: "这就把视线移向右边");
        string wire = valid.ToString(Formatting.None);
        Check(!ActorIntentProtocol.TryParse(wire, "请抬头。", out _, out _), "user text cannot substitute for role-draft evidence");
        var unknown = (JObject)valid.DeepClone(); unknown["source_id"] = "forged"; Reject(unknown, draft, "runtime identity cannot be model-authored");
        var wrongVersion = (JObject)valid.DeepClone(); wrongVersion["version"] = 1; Reject(wrongVersion, draft, "v1 cannot bypass domain validation");
        wrongVersion["version"] = 2; Reject(wrongVersion, draft, "v2 cannot silently infer v3 task and target");
        foreach (string field in new[] { "task", "target", "goal", "coverage" })
        {
            var missing = (JObject)valid.DeepClone(); missing.Remove(field); Reject(missing, draft, "wire must explicitly supply " + field);
            var nullField = (JObject)valid.DeepClone(); nullField[field] = JValue.CreateNull(); Reject(nullField, draft, "null wire " + field + " is not a default");
        }
        var missingDomain = (JObject)valid.DeepClone(); missingDomain.Remove("domain"); Reject(missingDomain, draft, "missing domain is not silently inferred");
        var nullDomain = (JObject)valid.DeepClone(); nullDomain["domain"] = JValue.CreateNull(); Reject(nullDomain, draft, "null domain rejected");
        Reject(Intent("claim", "head.right", evidence: "这就把视线移向右边", domain: "perception"), draft, "perception cannot claim physical completion");
        Reject(Intent("replan", evidence: draft, domain: "perception"), draft, "perception cannot be escalated by a contradictory wire result");
        Reject(Intent("act", "head.right", evidence: draft, domain: "other_action"), draft, "unsupported action cannot impersonate bounded head executor");
        string otherClaimText = "我已经唱完刚才那段旋律了。";
        var otherClaim = Parse(Intent("claim", evidence: "已经唱完刚才那段旋律了", domain: "other_action"), otherClaimText);
        Check(otherClaim.Domain == "other_action" && otherClaim.Kind == "claim" && otherClaim.Action == "none" && otherClaim.Condition == "none",
            "unsupported completed action has a distinct verification-only claim");
        Reject(Intent("claim", "head.hold", evidence: otherClaimText, domain: "other_action"), otherClaimText, "verification-only claim cannot carry a head action");
        Reject(Intent("claim", condition: "user_count_three", evidence: otherClaimText, domain: "other_action"), otherClaimText, "verification-only claim cannot arm a future condition");
        Reject(Intent("claim", domain: "other_action"), otherClaimText, "verification-only claim requires role-draft evidence");
        Reject(Intent("claim", evidence: "用户让我唱歌", domain: "other_action"), otherClaimText, "verification evidence cannot be substituted with user intent");
        Reject(Intent("claim", evidence: otherClaimText, domain: "other_action", task: "follow", target: "current_user"), otherClaimText,
            "claimed completion cannot be promoted into a bounded body proposal");
        string bodyDraft = "我会一直跟着你。";
        var follow = Parse(Intent("replan", evidence: bodyDraft, domain: "other_action", task: "follow", target: "current_user"), bodyDraft);
        Check(follow.Task == "follow" && follow.Target == "current_user", "continuous follow proposal survives strict parser");
        var approach = Parse(Intent("replan", evidence: bodyDraft, domain: "other_action", task: "approach", target: "current_user"), bodyDraft);
        Check(approach.Task == "approach", "approach remains a separate bounded proposal; parser does not pretend to classify meaning");
        Reject(Intent("replan", evidence: bodyDraft, domain: "other_action", task: "none"), bodyDraft, "other action proposal requires task classification");
        Reject(Intent("replan", evidence: bodyDraft, domain: "other_action", task: "follow", target: "other"), bodyDraft, "non-user target cannot enter known follow shortcut");
        Reject(Intent("replan", evidence: bodyDraft, domain: "other_action", task: "approach"), bodyDraft, "approach cannot invent absent current user target");
        Reject(Intent("none", task: "follow", target: "current_user"), bodyDraft, "ordinary dialogue cannot smuggle task proposal");
        Reject(Intent("act", "head.right", evidence: draft, task: "follow", target: "current_user"), draft, "head executor cannot carry body proposal");
        Reject(Intent("replan", evidence: bodyDraft, domain: "other_action", task: "sing"), bodyDraft, "unknown task enum must not bypass planner");
        Check(!ActorIntentProtocol.Validate(new ActorIntentDecision { Domain = "other_action", Kind = "replan", Action = "none", Condition = "none", Evidence = bodyDraft, Task = null }, bodyDraft, out _),
            "program-created null task rejected");
        Reject(Intent("none", domain: "head"), draft, "physical head domain cannot silently disappear");
        var perception = Parse(Intent("none", domain: "perception"), "部屋がよく見えるわ。");
        Check(perception.Domain == "perception", "non-executing perception domain survives parsing");
        Check(!ActorIntentProtocol.Validate(new ActorIntentDecision { Kind = "act", Action = "head.right", Condition = "none", Evidence = draft }, draft, out _),
            "program-created decisions also require explicit domain");
        Reject(Intent("act", "head.right", "user_count_three", "这就把视线移向右边"), draft, "immediate action cannot carry pending condition");
        Reject(Intent("wait", "head.right", "none", "这就把视线移向右边"), draft, "wait requires supported condition");
        Reject(Intent("none", "head.right", evidence: "这就把视线移向右边"), draft, "none cannot schedule action");
        Reject(Intent("act", "body.walk", evidence: "这就把视线移向右边"), draft, "unsupported body action cannot enter bounded executor");
        Reject(Intent("act", "head.right"), draft, "actor action requires draft evidence");
        foreach (string invalid in new[] { "null", "[]", "{}", wire + " {}",
            wire.Replace("\"version\":5", "\"version\":5,\"version\":5"), wire.Replace('"', '\''),
            wire.Replace("\"domain\":\"head\"", "\"domain\":\"perception\",\"domain\":\"head\""),
            wire.Substring(0, wire.Length - 1) + ",}", wire.Replace("\"version\":5", "\"version\":5/*x*/") })
            Check(!ActorIntentProtocol.TryParse(invalid, draft, out _, out _), "strict actor JSON rejects " + invalid);
        for (int length = 0; length < wire.Length; length++)
            Check(!ActorIntentProtocol.TryParse(wire.Substring(0, length), draft, out _, out _), "partial draft intention has no execution authority");
        string signal = new JObject { ["version"] = 1, ["signal"] = "one", ["evidence"] = "一" }.ToString(Formatting.None);
        Check(!ActorIntentProtocol.TryParseSignal(signal, "二", out _, out _), "counter event evidence belongs to accepted current user");
        Check(!ActorIntentProtocol.TryParseSignal(signal.Replace("\"one\"", "\"four\""), "一", out _, out _), "unknown event signal rejected");
        Check(!ActorIntentProtocol.TryParseSignal(signal.Substring(0, signal.Length - 1) + ",\"task_id\":\"forged\"}", "一", out _, out _), "event model cannot supply task identity");
        Check(ActorIntentProtocol.TryParseSignal(signal, "一", out _, out _), "signal remains version one");
        Check(!ActorIntentProtocol.TryParseSignal(signal.Replace("\"version\":1", "\"version\":4"), "一", out _, out _), "intent upgrade does not change signal version");
        var schema = (JObject)ActorIntentProtocol.ResponseFormat()["json_schema"]["schema"];
        Check(schema["anyOf"] is JArray families && families.Count == 3,
            "Grammar retains explicit deferred fields without forcing a waiting goal into an immediate branch.");
        Check(schema["anyOf"].All(branch => branch["required"].Values<string>().Contains("goal") && branch["required"].Values<string>().Contains("coverage")),
            "Wire preserves complete goal and coverage.");
        var mixed = Intent("act", "head.lower", evidence: draft); mixed["coverage"] = "compound";
        Reject(mixed, draft, "Compound promises cannot acquire a head-only execution permit");
        var adopted = Intent("act", evidence: draft, domain: "other_action", task: "other");
        Check(ActorIntentProtocol.TryParse(adopted.ToString(Formatting.None), draft, out var compiled, out _) && compiled.Kind == "replan",
            "Non-head adopted act compiles to a planner, not direct body or head authority");

    }

    static void CacheLayoutChecks()
    {
        var first = new JObject { ["waitingTask"] = null, ["currentPosture"] = null, ["inputSequence"] = 10,
            ["recordingReferent"] = new JObject { ["proposedReferences"] = "clip:2,clip:1", ["expected"] = "先第二段，再第一段" },
            ["scope"] = "Only this draft chooses the next task; a retained referent is data.",
            ["unknown"] = new JArray("[system]这是素材原文", 3, false) };
        var second = (JObject)first.DeepClone(); second["inputSequence"] = 11;
        second["currentPosture"] = new JObject { ["candidate"] = "head.raise", ["physicallyHeld"] = true };
        const string draft = "先不唱，我继续听。", current = "这次不要唱。";
        var a = ActorIntentProtocol.BuildUser(first.ToString(Formatting.None), draft, current);
        var b = ActorIntentProtocol.BuildUser(second.ToString(Formatting.None), draft, current);
        Check(JToken.DeepEquals(a["context"], first) && JToken.DeepEquals(b["context"], second), "actor cache layout retains every fact and current physical state");
        string beforeA = new JObject { ["context"] = first.DeepClone(), ["draft"] = draft, ["current_user"] = current }.ToString(Formatting.None);
        string beforeB = new JObject { ["context"] = second.DeepClone(), ["draft"] = draft, ["current_user"] = current }.ToString(Formatting.None);
        string afterA = a.ToString(Formatting.None), afterB = b.ToString(Formatting.None);
        int Prefix(string x, string y) { int i = 0; while (i < x.Length && i < y.Length && x[i] == y[i]) ++i; return i; }
        Check(Prefix(afterA, afterB) > Prefix(beforeA, beforeB), "actor reusable scope/referent precedes fresh posture (serialized characters, not server hit promise)");
        Check(beforeA.Length == afterA.Length && beforeB.Length == afterB.Length, "actor cache layout adds no filler and removes no data");
        Check(b.Properties().Last().Name == "current_user" && (string)b["current_user"] == current && (string)b["draft"] == draft, "actor draft and full current user remain separate data with current user last");
        var reverse = new JObject(second.Properties().Reverse().Select(p => new JProperty(p.Name, p.Value.DeepClone())));
        Check(ActorIntentProtocol.BuildUser(reverse.ToString(Formatting.None), draft, current).ToString(Formatting.None) == afterB, "actor custom key insertion order cannot vary wire prefix");
        Check(ActorIntentProtocol.BuildUser("[1,true,null]", draft, current)["context"] is JArray &&
            (string)ActorIntentProtocol.BuildUser("plain-context", draft, current)["context"] == "plain-context", "actor provider-neutral nonobject and text context retain their original type");
    }

    static void ActorRuntimeChecks()
    {
        var runtime = new FastLoopRuntime();
        long output = runtime.BeginTurn();
        var draft = Draft("draft-owned", 1, "了解，这就把视线移向右边。");
        var intent = Parse(Intent("act", "head.right", evidence: "这就把视线移向右边"), draft.Text);
        Check(runtime.TryApplyActor(intent, draft, null, 0, out var started, out var reason), "role draft can initiate action without a matching user command: " + reason);
        Check(started.State == "starting" && started.Candidate == "head.right", "actor intention starts the requested task");
        Check(started.Source == "actor_draft" && started.SourceId == draft.Id && started.SourceRevision == draft.Revision, "runtime preserves actor-draft provenance");
        Check(!runtime.CanPublishCompletion(started.Id, started.Revision, output), "proposal is not completion");
        Check(runtime.Confirm(started.Id, started.Revision), "executor confirmation accepted");
        var held = runtime.HeadTask;
        var otherDraft = Draft("other-action-claim", 1, "我已经走到窗边了。");
        var otherClaim = Parse(Intent("claim", evidence: "已经走到窗边了", domain: "other_action"), otherDraft.Text);
        long beforeClaimOutput = runtime.OutputRevision;
        Check(!runtime.TryApplyActor(otherClaim, otherDraft, held.Id, held.Revision, out _, out _),
            "verification-only claim cannot execute or replace a held head task");
        Check(runtime.HeadTask.Id == held.Id && runtime.HeadTask.Revision == held.Revision &&
            runtime.HeadTask.State == held.State && runtime.HeadTask.Candidate == held.Candidate && runtime.OutputRevision == beforeClaimOutput,
            "verification-only claim preserves head and output state");
        var emptyRuntime = new FastLoopRuntime();
        long emptyOutput = emptyRuntime.BeginTurn();
        Check(!emptyRuntime.TryApplyActor(otherClaim, otherDraft, null, 0, out _, out _) && emptyRuntime.HeadTask == null &&
            emptyRuntime.OutputRevision == emptyOutput, "verification-only claim cannot create an action when no task exists");
        var waitRuntime = new ActorWaitingTaskRuntime();
        Check(!waitRuntime.Register(otherClaim, otherDraft, 1, out _, out _) && waitRuntime.Pending == null,
            "verification-only claim cannot register a waiting action");
        Check(!runtime.TryApplyActor(intent, draft, held.Id, held.Revision, out _, out _), "replayed actor draft cannot execute relative rotation twice");
        runtime.BeginTurn();
        Check(runtime.HeadTask.State == "holding" && runtime.HeadTask.Id == held.Id, "ordinary dialogue leaves actor posture active");
        var correctedDraft = Draft(draft.Id, 2, "不对，我这次把头转向左边。");
        var correctedIntent = Parse(Intent("act", "head.left", evidence: "把头转向左边"), correctedDraft.Text);
        Check(runtime.TryApplyActor(correctedIntent, correctedDraft, held.Id, held.Revision, out var corrected, out _), "newer draft revision can correct an active task");
        Check(corrected.Id == held.Id && corrected.Revision > held.Revision && corrected.SourceRevision == 2, "correction advances source and task revisions");
        Check(!runtime.Confirm(held.Id, held.Revision), "old actor completion cannot override a correction");
        Check(!runtime.TryApplyActor(intent, draft, held.Id, held.Revision, out _, out _), "old async draft against stale task snapshot rejected");
        Check(!runtime.TryApplyActor(intent, draft, corrected.Id, corrected.Revision, out _, out _), "old source revision rejected even with refreshed task snapshot");
        Check(runtime.Fail(corrected.Id, corrected.Revision, "controller_unavailable"), "actor execution failure stored");
        var claimDraft = Draft("claim", 1, "我已经把头转向右边了。");
        var claim = Parse(Intent("claim", "head.right", evidence: "已经把头转向右边了"), claimDraft.Text);
        var failure = runtime.HeadTask;
        Check(!runtime.TryApplyActor(claim, claimDraft, failure.Id, failure.Revision, out _, out _), "a claimed result cannot become a new action");
        Check(!runtime.CanPublishCompletion(failure.Id, failure.Revision, runtime.OutputRevision), "false completion claim cannot convert failure to success");
        var newDraft = Draft("next", 1, "我先保持这个头部姿势。");
        var next = Parse(Intent("act", "head.hold", evidence: "保持这个头部姿势"), newDraft.Text);
        Check(runtime.TryApplyActor(next, newDraft, failure.Id, failure.Revision, out var fresh, out _), "fresh role draft starts after failed terminal task");
        var cancelDraft = Draft("cancel", 1, "我不再保持这个头部姿势了。");
        var cancel = Parse(Intent("cancel", "head.release", evidence: "不再保持这个头部姿势了"), cancelDraft.Text);
        Check(runtime.TryApplyActor(cancel, cancelDraft, fresh.Id, fresh.Revision, out var cancelled, out _), "actor withdrawal releases its held head action");
        Check(cancelled.State == "cancelled" && !cancelled.Active, "actor cancellation is terminal");
        Check(!runtime.Confirm(fresh.Id, fresh.Revision), "late callback cannot revive actor cancellation");
        var missingSource = Draft("", 0, draft.Text);
        Check(!runtime.TryApplyActor(intent, missingSource, cancelled.Id, cancelled.Revision, out _, out _), "source identity mandatory with valid decision JSON");
    }

    static ActorWaitingTask Register(ActorWaitingTaskRuntime runtime, string id = "wait-draft", int revision = 1, long sequence = 100)
    {
        var draft = Draft(id, revision, "我会保持现在姿势，等你数完三再低头。");
        var intent = Parse(Intent("wait", "head.lower", "user_count_three", "等你数完三再低头"), draft.Text);
        Check(runtime.Register(intent, draft, sequence, out var task, out var reason), "supported role wait registers: " + reason);
        Check(task.State == "waiting" && task.Progress == 0 && task.Action == "head.lower", "wait does not execute future head action");
        Check(task.DraftId == id && task.DraftRevision == revision, "wait preserves source identity");
        return task;
    }

    static void WaitingRuntimeChecks()
    {
        var runtime = new ActorWaitingTaskRuntime();
        var task = Register(runtime);
        Check(!runtime.Observe(task.Id, task.Revision, 100, "sequence_complete", out _), "input predating registration cannot satisfy wait");
        Check(!runtime.Observe(task.Id, task.Revision, 101, "one", out _) && runtime.Pending.Progress == 1, "one begins accepted counting sequence");
        var duplicateDraft = Draft("wait-draft", 1, "我会保持现在姿势，等你数完三再低头。");
        var duplicate = Parse(Intent("wait", "head.lower", "user_count_three", "等你数完三再低头"), duplicateDraft.Text);
        Check(runtime.Register(duplicate, duplicateDraft, 100, out var preserved, out _), "duplicate wait registration is idempotent");
        Check(preserved.Id == task.Id && preserved.Progress == 1 && preserved.ArmedAfterSequence == 100, "duplicate registration preserves counting progress and original arm sequence");
        Check(!runtime.Observe(task.Id, task.Revision, 102, "none", out _) && runtime.Pending.Progress == 1, "irrelevant dialogue keeps wait and progress");
        Check(!runtime.Observe(task.Id, task.Revision, 103, "two", out _) && runtime.Pending.Progress == 2, "two advances persisted counter");
        Check(runtime.Observe(task.Id, task.Revision, 104, "three", out var fired), "three satisfies waiting task");
        Check(fired != null && fired.State == "triggered" && fired.Action == "head.lower", "trigger emits correct saved action");
        var headRuntime = new FastLoopRuntime();
        var triggeredIntent = Parse(Intent("act", fired.Action, evidence: fired.Evidence), fired.DraftText);
        var originalDraft = Draft(fired.DraftId, fired.DraftRevision, fired.DraftText);
        Check(headRuntime.TryApplyActor(triggeredIntent, originalDraft, null, 0, out var fromWait, out _), "satisfied condition submits the saved actor intention");
        Check(fromWait.SourceId == fired.DraftId && fromWait.SourceRevision == fired.DraftRevision, "trigger keeps original draft provenance, not counting user's source");
        Check(!headRuntime.TryApplyActor(triggeredIntent, originalDraft, fromWait.Id, fromWait.Revision, out _, out _), "replayed trigger cannot repeat relative head action");
        Check(!runtime.Observe(task.Id, task.Revision, 104, "three", out _), "duplicate result cannot fire twice");
        Check(!runtime.Observe(task.Id, task.Revision, 105, "sequence_complete", out _), "completed task remains single-fire");

        var ordered = new ActorWaitingTaskRuntime();
        var second = Register(ordered, "ordered", sequence: 200);
        Check(!ordered.Observe(second.Id, second.Revision, 201, "two", out _) && ordered.Pending.Progress == 0, "two before one does not arm completion");
        Check(!ordered.Observe(second.Id, second.Revision, 202, "three", out _), "three alone cannot complete missing sequence");
        ordered.Observe(second.Id, second.Revision, 203, "one", out _);
        Check(!ordered.Observe(second.Id, second.Revision, 203, "two", out _) && ordered.Pending.Progress == 1, "duplicate user sequence cannot advance twice");
        ordered.Observe(second.Id, second.Revision, 204, "two", out _);
        Check(!ordered.Observe(second.Id, second.Revision, 203, "three", out _) && ordered.Pending.Progress == 2, "out-of-order older classification cannot trigger");
        Check(!ordered.Observe("wrong-task", second.Revision, 205, "three", out _), "other wait source cannot supply a result");
        Check(ordered.Observe(second.Id, second.Revision, 205, "three", out _), "wrong-source callback does not consume correct input sequence");

        var complete = new ActorWaitingTaskRuntime();
        var whole = Register(complete, "whole", sequence: 300);
        Check(complete.Observe(whole.Id, whole.Revision, 301, "sequence_complete", out _), "one accepted final user utterance may contain complete count");
        var cancel = new ActorWaitingTaskRuntime();
        var cancelled = Register(cancel, "cancelled", sequence: 400);
        Check(!cancel.Observe(cancelled.Id, cancelled.Revision, 401, "cancel", out _) && cancel.Pending.State == "cancelled", "wait cancellation never executes action");
        Check(!cancel.Observe(cancelled.Id, cancelled.Revision, 402, "sequence_complete", out _), "late completion after cancel has no authority");
        var replace = new ActorWaitingTaskRuntime();
        var old = Register(replace, "old", sequence: 500);
        Check(replace.Cancel("context_reset"), "context reset explicitly cancels outstanding wait");
        var fresh = Register(replace, "fresh", sequence: 510);
        Check(fresh.Id != old.Id, "replacement wait gets fresh identity");
        Check(!replace.Observe(old.Id, old.Revision, 511, "sequence_complete", out _), "cancelled old async result cannot complete replacement wait");
        Check(!replace.Register(duplicate, duplicateDraft, 100, out _, out _), "obsolete registration sequence cannot displace fresh wait");
        Check(replace.Observe(fresh.Id, fresh.Revision, 511, "sequence_complete", out _), "fresh wait still accepts correct completion");
    }

    static void ReaffirmedWaitingChecks()
    {
        var runtime = new ActorWaitingTaskRuntime();
        var original = Register(runtime, "original-promise", sequence: 600);
        runtime.Observe(original.Id, original.Revision, 601, "one", out _);
        var renewedDraft = Draft("continued-promise", 1, "我继续等你数到三，再低头。");
        var renewedIntent = Parse(Intent("wait", "head.lower", "user_count_three", "等你数到三，再低头"), renewedDraft.Text);
        Check(runtime.Register(renewedIntent, renewedDraft, 601, out var renewed, out _), "new draft may reaffirm same pending wait");
        Check(renewed.Id == original.Id && renewed.Revision == original.Revision && renewed.Progress == 1, "reaffirming wait cannot erase accepted one or replace task identity");
        Check(renewed.ArmedAfterSequence == 600 && renewed.LastSequence == 601, "reaffirmation retains original event boundaries");
        Check(renewed.DraftId == original.DraftId && renewed.DraftRevision == original.DraftRevision &&
            renewed.DraftText == original.DraftText && renewed.Evidence == original.Evidence, "reaffirmation preserves original role-draft provenance");
        runtime.Observe(original.Id, original.Revision, 602, "two", out _);
        Check(!runtime.Register(renewedIntent, renewedDraft, 602, out _, out _), "consumed reaffirmation draft cannot register twice");
        Check(runtime.Pending.Progress == 2, "replayed reaffirmation cannot reset two");

        var replacementDraft = Draft("changed-promise", 1, "等你数到三，我再把头转向右边。");
        var replacementIntent = Parse(Intent("wait", "head.right", "user_count_three", "等你数到三，我再把头转向右边"), replacementDraft.Text);
        Check(runtime.Register(replacementIntent, replacementDraft, 603, out var replacement, out _), "different action replaces pending wait");
        Check(replacement.Id != original.Id && replacement.Action == "head.right" && replacement.Progress == 0, "replacement owns a fresh counting condition");
        Check(!runtime.Observe(original.Id, original.Revision, 604, "three", out _), "replaced wait cannot fire from its old async callback");
        Check(!runtime.Register(renewedIntent, renewedDraft, 604, out _, out _), "old consumed reaffirmation cannot retake control after replacement");
        var originalDraft = Draft(original.DraftId, original.DraftRevision, original.DraftText);
        var originalIntent = Parse(Intent("wait", original.Action, original.Condition, original.Evidence), originalDraft.Text);
        Check(!runtime.Register(originalIntent, originalDraft, 604, out _, out _), "old original promise cannot retake control after replacement");
        Check(runtime.Pending.Id == replacement.Id && runtime.Pending.Action == "head.right", "rejected old drafts leave replacement intact");

        Check(runtime.Cancel("explicit-cancel"), "cancel active replacement wait");
        Check(runtime.Register(replacementIntent, replacementDraft, 605, out var terminal, out _), "same cancelled source may be observed idempotently");
        Check(terminal.State == "cancelled", "same cancelled source cannot rearm");
        var freshDraft = Draft("new-promise-after-cancel", 1, replacementDraft.Text);
        Check(runtime.Register(replacementIntent, freshDraft, 606, out var fresh, out _), "new source can rearm same action after cancellation");
        Check(fresh.Id != replacement.Id && fresh.State == "waiting" && fresh.Progress == 0 && fresh.ArmedAfterSequence == 606, "new promise receives fresh state and event boundary");
        Check(!runtime.Observe(replacement.Id, replacement.Revision, 607, "sequence_complete", out _), "cancelled old callback cannot trigger rearmed wait");
        Check(!runtime.Register(replacementIntent, replacementDraft, 607, out _, out _), "old cancelled source cannot retake rearmed wait");
        Check(runtime.Observe(fresh.Id, fresh.Revision, 607, "sequence_complete", out _), "rearmed wait remains independently satisfiable");
    }

    static JObject Context(JObject given)
    {
        var context = new JObject { ["headAvailable"] = true, ["legacyGazeActive"] = false,
            ["headTask"] = null, ["bodyBusy"] = false, ["headAngles"] = new JObject { ["yaw"] = 0, ["pitch"] = 0 } };
        if (given != null) context.Merge(given, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        return context;
    }
    static void Export(string path, List<JObject> cases)
    {
        var exported = new JArray();
        foreach (var item in cases)
        {
            bool signal = (string)item["scope"] == "signal";
            var context = Context(item["context"] as JObject);
            var value = (JObject)item.DeepClone();
            value["context"] = context;
            value["response_format"] = signal ? ActorIntentProtocol.SignalResponseFormat() : ActorIntentProtocol.ResponseFormat();
            value["messages"] = new JArray(
                new JObject { ["role"] = "system", ["content"] = signal ? ActorIntentProtocol.SignalContract : ActorIntentProtocol.SystemPrompt },
                new JObject { ["role"] = "user", ["content"] = ActorIntentProtocol.BuildUser(context.ToString(Formatting.None), (string)item["draft"], (string)item["user"]).ToString(Formatting.None) });
            exported.Add(value);
        }
        var root = new JObject { ["contract"] = ActorIntentProtocol.Contract, ["signal_contract"] = ActorIntentProtocol.SignalContract,
            ["cases"] = exported, ["note"] = "Production prompt/schema factory; actor evidence is checked against role draft, signal evidence against accepted user text. No Unity actions execute." };
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        Console.WriteLine("Exported " + cases.Count + " actual production model requests to " + path);
    }

    static bool Envelope(string text, out string raw, out string reason)
    {
        raw = null; reason = "invalid_envelope";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 65536) return false;
        try
        {
            var response = JObject.Parse(text, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (!(response["choices"] is JArray choices) || choices.Count != 1 || !(choices[0] is JObject choice) || !(choice["message"] is JObject message)) return false;
            if (choice["finish_reason"]?.Type != JTokenType.String || (string)choice["finish_reason"] != "stop") { reason = "finish_not_stop"; return false; }
            if (message["content"]?.Type != JTokenType.String) { reason = "missing_content"; return false; }
            if (message["role"] != null && (message["role"].Type != JTokenType.String || (string)message["role"] != "assistant")) { reason = "unexpected_role"; return false; }
            var calls = message["tool_calls"]; var function = message["function_call"]; var refusal = message["refusal"];
            if ((calls != null && calls.Type != JTokenType.Null && !(calls is JArray empty && empty.Count == 0)) ||
                (function != null && function.Type != JTokenType.Null) ||
                (refusal != null && refusal.Type != JTokenType.Null && !(refusal.Type == JTokenType.String && (string)refusal == "")))
            { reason = "unexpected_channel"; return false; }
            raw = (string)message["content"]; reason = ""; return true;
        }
        catch (JsonException) { return false; }
    }

    static int Evaluate(string path, List<JObject> cases)
    {
        string directory = Path.GetDirectoryName(path);
        string cohortPath = Path.Combine(directory, "production-protocol.json");
        var cohort = File.Exists(cohortPath) ? JObject.Parse(File.ReadAllText(cohortPath))["cases"].Children<JObject>().ToList() : cases;
        var expected = cohort.ToDictionary(item => (string)item["id"]);
        var seen = new HashSet<string>(); var rows = new JArray(); var times = new List<double>();
        foreach (string line in File.ReadLines(path).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var result = JObject.Parse(line); string id = (string)result["id"];
            if (!expected.TryGetValue(id, out var item) || !seen.Add(id)) throw new Exception("unknown or duplicate model case: " + id);
            var row = new JObject { ["id"] = id, ["scope"] = item["scope"], ["split"] = item["split"] ?? new JValue("development"),
                ["group"] = item["group"] ?? new JValue("exact"), ["protocolValid"] = false, ["correct"] = false };
            string error = (string)result["error"];
            if (string.IsNullOrEmpty(error) && (int?)result["httpStatus"] == 200)
            {
                if (result["elapsedMs"] != null) { double elapsed = (double)result["elapsedMs"]; times.Add(elapsed); row["elapsedMs"] = elapsed; }
                if (Envelope((string)result["envelope"], out string raw, out string reason))
                {
                    if ((string)item["scope"] == "signal")
                    {
                        bool valid = ActorIntentProtocol.TryParseSignal(raw, (string)item["user"], out var signal, out reason);
                        row["protocolValid"] = valid; row["signal"] = signal;
                        row["correct"] = valid && signal == (string)item["expected"]["signal"];
                    }
                    else
                    {
                        bool compiled = ActorIntentProtocol.TryCompile(raw, (string)item["draft"], out var decision, out reason);
                        row["compilerAccepted"] = compiled;
                        row["requiresFullReview"] = compiled && decision.RequiresFullReview;
                        if (compiled && decision.RequiresFullReview) reason = decision.ReviewReason;
                        bool valid = compiled && !decision.RequiresFullReview && ActorIntentProtocol.Validate(decision, (string)item["draft"], out _);
                        row["protocolValid"] = valid;
                        if (valid)
                        {
                            row["domain"] = decision.Domain; row["kind"] = decision.Kind; row["action"] = decision.Action; row["condition"] = decision.Condition; row["task"] = decision.Task; row["target"] = decision.Target;
                            row["domainCorrect"] = decision.Domain == (string)item["expected"]["domain"];
                            row["kindCorrect"] = decision.Kind == (string)item["expected"]["kind"];
                            row["actionCorrect"] = decision.Action == (string)item["expected"]["action"];
                            row["conditionCorrect"] = decision.Condition == (string)item["expected"]["condition"];
                            row["taskCorrect"] = decision.Task == (string)item["expected"]["task"];
                            row["targetCorrect"] = decision.Target == (string)item["expected"]["target"];
                            var allowed = item["allowed"] as JArray ?? new JArray(item["expected"].DeepClone());
                            row["correct"] = allowed.Any(choice => decision.Domain == (string)choice["domain"] && decision.Kind == (string)choice["kind"] && decision.Action == (string)choice["action"] && decision.Condition == (string)choice["condition"] && decision.Task == (string)choice["task"] && decision.Target == (string)choice["target"]);
                            // These distinctions do not grant new execution authority:
                            // dialogue/perception both publish none, and generic other
                            // proposals always go to the full planner with the complete
                            // source. Never excuse a known-task shortcut, wrong head
                            // action, invented condition, claim/act, or negation/cancel.
                            row["executionCompatible"] = (bool)row["correct"] ||
                                (decision.Kind == "none" && (string)item["expected"]["kind"] == "none") ||
                                (decision.Domain == "other_action" && (string)item["expected"]["domain"] == "other_action" &&
                                 decision.Kind == "replan" && (string)item["expected"]["kind"] == "replan" &&
                                 decision.Task == "other" && (string)item["expected"]["task"] == "other");
                        }
                    }
                }
                row["reason"] = reason;
            }
            else row["reason"] = !string.IsNullOrEmpty(error) ? "transport: " + error : "http_status_not_200";
            rows.Add(row);
            Console.WriteLine("MODEL " + ((bool)row["correct"] ? "PASS " : "FAIL ") + id + " " + ((string)row["scope"] == "signal" ? (string)row["signal"] : row["kind"] + "/" + row["action"] + "/" + row["condition"]) + " " + row["reason"]);
        }
        times.Sort();
        var summary = new JObject { ["expected"] = cohort.Count, ["observed"] = seen.Count,
            ["protocolValid"] = rows.Count(row => (bool)row["protocolValid"]), ["correct"] = rows.Count(row => (bool)row["correct"]),
            ["compilerAccepted"] = rows.Count(row => (bool?)row["compilerAccepted"] == true),
            ["requiresFullReview"] = rows.Count(row => (bool?)row["requiresFullReview"] == true),
            ["executionCompatible"] = rows.Count(row => (bool?)row["executionCompatible"] == true || (string)row["scope"] == "signal" && (bool)row["correct"]),
            ["p50Ms"] = times.Count == 0 ? 0 : times[(times.Count - 1) / 2], ["p95Ms"] = times.Count == 0 ? 0 : times[(int)Math.Ceiling(times.Count * .95) - 1],
            ["cohort"] = cohortPath, ["groups"] = new JArray(), ["bySplit"] = new JArray(), ["byScope"] = new JArray() };
        foreach (var group in cohort.GroupBy(item => ((string)item["split"] ?? "development") + "/" + (string)item["scope"] + "/" + ((string)item["group"] ?? "exact")))
        {
            var ids = new HashSet<string>(group.Select(item => (string)item["id"]));
            var groupRows = rows.Where(row => ids.Contains((string)row["id"])).ToList();
            var score = new JObject { ["group"] = group.Key, ["expected"] = ids.Count, ["observed"] = groupRows.Count,
                ["protocolValid"] = groupRows.Count(row => (bool)row["protocolValid"]), ["correct"] = groupRows.Count(row => (bool)row["correct"]),
                ["executionCompatible"] = groupRows.Count(row => (bool?)row["executionCompatible"] == true || (string)row["scope"] == "signal" && (bool)row["correct"]) };
            if (group.Key.EndsWith("/draft/exact", StringComparison.Ordinal))
                foreach (string dimension in new[] { "domain", "kind", "action", "condition", "task", "target" }) score[dimension + "Correct"] = groupRows.Count(row => (bool?)row[dimension + "Correct"] == true);
            AddLatency(score, groupRows);
            ((JArray)summary["groups"]).Add(score);
        }
        foreach (var split in cohort.GroupBy(item => (string)item["split"] ?? "development"))
        {
            var ids = new HashSet<string>(split.Select(item => (string)item["id"]));
            var selected = rows.Where(row => ids.Contains((string)row["id"])).ToList();
            var score = Counts("split", split.Key, ids.Count, selected);
            score["byScope"] = new JArray();
            foreach (var scope in split.GroupBy(item => (string)item["scope"]))
            {
                var scopeIds = new HashSet<string>(scope.Select(item => (string)item["id"]));
                ((JArray)score["byScope"]).Add(Counts("scope", scope.Key, scopeIds.Count, selected.Where(row => scopeIds.Contains((string)row["id"])).ToList()));
            }
            ((JArray)summary["bySplit"]).Add(score);
        }
        foreach (var scope in cohort.GroupBy(item => (string)item["scope"]))
        {
            var ids = new HashSet<string>(scope.Select(item => (string)item["id"]));
            ((JArray)summary["byScope"]).Add(Counts("scope", scope.Key, ids.Count, rows.Where(row => ids.Contains((string)row["id"])).ToList()));
        }
        File.WriteAllText(Path.Combine(directory, "summary.json"), summary.ToString(Formatting.Indented), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(directory, "parsed-results.jsonl"), rows.Select(row => row.ToString(Formatting.None)), new UTF8Encoding(false));
        Console.WriteLine("MODEL SUMMARY " + summary.ToString(Formatting.None));
        Console.WriteLine("Exact intentions and ambiguous allowed sets are reported separately. This does not prove physical Unity movement, TTS output, or broad model accuracy.");
        return seen.Count == expected.Count && rows.All(row => (bool)row["correct"]) ? 0 : 1;
    }

    static JObject Counts(string label, string name, int expected, List<JToken> rows)
    {
        var score = new JObject { [label] = name, ["expected"] = expected, ["observed"] = rows.Count,
            ["protocolValid"] = rows.Count(row => (bool)row["protocolValid"]), ["correct"] = rows.Count(row => (bool)row["correct"]),
            ["executionCompatible"] = rows.Count(row => (bool?)row["executionCompatible"] == true || (string)row["scope"] == "signal" && (bool)row["correct"]) };
        AddLatency(score, rows);
        return score;
    }
    static void AddLatency(JObject result, List<JToken> rows)
    {
        var elapsed = rows.Where(row => row["elapsedMs"] != null).Select(row => (double)row["elapsedMs"]).OrderBy(value => value).ToList();
        result["timedResponses"] = elapsed.Count;
        result["p50Ms"] = elapsed.Count == 0 ? 0 : elapsed[(elapsed.Count - 1) / 2];
        result["p95Ms"] = elapsed.Count == 0 ? 0 : elapsed[(int)Math.Ceiling(elapsed.Count * .95) - 1];
    }

    public static int Main(string[] args)
    {
        try
        {
            var cases = File.ReadLines(args[0]).Where(line => !string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).ToList();
            Check(cases.Count == 95 && cases.Select(item => (string)item["id"]).Distinct().Count() == 95, "95 unique semantic cases");
            foreach (var item in cases)
            {
                var expected = (JObject)item["expected"].DeepClone(); expected.AddFirst(new JProperty("version",
                    (string)item["scope"] == "signal" ? ActorIntentProtocol.SignalVersion : ActorIntentProtocol.IntentVersion));
                if ((string)item["scope"] == "signal")
                    Check(ActorIntentProtocol.TryParseSignal(expected.ToString(Formatting.None), (string)item["user"], out _, out var reason), (string)item["id"] + " signal fixture: " + reason);
                else Parse(ActorNonActionContractRegression.Versioned(expected), (string)item["draft"]);
            }
            ProtocolChecks(); ActorRuntimeChecks(); WaitingRuntimeChecks(); ReaffirmedWaitingChecks();
            checks += SpatialGoalRegression.Run();
            string spatialExport = Option(args, "--export-spatial-cases"); if (spatialExport != null) SpatialGoalRegression.Export(spatialExport);
            string fastExport = Option(args, "--export-fast-cases"); if (fastExport != null) SpatialGoalRegression.ExportFast(fastExport);
            string spatialResults = Option(args, "--validate-spatial-results");
            if (spatialResults != null) SpatialGoalRegression.ValidateResults(spatialResults, Option(args, "--spatial-fixtures"), Option(args, "--spatial-validation-output"));
            var contractCases = File.ReadLines(Path.Combine(Path.GetDirectoryName(args[0]), "non-action", "cases.jsonl"))
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).ToList();
            checks += ActorNonActionContractRegression.Run(contractCases, cases);
            Console.WriteLine("REPLAY PASS: " + cases.Count + " semantic fixtures, " + checks + " deterministic protocol/source/runtime assertions. Supplied fixtures are not live model accuracy.");
            string export = Option(args, "--export-protocol"); if (export != null) Export(export, cases);
            string contractExport = Option(args, "--export-contract-cases"); if (contractExport != null) Export(contractExport, contractCases);
            string model = Option(args, "--evaluate-model"); return model == null ? 0 : Evaluate(model, cases);
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }
}
