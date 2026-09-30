using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    /// <summary>Original PCM min/max envelope, sample-bound proposals and pointer selection. Never edits audio.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PersonalSingingLearningWaveform : MaskableGraphic, IPointerDownHandler,
        IPointerUpHandler, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private const int Block = 128;
        private float[] samples = Array.Empty<float>(), minima = Array.Empty<float>(), maxima = Array.Empty<float>();
        private JArray regions = new JArray();
        private Text[] ticks;
        private int rate = 16000, viewStart, viewEnd, start, end, anchor, pointerId, handle, originalStart, originalEnd;
        private bool pressed, dragged, regionSelection;
        private float peak = 1;
        private int playhead = -1;
        public int SelectionStart => start;
        public int SelectionEnd => end;
        public bool HasSelection => end > start;
        public int SampleCount => samples.Length;
        public event Action SelectionChanged;
        public event Action<int, int, int, int> BoundaryChanged;

        public void Configure(Font font)
        {
            raycastTarget = true;
            ticks = new Text[5];
            for (int i = 0; i < ticks.Length; i++)
            {
                var obj = new GameObject("Waveform time " + i, typeof(RectTransform)); obj.transform.SetParent(transform, false);
                var text = obj.AddComponent<Text>(); text.font = font; text.fontSize = 12; text.color = new Color(.7f,.78f,.78f);
                text.raycastTarget = false; text.alignment = i == 0 ? TextAnchor.MiddleLeft : i == 4 ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
                var rect = (RectTransform)obj.transform; rect.anchorMin = rect.anchorMax = new Vector2(i / 4f, 0);
                rect.pivot = new Vector2(i / 4f, 0); rect.anchoredPosition = new Vector2(i == 0 ? 8 : i == 4 ? -8 : 0, 1); rect.sizeDelta = new Vector2(90, 24);
                ticks[i] = text;
            }
            UpdateTicks();
        }

        public void SetAudio(float[] pcm, int sampleRate)
        {
            pcm = pcm ?? Array.Empty<float>();
            if (ReferenceEquals(samples, pcm) && rate == sampleRate) return;
            samples = pcm; rate = Math.Max(1, sampleRate); start = end = 0; pressed = false; regionSelection = false;
            minima = new float[(samples.Length + Block - 1) / Block]; maxima = new float[minima.Length]; peak = .02f;
            for (int b = 0; b < minima.Length; b++)
            {
                float lo = 0, hi = 0;
                for (int i = b * Block; i < Math.Min(samples.Length, (b + 1) * Block); i++)
                { float value = samples[i]; if (float.IsNaN(value) || float.IsInfinity(value)) continue; lo = Mathf.Min(lo, value); hi = Mathf.Max(hi, value); }
                minima[b] = lo; maxima[b] = hi; peak = Mathf.Max(peak, Mathf.Max(-lo, hi));
            }
            ShowAll();
        }
        public void SetRegions(JArray value)
        {
            regions = value ?? new JArray();
            RebindSelection(); SetVerticesDirty();
        }
        private void RebindSelection()
        {
            if (pressed || !regionSelection || !HasSelection) return;
            // Adjacent equal labels merge after a correction. Keep the handles
            // bound to the resulting interval instead of a now-missing old pair.
            foreach (JObject region in regions)
                if ((int)region["start_sample"] <= start && (int)region["end_sample"] >= end)
                { SetSelection((int)region["start_sample"], (int)region["end_sample"], true); return; }
            regionSelection = false;
        }
        public void SetPlayhead(int sample) { if (sample == playhead) return; playhead = sample; SetVerticesDirty(); }
        public void SelectAll() { SetSelection(0, samples.Length, false); }
        public void SelectSpan(int a, int b) { SetSelection(a, b, true); }
        public void ShowAll() { viewStart = 0; viewEnd = samples.Length; UpdateTicks(); SetVerticesDirty(); }
        public void ZoomSelection()
        {
            if (!HasSelection) return;
            int pad = Math.Max(rate / 10, (end - start) / 12); viewStart = Math.Max(0, start - pad); viewEnd = Math.Min(samples.Length, end + pad);
            UpdateTicks(); SetVerticesDirty();
        }
        private void SetSelection(int a, int b, bool existing)
        {
            start = Mathf.Clamp(Math.Min(a, b), 0, samples.Length); end = Mathf.Clamp(Math.Max(a, b), start, samples.Length);
            regionSelection = existing; SetVerticesDirty(); SelectionChanged?.Invoke();
        }
        private void UpdateTicks()
        {
            if (ticks == null) return;
            for (int i = 0; i < ticks.Length; i++) ticks[i].text = ((viewStart + (viewEnd - viewStart) * i / 4.0) / rate).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";
        }
        private float SampleX(int sample, Rect rect) => rect.xMin + 8 + (rect.width - 16) * (sample - viewStart) / Math.Max(1f, viewEnd - viewStart);
        private bool Pointer(PointerEventData data, out int sample, out float x)
        {
            sample = 0; x = 0;
            if (samples.Length == 0 || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, data.position, data.pressEventCamera, out Vector2 point)) return false;
            x = point.x; Rect rect = GetPixelAdjustedRect();
            sample = Mathf.Clamp(Mathf.RoundToInt(viewStart + Mathf.Clamp01((x - rect.xMin - 8) / Math.Max(1, rect.width - 16)) * (viewEnd - viewStart)), viewStart, viewEnd);
            return true;
        }
        public void OnInitializePotentialDrag(PointerEventData data) { data.useDragThreshold = true; }
        public void OnPointerDown(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left || !Pointer(data, out anchor, out float x)) return;
            pressed = true; dragged = false; pointerId = data.pointerId; handle = 0;
            var rect = GetPixelAdjustedRect();
            if (HasSelection && Mathf.Abs(x - SampleX(start, rect)) < 12) handle = -1;
            else if (HasSelection && Mathf.Abs(x - SampleX(end, rect)) < 12) handle = 1;
            if (handle == 0)
            {
                bool found = false;
                foreach (JObject region in regions)
                    if (anchor >= (int)region["start_sample"] && anchor < (int)region["end_sample"])
                    { SetSelection((int)region["start_sample"], (int)region["end_sample"], true); found = true; break; }
                if (!found) SetSelection(anchor, anchor, false);
            }
            originalStart = start; originalEnd = end;
        }
        public void OnBeginDrag(PointerEventData data) { if (pressed && data.pointerId == pointerId) dragged = true; }
        public void OnDrag(PointerEventData data)
        {
            if (!pressed || data.pointerId != pointerId || !Pointer(data, out int sample, out _)) return;
            dragged = true;
            if (handle < 0) SetSelection(Math.Min(sample, end - 1), end, regionSelection);
            else if (handle > 0) SetSelection(start, Math.Max(sample, start + 1), regionSelection);
            else SetSelection(anchor, sample, false);
        }
        public void OnEndDrag(PointerEventData data) { Finish(data); }
        public void OnPointerUp(PointerEventData data) { Finish(data); }
        private void Finish(PointerEventData data)
        {
            if (!pressed || data.pointerId != pointerId) return;
            pressed = false;
            if (dragged && handle != 0 && regionSelection && (start != originalStart || end != originalEnd))
                BoundaryChanged?.Invoke(originalStart, originalEnd, start, end);
            RebindSelection();
            SelectionChanged?.Invoke();
        }
        protected override void OnDisable() { pressed = false; base.OnDisable(); }

        public static Color LabelColor(string label) => label == "singing" ? new Color(.93f,.66f,.30f) : label == "speech" ? new Color(.35f,.72f,.87f) : label == "environment" ? new Color(.52f,.65f,.61f) : new Color(.66f,.59f,.77f);
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear(); Rect rect = GetPixelAdjustedRect();
            Quad(mesh, rect.xMin, rect.yMin, rect.xMax, rect.yMax, new Color(.045f,.075f,.08f,.98f));
            if (samples.Length == 0 || viewEnd <= viewStart) return;
            float bottom = rect.yMin + 29, top = rect.yMax - 10, mid = (top + bottom) / 2, amplitude = (top - bottom) * .43f;
            foreach (JObject region in regions)
            {
                float a = Mathf.Clamp(SampleX((int)region["start_sample"], rect), rect.xMin + 8, rect.xMax - 8);
                float b = Mathf.Clamp(SampleX((int)region["end_sample"], rect), rect.xMin + 8, rect.xMax - 8); if (a >= b) continue;
                var c = LabelColor((string)region["label"]); c.a = .12f; Quad(mesh, a, bottom, b, top, c); c.a = .8f; Quad(mesh, a, bottom, b, bottom + 4, c);
            }
            Quad(mesh, rect.xMin + 8, mid, rect.xMax - 8, mid + 1, new Color(.3f,.45f,.46f,.4f));
            int columns = Mathf.Clamp(Mathf.CeilToInt(rect.width - 16), 16, 1800);
            for (int col = 0; col < columns; col++)
            {
                int a = viewStart + (int)((long)(viewEnd - viewStart) * col / columns), b = viewStart + (int)((long)(viewEnd - viewStart) * (col + 1) / columns);
                Envelope(a, Math.Max(a + 1, b), out float lo, out float hi);
                float x = rect.xMin + 8 + (rect.width - 16) * col / columns;
                Quad(mesh, x, mid + lo / peak * amplitude, x + Mathf.Max(1, (rect.width - 16) / columns), mid + Mathf.Max(.002f, hi / peak) * amplitude, new Color(.64f,.84f,.81f,.95f));
            }
            if (HasSelection)
            {
                float a = Mathf.Clamp(SampleX(start, rect), rect.xMin + 8, rect.xMax - 8), b = Mathf.Clamp(SampleX(end, rect), rect.xMin + 8, rect.xMax - 8);
                Quad(mesh, a, bottom, b, top, new Color(.67f,.91f,.84f,.14f));
                foreach (float x in new[] { a, b }) { Quad(mesh, x - 1, bottom, x + 1, top, new Color(.84f,.96f,.90f)); Quad(mesh, x - 4, mid - 13, x + 4, mid + 13, new Color(.84f,.96f,.90f)); }
            }
            if (playhead >= viewStart && playhead < viewEnd) { float x = SampleX(playhead, rect); Quad(mesh, x, bottom, x + 2, top, Color.white); }
        }
        private void Envelope(int a, int b, out float lo, out float hi)
        {
            lo = hi = 0; b = Math.Min(b, samples.Length);
            while (a < b && a % Block != 0) { lo = Mathf.Min(lo, samples[a]); hi = Mathf.Max(hi, samples[a++]); }
            while (a + Block <= b) { lo = Mathf.Min(lo, minima[a / Block]); hi = Mathf.Max(hi, maxima[a / Block]); a += Block; }
            while (a < b) { lo = Mathf.Min(lo, samples[a]); hi = Mathf.Max(hi, samples[a++]); }
        }
        private static void Quad(VertexHelper mesh, float x0, float y0, float x1, float y1, Color c)
        {
            if (x1 <= x0 || y1 <= y0) return;
            int i = mesh.currentVertCount;
            mesh.AddVert(new Vector3(x0,y0),c,Vector2.zero); mesh.AddVert(new Vector3(x0,y1),c,Vector2.zero);
            mesh.AddVert(new Vector3(x1,y1),c,Vector2.zero); mesh.AddVert(new Vector3(x1,y0),c,Vector2.zero);
            mesh.AddTriangle(i,i+1,i+2); mesh.AddTriangle(i,i+2,i+3);
        }
    }
}
