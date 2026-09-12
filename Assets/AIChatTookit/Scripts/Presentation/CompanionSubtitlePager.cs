using System;
using UnityEngine;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    /// <summary>Shows the sentence being revealed without changing the speech/translation source.</summary>
    public static class CompanionSubtitlePager
    {
        public static string CurrentPage(string source, Text view, int lines = 2)
        {
            if (string.IsNullOrWhiteSpace(source)) return string.Empty;
            string value = source.Trim();
            int start = 0;
            // Keep the final punctuation on screen until the next sentence actually begins.
            for (int i = 0; i < value.Length - 1; i++)
            {
                char c = value[i];
                bool terminal = c == '。' || c == '！' || c == '？' || c == '!' || c == '?' ||
                    (c == '.' && char.IsWhiteSpace(value[i + 1]));
                if (!terminal) continue;
                int next = i + 1;
                while (next < value.Length && (char.IsWhiteSpace(value[next]) || "。！？!?\"'”’」』".IndexOf(value[next]) >= 0)) next++;
                if (next < value.Length) start = next;
            }
            value = value.Substring(start).TrimStart();
            if (view == null || view.font == null || view.rectTransform.rect.width < 1f) return value;
            var settings = view.GetGenerationSettings(new Vector2(view.rectTransform.rect.width, 10000f));
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            settings.horizontalOverflow = HorizontalWrapMode.Wrap;
            settings.richText = false;
            var generator = view.cachedTextGeneratorForLayout;
            generator.Populate(value, settings);
            int count = generator.lineCount;
            if (count <= lines) return value;
            int pageLine = ((count - 1) / Math.Max(1, lines)) * Math.Max(1, lines);
            int character = generator.lines[pageLine].startCharIdx;
            character = Mathf.Clamp(character, 0, value.Length);
            if (character > 0 && character < value.Length && char.IsLowSurrogate(value[character])) character--;
            return value.Substring(character).TrimStart();
        }
    }
}
