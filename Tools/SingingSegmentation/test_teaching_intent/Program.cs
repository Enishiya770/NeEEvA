using System;

static class Program
{
    private static int checks;
    private static void Check(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
    private static PersonalSingingTeachingCorrection Parse(string text, string mode, string first = null, string second = null)
    {
        Check(PersonalSingingTeachingIntent.TryParse(text, out var value), "Not recognized: " + text);
        Check(value.Mode == mode && value.FirstLabel == first && value.SecondLabel == second, "Wrong meaning: " + text + " -> " + value.Mode + " / " + value.FirstLabel + " / " + value.SecondLabel);
        Check(value.UserText == text, "Original utterance changed");
        return value;
    }
    public static void Main()
    {
        Parse("刚才开头那句是在说话，后面才是唱歌", "ordered", "speech", "singing");
        Parse("刚才前面是在哼唱，后面只是讲话", "ordered", "singing", "speech");
        Parse("刚才开头是环境音，后面是在歌唱", "ordered", "environment", "singing");
        Parse("我刚才是在说话，你却当成唱歌了", "whole", "speech");
        Parse("刚才不是唱歌，是说话", "whole", "speech");
        Parse("刚才不是唱歌而是说话", "whole", "speech");
        Parse("刚才开头不是唱歌而是说话后面才是在唱歌", "ordered", "speech", "singing");
        Parse("上一段全都是环境噪音", "whole", "environment");
        Parse("刚才我是在唱歌", "whole", "singing");
        Parse("刚才不是唱歌", "review");
        Parse("刚才开头是在说话", "review");
        Parse("刚才中间一句是在讲话", "review");
        Parse("刚才的录音你分错了", "review");
        Parse("教你区分说话和唱歌", "review");
        Parse("打开声音学习", "review");
        Parse("进入教她模式", "review");
        Parse("刚才是在说话还是唱歌", "review");
        var range = Parse("刚才3.7到14秒是在唱歌", "range", "singing");
        Check(range.StartSeconds == 3.7 && range.EndSeconds == 14, "Explicit seconds changed");
        range = Parse("那段录音从0秒到3.7秒是环境音", "range", "environment");
        Check(range.StartSeconds == 0 && range.EndSeconds == 3.7, "Zero-origin range changed");
        Parse("刚才14到3秒是唱歌", "review");
        Parse("刚才0到999秒是唱歌", "review");
        foreach (string text in new[] {
            "唱首歌给我听", "我现在是在唱歌", "刚才唱得怎么样", "刚才我唱歌你觉得怎么样", "我刚才想唱歌", "我刚才让你唱歌",
            "如果刚才开头是说话，后面是唱歌怎么办", "比如刚才不是唱歌，是说话", "她说‘刚才是在说话’", "你刚才是在唱歌", "刚才你是在说话",
            "刚才走错了", "刚才听错了", "那段录音的唱歌还不错", "", null })
            Check(!PersonalSingingTeachingIntent.TryParse(text, out _), "Unrelated/quoted input triggered a correction: " + text);
        Console.WriteLine("PASS " + checks + " teaching intent checks; no Unity, network, recordings or model labels modified.");
    }
}
