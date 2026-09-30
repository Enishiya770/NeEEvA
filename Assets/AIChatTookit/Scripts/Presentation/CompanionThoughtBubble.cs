using UnityEngine;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    /// <summary>A view-only projection of the current listening reaction, following the avatar's head.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(1100)]
    public sealed class CompanionThoughtBubble : MonoBehaviour
    {
        private const float MaximumWidth = 380f;
        private const float Padding = 22f;
        private const float Margin = 18f;
        private const float LingerSeconds = 8f;
        private static readonly Color Paper = new Color(.94f, .96f, .92f, .94f);
        private ChatSample chat;
        private Camera playerCamera;
        private Canvas canvas;
        private CanvasScaler scaler;
        private RectTransform root, card, nearDot, farDot;
        private CanvasGroup group;
        private Text label;
        private bool displayEnabled = true, vrActive;
        private int lastVersion = -1;
        private string lastText = string.Empty;
        private float changedAt;
        private float layoutWidth = -1f;

        public void Configure(ChatSample owner, Camera camera, Font font)
        {
            chat = owner;
            playerCamera = camera;
            if (canvas != null) return;
            root = (RectTransform)transform;
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 45;
            canvas.worldCamera = camera;
            scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            group = gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
            card = MakeSurface("Listening thought", root, new Vector2(MaximumWidth, 86f), 24f);
            var shadow = card.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(.03f, .06f, .06f, .12f);
            shadow.effectDistance = new Vector2(0f, -3f);
            nearDot = MakeSurface("Thought dot near bubble", root, new Vector2(14f, 14f), 7f);
            farDot = MakeSurface("Thought dot near avatar", root, new Vector2(8f, 8f), 4f);
            var textRect = new GameObject("Inner reaction", typeof(RectTransform)).GetComponent<RectTransform>();
            textRect.SetParent(card, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(Padding, 15f);
            textRect.offsetMax = new Vector2(-Padding, -15f);
            label = textRect.gameObject.AddComponent<Text>();
            label.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 24;
            label.lineSpacing = 1.15f;
            label.color = new Color(.16f, .23f, .23f, 1f);
            label.alignment = TextAnchor.MiddleLeft;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.supportRichText = false;
            label.raycastTarget = false;
        }

        public void SetDisplayEnabled(bool value)
        {
            displayEnabled = value;
            if (!value && group != null) group.alpha = 0f;
        }

        public void SetPresentationMode(bool vr)
        {
            vrActive = vr;
            if (canvas == null) return;
            canvas.renderMode = vr ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay;
            scaler.enabled = !vr;
            root.localScale = Vector3.one;
            root.rotation = Quaternion.identity;
            layoutWidth = -1f;
            group.alpha = 0f;
        }

        private void LateUpdate()
        {
            if (canvas == null) return;
            if (chat == null || playerCamera == null || !playerCamera.isActiveAndEnabled)
            {
                group.alpha = 0f;
                return;
            }
            string text = chat.PresentationThoughtText ?? string.Empty;
            int version = chat.PresentationThoughtVersion;
            if (version != lastVersion || text != lastText)
            {
                bool changed = text != lastText;
                lastVersion = version;
                lastText = text;
                changedAt = Time.unscaledTime;
                if (!string.IsNullOrEmpty(text))
                {
                    label.text = text;
                    layoutWidth = -1f;
                    // A quick dip distinguishes a replacement without delaying the new words.
                    if (changed) group.alpha = Mathf.Min(group.alpha, .45f);
                }
            }
            bool hasThought = displayEnabled && !string.IsNullOrEmpty(text) &&
                Time.unscaledTime - changedAt < LingerSeconds;
            if (!hasThought && group.alpha <= 0f)
            {
                if (label.text.Length > 0) label.text = string.Empty;
                return;
            }
            if (hasThought && label.text != text)
            {
                label.text = text;
                layoutWidth = -1f;
            }
            Vector3 head;
            if (!displayEnabled || !chat.TryGetPresentationHeadPosition(out head) ||
                !TryPosition(head))
            {
                group.alpha = 0f;
                return;
            }
            float target = hasThought ? 1f : 0f;
            group.alpha = Mathf.MoveTowards(group.alpha, target,
                Time.unscaledDeltaTime / (hasThought ? .16f : .18f));
            if (!hasThought && group.alpha <= 0f) label.text = string.Empty;
        }

        private bool TryPosition(Vector3 head)
        {
            Vector3 viewport = playerCamera.WorldToViewportPoint(head);
            // Hide when the head leaves the view, instead of pinning an unrelated thought to an edge.
            if (viewport.z <= playerCamera.nearClipPlane || viewport.z >= playerCamera.farClipPlane ||
                viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;
            Vector2 headPoint;
            if (vrActive)
            {
                // A camera-facing plane at the avatar's depth is comfortable in stereo. UI units
                // keep the same apparent size while the entire bubble follows the head in world space.
                float height = playerCamera.orthographic ? playerCamera.orthographicSize * 2f :
                    viewport.z * 2f * Mathf.Tan(playerCamera.fieldOfView * .5f * Mathf.Deg2Rad);
                root.sizeDelta = new Vector2(900f * playerCamera.aspect, 900f);
                root.localScale = Vector3.one * (height / 900f);
                root.SetPositionAndRotation(playerCamera.ViewportToWorldPoint(new Vector3(.5f, .5f, viewport.z)),
                    playerCamera.transform.rotation);
                headPoint = new Vector2((viewport.x - .5f) * root.rect.width, (viewport.y - .5f) * root.rect.height);
            }
            else
            {
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root,
                    playerCamera.WorldToScreenPoint(head),
                    canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : playerCamera,
                    out headPoint)) return false;
            }
            Rect bounds = root.rect;
            float availableWidth = Mathf.Min(MaximumWidth, bounds.width - Margin * 2f);
            if (availableWidth < 100f || bounds.height < 150f) return false;
            if (!Mathf.Approximately(availableWidth, layoutWidth))
            {
                layoutWidth = availableWidth;
                // Balance short CJK reactions across their estimated lines. A fixed maximum
                // width otherwise leaves a lone closing punctuation mark on the last line.
                float naturalWidth = label.preferredWidth;
                int lines = Mathf.Max(1, Mathf.CeilToInt(naturalWidth / (availableWidth - Padding * 2f)));
                float balancedContent = Mathf.Ceil(naturalWidth / lines / label.fontSize) * label.fontSize;
                float fittedWidth = Mathf.Min(availableWidth, Mathf.Max(176f, balancedContent + Padding * 2f + 4f));
                card.sizeDelta = new Vector2(fittedWidth, 100f);
                var settings = label.GetGenerationSettings(new Vector2(fittedWidth - Padding * 2f, 0f));
                float threeLines = label.cachedTextGeneratorForLayout.GetPreferredHeight("想\n想\n想", settings) / label.pixelsPerUnit;
                float textHeight = Mathf.Clamp(label.preferredHeight, 30f, Mathf.Max(90f, threeLines));
                card.sizeDelta = new Vector2(fittedWidth, textHeight + 30f);
            }
            float width = card.sizeDelta.x;
            float side = headPoint.x + width + 48f <= bounds.xMax - Margin ? 1f : -1f;
            float halfHeight = card.sizeDelta.y * .5f;
            Vector2 desired = headPoint + new Vector2(side * (width * .5f + 42f), halfHeight + 42f);
            desired.x = Mathf.Clamp(desired.x, bounds.xMin + width * .5f + Margin, bounds.xMax - width * .5f - Margin);
            desired.y = Mathf.Clamp(desired.y, bounds.yMin + halfHeight + 48f, bounds.yMax - halfHeight - Margin);
            card.anchoredPosition = desired;
            Vector2 corner = desired + new Vector2(-side * (width * .5f - 24f), -halfHeight);
            nearDot.anchoredPosition = corner + new Vector2(-side * 10f, -13f);
            farDot.anchoredPosition = corner + new Vector2(-side * 23f, -27f);
            return true;
        }

        private static RectTransform MakeSurface(string name, Transform parent, Vector2 size, float radius)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            var surface = rect.gameObject.AddComponent<CompanionSurface>();
            surface.color = Paper;
            surface.radius = radius;
            surface.raycastTarget = false;
            return rect;
        }

        private void OnDisable()
        {
            if (group != null) group.alpha = 0f;
        }
    }
}
