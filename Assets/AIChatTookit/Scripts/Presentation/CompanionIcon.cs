using UnityEngine;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    public enum CompanionIconKind
    {
        Microphone, MicrophoneOff, Keyboard, Send, Chat, History, Settings,
        Close, Back, ChevronDown, ChevronRight, Play, Stop, Volume, VolumeOff,
        Language, Subtitles, Recenter, Minimize, Check, Plus, Eye, EyeOff, User, More
    }

    /// <summary>
    /// A resolution-independent, rounded-stroke icon set for the companion UI.
    /// Stroke width is expressed in the icon's 24-unit design space. Graphic.color
    /// controls the tint. No textures, font glyphs, external assets or Update are required.
    /// </summary>
    [AddComponentMenu("NeEEvA/UI/Companion Icon")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class CompanionIcon : MaskableGraphic
    {
        [SerializeField] private CompanionIconKind m_Kind = CompanionIconKind.Microphone;
        [SerializeField, Range(0.75f, 3f)] private float m_StrokeWidth = 1.7f;

        private Vector2 m_Origin;
        private float m_Scale;

        public CompanionIcon()
        {
            useLegacyMeshGeneration = false;
        }

        public CompanionIconKind kind
        {
            get { return m_Kind; }
            set { if (m_Kind != value) { m_Kind = value; SetVerticesDirty(); } }
        }

        public float strokeWidth
        {
            get { return m_StrokeWidth; }
            set
            {
                float next = Mathf.Clamp(value, 0.75f, 3f);
                if (!Mathf.Approximately(m_StrokeWidth, next))
                {
                    m_StrokeWidth = next;
                    SetVerticesDirty();
                }
            }
        }

        protected override void Awake()
        {
            base.Awake();
            // Button hit areas belong to their parent surface, not the decorative icon.
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            m_Scale = Mathf.Min(rect.width, rect.height) / 24f;
            m_Origin = rect.center;
            if (m_Scale <= 0f) return;

            switch (m_Kind)
            {
                case CompanionIconKind.Microphone:
                case CompanionIconKind.MicrophoneOff:
                    RoundedBox(mesh, 9f, 2.5f, 6f, 11f, 3f);
                    Line(mesh, 6f, 10f, 6f, 11.5f);
                    Arc(mesh, 12f, 11.5f, 6f, 0f, 180f);
                    Line(mesh, 18f, 10f, 18f, 11.5f);
                    Line(mesh, 12f, 17.5f, 12f, 21f);
                    Line(mesh, 9f, 21f, 15f, 21f);
                    if (m_Kind == CompanionIconKind.MicrophoneOff) Line(mesh, 3f, 3f, 21f, 21f);
                    break;
                case CompanionIconKind.Keyboard:
                    RoundedBox(mesh, 2.5f, 5f, 19f, 14f, 2f);
                    for (int row = 0; row < 2; row++)
                        for (int col = 0; col < 4; col++)
                            Line(mesh, 6f + col * 4f, 9f + row * 3.5f,
                                6.4f + col * 4f, 9f + row * 3.5f);
                    Line(mesh, 8f, 16f, 16f, 16f);
                    break;
                case CompanionIconKind.Send:
                    Line(mesh, 3f, 10f, 21f, 3f);
                    Line(mesh, 21f, 3f, 14f, 21f);
                    Line(mesh, 14f, 21f, 10f, 14f);
                    Line(mesh, 10f, 14f, 3f, 10f);
                    Line(mesh, 10f, 14f, 21f, 3f);
                    break;
                case CompanionIconKind.Chat:
                    Line(mesh, 5f, 4f, 19f, 4f);
                    Arc(mesh, 19f, 6f, 2f, -90f, 0f);
                    Line(mesh, 21f, 6f, 21f, 15f);
                    Arc(mesh, 19f, 15f, 2f, 0f, 90f);
                    Line(mesh, 19f, 17f, 11f, 17f);
                    Line(mesh, 11f, 17f, 6f, 21f);
                    Line(mesh, 6f, 21f, 6f, 17f);
                    Line(mesh, 6f, 17f, 5f, 17f);
                    Arc(mesh, 5f, 15f, 2f, 90f, 180f);
                    Line(mesh, 3f, 15f, 3f, 6f);
                    Arc(mesh, 5f, 6f, 2f, 180f, 270f);
                    Dot(mesh, 8f, 10.5f, 0.9f);
                    Dot(mesh, 12f, 10.5f, 0.9f);
                    Dot(mesh, 16f, 10.5f, 0.9f);
                    break;
                case CompanionIconKind.History:
                    Arc(mesh, 12f, 12f, 8.5f, -135f, 165f);
                    Line(mesh, 3.5f, 3.5f, 3.5f, 8.5f);
                    Line(mesh, 3.5f, 8.5f, 8.5f, 8.5f);
                    Line(mesh, 12f, 7.5f, 12f, 12f);
                    Line(mesh, 12f, 12f, 15.5f, 14f);
                    break;
                case CompanionIconKind.Settings:
                    Line(mesh, 3f, 6f, 6f, 6f);
                    Line(mesh, 10f, 6f, 21f, 6f);
                    Ring(mesh, 8f, 6f, 2f);
                    Line(mesh, 3f, 12f, 14f, 12f);
                    Line(mesh, 18f, 12f, 21f, 12f);
                    Ring(mesh, 16f, 12f, 2f);
                    Line(mesh, 3f, 18f, 6f, 18f);
                    Line(mesh, 10f, 18f, 21f, 18f);
                    Ring(mesh, 8f, 18f, 2f);
                    break;
                case CompanionIconKind.Close:
                    Line(mesh, 6f, 6f, 18f, 18f);
                    Line(mesh, 18f, 6f, 6f, 18f);
                    break;
                case CompanionIconKind.Back:
                    Line(mesh, 10f, 5f, 3f, 12f);
                    Line(mesh, 3f, 12f, 10f, 19f);
                    Line(mesh, 3f, 12f, 21f, 12f);
                    break;
                case CompanionIconKind.ChevronDown:
                    Line(mesh, 6f, 9f, 12f, 15f);
                    Line(mesh, 12f, 15f, 18f, 9f);
                    break;
                case CompanionIconKind.ChevronRight:
                    Line(mesh, 9f, 6f, 15f, 12f);
                    Line(mesh, 15f, 12f, 9f, 18f);
                    break;
                case CompanionIconKind.Play:
                    Line(mesh, 7f, 4f, 20f, 12f);
                    Line(mesh, 20f, 12f, 7f, 20f);
                    Line(mesh, 7f, 20f, 7f, 4f);
                    break;
                case CompanionIconKind.Stop:
                    RoundedBox(mesh, 5f, 5f, 14f, 14f, 2f);
                    break;
                case CompanionIconKind.Volume:
                case CompanionIconKind.VolumeOff:
                    Line(mesh, 3f, 9f, 7f, 9f);
                    Line(mesh, 7f, 9f, 12f, 5f);
                    Line(mesh, 12f, 5f, 12f, 19f);
                    Line(mesh, 12f, 19f, 7f, 15f);
                    Line(mesh, 7f, 15f, 3f, 15f);
                    Line(mesh, 3f, 15f, 3f, 9f);
                    if (m_Kind == CompanionIconKind.Volume)
                    {
                        Arc(mesh, 11f, 12f, 6f, -48f, 48f);
                        Arc(mesh, 11f, 12f, 9.5f, -48f, 48f);
                    }
                    else
                    {
                        Line(mesh, 17f, 9.5f, 21f, 14.5f);
                        Line(mesh, 21f, 9.5f, 17f, 14.5f);
                    }
                    break;
                case CompanionIconKind.Language:
                    Ring(mesh, 12f, 12f, 9f);
                    Ellipse(mesh, 12f, 12f, 4f, 9f);
                    Line(mesh, 3f, 12f, 21f, 12f);
                    break;
                case CompanionIconKind.Subtitles:
                    RoundedBox(mesh, 2.5f, 5f, 19f, 14f, 2f);
                    Line(mesh, 6f, 10f, 10f, 10f);
                    Line(mesh, 13.5f, 10f, 18f, 10f);
                    Line(mesh, 6f, 14f, 13f, 14f);
                    Line(mesh, 16.5f, 14f, 18f, 14f);
                    break;
                case CompanionIconKind.Recenter:
                    Line(mesh, 3f, 8f, 3f, 3f);
                    Line(mesh, 3f, 3f, 8f, 3f);
                    Line(mesh, 16f, 3f, 21f, 3f);
                    Line(mesh, 21f, 3f, 21f, 8f);
                    Line(mesh, 21f, 16f, 21f, 21f);
                    Line(mesh, 21f, 21f, 16f, 21f);
                    Line(mesh, 8f, 21f, 3f, 21f);
                    Line(mesh, 3f, 21f, 3f, 16f);
                    Ring(mesh, 12f, 12f, 3f);
                    Dot(mesh, 12f, 12f, 0.65f);
                    break;
                case CompanionIconKind.Minimize:
                    Line(mesh, 5f, 12f, 19f, 12f);
                    break;
                case CompanionIconKind.Check:
                    Line(mesh, 4f, 12f, 9f, 17f);
                    Line(mesh, 9f, 17f, 20f, 6f);
                    break;
                case CompanionIconKind.Plus:
                    Line(mesh, 4f, 12f, 20f, 12f);
                    Line(mesh, 12f, 4f, 12f, 20f);
                    break;
                case CompanionIconKind.Eye:
                case CompanionIconKind.EyeOff:
                    Curve(mesh, new Vector2(3f, 12f), new Vector2(8f, 4f),
                        new Vector2(16f, 4f), new Vector2(21f, 12f));
                    Curve(mesh, new Vector2(21f, 12f), new Vector2(16f, 20f),
                        new Vector2(8f, 20f), new Vector2(3f, 12f));
                    Ring(mesh, 12f, 12f, 3f);
                    if (m_Kind == CompanionIconKind.EyeOff) Line(mesh, 3f, 3f, 21f, 21f);
                    break;
                case CompanionIconKind.User:
                    Ring(mesh, 12f, 7f, 3.5f);
                    Arc(mesh, 12f, 21f, 7f, 180f, 360f);
                    break;
                case CompanionIconKind.More:
                    Dot(mesh, 5f, 12f, 1.4f);
                    Dot(mesh, 12f, 12f, 1.4f);
                    Dot(mesh, 19f, 12f, 1.4f);
                    break;
            }
        }

        private Vector3 Position(float x, float y)
        {
            return new Vector3(m_Origin.x + (x - 12f) * m_Scale,
                m_Origin.y + (12f - y) * m_Scale, 0f);
        }

        private void Vertex(VertexHelper mesh, float x, float y)
        {
            mesh.AddVert(Position(x, y), color, Vector2.zero);
        }

        // A single capsule has no overlapping triangles; both ends are true round caps.
        private void Line(VertexHelper mesh, float x1, float y1, float x2, float y2)
        {
            const int capSegments = 6;
            float angle = Mathf.Atan2(y2 - y1, x2 - x1);
            float radius = Mathf.Clamp(m_StrokeWidth, 0.75f, 3f) * 0.5f;
            int start = mesh.currentVertCount;
            Vertex(mesh, (x1 + x2) * 0.5f, (y1 + y2) * 0.5f);
            for (int cap = 0; cap < 2; cap++)
            {
                float cx = cap == 0 ? x2 : x1;
                float cy = cap == 0 ? y2 : y1;
                float begin = angle - Mathf.PI * 0.5f + cap * Mathf.PI;
                for (int step = 0; step <= capSegments; step++)
                {
                    float a = begin + step * Mathf.PI / capSegments;
                    Vertex(mesh, cx + Mathf.Cos(a) * radius, cy + Mathf.Sin(a) * radius);
                }
            }
            int boundaryCount = 2 * (capSegments + 1);
            for (int i = 0; i < boundaryCount; i++)
                mesh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % boundaryCount);
        }

        private void Dot(VertexHelper mesh, float cx, float cy, float radius)
        {
            const int segments = 16;
            int start = mesh.currentVertCount;
            Vertex(mesh, cx, cy);
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                Vertex(mesh, cx + Mathf.Cos(a) * radius, cy + Mathf.Sin(a) * radius);
            }
            for (int i = 0; i < segments; i++)
                mesh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % segments);
        }

        private void Ring(VertexHelper mesh, float cx, float cy, float radius)
        {
            Arc(mesh, cx, cy, radius, 0f, 360f, false);
        }

        private void Arc(VertexHelper mesh, float cx, float cy, float radius,
            float from, float to, bool roundCaps = true)
        {
            int segments = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(to - from) / 10f));
            float half = Mathf.Clamp(m_StrokeWidth, 0.75f, 3f) * 0.5f;
            int start = mesh.currentVertCount;
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Lerp(from, to, i / (float)segments) * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle);
                float y = Mathf.Sin(angle);
                Vertex(mesh, cx + x * (radius + half), cy + y * (radius + half));
                Vertex(mesh, cx + x * Mathf.Max(0f, radius - half),
                    cy + y * Mathf.Max(0f, radius - half));
                if (i == 0) continue;
                int a = start + (i - 1) * 2;
                mesh.AddTriangle(a, a + 2, a + 1);
                mesh.AddTriangle(a + 1, a + 2, a + 3);
            }
            if (!roundCaps) return;
            ArcCap(mesh, cx, cy, radius, from, half, -1f);
            ArcCap(mesh, cx, cy, radius, to, half, 1f);
        }

        private void ArcCap(VertexHelper mesh, float cx, float cy, float radius,
            float angle, float half, float direction)
        {
            const int segments = 6;
            float a = angle * Mathf.Deg2Rad;
            float x = cx + Mathf.Cos(a) * radius;
            float y = cy + Mathf.Sin(a) * radius;
            int start = mesh.currentVertCount;
            Vertex(mesh, x, y);
            for (int i = 0; i <= segments; i++)
            {
                float capAngle = a + direction * i * Mathf.PI / segments;
                Vertex(mesh, x + Mathf.Cos(capAngle) * half, y + Mathf.Sin(capAngle) * half);
                if (i > 0) mesh.AddTriangle(start, start + i, start + i + 1);
            }
        }

        private void Ellipse(VertexHelper mesh, float cx, float cy, float rx, float ry)
        {
            const int segments = 48;
            float half = Mathf.Clamp(m_StrokeWidth, 0.75f, 3f) * 0.5f;
            int start = mesh.currentVertCount;
            for (int i = 0; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                float x = Mathf.Cos(angle);
                float y = Mathf.Sin(angle);
                Vector2 normal = new Vector2(x / rx, y / ry).normalized * half;
                Vertex(mesh, cx + rx * x + normal.x, cy + ry * y + normal.y);
                Vertex(mesh, cx + rx * x - normal.x, cy + ry * y - normal.y);
                if (i == 0) continue;
                int a = start + (i - 1) * 2;
                mesh.AddTriangle(a, a + 2, a + 1);
                mesh.AddTriangle(a + 1, a + 2, a + 3);
            }
        }

        private void RoundedBox(VertexHelper mesh, float x, float y,
            float width, float height, float radius)
        {
            Line(mesh, x + radius, y, x + width - radius, y);
            Arc(mesh, x + width - radius, y + radius, radius, -90f, 0f, false);
            Line(mesh, x + width, y + radius, x + width, y + height - radius);
            Arc(mesh, x + width - radius, y + height - radius, radius, 0f, 90f, false);
            Line(mesh, x + width - radius, y + height, x + radius, y + height);
            Arc(mesh, x + radius, y + height - radius, radius, 90f, 180f, false);
            Line(mesh, x, y + height - radius, x, y + radius);
            Arc(mesh, x + radius, y + radius, radius, 180f, 270f, false);
        }

        private void Curve(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            const int segments = 16;
            Vector2 previous = a;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                float u = 1f - t;
                Vector2 next = u * u * u * a + 3f * u * u * t * b +
                    3f * u * t * t * c + t * t * t * d;
                Line(mesh, previous.x, previous.y, next.x, next.y);
                previous = next;
            }
        }
    }
}
