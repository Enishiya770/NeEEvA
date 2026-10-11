using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckTaskSequenceDispatch(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var recorded = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskSequence.latest-2003-20261011.json"));
        string request = (string)recorded["request"];
        bool old = model.TaskSequencesEnabled;
        model.TaskSequencesEnabled = true;
        try
        {
            yield return PrepareRoomPlanning(chat, bridge, request);
            var initial = model.Decisions.Last(); int decisions = model.Decisions.Count, revision = bridge.Controller.OperationRevision;
            var wrong = new JObject { ["intent"] = "approach", ["origin"] = "user", ["evidence"] = "走到我身边来", ["taskStep"] = "s2",
                ["taskPlan"] = new JArray(
                    new JObject { ["id"] = "s1", ["quote"] = "把你刚才关掉的那盏小灯打开", ["kind"] = "device-state", ["desiredState"] = "on", ["after"] = new JArray() },
                    new JObject { ["id"] = "s2", ["quote"] = "走到我身边来", ["kind"] = "approach", ["desiredState"] = null, ["after"] = new JArray("s1") }) };
            initial.Complete(true, wrong.ToString(Formatting.None), "controlled-latest-wrong-order");
            yield return Wait(() => model.Decisions.Count > decisions, "One bounded plan correction for an unmet predecessor");
            var correction = model.Decisions.Last(); var input = JObject.Parse(correction.Input);
            Require(bridge.Controller.OperationRevision == revision &&
                (string)input.SelectToken("taskSequence.steps[0].status") == "pending" &&
                (string)input.SelectToken("decisionRepair.rejectionReason") == "invalid-task-sequence-prerequisite",
                "Production callback rejects the latest Play's wrong-order approach before submitting movement; repair retains the first step.");
            File.WriteAllText(Path.Combine(output, "sequence-wrong-order-repair.request.json"), correction.Wire);
            // The rejected planner cannot erase its first step in its retry.
            wrong.Remove("taskPlan"); correction.Complete(true, wrong.ToString(Formatting.None), "controlled-repeat-wrong-order");
            Require(bridge.Controller.OperationRevision == revision && model.Decisions.Count == decisions + 1 &&
                (string)((JObject)Call(chat, "ReadRoomTaskDecisionFailure"))?["validationReason"] == "invalid-task-sequence-prerequisite",
                "A second wrong order settles as an explicit planning failure with no action or unbounded retry.");
            Call(chat, "InvalidateFormalResponse", "sequence-regression-complete");
        }
        finally { model.TaskSequencesEnabled = old; }

        // Exercise the real serializer using an actual recorded production frame,
        // authored persona and multi-turn accepted text, rather than a tiny scene.
        var history = serializer.m_DataList.ToList(); string trailing = serializer.TrailingContext;
        try
        {
            foreach (JObject reply in recorded["dialogue"]) serializer.m_DataList.Add(new LLM.SendData("assistant", (string)reply["publicText"]));
            string prompt = (string)Call(chat, "BuildOrdinaryAutonomyIntentProbePrompt", "latest-full-frame", (string)recorded["frame"]);
            string Wire() => (string)typeof(ChatQW).GetMethod("BuildAutonomyIntentRequestJsonWithImage", Flags)
                .Invoke(serializer, new object[] { prompt, "data:image/png;base64,AA==" });
            string wire = Wire(); object[] args = { wire, null };
            Require((bool)typeof(ChatQW).GetMethod("AdmitAutonomyIntentWire", Flags).Invoke(serializer, args),
                "Latest production frame with the real complete message envelope fits the unchanged autonomy budget: " + args[1]);
            File.WriteAllText(Path.Combine(output, "latest-autonomy-full-envelope.request.json"), wire);
            LastDecisionFailureReport["latestAutonomyFullEnvelopeBudget"] = (JObject)args[1];
            serializer.TrailingContext = "Unknown authored restriction that must remain literal: " + new string('约', 30000);
            wire = Wire(); args = new object[] { wire, null };
            Require(!(bool)typeof(ChatQW).GetMethod("AdmitAutonomyIntentWire", Flags).Invoke(serializer, args) && wire.Contains(new string('约', 30000)),
                "Oversized mandatory constraints still reject rather than silently disappearing in the autonomy projection.");
        }
        finally { serializer.m_DataList = history; serializer.TrailingContext = trailing; }
    }
}
