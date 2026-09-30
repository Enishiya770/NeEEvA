using System;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>A request to review a frozen original recording, never a confirmed training label.</summary>
public sealed class PersonalSingingTeachingCorrection
{
    public string Mode; // review, whole, ordered, range
    public string FirstLabel, SecondLabel, UserText;
    public double StartSeconds, EndSeconds;
}

/// <summary>
/// Deliberately narrow, immediate Chinese teaching shortcuts. Ambiguous language opens
/// a review card instead of guessing labels or a time boundary. Ordinary requests to
/// sing, quotes, future plans and generic complaints do not trigger this side channel.
/// </summary>
public static class PersonalSingingTeachingIntent
{
    private const string Labels = "说话|讲话|唱歌|歌唱|哼唱|环境音|环境噪音|噪音|静音|安静|不确定";
    private static readonly Regex LabelTokens = new Regex(Labels, RegexOptions.CultureInvariant);
    private static readonly Regex Range = new Regex(@"(?<start>\d+(?:\.\d+)?)\s*秒?\s*(?:到|至|—|–|~|～|-)\s*(?<end>\d+(?:\.\d+)?)\s*秒", RegexOptions.CultureInvariant);
    private static bool Has(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out PersonalSingingTeachingCorrection correction)
    {
        correction = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000) return false;
        string value = text.Trim();
        // A quotation may describe a correction without actually making one. Let the
        // ordinary character conversation handle it rather than mutating a review.
        if (Has(value, "[“”‘’\"「」『』]|比如|例如|假如|假设|如果|能不能|怎么才能|如何")) return false;
        bool sound = Has(value, Labels + "|声音|录音|声音分类|分段|区间|波形");
        bool teach = (sound && Has(value, "(?:教你|教她).*(?:分辨|区分|识别|判断|声音)|(?:打开|进入|开始).*(?:教她|声音学习|教学)|(?:检查|纠正).*(?:录音|声音|判断|分段)")) ||
            Has(value, "^(?:请)?(?:打开|进入|开始)(?:一下)?(?:教她|声音学习|声音教学)(?:模式|面板)?[。！!]*$");
        bool previous = Has(value, "刚才|刚刚|上一段|上段录音|上一条录音|刚那段|刚那句|那段录音|这段录音|这段声音");
        bool mistaken = sound && Has(value, "听错|分错|判断错|识别错|弄错|搞错|误判|当成|当作|纠正|标错");
        if (teach) { correction = Result("review", value); return true; }
        if (!previous) return false;
        // The character's own audio is not the user's last microphone recording.
        if (Has(value, "(?:你|她|角色)(?:刚才|刚刚).*(?:是在|在|是)(?:说话|讲话|唱歌|歌唱|哼唱)|(?:刚才|刚刚)(?:你|她|角色).*(?:是在|在|是)(?:说话|讲话|唱歌|歌唱|哼唱)") && !mistaken)
            return false;
        if (Has(value, "(?:准备|打算|想要|想|希望|请你|让你|让她|教你|教她).*(?:唱歌|歌唱|哼唱|说话|讲话)|(?:唱歌|歌唱|哼唱|说话|讲话).*(?:怎么样|好吗|好听吗|可以吗|好不好)"))
            return false;
        if (sound && Has(value, "还是|是不是|是否|吗[？?。.!！]?$|么[？?。.!！]?$"))
        { correction = Result("review", value); return true; }

        Match range = Range.Match(value);
        if (range.Success)
        {
            string after = value.Substring(range.Index + range.Length);
            string label = PositiveLabel(after);
            if (label != null && double.TryParse(range.Groups["start"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) &&
                double.TryParse(range.Groups["end"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double end) && start >= 0 && end > start && end <= 120)
            { correction = Result("range", value, label); correction.StartSeconds = start; correction.EndSeconds = end; return true; }
            if (sound) { correction = Result("review", value); return true; }
        }

        Match later = Regex.Match(value, "后面|后半段|后边|之后|后来", RegexOptions.CultureInvariant);
        bool head = Has(value, "开头|开始那|前面|前半段|前边|第一句");
        if (head && later.Success)
        {
            string first = PositiveLabel(value.Substring(0, later.Index));
            string second = PositiveLabel(value.Substring(later.Index + later.Length));
            if (first != null && second != null)
            { correction = Result("ordered", value, first, second); return true; }
            if (sound) { correction = Result("review", value); return true; }
        }
        // Describing only the beginning/end is not permission to relabel the whole.
        if (head || later.Success || Has(value, "最后|结尾|中间|其中|一部分|一句|一点"))
        {
            if (mistaken || PositiveLabel(value) != null) { correction = Result("review", value); return true; }
            return false;
        }
        string whole = PositiveLabel(value);
        if (whole != null)
        { correction = Result("whole", value, whole); return true; }
        if (mistaken || (sound && Has(value, "不是|没有|并非|不算"))) { correction = Result("review", value); return true; }
        return false;
    }

    private static string PositiveLabel(string clause)
    {
        string chosen = null;
        foreach (Match token in LabelTokens.Matches(clause))
        {
            string prefix = clause.Substring(0, token.Index);
            int punctuation = Math.Max(Math.Max(prefix.LastIndexOf('，'), prefix.LastIndexOf(',')), Math.Max(prefix.LastIndexOf('。'), prefix.LastIndexOf('；')));
            string local = prefix.Substring(punctuation + 1);
            // ASR may omit punctuation in "不是唱歌而是说话". Its affirmative
            // replacement is an assertion, not part of the preceding negation.
            var replacements = Regex.Matches(local, "(?:而|其实|实际上|实际)(?=是)", RegexOptions.CultureInvariant);
            if (replacements.Count > 0)
            { Match replacement = replacements[replacements.Count - 1]; local = local.Substring(replacement.Index + replacement.Length); }
            if (Has(local, "(?:不是|没有|没在|并非|不算|别当|不要|当成|当作|误判成|误认为)[^，,。；]*$")) continue;
            // Require an assertion ('是在说话', '是噪音', '改成讲话'), not merely a noun.
            if (!Has(local, "(?:是|在|为|属于|算|改成|改为|标成|标为|只是|其实)[\\s的本人我还只才都全]*$")) continue;
            switch (token.Value)
            {
                case "说话": case "讲话": chosen = "speech"; break;
                case "唱歌": case "歌唱": case "哼唱": chosen = "singing"; break;
                case "环境音": case "环境噪音": case "噪音": case "静音": case "安静": chosen = "environment"; break;
                case "不确定": chosen = "uncertain"; break;
            }
        }
        return chosen;
    }

    private static PersonalSingingTeachingCorrection Result(string mode, string text, string first = null, string second = null)
        => new PersonalSingingTeachingCorrection { Mode = mode, UserText = text, FirstLabel = first, SecondLabel = second };
}
