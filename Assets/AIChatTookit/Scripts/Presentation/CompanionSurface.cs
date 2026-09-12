using UnityEngine;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    /// <summary>A texture-free rounded panel. Radius and edge softness use UI units.</summary>
    [AddComponentMenu("NeEEvA/UI/Companion Surface")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class CompanionSurface : MaskableGraphic
    {
        [SerializeField, Min(0f)] private float m_Radius = 16f;
        [SerializeField] private bool m_AntiAliasing = true;
        [SerializeField, Range(0f, 2f)] private float m_EdgeSoftness = 0.75f;

        public CompanionSurface()
        {
            useLegacyMeshGeneration = false;
        }

        public float radius
        {
            get { return m_Radius; }
            set
            {
                float next = Mathf.Max(0f, value);
                if (!Mathf.Approximately(m_Radius, next)) { m_Radius = next; SetVerticesDirty(); }
            }
        }

        public bool antiAliasing
        {
            get { return m_AntiAliasing; }
            set { if (m_AntiAliasing != value) { m_AntiAliasing = value; SetVerticesDirty(); } }
        }

        public float edgeSoftness
        {
            get { return m_EdgeSoftness; }
            set
            {
                float next = Mathf.Clamp(value, 0f, 2f);
                if (!Mathf.Approximately(m_EdgeSoftness, next))
                {
                    m_EdgeSoftness = next;
                    SetVerticesDirty();
                }
            }
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f) return;

            float limit = Mathf.Min(rect.width, rect.height) * 0.5f;
            float outerRadius = Mathf.Clamp(m_Radius, 0f, limit);
            float feather = m_AntiAliasing ? Mathf.Clamp(m_EdgeSoftness, 0f, limit) : 0f;
            float innerRadius = Mathf.Max(0f, outerRadius - feather);
            Rect inner = new Rect(rect.xMin + feather, rect.yMin + feather,
                Mathf.Max(0f, rect.width - feather * 2f), Mathf.Max(0f, rect.height - feather * 2f));
            int segments = Mathf.Clamp(Mathf.CeilToInt(outerRadius * 0.5f), 4, 16);
            int boundaryCount = (segments + 1) * 4;
            Color32 transparent = color;
            transparent.a = 0;

            mesh.AddVert(rect.center, color, Vector2.zero);
            for (int i = 0; i < boundaryCount; i++)
            {
                int corner = i / (segments + 1);
                float angle = (corner * 90f + (i % (segments + 1)) * 90f / segments) * Mathf.Deg2Rad;
                mesh.AddVert(CornerPoint(inner, innerRadius, corner, angle), color, Vector2.zero);
                if (feather > 0f)
                    mesh.AddVert(CornerPoint(rect, outerRadius, corner, angle), transparent, Vector2.zero);
            }

            int stride = feather > 0f ? 2 : 1;
            for (int i = 0; i < boundaryCount; i++)
            {
                int a = 1 + i * stride;
                int b = 1 + ((i + 1) % boundaryCount) * stride;
                mesh.AddTriangle(0, a, b);
                if (feather <= 0f) continue;
                mesh.AddTriangle(a, a + 1, b);
                mesh.AddTriangle(b, a + 1, b + 1);
            }
        }

        private static Vector3 CornerPoint(Rect rect, float radius, int corner, float angle)
        {
            float cx = corner == 0 || corner == 3 ? rect.xMax - radius : rect.xMin + radius;
            float cy = corner < 2 ? rect.yMax - radius : rect.yMin + radius;
            return new Vector3(cx + Mathf.Cos(angle) * radius, cy + Mathf.Sin(angle) * radius, 0f);
        }
    }
}
