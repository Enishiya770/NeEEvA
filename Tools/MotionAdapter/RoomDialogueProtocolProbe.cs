using System;
using System.IO;
using System.Web.Script.Serialization;

/// <summary>CLI harness for the production room contract and public executable parser, with no Unity or model runtime.</summary>
public static class RoomDialogueProtocolProbe
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--contract")
        {
            string contract = DialogueMotionProtocol.GeneratedOutputContract + "\n" + DialogueMotionProtocol.MotionFeedbackOutputContract;
            contract += "\n一次 motion 会独立执行，不会随着下一句话自动重新开始。" +
                "continue/next 只调度后续回复，不表示身体动作已经完成；不要为同一动作在续轮中重复发送 motion。" +
                "已有动作进行中时可以继续说话；只有确实要改变动作时才发送新 motion，停止用 none。";
            if (args[1] == "enabled") contract = DialogueMotionProtocol.IncludeRoomOutput(contract)
                .Replace("停止用 none。", "停止上身动作用 none；停止房间移动用 stop-moving。");
            Console.WriteLine(contract);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--inspect")
        {
            var channels = RoleOutputChannels.Parse(File.ReadAllText(args[1]));
            string executable = channels.ToExecutableText();
            bool accepted = DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out string rejection, args[2] == "enabled");
            Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                accepted, rejection, name = accepted ? intent.Name : null, speech = channels.Speech,
                description = accepted ? intent.Description : null, isRoomMotion = accepted && intent.IsRoomMotion,
                controlPlan = accepted ? intent.ControlPlan : null, goal = accepted ? intent.Goal : null,
                malformedTool = channels.HasMalformedTool, executableWithoutMotion = executable
            }));
            return 0;
        }
        throw new ArgumentException("Use --contract enabled|disabled or --inspect PUBLIC_REPLY_FILE enabled|disabled.");
    }
}
