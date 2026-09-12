using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>
    /// Authored mesh references captured before Unity replaces MeshFilter meshes during static batching.
    /// The meshes remain original asset references; no read/write flags or batching settings are changed.
    /// Prepare this component in edit mode and serialize it with the selected environment root.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArdyRoomGeometryReference : MonoBehaviour
    {
        [Serializable]
        public sealed class Entry
        {
            public MeshRenderer renderer;
            public Mesh authoredMesh;
        }

        public Transform environmentRoot;
        public Entry[] entries = Array.Empty<Entry>();
        private Dictionary<MeshRenderer, Mesh> lookup;
        private HashSet<MeshRenderer> ambiguous;

        public void InvalidateLookup() { lookup = null; ambiguous = null; }

        public bool TryGetAuthoredMesh(MeshRenderer renderer, out Mesh mesh)
        {
            mesh = null;
            if (renderer == null || environmentRoot == null || !renderer.transform.IsChildOf(environmentRoot)) return false;
            if (lookup == null)
            {
                lookup = new Dictionary<MeshRenderer, Mesh>();
                ambiguous = new HashSet<MeshRenderer>();
                if (entries != null)
                    foreach (var entry in entries)
                    {
                        if (entry == null || entry.renderer == null || entry.authoredMesh == null) continue;
                        if (lookup.TryGetValue(entry.renderer, out var previous) && previous != entry.authoredMesh)
                            ambiguous.Add(entry.renderer);
                        lookup[entry.renderer] = entry.authoredMesh;
                    }
            }
            return !ambiguous.Contains(renderer) && lookup.TryGetValue(renderer, out mesh) && mesh != null;
        }

        private void OnValidate() { InvalidateLookup(); }
    }
}
