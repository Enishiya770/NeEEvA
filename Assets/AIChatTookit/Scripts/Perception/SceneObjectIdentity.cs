using System;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>Explicit scene authoring, not a name inferred from pixels or an exported scene inventory.
    /// Rendered children belong to their nearest active identity component.</summary>
    [DisallowMultipleComponent]
    public sealed class SceneObjectIdentity : MonoBehaviour
    {
        [SerializeField, Tooltip("Explicit scene-authored label; not a claim of visual recognition.")]
        private string sceneLabel = "";
        [SerializeField] private string authoredKey = "";
        [NonSerialized] private string runtimeId;
        public string EntityId => "scene-object-" + (runtimeId ?? (runtimeId = Guid.NewGuid().ToString("N")));
        public string SceneLabel => sceneLabel ?? "";
        public string AuthoredKey => authoredKey ?? "";
        public string IdentitySource => "scene-authored";
        public Transform Root => transform;
        internal static readonly string RendererSession = Guid.NewGuid().ToString("N");

        public void Configure(string label, string authoredKey = null)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > 80 || (authoredKey?.Length ?? 0) > 128)
                throw new ArgumentException("An explicit object identity requires a bounded scene-authored label/key.");
            sceneLabel = label.Trim(); this.authoredKey = authoredKey ?? "";
            _ = EntityId;
        }
        internal static SceneObjectIdentity For(Renderer renderer)
        {
            if (renderer == null) return null;
            var value = renderer.GetComponentInParent<SceneObjectIdentity>();
            return value != null && value.isActiveAndEnabled ? value : null;
        }
        internal static string RendererId(Renderer renderer) => renderer == null ? "" :
            "renderer-object-" + RendererSession + "-" + renderer.GetInstanceID().ToString("x");
    }
}
