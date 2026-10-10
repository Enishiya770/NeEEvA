using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace NeEEvA.Presentation
{
    /// <summary>
    /// XRI 2.6.5 initializes its static hover registration, local physics scene
    /// and poke bindings only in Awake. Existing Play Mode components can receive
    /// enable/disable after an assembly reload without another Awake. Rebuild that
    /// complete per-domain state before entering either inherited callback.
    /// Recheck this lifecycle adapter when upgrading the pinned XRI package.
    /// </summary>
    [AddComponentMenu("NeEEvA/UI/Reload Safe Tracked Device Graphic Raycaster")]
    public sealed class ReloadSafeTrackedDeviceGraphicRaycaster : TrackedDeviceGraphicRaycaster
    {
        [NonSerialized] private bool initializedInDomain;

        private void EnsureDomainInitialization()
        {
            if (initializedInDomain) return;
            base.Awake();
            initializedInDomain = true;
        }

        protected override void Awake() => EnsureDomainInitialization();

        protected override void OnEnable()
        {
            EnsureDomainInitialization();
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            EnsureDomainInitialization();
            base.OnDisable();
        }
        // Keep the package's normal OnDestroy unregister/binding cleanup. A
        // destroyed component never needs another initialization in this domain.
    }
}
