using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Capture authored mesh references before Play-mode static batching replaces MeshFilter meshes.</summary>
public static class ArdyRoomGeometryPreparation
{
    public static ArdyRoomGeometryReference Prepare(Transform environmentRoot, bool recordUndo = true)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请在进入 Play 模式前准备房间几何，以保留合批前的原始网格引用。");
        if (environmentRoot == null || EditorUtility.IsPersistent(environmentRoot) || !environmentRoot.gameObject.scene.IsValid())
            throw new ArgumentException("请选择场景中的房间环境根节点。");
        var entries = new List<ArdyRoomGeometryReference.Entry>();
        foreach (var renderer in environmentRoot.GetComponentsInChildren<MeshRenderer>(true))
        {
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;
            if (renderer.isPartOfStaticBatch)
                throw new InvalidOperationException("当前场景的 " + renderer.name + " 已合批，无法将合并网格作为原始房间几何；请退出 Play 后再准备。");
            entries.Add(new ArdyRoomGeometryReference.Entry { renderer = renderer, authoredMesh = filter.sharedMesh });
        }
        if (entries.Count == 0) throw new InvalidOperationException("所选环境下没有可以记录的 MeshRenderer / MeshFilter。");
        // Validate first, then write one reversible scene component. Imported meshes and batching flags stay untouched.
        var reference = environmentRoot.GetComponent<ArdyRoomGeometryReference>();
        if (reference == null)
            reference = recordUndo ? Undo.AddComponent<ArdyRoomGeometryReference>(environmentRoot.gameObject)
                : environmentRoot.gameObject.AddComponent<ArdyRoomGeometryReference>();
        else if (recordUndo) Undo.RecordObject(reference, "Prepare ARDY room geometry");
        reference.environmentRoot = environmentRoot;
        reference.entries = entries.ToArray();
        reference.InvalidateLookup();
        EditorUtility.SetDirty(reference);
        PrefabUtility.RecordPrefabInstancePropertyModifications(reference);
        EditorSceneManager.MarkSceneDirty(environmentRoot.gameObject.scene);
        return reference;
    }
}
