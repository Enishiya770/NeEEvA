using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace NeEEvA.Perception
{
    /// <summary>Explicit runtime device capability. It changes only an owned emission material and Light.enabled.</summary>
    [DisallowMultipleComponent]
    public sealed class SceneObjectLightSwitch : MonoBehaviour
    {
        [Serializable] public sealed class State
        {
            public string entityId, sceneLabel, lightState, emissionState, conflict;
            public bool registered, on, lightEnabled, emissionEnabled, bakedLightingResidualUncontrolled;
            public Color emissionColor;
            public float lightIntensity;
            public string lightBakeType;
            public string scope = "Runtime device state for the bound visible emissive element and Light component only. Baked illumination may remain and is not controlled. This is not a physical hand press, scene-wide darkness or a fresh visual observation.";
        }

        [Serializable] public sealed class Result
        {
            public string actionId, entityId, reason;
            public bool on, changed, completed, replayed, partial, physicalSwitchPress, freshVisualObservation;
            public State actual;
            public string scope = "Whitelist device action: set the registered lamp's visible emission and Light component power. Original material assets and baked lighting are unchanged; visual confirmation must be obtained separately.";
        }

        public Transform Root => transform;
        public SceneObjectIdentity Identity => GetComponent<SceneObjectIdentity>();
        public string EntityId => Identity != null ? Identity.EntityId : "";
        public Renderer EmissionRenderer => emissionRenderer;
        public Light DeviceLight => deviceLight;
        public bool IsBound => bound;
        public string DisplayLabel => "场景标注：" + (Identity != null ? Identity.SceneLabel : "未登记灯具");

        private Renderer emissionRenderer;
        private Light deviceLight;
        private Material originalMaterial, ownedMaterial;
        private Mesh emissionMesh;
        private bool originalLightEnabled, originalEmissionKeyword, expectedLightEnabled, expectedEmissionKeyword;
        private Color originalEmissionColor, expectedEmissionColor;
        private int sceneHandle;
        private bool bound, applying;
        private string expectedMaterialState, expectedLightConfiguration, boundEntityId;
        private readonly Dictionary<string, bool> appliedActions = new Dictionary<string, bool>();
        private readonly Queue<string> actionOrder = new Queue<string>();

        public bool TryBind(Transform explicitRoot, Renderer emissive, Light light, out string failure)
        {
            failure = null;
            if (!Application.isPlaying) failure = "灯具开关仅在 Play 中显式绑定，不编辑场景或材质资产。";
            else if (applying) failure = "灯具状态正在切换。";
            else if (!isActiveAndEnabled) failure = "灯具能力未启用，未重新获取控制。";
            else if (explicitRoot != transform || emissive == null || light == null ||
                emissive.transform.parent != transform || light.transform.parent != transform ||
                !gameObject.scene.IsValid() || !gameObject.scene.isLoaded ||
                Identity == null || !Identity.enabled || Identity.AuthoredKey != SceneObjectLampCatalog.AuthoredKey ||
                Identity.SceneLabel != SceneObjectLampCatalog.Label)
                failure = "需要已登记的同一灯具根、发光子物件及 Light。";
            if (failure != null) return false;
            if (bound)
            {
                if (emissive != emissionRenderer || light != deviceLight) { failure = "灯具已绑定其他组件。"; return false; }
                return TryValidate(out failure);
            }
            Material[] materials = emissive.sharedMaterials;
            var filter = emissive.GetComponent<MeshFilter>();
            if (materials.Length != 1 || materials[0] == null || !materials[0].HasProperty("_EmissionColor") ||
                filter == null || filter.sharedMesh == null)
            { failure = "此能力只支持已登记的单材质发光网格。"; return false; }
            originalMaterial = materials[0];
            originalEmissionColor = originalMaterial.GetColor("_EmissionColor");
            originalEmissionKeyword = originalMaterial.IsKeywordEnabled("_EMISSION");
            if (!originalEmissionKeyword || originalEmissionColor.maxColorComponent <= .001f)
            { failure = "原始发光体没有可恢复的已启用发光状态。"; originalMaterial = null; return false; }
            emissionRenderer = emissive; deviceLight = light; emissionMesh = filter.sharedMesh;
            originalLightEnabled = expectedLightEnabled = light.enabled;
            expectedEmissionColor = originalEmissionColor; expectedEmissionKeyword = originalEmissionKeyword;
            sceneHandle = gameObject.scene.handle;
            boundEntityId = EntityId;
            expectedLightConfiguration = LightConfiguration(light);
            try
            {
                ownedMaterial = new Material(originalMaterial) { name = originalMaterial.name + " [runtime lamp power]", hideFlags = HideFlags.DontSave };
                expectedMaterialState = MaterialState(ownedMaterial);
                emissionRenderer.sharedMaterial = ownedMaterial;
                bound = true; appliedActions.Clear(); actionOrder.Clear();
                return true;
            }
            catch (Exception error)
            {
                if (OwnsMaterial()) emissionRenderer.sharedMaterial = originalMaterial;
                if (ownedMaterial != null && !MaterialStillReferenced(ownedMaterial)) DestroyOwned(ownedMaterial);
                ownedMaterial = null; originalMaterial = null;
                failure = "灯具实例绑定未完成：" + error.Message;
                return false;
            }
        }

        public bool TryValidate(out string failure)
        {
            failure = null;
            if (!bound || !isActiveAndEnabled || !gameObject.activeInHierarchy || gameObject.scene.handle != sceneHandle ||
                !gameObject.scene.isLoaded || Identity == null || !Identity.isActiveAndEnabled || EntityId != boundEntityId ||
                Identity.AuthoredKey != SceneObjectLampCatalog.AuthoredKey || Identity.SceneLabel != SceneObjectLampCatalog.Label)
                failure = "灯具能力或登记身份已失效。";
            else if (emissionRenderer == null || deviceLight == null || emissionRenderer.transform.parent != transform ||
                deviceLight.transform.parent != transform || !emissionRenderer.gameObject.activeInHierarchy ||
                !deviceLight.gameObject.activeInHierarchy || !emissionRenderer.enabled ||
                emissionRenderer.GetComponent<MeshFilter>()?.sharedMesh != emissionMesh)
                failure = "灯具的发光网格、Light 或从属关系已改变。";
            else if (!OwnsMaterial()) failure = "发光材质引用已由其他逻辑改变，未覆盖它。";
            else if (!MaterialMatchesExpected()) failure = "发光材质状态已由其他逻辑改变，未覆盖它。";
            else if (!LightMatchesExpected())
                failure = "Light 状态已由其他逻辑改变，未覆盖它。";
            return failure == null;
        }

        public State ReadState()
        {
            bool valid = TryValidate(out string conflict);
            Material current = null;
            if (emissionRenderer != null && emissionRenderer.sharedMaterials.Length == 1) current = emissionRenderer.sharedMaterials[0];
            Color emission = current != null && current.HasProperty("_EmissionColor") ? current.GetColor("_EmissionColor") : Color.black;
            bool emits = current != null && current.IsKeywordEnabled("_EMISSION") && emission.maxColorComponent > .001f;
            bool enabledLight = deviceLight != null && deviceLight.enabled;
            return new State { entityId = EntityId, sceneLabel = Identity != null ? Identity.SceneLabel : "",
                registered = valid, on = emits, emissionEnabled = emits, emissionColor = emission,
                lightEnabled = enabledLight, lightIntensity = deviceLight != null ? deviceLight.intensity : 0,
                lightBakeType = deviceLight != null ? deviceLight.lightmapBakeType.ToString() : "unavailable",
                lightState = deviceLight == null ? "unavailable" : enabledLight ? "enabled" : "disabled",
                emissionState = current == null ? "unavailable" : emits ? "on" : "off", conflict = conflict,
                bakedLightingResidualUncontrolled = deviceLight == null || deviceLight.lightmapBakeType != LightmapBakeType.Realtime };
        }

        public bool TrySetPower(string actionId, bool on, out Result result, out string failure)
        {
            failure = null;
            result = new Result { actionId = actionId ?? "", entityId = EntityId, on = on };
            if (string.IsNullOrWhiteSpace(actionId) || actionId.Length > 256) failure = "缺少有效灯控动作标识。";
            else if (applying) failure = "灯具状态正在切换；未重入执行。";
            else if (!TryValidate(out failure)) { }
            else if (appliedActions.TryGetValue(actionId, out bool previous) && previous != on)
                failure = "同一灯控动作标识不能改成相反操作。";
            if (failure != null) { result.reason = failure; result.actual = ReadState(); return false; }
            bool replayed = appliedActions.ContainsKey(actionId);
            bool desiredLight = on;
            Color desiredEmission = on ? originalEmissionColor : Color.black;
            bool desiredKeyword = on && originalEmissionKeyword;
            if (replayed && (expectedLightEnabled != desiredLight || expectedEmissionKeyword != desiredKeyword || expectedEmissionColor != desiredEmission))
            {
                failure = result.reason = "该旧动作已执行过，后续操作改变了状态；未重放旧动作。";
                result.replayed = true; result.actual = ReadState(); return false;
            }
            applying = true;
            bool beforeLightEnabled = expectedLightEnabled, beforeEmissionKeyword = expectedEmissionKeyword;
            Color beforeEmissionColor = expectedEmissionColor;
            try
            {
                result.changed = expectedLightEnabled != desiredLight || expectedEmissionKeyword != desiredKeyword || expectedEmissionColor != desiredEmission;
                ownedMaterial.SetColor("_EmissionColor", desiredEmission);
                if (desiredKeyword) ownedMaterial.EnableKeyword("_EMISSION"); else ownedMaterial.DisableKeyword("_EMISSION");
                deviceLight.enabled = desiredLight;
                expectedLightEnabled = desiredLight; expectedEmissionColor = desiredEmission; expectedEmissionKeyword = desiredKeyword;
                expectedMaterialState = MaterialState(ownedMaterial);
                if (!replayed)
                {
                    appliedActions[actionId] = on; actionOrder.Enqueue(actionId);
                    while (actionOrder.Count > 32) appliedActions.Remove(actionOrder.Dequeue());
                }
                result.completed = true; result.replayed = replayed;
                result.reason = on ? "已开启登记灯具的可见发光体及 Light 组件。" : "已关闭登记灯具的可见发光体及 Light 组件；未移除烘焙余光。";
                result.actual = ReadState();
                return true;
            }
            catch (Exception error)
            {
                failure = result.reason = "灯具切换未完成：" + error.Message;
                result.actual = ReadState();
                result.changed = result.actual.lightEnabled != beforeLightEnabled ||
                    (ownedMaterial != null && (ownedMaterial.GetColor("_EmissionColor") != beforeEmissionColor ||
                        ownedMaterial.IsKeywordEnabled("_EMISSION") != beforeEmissionKeyword));
                result.partial = result.changed;
                return false;
            }
            finally { applying = false; }
        }

        /// <summary>Restore only properties still owned by this component; never undo an external material/light edit.</summary>
        public bool RestoreOriginal(out string failure)
        {
            failure = null;
            if (applying) { failure = "灯具正在切换，未抢占恢复。"; return false; }
            if (!bound) return true;
            bool ownsMaterial = OwnsMaterial(), materialUnchanged = ownsMaterial && MaterialMatchesExpected();
            bool restoredMaterial = materialUnchanged && originalMaterial != null;
            if (restoredMaterial) emissionRenderer.sharedMaterial = originalMaterial;
            else failure = "发光材质已由外部修改，保留外部状态。";
            if (deviceLight != null)
            {
                if (LightMatchesExpected()) deviceLight.enabled = originalLightEnabled;
                else failure = (failure == null ? "" : failure + " ") + "Light 开关或配置已由外部修改，保留外部状态。";
            }
            // An externally edited owned instance may still be displayed; do not destroy that material.
            if (ownedMaterial != null && (!ownsMaterial || restoredMaterial) && !MaterialStillReferenced(ownedMaterial)) DestroyOwned(ownedMaterial);
            ownedMaterial = null; originalMaterial = null; bound = false;
            appliedActions.Clear(); actionOrder.Clear();
            return failure == null;
        }

        private bool OwnsMaterial() => emissionRenderer != null && ownedMaterial != null &&
            emissionRenderer.sharedMaterials.Length == 1 && emissionRenderer.sharedMaterials[0] == ownedMaterial;
        private bool MaterialMatchesExpected() => ownedMaterial != null && ownedMaterial.HasProperty("_EmissionColor") &&
            ownedMaterial.GetColor("_EmissionColor") == expectedEmissionColor && ownedMaterial.IsKeywordEnabled("_EMISSION") == expectedEmissionKeyword &&
            MaterialState(ownedMaterial) == expectedMaterialState;
        private bool LightMatchesExpected() => deviceLight != null && deviceLight.enabled == expectedLightEnabled &&
            LightConfiguration(deviceLight) == expectedLightConfiguration;
        private static string LightConfiguration(Light light)
        {
            if (light == null) return "missing";
            return string.Join("|", light.color.ToString("R"), light.intensity.ToString("R", CultureInfo.InvariantCulture),
                light.range.ToString("R", CultureInfo.InvariantCulture), light.type, light.lightmapBakeType,
                light.colorTemperature.ToString("R", CultureInfo.InvariantCulture), light.useColorTemperature,
                light.bounceIntensity.ToString("R", CultureInfo.InvariantCulture), light.cullingMask, light.renderMode,
                light.shadows, light.shadowStrength.ToString("R", CultureInfo.InvariantCulture),
                light.shadowBias.ToString("R", CultureInfo.InvariantCulture), light.shadowNormalBias.ToString("R", CultureInfo.InvariantCulture),
                light.shadowNearPlane.ToString("R", CultureInfo.InvariantCulture), light.shadowResolution, light.shadowCustomResolution,
                light.cookie != null ? light.cookie.GetInstanceID() : 0, light.cookieSize.ToString("R", CultureInfo.InvariantCulture),
                light.flare != null ? light.flare.GetInstanceID() : 0);
        }
        private static string MaterialState(Material material)
        {
            if (material == null || material.shader == null) return "missing";
            var state = new StringBuilder();
            state.Append(material.name).Append('|').Append(material.shader.GetInstanceID()).Append('|').Append(material.renderQueue).Append('|')
                .Append(material.enableInstancing).Append('|').Append(material.doubleSidedGI).Append('|').Append(material.globalIlluminationFlags);
            string[] keywords = material.shaderKeywords; Array.Sort(keywords, StringComparer.Ordinal);
            foreach (string keyword in keywords) state.Append('|').Append(keyword);
            Shader shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string property = shader.GetPropertyName(i); state.Append('|').Append(property).Append('=');
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: state.Append(material.GetColor(property).ToString("R")); break;
                    case ShaderPropertyType.Vector: state.Append(material.GetVector(property).ToString("R")); break;
                    case ShaderPropertyType.Texture:
                        var texture = material.GetTexture(property);
                        state.Append(texture != null ? texture.GetInstanceID() : 0).Append(':')
                            .Append(material.GetTextureOffset(property).ToString("R")).Append(':').Append(material.GetTextureScale(property).ToString("R")); break;
                    default: state.Append(material.GetFloat(property).ToString("R", CultureInfo.InvariantCulture)); break;
                }
            }
            return state.ToString();
        }
        private static bool MaterialStillReferenced(Material material)
        {
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
                foreach (Material reference in renderer.sharedMaterials) if (reference == material) return true;
            return false;
        }
        private static void DestroyOwned(UnityEngine.Object value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
        private void OnDisable() { RestoreOriginal(out _); }
        private void OnDestroy() { RestoreOriginal(out _); }
    }
}
