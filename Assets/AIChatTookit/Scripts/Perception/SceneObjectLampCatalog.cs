using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>One audited authored lamp profile, explicitly installed by the scene-action owner. No global discovery.</summary>
    public static class SceneObjectLampCatalog
    {
        public const string AuthoredKey = "sailingmoon-standlight-4-v1";
        public const string Label = "桌面装饰灯";
        private const string AuditedShader = "Autodesk Interactive";

        public static bool EnsureRegistered(Transform environmentRoot, out SceneObjectLightSwitch device, out string failure)
        {
            device = null; failure = null;
            if (!Application.isPlaying || environmentRoot == null || !environmentRoot.gameObject.activeInHierarchy ||
                !environmentRoot.gameObject.scene.IsValid() || !environmentRoot.gameObject.scene.isLoaded)
            { failure = "需要 Play 中明确绑定且已启用的房间环境。"; return false; }
            Transform match = null;
            foreach (Transform item in environmentRoot.GetComponentsInChildren<Transform>(true))
            {
                if (item.name != "standlight (4)") continue;
                if (match != null) { failure = "房间出现多个同名登记灯具，未猜测选择。"; return false; }
                match = item;
            }
            if (match == null || match.parent == null || match.parent.name != "1階家具" || !match.gameObject.activeInHierarchy)
            { failure = "当前环境中未找到精确登记的 SailingMoon 灯具结构。"; return false; }
            var existing = match.GetComponent<SceneObjectLightSwitch>();
            if (existing != null && !existing.enabled)
            { failure = "登记灯控已被禁用，未重新启用或抢占控制。"; return false; }
            if (existing != null && existing.IsBound)
            {
                if (!existing.TryValidate(out failure)) return false;
                device = existing; return true;
            }
            if (match.childCount != 2) { failure = "登记灯具子物件数量不符：expected=2 actual=" + match.childCount; return false; }
            Transform glow = match.Find("スタンドライト.001"), lightNode = match.Find("Point Light");
            var rootRenderer = match.GetComponent<MeshRenderer>();
            var rootMesh = match.GetComponent<MeshFilter>();
            var animator = match.GetComponent<Animator>();
            var emission = glow != null ? glow.GetComponent<MeshRenderer>() : null;
            var filter = glow != null ? glow.GetComponent<MeshFilter>() : null;
            var light = lightNode != null ? lightNode.GetComponent<Light>() : null;
            var mismatch = new List<string>();
            void Require(bool matches, string detail) { if (!matches) mismatch.Add(detail); }
            Require(glow != null && lightNode != null && glow.parent == match && lightNode.parent == match,
                "direct children expected=[スタンドライト.001,Point Light] actual=[" + string.Join(",", match.Cast<Transform>().Select(child => child.name)) + "]");
            Require(glow != null && glow.childCount == 0 && lightNode != null && lightNode.childCount == 0,
                "nested child counts expected=[0,0] actual=[" + (glow != null ? glow.childCount.ToString() : "missing") + "," + (lightNode != null ? lightNode.childCount.ToString() : "missing") + "]");
            Require(rootRenderer != null && rootMesh?.sharedMesh != null,
                "root MeshRenderer/MeshFilter required; actual mesh=" + (rootMesh?.sharedMesh != null ? rootMesh.sharedMesh.name : "missing"));
            Require(emission != null && filter?.sharedMesh != null && filter.sharedMesh != rootMesh?.sharedMesh,
                "emission mesh must exist and differ from root; actual emission=" + (filter?.sharedMesh != null ? filter.sharedMesh.name : "missing") +
                ", root=" + (rootMesh?.sharedMesh != null ? rootMesh.sharedMesh.name : "missing"));
            Require(light != null && light.type == LightType.Point, "Light type expected=Point actual=" + (light != null ? light.type.ToString() : "missing"));
            var controller = animator != null ? animator.runtimeAnimatorController : null;
            Require(animator != null && animator.enabled && controller != null && controller.name == "Standlight",
                "Animator expected=enabled Standlight actual=" + (animator != null ? "enabled:" + animator.enabled + " controller:" + (controller != null ? controller.name : "missing") : "missing"));
            Require(controller != null && AnimationMatches(controller),
                "animation expected=[lightmotion:3.1666667] actual=" + AnimationDescription(controller));
            Require(Vector3.Distance(match.localScale, Vector3.one * .06f) <= .001f,
                "root localScale expected=(0.06,0.06,0.06) actual=" + match.localScale.ToString("R"));
            int lights = match.GetComponentsInChildren<Light>(true).Length, renderers = match.GetComponentsInChildren<Renderer>(true).Length;
            Require(lights == 1 && renderers == 2, "component counts expected=1 Light/2 Renderers actual=" + lights + "/" + renderers);
            Require(rootRenderer != null && Materials(rootRenderer, "Glass 1", "plastic"),
                "root materials expected=[Glass 1:" + AuditedShader + ",plastic:" + AuditedShader + "] actual=" + MaterialDescription(rootRenderer));
            Require(emission != null && Materials(emission, "Light 2"),
                "emission materials expected=[Light 2:" + AuditedShader + "] actual=" + MaterialDescription(emission));
            if (mismatch.Count != 0)
            { failure = "灯具配置与已核实配置不符，未添加操作能力：" + string.Join("; ", mismatch); return false; }
            var identity = match.GetComponent<SceneObjectIdentity>();
            if (identity != null && (!identity.isActiveAndEnabled || identity.AuthoredKey != AuthoredKey || identity.SceneLabel != Label))
            { failure = "灯具身份已由其他逻辑登记或修改，未覆盖它。"; return false; }
            if (identity == null) identity = match.gameObject.AddComponent<SceneObjectIdentity>();
            identity.Configure(Label, AuthoredKey);
            device = existing != null ? existing : match.gameObject.AddComponent<SceneObjectLightSwitch>();
            if (!device.TryBind(match, emission, light, out failure)) { device = null; return false; }
            return true;
        }

        private static bool AnimationMatches(RuntimeAnimatorController controller)
        {
            AnimationClip[] clips = controller.animationClips;
            return clips.Length == 1 && clips[0] != null && clips[0].name == "lightmotion" &&
                Mathf.Abs(clips[0].length - 3.1666667f) < .01f;
        }

        private static bool Materials(Renderer renderer, params string[] names)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length != names.Length) return false;
            for (int i = 0; i < names.Length; i++)
                if (materials[i] == null || materials[i].name != names[i] || materials[i].shader == null ||
                    materials[i].shader.name != AuditedShader) return false;
            return true;
        }
        private static string MaterialDescription(Renderer renderer) => renderer == null ? "missing" :
            "[" + string.Join(",", renderer.sharedMaterials.Select(material => material == null ? "null" :
                material.name + ":" + (material.shader != null ? material.shader.name : "missing shader"))) + "]";
        private static string AnimationDescription(RuntimeAnimatorController controller) => controller == null ? "missing" :
            "[" + string.Join(",", controller.animationClips.Select(clip => clip == null ? "null" :
                clip.name + ":" + clip.length.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }
}
