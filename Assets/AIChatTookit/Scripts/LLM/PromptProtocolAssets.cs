using System;
using UnityEngine;

/// <summary>Explicit compatibility contracts for the general tool route.
/// Resources keeps them available in Player without changing existing scenes.</summary>
public static class PromptProtocolAssets
{
    public const string GeneralPath = "PromptProtocols/general_tools";
    public const string SingingPath = "PromptProtocols/singing_tools";

    public static string GeneralTools() => Required(GeneralPath);
    public static string SingingTools() => Required(SingingPath);

    private static string Required(string path)
    {
        var asset = Resources.Load<TextAsset>(path);
        if (asset == null || string.IsNullOrWhiteSpace(asset.text))
            throw new InvalidOperationException("[LLM/ProtocolAsset] Required general-tool contract is missing or empty: " +
                path + ". Request construction stopped; no fallback to an old prompt or incomplete tool manual.");
        return asset.text.Trim();
    }
}
