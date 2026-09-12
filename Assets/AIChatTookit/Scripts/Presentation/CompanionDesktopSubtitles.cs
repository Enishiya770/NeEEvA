using System;
using System.Collections.Generic;

namespace NeEEvA.Presentation
{
    public interface IDesktopSubtitleWindow : IDisposable
    {
        bool IsUnityInBackground { get; }
        void Present(string original, string translation, int fontSize, float opacity, string speaker = "");
        void Hide();
    }

    /// <summary>Routes the already projected subtitles to a desktop window, on the Unity main thread.</summary>
    public sealed class CompanionDesktopSubtitles : IDisposable
    {
        private static readonly HashSet<CompanionDesktopSubtitles> instances = new HashSet<CompanionDesktopSubtitles>();
        private readonly IDesktopSubtitleWindow window;
        private readonly IDesktopSubtitleWindow recognitionWindow;
        private bool disposed;

        public bool IsDisposed => disposed;

        public CompanionDesktopSubtitles(IDesktopSubtitleWindow window, IDesktopSubtitleWindow recognitionWindow = null)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            this.recognitionWindow = recognitionWindow;
            instances.Add(this);
        }

        public void UpdateFrame(bool enabled, bool inVR, string original, string translation, int fontSize, float opacity,
            string speaker = "", string recognition = "", float recognitionOpacity = 0)
        {
            if (disposed) return;
            if (!enabled || inVR || !window.IsUnityInBackground)
            {
                window.Hide();
                recognitionWindow?.Hide();
                return;
            }
            if (!HasOpacity(opacity) || (string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(translation)))
                window.Hide();
            else
                window.Present(original ?? string.Empty, translation ?? string.Empty,
                    Math.Max(24, Math.Min(34, fontSize)), Math.Min(1f, opacity), speaker ?? string.Empty);
            // User speech has its own lifetime; it must remain visible while the character is silent.
            if (recognitionWindow != null)
            {
                if (!HasOpacity(recognitionOpacity) || string.IsNullOrWhiteSpace(recognition)) recognitionWindow.Hide();
                else recognitionWindow.Present(recognition, string.Empty, 17, Math.Min(1f, recognitionOpacity), "您");
            }
        }

        private static bool HasOpacity(float value) => !float.IsNaN(value) && value > .01f;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            instances.Remove(this);
            try { window.Dispose(); }
            finally { if (!ReferenceEquals(window, recognitionWindow)) recognitionWindow?.Dispose(); }
        }

        /// <summary>Editor reload/Play exit safety net, including disabled domain reload.</summary>
        public static void DisposeAll()
        {
            var snapshot = new List<CompanionDesktopSubtitles>(instances);
            List<Exception> errors = null;
            foreach (var instance in snapshot)
            {
                try { instance.Dispose(); }
                catch (Exception error) { (errors ?? (errors = new List<Exception>())).Add(error); }
            }
            if (errors != null) throw new AggregateException("Desktop subtitle window cleanup failed.", errors);
        }
    }
}
