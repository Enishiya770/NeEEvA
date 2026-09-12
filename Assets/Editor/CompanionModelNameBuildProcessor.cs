using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

/// <summary>Keep Project model file names available in players without an AssetDatabase.</summary>
public sealed class CompanionModelNameBuildProcessor : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var chat in root.GetComponentsInChildren<ChatSample>(true))
                chat.BakePresentationModelFileName();
    }
}
