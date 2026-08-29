#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 在 SenseVoiceSpeechToText 的 Inspector 中管理“身份 → 多声纹”仓库。
/// 所有写操作都走本机服务 API；服务端负责原子写入和操作前备份。
/// </summary>
[CustomEditor(typeof(SenseVoiceSpeechToText))]
public sealed class SenseVoiceSpeechToTextEditor : Editor
{
    [Serializable]
    private sealed class SpeakerStoreResponse
    {
        public int version;
        public string profile_path = "";
        public SpeakerIdentity[] identities = Array.Empty<SpeakerIdentity>();
        public SpeakerVoiceprint[] unassigned_candidates = Array.Empty<SpeakerVoiceprint>();
        public bool auto_organize_enabled;
        public float auto_merge_threshold;
        public int blocked_auto_pair_count;
        public SpeakerOperation[] operations = Array.Empty<SpeakerOperation>();
    }

    [Serializable]
    private sealed class SpeakerIdentity
    {
        public string identity_id = "";
        public string display_name = "";
        public string kind = "";
        public string status = "";
        public bool locked;
        public bool persistent;
        public int voiceprint_count;
        public int confirmed_voiceprints;
        public int candidate_voiceprints;
        public int utterance_count;
        public int enroll_utterances;
        public int total_speech_ms;
        public string created_at = "";
        public string last_seen = "";
        public string[] aliases = Array.Empty<string>();
        public SpeakerVoiceprint[] voiceprints = Array.Empty<SpeakerVoiceprint>();
    }

    [Serializable]
    private sealed class SpeakerVoiceprint
    {
        public string voiceprint_id = "";
        public string status = "";
        public bool persistent;
        public bool assigned;
        public string identity_id = "";
        public string identity_name = "";
        public string identity_kind = "";
        public string provisional_name = "";
        public float enrollment_progress;
        public int utterance_count;
        public int enroll_utterances;
        public int total_speech_ms;
        public string created_at = "";
        public string last_seen = "";
    }

    [Serializable]
    private sealed class BasicResponse
    {
        public bool ok;
        public string error = "";
    }

    [Serializable]
    private sealed class MergePreviewResponse
    {
        public bool ok;
        public MergePreview preview;
        public string error = "";
    }

    [Serializable]
    private sealed class MergePreview
    {
        public string source_id = "";
        public string source_name = "";
        public string source_type = "";
        public bool source_persistent;
        public int source_voiceprint_count;
        public string target_id = "";
        public string target_name = "";
        public string target_kind = "";
        public bool target_persistent;
        public int target_voiceprint_count;
        public float best_similarity;
        public float match_threshold;
        public float session_threshold;
    }

    [Serializable]
    private sealed class SpeakerOperation
    {
        public string operation_id = "";
        public string type = "";
        public string mode = "";
        public string created_at = "";
        public string undone_at = "";
        public string source_id = "";
        public string source_name = "";
        public string target_id = "";
        public string target_name = "";
        public float best_similarity;
        public string[] moved_voiceprint_ids = Array.Empty<string>();
    }

    private sealed class IdentityRowState
    {
        public string originalName = "";
        public string editedName = "";
        public bool expanded;
    }

    private sealed class VoiceprintRowState
    {
        public bool expanded;
        public string candidateName = "";
    }

    private enum PendingMode
    {
        None,
        MoveVoiceprint,
        MergeIdentity,
    }

    private readonly Dictionary<string, IdentityRowState> m_IdentityRows =
        new Dictionary<string, IdentityRowState>(StringComparer.Ordinal);
    private readonly Dictionary<string, VoiceprintRowState> m_VoiceprintRows =
        new Dictionary<string, VoiceprintRowState>(StringComparer.Ordinal);

    private SerializedProperty m_ServerSetting;
    private SpeakerStoreResponse m_Store;
    private UnityWebRequest m_Request;
    private UnityWebRequestAsyncOperation m_Operation;
    private Action<UnityWebRequest> m_OnRequestComplete;
    private bool m_ShowRepository = true;
    private bool m_IncludeSession = true;
    private string m_Message = "点击“刷新声纹仓库”读取本机档案。";
    private MessageType m_MessageType = MessageType.Info;

    private PendingMode m_PendingMode;
    private string m_PendingSourceId = "";
    private string m_PendingSourceName = "";
    private string m_PendingSourceParentId = "";
    private bool m_PendingSourcePersistent;
    private string m_PendingTargetId = "";
    private string m_MergeDisplayName = "";
    private MergePreview m_Preview;
    private bool m_AllowLowSimilarity;

    private bool IsBusy => m_Request != null;

    private void OnEnable()
    {
        m_ServerSetting = serializedObject.FindProperty("m_ServerSetting");
    }

    private void OnDisable()
    {
        EditorApplication.update -= PollRequest;
        if (m_Request != null)
        {
            m_Request.Abort();
            m_Request.Dispose();
        }
        m_Request = null;
        m_Operation = null;
        m_OnRequestComplete = null;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10f);
        m_ShowRepository = EditorGUILayout.BeginFoldoutHeaderGroup(
            m_ShowRepository,
            "声纹仓库与身份管理");
        if (m_ShowRepository)
            DrawSpeakerRepository();
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    private void DrawSpeakerRepository()
    {
        EditorGUILayout.HelpBox(
            "外层是“人（Identity）”，内层是这个人的不同声线（Voiceprint）。" +
            "陌生访客的 candidate 在确认归属前保持独立。显示名建议只写裸名，例如“ユウ”。",
            MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        bool includeSession = EditorGUILayout.ToggleLeft(
            "包含本次服务会话中的候选声纹",
            m_IncludeSession,
            GUILayout.ExpandWidth(true));
        if (includeSession != m_IncludeSession)
        {
            m_IncludeSession = includeSession;
            if (!IsBusy) RefreshRepository();
        }

        using (new EditorGUI.DisabledScope(IsBusy || string.IsNullOrWhiteSpace(ServerUrl)))
        {
            if (GUILayout.Button(IsBusy ? "请求中…" : "刷新声纹仓库", GUILayout.Width(112f)))
                RefreshRepository();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("服务", ServerUrl, EditorStyles.miniLabel);
        if (!string.IsNullOrEmpty(m_Message))
            EditorGUILayout.HelpBox(m_Message, m_MessageType);
        if (m_Store == null) return;

        if (m_Store.version < 2)
        {
            EditorGUILayout.HelpBox(
                "服务仍返回旧版声纹结构。请重启 SenseVoice 服务；首次启动会自动迁移并备份旧档案。",
                MessageType.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(m_Store.profile_path))
            DrawCopyableValue("档案文件", m_Store.profile_path);

        DrawAutomaticOrganization();

        SpeakerIdentity[] identities = m_Store.identities ?? Array.Empty<SpeakerIdentity>();
        SpeakerVoiceprint[] unassigned =
            m_Store.unassigned_candidates ?? Array.Empty<SpeakerVoiceprint>();
        int voiceprintCount = unassigned.Length;
        for (int i = 0; i < identities.Length; i++)
            if (identities[i] != null) voiceprintCount += identities[i].voiceprint_count;

        EditorGUILayout.LabelField(
            string.Format("身份 {0} · 声纹 {1} · 未归属候选 {2}",
                identities.Length, voiceprintCount, unassigned.Length),
            EditorStyles.boldLabel);

        string lastIdentityId = ((SenseVoiceSpeechToText)target).LastSpeakerId;
        string lastVoiceprintId = ((SenseVoiceSpeechToText)target).LastSpeakerVoiceprintId;
        for (int i = 0; i < identities.Length; i++)
            DrawIdentity(identities[i], lastIdentityId, lastVoiceprintId);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("未归属的陌生访客候选", EditorStyles.boldLabel);
        if (unassigned.Length == 0)
        {
            EditorGUILayout.HelpBox("当前没有未归属候选。", MessageType.None);
        }
        else
        {
            for (int i = 0; i < unassigned.Length; i++)
                DrawUnassignedVoiceprint(unassigned[i], lastVoiceprintId);
        }

        DrawPendingAction();
    }

    private void DrawAutomaticOrganization()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(
            string.Format("自动整理：{0} · 阈值 {1:F2} · 已阻止重复合并 {2} 对",
                m_Store.auto_organize_enabled ? "开启" : "关闭",
                m_Store.auto_merge_threshold,
                m_Store.blocked_auto_pair_count),
            EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(IsBusy))
        {
            if (GUILayout.Button("立即整理", GUILayout.Width(72f)))
                RunAutomaticOrganization();
        }
        EditorGUILayout.EndHorizontal();

        SpeakerOperation[] operations = m_Store.operations ?? Array.Empty<SpeakerOperation>();
        string latestActiveId = "";
        for (int i = operations.Length - 1; i >= 0; i--)
        {
            if (operations[i] != null && string.IsNullOrEmpty(operations[i].undone_at))
            {
                latestActiveId = operations[i].operation_id;
                break;
            }
        }
        if (operations.Length == 0)
        {
            EditorGUILayout.LabelField("尚无身份合并历史。", EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("最近整理历史（撤销必须从最新项开始）", EditorStyles.miniBoldLabel);
            int first = Math.Max(0, operations.Length - 6);
            for (int i = operations.Length - 1; i >= first; i--)
            {
                SpeakerOperation operation = operations[i];
                if (operation == null) continue;
                bool undone = !string.IsNullOrEmpty(operation.undone_at);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    string.Format("{0} · {1} → {2} · {3:F3}{4}",
                        operation.mode,
                        operation.source_name,
                        operation.target_name,
                        operation.best_similarity,
                        undone ? " · 已撤销" : ""),
                    EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(
                           IsBusy || undone || operation.operation_id != latestActiveId))
                {
                    if (GUILayout.Button("撤销", GUILayout.Width(48f)))
                        ConfirmUndoOperation(operation);
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawIdentity(
        SpeakerIdentity identity,
        string lastIdentityId,
        string lastVoiceprintId)
    {
        if (identity == null || string.IsNullOrEmpty(identity.identity_id)) return;
        IdentityRowState row = GetIdentityRow(identity);
        bool isLast = string.Equals(identity.identity_id, lastIdentityId, StringComparison.Ordinal);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        string header = string.Format(
            "{0}  ·  {1} / {2}  ·  {3} 个声纹",
            identity.display_name, identity.kind, identity.status, identity.voiceprint_count);
        if (isLast) header += "  ·  最近识别";
        row.expanded = EditorGUILayout.Foldout(row.expanded, header, true);
        GUILayout.FlexibleSpace();
        GUILayout.Label(identity.persistent ? "持久" : "会话", EditorStyles.miniBoldLabel);
        EditorGUILayout.EndHorizontal();

        if (row.expanded)
        {
            EditorGUI.indentLevel++;
            DrawCopyableValue("identity_id", identity.identity_id);

            EditorGUILayout.BeginHorizontal();
            row.editedName = EditorGUILayout.TextField("显示名", row.editedName);
            bool nameChanged = !string.Equals(
                row.originalName,
                (row.editedName ?? "").Trim(),
                StringComparison.Ordinal);
            using (new EditorGUI.DisabledScope(
                       IsBusy || !nameChanged || string.IsNullOrWhiteSpace(row.editedName)))
            {
                if (GUILayout.Button("写回", GUILayout.Width(48f)))
                    RenameIdentity(identity, row);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "状态",
                string.Format("{0} / {1}{2} · confirmed {3} / candidate {4}",
                    identity.kind,
                    identity.status,
                    identity.locked ? " / locked" : "",
                    identity.confirmed_voiceprints,
                    identity.candidate_voiceprints));
            EditorGUILayout.LabelField(
                "累计",
                string.Format("识别 {0} · 注册样本 {1} · 有效语音 {2}",
                    identity.utterance_count,
                    identity.enroll_utterances,
                    FormatDuration(identity.total_speech_ms)));
            if (identity.aliases != null && identity.aliases.Length > 0)
                EditorGUILayout.LabelField("历史 ID", string.Join(", ", identity.aliases));
            if (!string.IsNullOrEmpty(identity.created_at))
                EditorGUILayout.LabelField("创建", FormatTimestamp(identity.created_at));
            if (!string.IsNullOrEmpty(identity.last_seen))
                EditorGUILayout.LabelField("最后识别", FormatTimestamp(identity.last_seen));

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("声纹", EditorStyles.miniBoldLabel);
            SpeakerVoiceprint[] voiceprints = identity.voiceprints ?? Array.Empty<SpeakerVoiceprint>();
            for (int i = 0; i < voiceprints.Length; i++)
                DrawAssignedVoiceprint(voiceprints[i], identity, lastVoiceprintId);

            EditorGUILayout.BeginHorizontal();
            bool canMergeAway = !identity.locked &&
                                identity.kind != "owner" && identity.kind != "ai";
            using (new EditorGUI.DisabledScope(IsBusy || !canMergeAway))
            {
                if (GUILayout.Button("合并到其他身份…"))
                    StartMergeIdentity(identity);
            }
            using (new EditorGUI.DisabledScope(IsBusy || identity.locked || identity.kind == "ai"))
            {
                if (GUILayout.Button("删除整个身份…"))
                    ConfirmDeleteIdentity(identity);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawAssignedVoiceprint(
        SpeakerVoiceprint voiceprint,
        SpeakerIdentity identity,
        string lastVoiceprintId)
    {
        if (voiceprint == null || string.IsNullOrEmpty(voiceprint.voiceprint_id)) return;
        VoiceprintRowState row = GetVoiceprintRow(voiceprint);
        bool isLast = string.Equals(
            voiceprint.voiceprint_id, lastVoiceprintId, StringComparison.Ordinal);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        string header = ShortId(voiceprint.voiceprint_id) + "  ·  " + voiceprint.status;
        if (isLast) header += "  ·  最近匹配";
        row.expanded = EditorGUILayout.Foldout(row.expanded, header, true);
        if (row.expanded)
        {
            EditorGUI.indentLevel++;
            DrawCopyableValue("voiceprint_id", voiceprint.voiceprint_id);
            DrawVoiceprintMetrics(voiceprint);

            EditorGUILayout.BeginHorizontal();
            if (voiceprint.status != "confirmed")
            {
                using (new EditorGUI.DisabledScope(IsBusy))
                {
                    if (GUILayout.Button("确认声纹"))
                        ConfirmVoiceprint(voiceprint, identity);
                }
            }

            bool protectedLast = identity.locked && identity.voiceprint_count <= 1;
            bool isAi = identity.kind == "ai";
            using (new EditorGUI.DisabledScope(IsBusy || protectedLast || isAi))
            {
                if (GUILayout.Button("移动到…"))
                    StartMoveVoiceprint(voiceprint, identity);
                if (GUILayout.Button("移出身份"))
                    ConfirmDetachVoiceprint(voiceprint, identity);
                if (GUILayout.Button("删除…"))
                    ConfirmDeleteVoiceprint(voiceprint, identity.display_name);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawUnassignedVoiceprint(SpeakerVoiceprint voiceprint, string lastVoiceprintId)
    {
        if (voiceprint == null || string.IsNullOrEmpty(voiceprint.voiceprint_id)) return;
        VoiceprintRowState row = GetVoiceprintRow(voiceprint);
        bool isLast = string.Equals(
            voiceprint.voiceprint_id, lastVoiceprintId, StringComparison.Ordinal);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        string display = string.IsNullOrWhiteSpace(voiceprint.provisional_name)
            ? "陌生访客"
            : voiceprint.provisional_name;
        string header = display + "  ·  candidate  ·  " +
                        (voiceprint.persistent ? "持久" : "会话");
        if (isLast) header += "  ·  最近匹配";
        row.expanded = EditorGUILayout.Foldout(row.expanded, header, true);
        if (row.expanded)
        {
            EditorGUI.indentLevel++;
            DrawCopyableValue("voiceprint_id", voiceprint.voiceprint_id);
            DrawVoiceprintMetrics(voiceprint);

            EditorGUILayout.BeginHorizontal();
            row.candidateName = EditorGUILayout.TextField("新身份显示名", row.candidateName);
            using (new EditorGUI.DisabledScope(
                       IsBusy || string.IsNullOrWhiteSpace(row.candidateName)))
            {
                if (GUILayout.Button("建立身份", GUILayout.Width(68f)))
                    CreateIdentityFromCandidate(voiceprint, row);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(IsBusy))
            {
                if (GUILayout.Button("归入已有身份…"))
                    StartMoveVoiceprint(voiceprint, null);
                if (GUILayout.Button("删除候选…"))
                    ConfirmDeleteVoiceprint(voiceprint, display);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndVertical();
    }

    private static void DrawVoiceprintMetrics(SpeakerVoiceprint voiceprint)
    {
        EditorGUILayout.LabelField(
            "注册",
            string.Format("{0:P0} · 有效样本 {1} · 累计识别 {2}",
                voiceprint.enrollment_progress,
                voiceprint.enroll_utterances,
                voiceprint.utterance_count));
        EditorGUILayout.LabelField("有效语音", FormatDuration(voiceprint.total_speech_ms));
        if (!string.IsNullOrEmpty(voiceprint.created_at))
            EditorGUILayout.LabelField("创建", FormatTimestamp(voiceprint.created_at));
        if (!string.IsNullOrEmpty(voiceprint.last_seen))
            EditorGUILayout.LabelField("最后识别", FormatTimestamp(voiceprint.last_seen));
    }

    private void DrawPendingAction()
    {
        if (m_PendingMode == PendingMode.None) return;

        EditorGUILayout.Space(8f);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(
            m_PendingMode == PendingMode.MergeIdentity ? "合并身份" : "移动 / 归属声纹",
            EditorStyles.boldLabel);
        EditorGUILayout.LabelField("来源", m_PendingSourceName + "  (" + m_PendingSourceId + ")");

        List<SpeakerIdentity> targets = GetActionTargets();
        if (targets.Count == 0)
        {
            EditorGUILayout.HelpBox("当前没有可用的目标身份。", MessageType.Warning);
            if (GUILayout.Button("取消")) ClearPendingAction();
            EditorGUILayout.EndVertical();
            return;
        }

        int selectedIndex = FindTargetIndex(targets, m_PendingTargetId);
        string[] labels = new string[targets.Count];
        for (int i = 0; i < targets.Count; i++)
            labels[i] = targets[i].display_name + "  (" + targets[i].identity_id + ")";
        int newIndex = EditorGUILayout.Popup("目标身份", selectedIndex, labels);
        if (newIndex != selectedIndex || string.IsNullOrEmpty(m_PendingTargetId))
        {
            m_PendingTargetId = targets[newIndex].identity_id;
            m_MergeDisplayName = targets[newIndex].display_name;
            m_Preview = null;
            m_AllowLowSimilarity = false;
        }

        if (m_PendingMode == PendingMode.MergeIdentity)
            m_MergeDisplayName = EditorGUILayout.TextField("合并后显示名", m_MergeDisplayName);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(IsBusy))
        {
            if (GUILayout.Button("检查相似度")) RequestMergePreview();
            if (GUILayout.Button("取消")) ClearPendingAction();
        }
        EditorGUILayout.EndHorizontal();

        if (m_Preview != null && m_Preview.target_id == m_PendingTargetId)
            DrawPreviewAndConfirm();
        EditorGUILayout.EndVertical();
    }

    private void DrawPreviewAndConfirm()
    {
        bool strong = m_Preview.best_similarity >= m_Preview.match_threshold;
        bool plausible = m_Preview.best_similarity >= m_Preview.session_threshold;
        MessageType type = strong ? MessageType.Info : plausible ? MessageType.Warning : MessageType.Error;
        string assessment = strong
            ? "达到正式匹配阈值"
            : plausible ? "只达到会话候选阈值，请人工核对" : "低于会话阈值，极可能不是同一人";
        EditorGUILayout.HelpBox(
            string.Format(
                "最佳声纹相似度 {0:F3}（正式阈值 {1:F3} / 会话阈值 {2:F3}）\n{3}\n" +
                "操作只会重新归属声纹，不会把向量平均成一个声纹。",
                m_Preview.best_similarity,
                m_Preview.match_threshold,
                m_Preview.session_threshold,
                assessment),
            type);

        if (!plausible)
            m_AllowLowSimilarity = EditorGUILayout.ToggleLeft(
                "高级确认：即使低相似度也允许执行（我已核实确为同一人）",
                m_AllowLowSimilarity);

        bool canConfirm = plausible || m_AllowLowSimilarity;
        if (m_PendingMode == PendingMode.MergeIdentity &&
            string.IsNullOrWhiteSpace(m_MergeDisplayName))
            canConfirm = false;

        using (new EditorGUI.DisabledScope(IsBusy || !canConfirm))
        {
            string button = m_PendingMode == PendingMode.MergeIdentity
                ? "确认合并身份…"
                : "确认移动声纹…";
            if (GUILayout.Button(button)) ConfirmPendingAction();
        }
    }

    private List<SpeakerIdentity> GetActionTargets()
    {
        List<SpeakerIdentity> result = new List<SpeakerIdentity>();
        SpeakerIdentity[] identities = m_Store == null || m_Store.identities == null
            ? Array.Empty<SpeakerIdentity>()
            : m_Store.identities;
        for (int i = 0; i < identities.Length; i++)
        {
            SpeakerIdentity candidate = identities[i];
            if (candidate == null || candidate.kind == "ai") continue;
            if (m_PendingMode == PendingMode.MergeIdentity &&
                candidate.identity_id == m_PendingSourceId) continue;
            if (m_PendingMode == PendingMode.MoveVoiceprint &&
                candidate.identity_id == m_PendingSourceParentId) continue;
            if (m_PendingSourcePersistent && !candidate.persistent) continue;
            result.Add(candidate);
        }
        return result;
    }

    private static int FindTargetIndex(List<SpeakerIdentity> targets, string targetId)
    {
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].identity_id == targetId) return i;
        return 0;
    }

    private void StartMoveVoiceprint(SpeakerVoiceprint voiceprint, SpeakerIdentity parent)
    {
        m_PendingMode = PendingMode.MoveVoiceprint;
        m_PendingSourceId = voiceprint.voiceprint_id;
        m_PendingSourceName = parent == null
            ? (string.IsNullOrWhiteSpace(voiceprint.provisional_name)
                ? voiceprint.voiceprint_id
                : voiceprint.provisional_name)
            : parent.display_name + " / " + ShortId(voiceprint.voiceprint_id);
        m_PendingSourceParentId = parent == null ? "" : parent.identity_id;
        m_PendingSourcePersistent = voiceprint.persistent;
        InitializePendingTarget();
    }

    private void StartMergeIdentity(SpeakerIdentity identity)
    {
        m_PendingMode = PendingMode.MergeIdentity;
        m_PendingSourceId = identity.identity_id;
        m_PendingSourceName = identity.display_name;
        m_PendingSourceParentId = "";
        m_PendingSourcePersistent = identity.persistent;
        InitializePendingTarget();
    }

    private void InitializePendingTarget()
    {
        m_PendingTargetId = "";
        m_MergeDisplayName = "";
        m_Preview = null;
        m_AllowLowSimilarity = false;
        List<SpeakerIdentity> targets = GetActionTargets();
        if (targets.Count > 0)
        {
            m_PendingTargetId = targets[0].identity_id;
            m_MergeDisplayName = targets[0].display_name;
        }
    }

    private void ClearPendingAction()
    {
        m_PendingMode = PendingMode.None;
        m_PendingSourceId = "";
        m_PendingSourceName = "";
        m_PendingSourceParentId = "";
        m_PendingTargetId = "";
        m_Preview = null;
        m_AllowLowSimilarity = false;
        Repaint();
    }

    private void RequestMergePreview()
    {
        WWWForm form = new WWWForm();
        form.AddField("source_id", m_PendingSourceId);
        form.AddField("target_id", m_PendingTargetId);
        form.AddField(
            "source_type",
            m_PendingMode == PendingMode.MergeIdentity ? "identity" : "voiceprint");
        BeginRequest(UnityWebRequest.Post(ServerUrl + "/speakers/merge-preview", form), request =>
        {
            if (!RequestSucceeded(request, "检查声纹相似度")) return;
            try
            {
                MergePreviewResponse response =
                    JsonUtility.FromJson<MergePreviewResponse>(request.downloadHandler.text);
                if (response == null || !response.ok || response.preview == null)
                    throw new InvalidOperationException(
                        response == null || string.IsNullOrWhiteSpace(response.error)
                            ? "服务未返回预览"
                            : response.error);
                m_Preview = response.preview;
                m_AllowLowSimilarity = false;
                SetMessage("相似度检查完成；请核对后再确认。", MessageType.Info);
            }
            catch (Exception exception)
            {
                SetMessage("解析合并预览失败：" + exception.Message, MessageType.Error);
            }
        });
    }

    private void ConfirmPendingAction()
    {
        string operation = m_PendingMode == PendingMode.MergeIdentity ? "合并身份" : "移动声纹";
        string detail = operation + "会改变今后的说话人归属。服务会在写入前自动备份档案。\n\n" +
                        "来源：" + m_PendingSourceName + "\n目标：" + m_PendingTargetId;
        if (!EditorUtility.DisplayDialog("确认" + operation, detail, "确认执行", "取消")) return;

        WWWForm form = new WWWForm();
        string route;
        if (m_PendingMode == PendingMode.MergeIdentity)
        {
            route = "/speakers/merge";
            form.AddField("source_id", m_PendingSourceId);
            form.AddField("target_id", m_PendingTargetId);
            form.AddField("display_name", (m_MergeDisplayName ?? "").Trim());
        }
        else
        {
            route = "/speakers/assign-voiceprint";
            form.AddField("voiceprint_id", m_PendingSourceId);
            form.AddField("target_identity_id", m_PendingTargetId);
        }
        PostMutation(route, form, operation, operation + "完成。", true);
    }

    private void RenameIdentity(SpeakerIdentity identity, IdentityRowState row)
    {
        string name = (row.editedName ?? "").Trim();
        WWWForm form = new WWWForm();
        form.AddField("speaker_id", identity.identity_id);
        form.AddField("display_name", name);
        PostMutation("/speakers/rename", form, "修改显示名", "显示名已改为“" + name + "”。");
    }

    private void CreateIdentityFromCandidate(
        SpeakerVoiceprint voiceprint,
        VoiceprintRowState row)
    {
        string name = (row.candidateName ?? "").Trim();
        WWWForm form = new WWWForm();
        form.AddField("speaker_id", voiceprint.voiceprint_id);
        form.AddField("display_name", name);
        PostMutation(
            "/speakers/rename",
            form,
            "建立身份",
            "已为候选声纹建立身份“" + name + "”。");
    }

    private void ConfirmVoiceprint(SpeakerVoiceprint voiceprint, SpeakerIdentity identity)
    {
        string message = "将把这条 candidate 声纹人工确认为 confirmed。\n\n身份：" +
                         identity.display_name + "\n声纹：" + voiceprint.voiceprint_id;
        if (!EditorUtility.DisplayDialog("确认声纹", message, "确认", "取消")) return;
        WWWForm form = new WWWForm();
        form.AddField("voiceprint_id", voiceprint.voiceprint_id);
        PostMutation("/speakers/confirm-voiceprint", form, "确认声纹", "声纹已确认。");
    }

    private void ConfirmDetachVoiceprint(SpeakerVoiceprint voiceprint, SpeakerIdentity identity)
    {
        string message = "声纹会从“" + identity.display_name +
                         "”移出，恢复为独立的陌生访客 candidate。\n\n" +
                         voiceprint.voiceprint_id;
        if (!EditorUtility.DisplayDialog("移出声纹", message, "确认移出", "取消")) return;
        WWWForm form = new WWWForm();
        form.AddField("voiceprint_id", voiceprint.voiceprint_id);
        PostMutation("/speakers/detach-voiceprint", form, "移出声纹", "声纹已移出身份。");
    }

    private void ConfirmDeleteVoiceprint(SpeakerVoiceprint voiceprint, string displayName)
    {
        string message = "将永久删除“" + displayName + "”下的这一条声纹。\n\n" +
                         voiceprint.voiceprint_id +
                         "\n\n服务会先建立备份，但此声纹之后不会再参与识别。";
        if (!EditorUtility.DisplayDialog("删除声纹", message, "删除", "取消")) return;
        WWWForm form = new WWWForm();
        form.AddField("voiceprint_id", voiceprint.voiceprint_id);
        PostMutation("/speakers/delete-voiceprint", form, "删除声纹", "声纹已删除。");
    }

    private void ConfirmDeleteIdentity(SpeakerIdentity identity)
    {
        string message = "将永久删除身份“" + identity.display_name + "”及其全部 " +
                         identity.voiceprint_count + " 条声纹。\n\n" + identity.identity_id +
                         "\n\n服务会先建立备份。";
        if (!EditorUtility.DisplayDialog("删除整个身份", message, "删除", "取消")) return;
        WWWForm form = new WWWForm();
        form.AddField("identity_id", identity.identity_id);
        PostMutation("/speakers/delete-identity", form, "删除身份", "身份及其声纹已删除。");
    }

    private void RunAutomaticOrganization()
    {
        PostMutation(
            "/speakers/auto-organize",
            new WWWForm(),
            "自动整理",
            "自动整理已完成；没有达到保守阈值的身份会继续保持独立。");
    }

    private void ConfirmUndoOperation(SpeakerOperation operation)
    {
        string message = "将撤销这次身份合并，并阻止同一组身份再次被自动合并。\n\n" +
                         operation.source_name + " → " + operation.target_name +
                         "\n相似度：" + operation.best_similarity.ToString("F3") +
                         "\n操作 ID：" + operation.operation_id;
        if (!EditorUtility.DisplayDialog("撤销声纹整理", message, "撤销", "取消")) return;
        WWWForm form = new WWWForm();
        form.AddField("operation_id", operation.operation_id);
        PostMutation("/speakers/undo", form, "撤销声纹整理", "声纹整理已撤销。");
    }

    private void PostMutation(
        string route,
        WWWForm form,
        string action,
        string successMessage,
        bool clearPending = false)
    {
        BeginRequest(UnityWebRequest.Post(ServerUrl + route, form), request =>
        {
            if (!RequestSucceeded(request, action)) return;
            try
            {
                BasicResponse response =
                    JsonUtility.FromJson<BasicResponse>(request.downloadHandler.text);
                if (response == null || !response.ok)
                    throw new InvalidOperationException(
                        response == null || string.IsNullOrWhiteSpace(response.error)
                            ? "服务未确认操作成功"
                            : response.error);
                if (clearPending) ClearPendingAction();
                RefreshRepository(successMessage);
            }
            catch (Exception exception)
            {
                SetMessage("解析" + action + "结果失败：" + exception.Message, MessageType.Error);
            }
        });
    }

    private IdentityRowState GetIdentityRow(SpeakerIdentity identity)
    {
        IdentityRowState row;
        if (!m_IdentityRows.TryGetValue(identity.identity_id, out row))
        {
            row = new IdentityRowState
            {
                originalName = identity.display_name ?? "",
                editedName = identity.display_name ?? "",
                expanded = identity.identity_id == "owner",
            };
            m_IdentityRows.Add(identity.identity_id, row);
        }
        return row;
    }

    private VoiceprintRowState GetVoiceprintRow(SpeakerVoiceprint voiceprint)
    {
        VoiceprintRowState row;
        if (!m_VoiceprintRows.TryGetValue(voiceprint.voiceprint_id, out row))
        {
            row = new VoiceprintRowState
            {
                candidateName = voiceprint.provisional_name ?? "",
            };
            m_VoiceprintRows.Add(voiceprint.voiceprint_id, row);
        }
        return row;
    }

    private string ServerUrl
    {
        get
        {
            if (m_ServerSetting == null) return "";
            serializedObject.UpdateIfRequiredOrScript();
            return (m_ServerSetting.stringValue ?? "").Trim().TrimEnd('/');
        }
    }

    private void RefreshRepository(string successMessage = "")
    {
        string server = ServerUrl;
        if (string.IsNullOrWhiteSpace(server))
        {
            SetMessage("请先填写 SenseVoice 服务地址。", MessageType.Warning);
            return;
        }

        string url = server + "/speakers?include_session=" +
                     (m_IncludeSession ? "true" : "false");
        BeginRequest(UnityWebRequest.Get(url), request =>
        {
            if (!RequestSucceeded(request, "读取声纹仓库")) return;
            try
            {
                SpeakerStoreResponse response =
                    JsonUtility.FromJson<SpeakerStoreResponse>(request.downloadHandler.text);
                if (response == null)
                    throw new InvalidOperationException("服务返回了空响应");
                m_Store = response;
                SynchronizeRows(response);
                int identityCount = response.identities == null ? 0 : response.identities.Length;
                int candidateCount = response.unassigned_candidates == null
                    ? 0
                    : response.unassigned_candidates.Length;
                SetMessage(
                    string.IsNullOrEmpty(successMessage)
                        ? string.Format("已读取 {0} 个身份、{1} 个未归属候选。",
                            identityCount, candidateCount)
                        : successMessage,
                    MessageType.Info);
            }
            catch (Exception exception)
            {
                SetMessage("解析声纹仓库失败：" + exception.Message, MessageType.Error);
            }
        });
    }

    private void SynchronizeRows(SpeakerStoreResponse response)
    {
        HashSet<string> identityIds = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> voiceprintIds = new HashSet<string>(StringComparer.Ordinal);
        SpeakerIdentity[] identities = response.identities ?? Array.Empty<SpeakerIdentity>();
        for (int i = 0; i < identities.Length; i++)
        {
            SpeakerIdentity identity = identities[i];
            if (identity == null || string.IsNullOrEmpty(identity.identity_id)) continue;
            identityIds.Add(identity.identity_id);
            IdentityRowState row = GetIdentityRow(identity);
            bool edited = !string.Equals(
                row.originalName, (row.editedName ?? "").Trim(), StringComparison.Ordinal);
            row.originalName = identity.display_name ?? "";
            if (!edited) row.editedName = row.originalName;
            AddVoiceprintRows(identity.voiceprints, voiceprintIds);
        }
        AddVoiceprintRows(response.unassigned_candidates, voiceprintIds);
        RemoveMissingKeys(m_IdentityRows, identityIds);
        RemoveMissingKeys(m_VoiceprintRows, voiceprintIds);
    }

    private void AddVoiceprintRows(
        SpeakerVoiceprint[] voiceprints,
        HashSet<string> voiceprintIds)
    {
        if (voiceprints == null) return;
        for (int i = 0; i < voiceprints.Length; i++)
        {
            SpeakerVoiceprint voiceprint = voiceprints[i];
            if (voiceprint == null || string.IsNullOrEmpty(voiceprint.voiceprint_id)) continue;
            voiceprintIds.Add(voiceprint.voiceprint_id);
            GetVoiceprintRow(voiceprint);
        }
    }

    private static void RemoveMissingKeys<T>(Dictionary<string, T> rows, HashSet<string> present)
    {
        List<string> removed = new List<string>();
        foreach (string id in rows.Keys)
            if (!present.Contains(id)) removed.Add(id);
        for (int i = 0; i < removed.Count; i++) rows.Remove(removed[i]);
    }

    private void BeginRequest(UnityWebRequest request, Action<UnityWebRequest> onComplete)
    {
        if (IsBusy)
        {
            request.Dispose();
            return;
        }
        m_Request = request;
        m_Request.timeout = 8;
        m_OnRequestComplete = onComplete;
        m_Operation = m_Request.SendWebRequest();
        EditorApplication.update += PollRequest;
        SetMessage("正在连接本机 SenseVoice 服务…", MessageType.Info);
        Repaint();
    }

    private void PollRequest()
    {
        if (m_Operation != null && !m_Operation.isDone) return;
        EditorApplication.update -= PollRequest;
        UnityWebRequest completed = m_Request;
        Action<UnityWebRequest> callback = m_OnRequestComplete;
        m_Request = null;
        m_Operation = null;
        m_OnRequestComplete = null;
        try
        {
            callback?.Invoke(completed);
        }
        finally
        {
            completed?.Dispose();
            Repaint();
        }
    }

    private bool RequestSucceeded(UnityWebRequest request, string action)
    {
        if (request != null && request.result == UnityWebRequest.Result.Success)
            return true;
        string details = request == null ? "请求不存在" : request.error;
        if (request != null && request.downloadHandler != null &&
            !string.IsNullOrWhiteSpace(request.downloadHandler.text))
        {
            string body = request.downloadHandler.text;
            try
            {
                BasicResponse error = JsonUtility.FromJson<BasicResponse>(body);
                details = error != null && !string.IsNullOrWhiteSpace(error.error)
                    ? error.error
                    : body;
            }
            catch
            {
                details = body;
            }
        }
        SetMessage(
            action + "失败：" + details +
            "\n请确认 SenseVoice 服务已重启，且 Inspector 中的服务地址正确。",
            MessageType.Error);
        return false;
    }

    private void SetMessage(string message, MessageType type)
    {
        m_Message = message;
        m_MessageType = type;
        Repaint();
    }

    private static void DrawCopyableValue(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(label);
        EditorGUILayout.SelectableLabel(
            value ?? "", EditorStyles.textField,
            GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("复制", GUILayout.Width(42f)))
            EditorGUIUtility.systemCopyBuffer = value ?? "";
        EditorGUILayout.EndHorizontal();
    }

    private static string ShortId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= 20) return value;
        return value.Substring(0, 9) + "…" + value.Substring(value.Length - 8);
    }

    private static string FormatDuration(int milliseconds)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (duration.TotalHours >= 1d)
            return string.Format("{0:0.0} 小时", duration.TotalHours);
        if (duration.TotalMinutes >= 1d)
            return string.Format("{0:0.0} 分钟", duration.TotalMinutes);
        return string.Format("{0:0.0} 秒", duration.TotalSeconds);
    }

    private static string FormatTimestamp(string value)
    {
        DateTime parsed;
        if (!DateTime.TryParse(value, out parsed)) return value;
        return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
#endif
