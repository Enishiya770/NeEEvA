using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Exercises the production Chat catalog hook with a real authored lamp and a temporarily inactive room.</summary>
public static class SceneObjectCatalogLifecycleRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }

    // Pass the unbound standlight (4) from the authored SailingMoon room before
    // another fixture installs its runtime capability. No full room/VRM is needed.
    public static JObject RunChecks(Transform authoredLamp, string output)
    {
        if (!Application.isPlaying || !Application.isBatchMode || authoredLamp == null ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use an actual authored lamp in the isolated batch Play fixture.");
        if (authoredLamp.GetComponent<SceneObjectLightSwitch>()?.IsBound == true)
            throw new InvalidOperationException("Pass the authored lamp before binding a runtime material instance.");

        int checks = 0;
        var cases = new JArray();
        LastReport = new JObject {
            ["status"] = "running", ["cases"] = cases,
            ["scope"] = "Production Chat catalog hook with a temporarily inactive room, followed by real authored lamp registration and emission/Light mutation. Exercises the shared readiness gate used during scene loading; does not emulate Unity's scene-loaded event, render images, call models or claim physical switch interaction.",
            ["modelHttpRequests"] = 0
        };
        void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
            cases.Add(reason);
        }
        var objects = new List<GameObject>();
        var sourceGlow = authoredLamp.Find("スタンドライト.001").GetComponent<Renderer>();
        var sourceLight = authoredLamp.Find("Point Light").GetComponent<Light>();
        Material sourceMaterial = sourceGlow.sharedMaterial;
        Color sourceEmission = sourceMaterial.GetColor("_EmissionColor");
        bool sourceEnabled = sourceLight.enabled;
        Transform MakeEnvironment(string label, out Transform lamp)
        {
            var environment = new GameObject(label); objects.Add(environment); environment.SetActive(false);
            var furniture = new GameObject("1階家具"); furniture.transform.SetParent(environment.transform, false);
            lamp = Object.Instantiate(authoredLamp.gameObject, furniture.transform, false).transform;
            lamp.name = "standlight (4)";
            return environment.transform;
        }
        try
        {
            // Keep the host inactive: invoke the actual catalog hook without Chat
            // Start, microphone, HTTP, unrelated UI or body controllers.
            var host = new GameObject("Lamp catalog lifecycle chat"); objects.Add(host); host.SetActive(false);
            var chat = host.AddComponent<ChatSample>();
            var bridge = host.AddComponent<ArdyRoomDialogueBridge>(); bridge.enabled = false;
            Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneActions", true);
            Transform environment = MakeEnvironment("Lamp lifecycle temporarily unavailable room", out Transform lamp);
            bridge.environmentRoot = environment;
            Check(!SceneObjectLampCatalog.IsEnvironmentReady(environment, out _), "Inactive room is a temporary readiness failure.");
            for (int i = 0; i < 16; i++) Ensure(chat);
            Check(!(bool)Get(chat, "m_SceneObjectCatalogAttempted") && lamp.GetComponent<SceneObjectLightSwitch>() == null,
                "Repeated pre-ready capture hooks do not consume the profile attempt or install a capability.");
            environment.gameObject.SetActive(true);
            Ensure(chat);
            var device = lamp.GetComponent<SceneObjectLightSwitch>();
            Check((bool)Get(chat, "m_SceneObjectCatalogAttempted") && device != null && device.TryValidate(out _),
                "The next capture hook after room readiness registers the same previously unavailable environment.");
            Check(device.Identity.AuthoredKey == SceneObjectLampCatalog.AuthoredKey && device.Identity.Root == lamp,
                "Registration provides the audited authored identity rather than a renderer-session placeholder.");
            Ensure(chat);
            Check(lamp.GetComponents<SceneObjectLightSwitch>().Length == 1 && lamp.GetComponent<SceneObjectLightSwitch>() == device,
                "Subsequent capture hooks preserve the single installed capability.");

            var cloneGlow = lamp.Find("スタンドライト.001").GetComponent<Renderer>();
            var cloneLight = lamp.Find("Point Light").GetComponent<Light>();
            Check(device.TrySetPower("lifecycle-off", false, out var off, out string reason) && off.completed &&
                !off.actual.emissionEnabled && !off.actual.lightEnabled && !cloneLight.enabled &&
                cloneGlow.sharedMaterial.GetColor("_EmissionColor") == Color.black,
                "Recovered registration actually switches the cloned lamp off: " + reason);
            Check(device.TrySetPower("lifecycle-on", true, out var on, out reason) && on.completed &&
                on.actual.emissionEnabled && on.actual.lightEnabled && cloneLight.enabled &&
                cloneGlow.sharedMaterial.GetColor("_EmissionColor") == sourceEmission,
                "Recovered registration actually restores the cloned lamp's emissive and Light state: " + reason);
            Check(sourceGlow.sharedMaterial == sourceMaterial && sourceMaterial.GetColor("_EmissionColor") == sourceEmission &&
                sourceLight.enabled == sourceEnabled,
                "Recovery and real switching leave the authored source lamp and shared material unchanged.");
            device.enabled = false;
            Ensure(chat);
            Check(!device.enabled && !device.IsBound && cloneGlow.sharedMaterial == sourceMaterial,
                "Later capture hooks do not reenable a deliberately disabled capability.");

            Transform rejectedEnvironment = MakeEnvironment("Lamp lifecycle permanent profile rejection", out Transform rejectedLamp);
            rejectedLamp.name = "standlight (40)";
            rejectedEnvironment.gameObject.SetActive(true); bridge.environmentRoot = rejectedEnvironment;
            Ensure(chat);
            Check((bool)Get(chat, "m_SceneObjectCatalogAttempted") && rejectedLamp.GetComponent<SceneObjectLightSwitch>() == null,
                "A ready but unaudited profile consumes its one structure scan and receives no capability.");
            rejectedLamp.name = "standlight (4)";
            for (int i = 0; i < 16; i++) Ensure(chat);
            Check(rejectedLamp.GetComponent<SceneObjectLightSwitch>() == null,
                "Permanent profile rejection is not retried on every later capture or silently widened.");

            Transform replacement = MakeEnvironment("Lamp lifecycle new explicit room", out Transform replacementLamp);
            replacement.gameObject.SetActive(true); bridge.environmentRoot = replacement;
            Ensure(chat);
            Check(replacementLamp.GetComponent<SceneObjectLightSwitch>()?.TryValidate(out _) == true,
                "An explicitly changed ready environment receives its own audited registration attempt.");
            LastReport["status"] = "passed";
        }
        catch (Exception error)
        {
            LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw;
        }
        finally
        {
            for (int i = objects.Count - 1; i >= 0; i--) if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            LastReport["checks"] = checks;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "lamp-catalog-lifecycle.json"), LastReport.ToString() + "\n");
        }
        return LastReport;
    }

    private static void Ensure(ChatSample chat) => typeof(ChatSample).GetMethod("EnsureSceneObjectCatalog", Flags).Invoke(chat, null);
    private static object Get(ChatSample chat, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(chat);
    private static void Set(ChatSample chat, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(chat, value);
}
