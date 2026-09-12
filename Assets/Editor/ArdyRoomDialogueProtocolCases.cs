using System;
using System.Text;

/// <summary>Production parser and stream-boundary cases; also runnable with the CLI C# compiler.</summary>
public static class ArdyRoomDialogueProtocolCases
{
    public static int Run()
    {
        int checks = 0;
        Action<bool, string> check = (ok, message) => { checks++; if (!ok) throw new InvalidOperationException(message); };
        foreach (string name in new[] { "approach", "stop-moving" })
        {
            string tag = "<motion name=\"" + name + "\"/>";
            string disabled = tag;
            check(!DialogueMotionProtocol.TryExtract(ref disabled, 3, 4, out _, out string reason) &&
                reason.Length > 0 && disabled.Length == 0, "Room motion was not opt-in: " + name);
            string raw = "<lang code=\"zh\"/><speech mode=\"independent\"/>我听到了。" + tag;
            for (int split = 0; split <= raw.Length; split++)
            {
                var spoken = new StringBuilder();
                var channels = new RoleOutputChannels(part => spoken.Append(part.Text));
                channels.Push(raw.Substring(0, split)); channels.Push(raw.Substring(split)); channels.Finish();
                string executable = channels.ToExecutableText();
                check(spoken.ToString() == "我听到了。" && channels.Speech == "我听到了。" &&
                    !channels.HasInvalidSpeechPhase && channels.SpeechDependency == SpeechActionDependency.Independent,
                    "Room tag leaked to speech or conflicted with independent staging at " + split);
                check(DialogueMotionProtocol.TryExtract(ref executable, 3, split, out var intent, out _, true) &&
                    intent.IsRoomMotion && intent.Name == name && intent.ResponseGeneration == 3 && intent.Sequence == split &&
                    intent.ActionId == "" && intent.Goal == null && intent.ControlPlan == null && intent.ActionPlan == null &&
                    executable == "我听到了。", "Room intent or metadata changed at " + split);
                var assigned = intent.WithExecutionIdentity("binding:3:" + split, "", 0);
                check(assigned.IsRoomMotion && assigned.ActionId.EndsWith(":" + split) && assigned.RepairAttempt == 0,
                    "Program identity did not preserve room intent");
            }
            foreach (string quoted in new[] { "<thought>" + tag + "</thought>", "<think>" + tag + "</think>",
                "`" + tag + "`", "```\n" + tag + "\n```", "<say>" + tag + "</say>",
                tag.Substring(0, tag.Length - 1) })
            {
                var channels = new RoleOutputChannels();
                foreach (char c in quoted) channels.Push(c.ToString());
                channels.Finish(); string executable = channels.ToExecutableText();
                check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out _, true), "Private/quoted/incomplete room motion executed: " + quoted);
                check(!channels.Speech.Contains("motion"), "Private room tag leaked to speech");
            }
            foreach (string attr in new[] { "text=\"walk\"", "x=\"1\"", "distance=\"0.5\"", "speed=\"1\"",
                "ref=\"last\"", "leftGoal=\"forward\"", "actionId=\"invented\"", "repairAttempt=\"0\"", "name=\"nod\"" })
            {
                string invalid = "<motion name=\"" + name + "\" " + attr + "/>";
                check(!DialogueMotionProtocol.TryExtract(ref invalid, 1, 1, out _, out string error, true) &&
                    error.Length > 0 && invalid.Length == 0, "Room schema accepted " + attr);
            }
            string combined = tag + "<motion name=\"nod\"/>";
            check(!DialogueMotionProtocol.TryExtract(ref combined, 1, 1, out _, out _, true), "Multiple routes executed in one response");
            string silent = RoleOutputChannels.Parse("<silent/>" + tag).ToExecutableText();
            check(DialogueMotionProtocol.TryExtract(ref silent, 1, 1, out _, out _, true), "Silent room motion lost");
        }
        foreach (string basic in new[] { "left-wave", "right-wave", "nod", "shake-head", "none" })
        {
            string tag = "<motion name=\"" + basic + "\"/>";
            check(DialogueMotionProtocol.TryExtract(ref tag, 1, 1, out var intent, out _, true) && !intent.IsRoomMotion,
                "Room capability altered existing gesture " + basic);
        }
        foreach (string original in new[] { DialogueMotionProtocol.BasicOutputContract,
            DialogueMotionProtocol.GeneratedOutputContract + DialogueMotionProtocol.MotionFeedbackOutputContract,
            DialogueMotionProtocol.SemanticOutputContract + DialogueMotionProtocol.MotionFeedbackOutputContract })
        {
            check(!original.Contains("name=\"approach\""), "Default contract advertised room motion");
            string enabled = DialogueMotionProtocol.IncludeRoomOutput(original);
            check(enabled.Contains(DialogueMotionProtocol.RoomOutputContract) && enabled.Contains("generate/compose/plan仍限制为原地上身动作"),
                "Room contract omitted route boundaries");
            check(enabled.Contains("none只停止上身动作") && enabled.Contains("不套用手腕或手臂自动修订"), "Room contract conflated lifetime or feedback");
            check(!enabled.Contains("当前不能独立控制单根手指，不能执行根腿移动"), "Global upper-body restriction contradicts room channel");
        }
        return checks;
    }
}
