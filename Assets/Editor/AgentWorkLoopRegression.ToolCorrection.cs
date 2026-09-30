using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;

public static partial class AgentWorkLoopRegression
{
    public static void RunToolCorrectionBatch()
    {
        SessionState.SetBool("NeEEvA.ToolCorrection.Only", true);
        RunBatch();
    }

    private static IEnumerator ToolCorrectionScenarios()
    {
        Check(!File.ReadAllText("Assets/AIChatTookit/Prompts/behavior.txt").Contains("<sing") &&
            PromptProtocolAssets.GeneralTools().Contains("skill_control"),
            "Behavior principles and the general tool contract are not separated.");
        foreach (string tool in new[] { "song_search query=\"public canary\"", "song_catalog" })
        {
            var query = RoleOutputChannels.Parse("<lang code=\"zh\"/><speech mode=\"independent\"/><say>我查一下。</say><" + tool + "/>",
                RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            Check(!query.HasInvalidSpeechPhase && query.HasActions && !query.HasInvalidPublicBoundary,
                "An independent read-only query became an invalid speech phase.");
            var bad = RoleOutputChannels.Parse("<lang code=\"zh\"/><speech mode=\"after_action\"/><say>我开始唱。</say><" + tool + "/>",
                RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            Check(bad.HasInvalidSpeechPhase, "An after_action promise without a dependent action was accepted.");
        }
        var switchPhase = RoleOutputChannels.Parse("<lang code=\"zh\"/><speech mode=\"independent\"/><say>已经找到。</say>" +
            "<speech mode=\"after_action\"/><say>现在唱。</say><sing refs=\"clip:public-canary\"/>", RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        Check(switchPhase.HasInvalidSpeechPhase && switchPhase.HasInvalidPublicBoundary && !switchPhase.HasActions,
            "A late dependent action escaped the strict public boundary.");
        foreach (IEnumerator scenario in new[] { CorrectionWaitsForResult(), CorrectionWaitIsBounded(), CorrectionWaitCancellation(), CorrectionWithoutReadIsImmediate() })
            while (scenario.MoveNext()) yield return scenario.Current;
    }

    private static void StartCorrection(Fixture f, bool search, bool catalog)
    {
        f.BeginUser("请查一下这段歌，再根据结果决定下一步。");
        Set(f.chat, "m_AgentRoundInFlight", true);
        Set(f.chat, "m_AgentCurrentRoundIsTick", false);
        Set(f.chat, "m_UserTurnAwaitingReplySince", Time.realtimeSinceStartup + .01f);
        Set(f.chat, "m_SongSearchInFlight", search); Set(f.chat, "m_SongSearchGeneration", 11);
        Set(f.chat, "m_SongCatalogInFlight", catalog); Set(f.chat, "m_SongCatalogGeneration", 12);
        Call(f.chat, "ReportToolFailureForLlm", "speech", "speech_phase_conflict", "没有实际演唱动作。", "仅查询应使用independent。");
        Call(f.chat, "OnStreamComplete", "<silent/>");
    }

    private static IEnumerator CorrectionWaitsForResult()
    {
        fixture = new Fixture("correction_coalesces_read_results"); var f = fixture; yield return null;
        StartCorrection(f, true, true);
        int budget = (int)Get(f.chat, "m_WorkContinuations");
        Check(f.model.Requests.Count == 0, "Correction raced an unfinished query.");
        Set(f.chat, "m_SongSearchInFlight", false); Set(f.chat, "m_SongSearchResultPending", true);
        Set(f.chat, "m_LastSongSearchResult", "PUBLIC_SEARCH_RESULT_NOT_CONFIRMED");
        yield return null;
        Check(f.model.Requests.Count == 0, "Correction ignored the second read from the same response.");
        Set(f.chat, "m_SongCatalogInFlight", false); Set(f.chat, "m_SongCatalogResultPending", true);
        Set(f.chat, "m_LastSongCatalogResult", "PUBLIC_CATALOG_RESULT_READY");
        float limit = Time.realtimeSinceStartup + 3f;
        while (f.model.Requests.Count == 0 && Time.realtimeSinceStartup < limit) yield return null;
        Check(f.model.Requests.Count == 1, "Completed reads did not resume exactly one correction.");
        string wire = f.model.LastRequest.ToString();
        Check(wire.Contains("PUBLIC_SEARCH_RESULT_NOT_CONFIRMED") && wire.Contains("PUBLIC_CATALOG_RESULT_READY"),
            "The resumed correction used a snapshot taken before the results arrived.");
        Check((int)Get(f.chat, "m_WorkContinuations") == budget && (int)Get(f.chat, "m_ToolCorrectionAttemptsThisUserTurn") == 1,
            "Waiting consumed an additional work/correction allowance.");
        Check((int)Get(f.chat, "m_SongSearchGeneration") == 11 && (int)Get(f.chat, "m_SongCatalogGeneration") == 12,
            "Waiting resent or cancelled a read.");
        f.model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>没有查到确定歌名。</say>");
        yield return null;
        Check(f.model.Requests.Count == 1, "A read result dispatched duplicate feedback.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator CorrectionWaitIsBounded()
    {
        fixture = new Fixture("correction_read_wait_has_realtime_deadline"); var f = fixture; yield return null;
        float started = Time.realtimeSinceStartup;
        StartCorrection(f, true, false);
        float limit = started + 4f;
        while (f.model.Requests.Count == 0 && Time.realtimeSinceStartup < limit) yield return null;
        Check(f.model.Requests.Count == 1 && Time.realtimeSinceStartup - started < 3.5f,
            "A read without a callback held the correction for a network timeout.");
        Check((bool)Get(f.chat, "m_SongSearchInFlight") && f.model.LastRequest.ToString().Contains("请求仍在进行，结果未知"),
            "Bounded waiting invented a failed/completed result or lost the pending fact.");
        Set(f.chat, "m_SongSearchInFlight", false); Set(f.chat, "m_SongSearchResultPending", true);
        Set(f.chat, "m_LastSongSearchResult", "PUBLIC_RESULT_AFTER_DEADLINE");
        f.model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>查询还没返回。</say>");
        Check((bool)Get(f.chat, "m_SongSearchResultPending"), "An older request acknowledged a late result it never received.");
        yield return null;
        Check(f.model.Requests.Count == 1, "A late read created another immediate correction.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator CorrectionWaitCancellation()
    {
        foreach (string cancel in new[] { "new-user", "generation", "stopped", "user-speaking", "disabled" })
        {
            fixture = new Fixture("correction_wait_cancelled_" + cancel); var f = fixture; yield return null;
            StartCorrection(f, true, false);
            if (cancel == "new-user") f.BeginUser("取消那个任务，只回答新问题。");
            if (cancel == "generation") Set(f.chat, "m_FormalResponseGeneration", (int)Get(f.chat, "m_FormalResponseGeneration") + 1);
            if (cancel == "stopped") Set(f.chat, "m_AgentRunning", false);
            if (cancel == "user-speaking") Set(f.chat, "m_UserSpeechActiveForAutonomy", true);
            if (cancel == "disabled") f.chat.enabled = false;
            Set(f.chat, "m_SongSearchInFlight", false); Set(f.chat, "m_SongSearchResultPending", true);
            Set(f.chat, "m_LastSongSearchResult", "PUBLIC_STALE_QUERY_RESULT");
            yield return null; yield return null;
            Check(f.model.Requests.Count == 0, "A stale read resumed a cancelled correction: " + cancel);
            if (cancel == "disabled")
            {
                Check(!(bool)Get(f.chat, "m_FormalResponseInFlight") && !(bool)Get(f.chat, "m_AgentRoundInFlight"),
                    "Disabling during the read wait left the conversation busy.");
                f.chat.enabled = true;
                Set(f.chat, "m_ToolCorrectionAttemptsThisUserTurn", 0);
                StartCorrection(f, false, false);
                Check(f.model.Requests.Count == 1, "Re-enabling could not dispatch a fresh correction.");
            }
            SaveScenario(f); f.Dispose(); fixture = null;
        }
    }

    private static IEnumerator CorrectionWithoutReadIsImmediate()
    {
        fixture = new Fixture("correction_without_read_does_not_wait"); var f = fixture; yield return null;
        // Writes have their own acknowledgement owner. A correction never waits
        // on one, a renderer, motion completion or a pending goal review.
        Set(f.chat, "m_SongMemoryInFlight", true);
        StartCorrection(f, false, false);
        Check(f.model.Requests.Count == 1, "An unrelated write blocked the correction.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }
}
