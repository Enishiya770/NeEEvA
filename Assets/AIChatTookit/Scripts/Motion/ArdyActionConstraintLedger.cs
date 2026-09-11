using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NeEEvA.Motion
{
    /// <summary>User requirements are historical intent, never proof of an achieved pose.</summary>
    public sealed class ArdyActionConstraintLedger
    {
        [Serializable] public sealed class Entry
        {
            public string field, value, evidence;
            public int userTurn;
            public Entry Copy() => (Entry)MemberwiseClone();
        }

        public sealed class Prepared
        {
            public ArdyActionPlan effective;
            internal int revision;
            internal Dictionary<string, Entry> next;
            internal string purpose;
        }

        private Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private string currentText = "", purpose = "";
        private int revision;
        public int CurrentUserTurn { get; private set; }
        public string CurrentUserText => currentText;
        public string Purpose => purpose;
        public bool HasConstraints => entries.Count != 0;
        public Entry[] Entries => entries.Values.OrderBy(e => e.field, StringComparer.Ordinal).Select(e => e.Copy()).ToArray();

        public void RecordUserTurn(string text)
        {
            CurrentUserTurn = checked(CurrentUserTurn + 1);
            currentText = text ?? "";
            revision++;
        }

        public void ClearGoal()
        {
            entries.Clear();
            purpose = "";
            revision++;
        }

        public void ClearGoalOnStop(int requestUserTurn)
        {
            // A role may stop its body on its own. Reusing the original request
            // must not also erase that user's requirements for the next plan.
            if (requestUserTurn != CurrentUserTurn || entries.Values.Any(e => e.userTurn >= requestUserTurn)) return;
            ClearGoal();
        }

        public void Reset()
        {
            ClearGoal();
            currentText = "";
            // Monotonic IDs prevent a previous binding's source from matching a new one.
            CurrentUserTurn = checked(CurrentUserTurn + 1);
        }

        public Prepared Prepare(ArdyActionPlan requested, int requestUserTurn)
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            requested.ValidateRaw();
            if (requestUserTurn != CurrentUserTurn)
                throw new ArgumentException("动作计划来自旧用户轮次，不能应用到当前要求。");
            var effective = requested.Copy();
            bool continuation = string.Equals(requested.scope, "continue", StringComparison.Ordinal);
            bool sourceNeeded = requested.LockFields.Length != 0 || requested.ReleaseFields.Length != 0
                || (!continuation && HasConstraints);
            if (sourceNeeded) VerifySource(requested, requestUserTurn);
            else if (requested.userTurn != 0 && requested.userTurn != requestUserTurn)
                throw new ArgumentException("计划引用的userTurn不属于发出本次请求的用户轮次。");

            if (!continuation && HasConstraints && entries.Values.Any(e => e.userTurn >= requestUserTurn))
                throw new ArgumentException("scope=new替换明确要求需要更新的用户轮次，不能用原要求的引句撤销自身。");

            var next = continuation ? Clone(entries) : new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in requested.ReleaseFields)
            {
                if (!continuation || !next.TryGetValue(field, out var oldEntry))
                    throw new ArgumentException("release只能撤销continue计划中已有的约束：" + field);
                if (requestUserTurn <= oldEntry.userTurn)
                    throw new ArgumentException("release需要晚于该约束来源的新用户依据：" + field);
                next.Remove(field);
            }

            foreach (var item in next.Values)
            {
                if (effective.HasField(item.field) && !Equivalent(item.field, effective.GetValue(item.field), item.value))
                    throw new ArgumentException("计划改变了仍有效的用户约束 " + item.field
                        + "=" + item.value + "；有新用户依据时先release再设置。");
                effective.SetValue(item.field, item.value);
            }

            foreach (string field in requested.LockFields)
            {
                if (!requested.HasField(field) || string.IsNullOrWhiteSpace(requested.GetValue(field)))
                    throw new ArgumentException("lock缺少对应的明确值：" + field);
                string value = requested.GetValue(field);
                VerifyNumericEvidence(field, value, requested.evidence);
                next[field] = new Entry { field = field, value = value, userTurn = requested.userTurn,
                    evidence = requested.evidence };
                effective.SetValue(field, value);
            }

            if (next.Count > 18) throw new ArgumentException("用户动作约束过多，请用更简洁的计划表达。");
            effective.lockFields = string.Join(",", next.Keys.OrderBy(x => x, StringComparer.Ordinal));
            return new Prepared { effective = effective, next = next, revision = revision,
                purpose = string.IsNullOrEmpty(requested.purpose) && continuation ? purpose : requested.purpose };
        }

        public void Commit(Prepared prepared)
        {
            if (prepared == null || prepared.revision != revision)
                throw new InvalidOperationException("动作约束在计划编译期间已变化，请重新规划。");
            entries = Clone(prepared.next);
            purpose = prepared.purpose ?? "";
            revision++;
        }

        private void VerifySource(ArdyActionPlan plan, int requestUserTurn)
        {
            if (plan.userTurn <= 0 || plan.userTurn != requestUserTurn || string.IsNullOrWhiteSpace(plan.evidence)
                || plan.evidence.Length > 256 || currentText.IndexOf(plan.evidence, StringComparison.Ordinal) < 0)
                throw new ArgumentException("用户约束需要本次userTurn与逐字匹配的evidence引句（最多256字符）；不能引用角色自己的补充。");
            if (plan.evidence.IndexOf('<') >= 0 || plan.evidence.IndexOf('>') >= 0 || plan.evidence.IndexOf('`') >= 0)
                throw new ArgumentException("不能把代码或动作标签示例当作用户动作约束的依据。");
        }

        private static readonly HashSet<string> NumericFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "leftBend", "rightBend", "amplitude", "cycles", "seconds" };

        private static void VerifyNumericEvidence(string field, string value, string evidence)
        {
            if (!NumericFields.Contains(field)) return;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                || double.IsNaN(number) || double.IsInfinity(number))
                throw new ArgumentException("明确数值无效：" + field);
            bool found = Regex.Matches(evidence ?? "", @"(?<![\d.])-?\d+(?:\.\d+)?(?![\d.])")
                .Cast<Match>().Any(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    && Math.Abs(n - number) < .0001);
            if (!found && (field.Equals("leftBend", StringComparison.OrdinalIgnoreCase)
                || field.Equals("rightBend", StringComparison.OrdinalIgnoreCase)) && number == 0)
                found = Regex.IsMatch(evidence ?? "", "伸直|straight|まっすぐ|伸ばし切", RegexOptions.IgnoreCase);
            if (!found && field.Equals("cycles", StringComparison.OrdinalIgnoreCase))
            {
                string words = number == 1 ? "一次|一回|once|one time" : number == 2 ? "两次|兩次|二次|二回|twice|two times"
                    : number == 3 ? "三次|三回|three times" : "(?!)";
                found = Regex.IsMatch(evidence ?? "", words, RegexOptions.IgnoreCase);
            }
            if (!found)
                throw new ArgumentException(field + "的数值没有出现在用户引句中；模型自行选择时应使用tempo/size/repeat或省略肘角。");
        }

        private static bool Equivalent(string field, string a, string b)
        {
            if (NumericFields.Contains(field) && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return Math.Abs(x - y) < .0001;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static Dictionary<string, Entry> Clone(Dictionary<string, Entry> source) =>
            source.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.OrdinalIgnoreCase);
    }
}
