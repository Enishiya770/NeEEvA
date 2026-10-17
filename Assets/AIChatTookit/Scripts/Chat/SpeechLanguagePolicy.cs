using System.Linq;

// Script disagreement only triggers a words-only check. It is NOT a language
// verdict: shared Han nouns, names and mixed clauses are deliberately unresolved.
public static class SpeechLanguagePolicy
{
    public static bool NeedsCheck(string text,string declared)
    {
        int han=(text??"").Count(c=>c>='\u4e00'&&c<='\u9fff');
        int kana=(text??"").Count(c=>c>='\u3040'&&c<='\u30ff'||c>='\uff66'&&c<='\uff9d');
        int latin=(text??"").Count(c=>c>='a'&&c<='z'||c>='A'&&c<='Z');
        return declared=="ja"&&kana==0&&(han>=6||latin>=12) || declared=="zh"&&(kana>0||han==0&&latin>=12) ||
            declared=="en"&&(han>=6||kana>0);
    }
}
