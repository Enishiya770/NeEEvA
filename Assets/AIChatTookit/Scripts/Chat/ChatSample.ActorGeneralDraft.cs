using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class ActorGeneralCandidate
    {
        public LLM Model;
        public string Prompt, Image, Context, Text;
        public int Generation, Epoch, Round, Serial, ActorSerial;
        public long OutputRevision;
        public bool RecordHistory, Consumed, AwaitingPublication, CompilingActor;
        public ObservationReceipt Observations;
        public LLM.SendData AcceptedUser, UserBeforeSend;
        public readonly SpeechTextBuffer Buffer = new SpeechTextBuffer();
    }
    private ActorGeneralCandidate m_ActorGeneralCandidate;
    private int m_ActorGeneralSerial;
    private int m_ActorGeneralAttemptGeneration = -1, m_ActorGeneralAttemptEpoch = -1;

    private bool ActorGeneralCurrent(ActorGeneralCandidate value) => value != null &&
        ReferenceEquals(value, m_ActorGeneralCandidate) && value.Serial == m_ActorGeneralSerial &&
        (value.CompilingActor || value.ActorSerial == m_ActorIntentRequestSerial) &&
        value.Generation == m_FormalResponseGeneration && value.Epoch == m_WorkEpoch &&
        value.Round == m_CurrentSpeechRoundId && value.OutputRevision == m_FastLoop.OutputRevision &&
        ReferenceEquals(value.Model, m_ChatSettings?.m_ChatModel) && isActiveAndEnabled &&
        !m_UserSpeechActiveForAutonomy && !m_SceneVisionApplicationPaused &&
        m_EnableFastTaskLoop && m_EnableActorIntentCompiler && !m_SilencedSpeechRoundIds.Contains(value.Round);

    /// <summary>Only a General leaf uses this entry; typed recording/spatial stages retain their own gates.</summary>
    private bool TryDispatchActorGeneralDraft(string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext, bool continuation)
    {
        if (!m_EnableFastTaskLoop || !m_EnableActorIntentCompiler) return false;
        var model = m_ChatSettings?.m_ChatModel;
        if (model == null || generation != m_FormalResponseGeneration || !isActiveAndEnabled ||
            m_UserSpeechActiveForAutonomy || m_SceneVisionApplicationPaused) return true;
        if (ActorReplyFailureAlreadyClosed(model, generation))
        { m_FormalResponseInFlight = false; return true; }
        if (HasActiveActorPlan)
        {
            var plan = m_ActorPlan;
            // Registration alone is not execution. A General leaf can be
            // reached because the room planner is unavailable; in that case no
            // watcher will wake a merely "planning" goal. Only the bound,
            // current step that actually has its result watcher owns this wait.
            if (plan.State == "executing" && !string.IsNullOrEmpty(plan.PendingActionId) &&
                m_ActorPlanWatchedRevision == plan.Revision && ActorPlanCurrent(plan, m_ActorPlanTurn))
            {
                m_ResponseTrace?.Mark("actor_general.deferred_to_plan", new JObject {
                    ["generation"] = generation, ["planId"] = plan.Id,
                    ["actionId"] = plan.PendingActionId, ["state"] = plan.State });
                return true;
            }
            int priorEpoch = m_WorkEpoch, priorRound = m_CurrentSpeechRoundId;
            long priorOutput = m_FastLoop.OutputRevision;
            int priorActorSerial = m_ActorIntentRequestSerial;
            CancelActorPlan("general-entry-without-executable-plan-step");
            // Cancellation revokes receipt audio and emits diagnostics, both
            // can synchronously replace the owner. Never cancel or dispatch for
            // the newer turn/plan that such a callback may have installed.
            if (!ReferenceEquals(plan, m_ActorPlan) || generation != m_FormalResponseGeneration ||
                priorEpoch != m_WorkEpoch || priorRound != m_CurrentSpeechRoundId ||
                priorOutput != m_FastLoop.OutputRevision || priorActorSerial != m_ActorIntentRequestSerial ||
                !ReferenceEquals(model, m_ChatSettings?.m_ChatModel) || !isActiveAndEnabled ||
                m_UserSpeechActiveForAutonomy || m_SceneVisionApplicationPaused) return true;
            UpdateActorPlanProposal();
            transientContext = (transientContext ?? "") + "\n[尚未执行的完整目标转交]\n" +
                plan.Facts().ToString(Newtonsoft.Json.Formatting.None) + "\n" +
                "空间规划没有建立可等待的受理动作，原计划已撤销。保留完整原目标、剩余步骤、已知实体与真实已完成结果；" +
                "这不是执行或完成证明。根据原角色意向决定解释、澄清或新的完整草稿，不能把用户目标冒充角色采纳，不能补称未执行动作已完成。";
        }
        // A planner may return to General once. A new adoption can reenter the
        // typed planner, but cannot spin General -> Actor -> planner -> General.
        if (m_ActorGeneralAttemptGeneration == generation && m_ActorGeneralAttemptEpoch == m_WorkEpoch)
        {
            if (ActorGeneralCurrent(m_ActorGeneralCandidate) &&
                (!m_ActorGeneralCandidate.Consumed || m_ActorGeneralCandidate.AwaitingPublication && m_FastLoopTurn?.HandedOff != true))
                return true;
            CompleteActorInterpretationFailure(model, prompt, RoomFallbackLanguage(m_ActorTaskLanguage), generation,
                recordHistory, "actor-general-recompile-limit");
            return true;
        }
        m_ActorGeneralAttemptGeneration = generation; m_ActorGeneralAttemptEpoch = m_WorkEpoch;
        int actorSerial = m_ActorIntentRequestSerial + 1;
        CancelActorIntentRequest();
        if (actorSerial != m_ActorIntentRequestSerial || generation != m_FormalResponseGeneration ||
            !ReferenceEquals(model, m_ChatSettings?.m_ChatModel)) return true;
        var value = new ActorGeneralCandidate {
            Model = model, Prompt = prompt ?? m_LastUserMsg ?? "", Image = imageUrl, Context = transientContext,
            Generation = generation, Epoch = m_WorkEpoch, Round = m_CurrentSpeechRoundId,
            OutputRevision = m_FastLoop.OutputRevision, Serial = ++m_ActorGeneralSerial,
            ActorSerial = m_ActorIntentRequestSerial,
            RecordHistory = recordHistory, Observations = CaptureObservationReceipt()
        };
        value.UserBeforeSend = model.m_DataList.LastOrDefault(x => x?.role == "user");
        value.AcceptedUser = ReferenceEquals(m_CurrentInputHistoryModel, model) ? m_CurrentInputHistoryUser : null;
        m_ActorGeneralCandidate = value;
        m_FormalResponseInFlight = true;
        m_FormalTransportDispatchGeneration = generation;
        ClearRecordingDecisionRoute();
        BindRecordingDecisionContext(false, generation);
        PublishSpokenPrefixToLlm();
        var trace = m_ResponseTrace?.Child("actor-general-draft");
        trace?.Mark("actor_general.request", new JObject { ["generation"] = generation,
            ["continuation"] = continuation, ["recordAssistantHistory"] = false,
            ["scope"] = "private General candidate; public speech and legacy tags cannot execute before actor compilation" });
        if (!ActorGeneralCurrent(value)) return true;
        void OnPart(SpeechText part)
        {
            if (!value.Consumed && ActorGeneralCurrent(value)) value.Buffer.Append(part);
        }
        void Complete(string full)
        {
            if (value.Consumed || !ActorGeneralCurrent(value)) return;
            value.Consumed = true;
            var channels = RoleOutputChannels.Parse(full ?? "");
            // Prefer the complete validated projection. Deltas are withheld and
            // only supply language runs; a truncated stream is never published.
            string text = channels.Speech ?? "";
            bool consistent = string.Equals(text.TrimEnd(), value.Buffer.ToString().TrimEnd(), StringComparison.Ordinal);
            trace?.Mark("actor_general.candidate", new JObject { ["generation"] = generation,
                ["characters"] = text.Length, ["languages"] = new JArray(value.Buffer.Snapshot().Select(x => x.LanguageCode).Distinct()),
                ["rawTagsDiscarded"] = channels.HasAnyActionTag, ["completeSpeechMatchesDeltas"] = consistent,
                ["scope"] = "legacy-only tags are not migrated into execution; existing typed skill and room stages are separate" });
            if (!ActorGeneralCurrent(value)) return;
            string language = RoomFallbackLanguage(value.Buffer.LanguageCode);
            if (channels.HasInvalidPublicBoundary || channels.HasInvalidSpeechPhase || channels.HasInvalidLanguage ||
                !consistent || text.Length > ActorIntentProtocol.MaximumEvidenceCharacters)
            {
                CompleteActorInterpretationFailure(model, value.Prompt, language, generation, recordHistory,
                    "actor-general-candidate-invalid"); return;
            }
            // A tool-only legacy result has no adopted actor speech to compile.
            // Never manufacture a spoken intention from user text or replay tags.
            if (string.IsNullOrWhiteSpace(text) && channels.HasAnyActionTag)
            {
                CompleteActorInterpretationFailure(model, value.Prompt, language, generation, recordHistory,
                    "actor-general-tool-requires-typed-route"); return;
            }
            if (recordHistory)
            {
                var latest = model.m_DataList.LastOrDefault(x => x?.role == "user");
                // A fresh General request can add its user synchronously. A
                // continuation must already own the exact accepted input; it
                // cannot borrow an unrelated earlier history entry.
                var accepted = value.AcceptedUser;
                if (accepted == null && !continuation && latest != null && !ReferenceEquals(latest, value.UserBeforeSend))
                    accepted = latest;
                if (!(model is IFastLoopRecoveryHistoryBackend history) || accepted == null ||
                    !ReferenceEquals(accepted, latest) || !model.m_DataList.Contains(accepted) ||
                    !history.ResumeFastLoopHistory(accepted, imageUrl))
                {
                    // Do not create a duplicate user or commit an unapproved
                    // assistant when this provider cannot retain the real lease.
                    CompleteActorInterpretationFailure(model, value.Prompt, language, generation, false,
                        "actor-general-history-owner-unavailable"); return;
                }
                if (!ActorGeneralCurrent(value)) return;
                m_CurrentInputHistoryModel = model; m_CurrentInputHistoryUser = accepted;
            }
            var previous = m_FastLoopTurn;
            var turn = new FastLoopTurnContext(model, value.Prompt, imageUrl, transientContext,
                generation, value.Epoch, value.Round, value.OutputRevision, recordHistory) {
                BodyControl = CaptureFastTaskControl(),
                ControlResult = previous?.Generation == generation ? previous.ControlResult?.DeepClone() as JObject : null
            };
            m_FastLoopTurn = turn;
            m_FastLoopDispatchGeneration = generation;
            m_FormalTransportDispatchGeneration = -1;
            value.Text = text;
            value.AwaitingPublication = true;
            value.CompilingActor = true;
            if (string.IsNullOrWhiteSpace(text))
            { CompleteFastLoopReply(model, value.Prompt, "", language, generation, recordHistory); return; }
            CompileActorDraft(model, value.Prompt, text, language, generation, recordHistory, value.OutputRevision);
        }
        try
        {
            string privateContext = (transientContext ?? "") + "\n[本次普通回复为待审核的角色草稿]\n" +
                "保持原角色与语言，以400字符以内完整表述本次回复；若自己采纳行动，保留所有目标和条件，不截取第一步。" +
                "本阶段只提交公开草稿，不直接用旧动作标签授权执行；实际行动仍由后续意图与现有工具决策审核。不要因为本阶段不能直接执行标签就谎称动作已完成。";
            if (continuation)
                model.PostSpeechFeedbackStream(BuildMotionRequestContext(""), privateContext, OnPart, Complete, imageUrl, false);
            else
            {
                model.RequestContext = privateContext;
                model.PostSpeechStreamForStage(value.Prompt, OnPart, Complete, imageUrl, false, FormalResponseStage.General);
            }
        }
        catch (Exception)
        {
            if (!value.Consumed && ActorGeneralCurrent(value))
            {
                value.Consumed = true;
                CompleteActorInterpretationFailure(model, value.Prompt, RoomFallbackLanguage(value.Buffer.LanguageCode),
                    generation, recordHistory, "actor-general-provider-error");
            }
        }
        return true;
    }

    /// <summary>CompleteFastLoopReply calls this after its owner guard, before its single-language publication.</summary>
    private bool TryCompleteActorGeneralReply(LLM model, string prompt, string text, string language,
        int generation, bool recordHistory)
    {
        var value = m_ActorGeneralCandidate;
        if (value == null || !value.AwaitingPublication) return false;
        if (!ActorGeneralCurrent(value) || !ReferenceEquals(model, value.Model) || generation != value.Generation) return false;
        value.AwaitingPublication = false;
        if (!string.Equals(text ?? "", value.Text ?? "", StringComparison.Ordinal)) return false;
        // The same approved speech may contain separate language runs. Reusing a
        // whole-draft single-language preparation would erase that distinction.
        var parts = value.Buffer.Snapshot();
        if (parts.Select(x => x.LanguageCode).Distinct().Count() > 1) CancelActorSpeechPreparation("actor-general-language-runs");
        else ResolveActorSpeechPreparation(text, language, generation);
        if (!ActorGeneralCurrent(value)) return true;
        string envelope = "<silent/>";
        if (!string.IsNullOrWhiteSpace(text))
        {
            var safe = new StringBuilder("<speech mode=\"independent\"/>");
            foreach (var part in parts)
                safe.Append("<lang code=\"").Append(RoomFallbackLanguage(part.LanguageCode)).Append("\"/><say>")
                    .Append(System.Security.SecurityElement.Escape(part.Text)).Append("</say>");
            envelope = safe.ToString();
        }
        if (recordHistory && !TryCompleteActorPlanAcceptanceHistory(model, generation, envelope))
        {
            (model as IFastLoopHistoryBackend)?.CompleteFastLoopHistory();
            model.m_DataList.Add(new LLM.SendData("assistant", envelope));
            model.CheckHistory();
        }
        if (!ActorGeneralCurrent(value)) return true;
        CancelClosedRoomPreparation("actor-general-reply-selected");
        AcknowledgeObservations(value.Observations, generation, envelope);
        if (!ActorGeneralCurrent(value)) return true;
        m_FormalResponseInFlight = false;
        if (m_FastLoopTurn?.Generation == generation) m_FastLoopTurn = null;
        ClearStagedFormalObservation();
        if (string.IsNullOrWhiteSpace(text)) OnStreamDelta("<silent/>");
        else foreach (var part in parts)
            OnSpeechStreamDelta(new SpeechText(part.Text, part.LanguageCode, "actor-general-approved", SpeechActionDependency.Independent, part.SuppressTts));
        OnStreamComplete(envelope);
        return true;
    }
}
