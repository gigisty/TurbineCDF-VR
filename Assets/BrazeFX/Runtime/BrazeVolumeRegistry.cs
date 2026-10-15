using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// A volume render source the URP <see cref="BrazeRenderFeature"/> should account for when it decides whether to
    /// run its full-screen composite passes. Implemented by anything that draws a BrazeFX volume WITHOUT being a
    /// <see cref="BrazeContainer"/> — primarily the container-free cache renderer behind <see cref="BrazeCachePlayer"/>.
    /// </summary>
    public interface IBrazeVolumeSource
    {
        /// <summary>This source wants its volume composited to screen this frame.</summary>
        bool VolumeRendering { get; }
        /// <summary>True only when detail/content changes independently of camera motion and cannot be reprojected exactly.</summary>
        bool TemporalDetailUnstable { get; }
        /// <summary>Legacy serialized/source cap retained for compatibility. Braze Render Feature owns the active cap.</summary>
        int MaxRaymarchHeightCap { get; }
        /// <summary>This source is casting fire light onto the scene this frame (configured injection material ready).</summary>
        bool FireLightActive { get; }
        /// <summary>This source is casting a volume shadow onto the scene this frame.</summary>
        bool ShadowCastActive { get; }
        /// <summary>The fire-light/shadow injection material (BrazeFX/FireLight) drawn fullscreen by the feature.</summary>
        Material FireLightMaterial { get; }
    }

    /// <summary>
    /// Registry of non-container <see cref="IBrazeVolumeSource"/>s, parallel to <see cref="BrazeContainer.Active"/>.
    /// The URP feature ORs this with the container registry so a cache renderer with NO container in the scene still
    /// triggers the composite/raymarch passes. Kept separate from the container list (rather than unifying the two)
    /// so the live container render path is untouched.
    /// </summary>
    public static class BrazeVolumeRegistry
    {
        static readonly List<IBrazeVolumeSource> sources = new List<IBrazeVolumeSource>();

        /// <summary>Bumped whenever a source registers/unregisters or its look changes → the feature resets temporal
        /// history (mirrors <see cref="BrazeContainer.RenderSettingsVersion"/>).</summary>
        public static int Version { get; private set; }

        public static void Register(IBrazeVolumeSource s)
        {
            if (s == null || sources.Contains(s)) return;
            sources.Add(s);
            Touch();
        }

        public static void Unregister(IBrazeVolumeSource s)
        {
            if (sources.Remove(s)) Touch();
        }

        /// <summary>Mark render settings dirty (look change) so the feature drops stale temporal history.</summary>
        public static void Touch() { unchecked { Version++; } }

        /// <summary>Any registered source drawing this frame.</summary>
        public static bool AnyRendering()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.VolumeRendering) return true;
            }
            return false;
        }

        public static bool AnyTemporallyUnstableDetail()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.VolumeRendering && s.TemporalDetailUnstable) return true;
            }
            return false;
        }

        /// <summary>Legacy compatibility helper. The active render path no longer consumes source-owned caps.</summary>
        public static int CombineMaxRaymarchHeight(int seedCap)
        {
            int cap = seedCap;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s == null) continue;
                int h = Mathf.Max(0, s.MaxRaymarchHeightCap);
                if (h <= 0) continue;
                cap = cap == 0 ? h : Mathf.Min(cap, h);
            }
            return cap;
        }

        public static bool AnyFireLighting()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.FireLightActive && s.FireLightMaterial != null) return true;
            }
            return false;
        }

        public static bool AnyShadowCasting()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.ShadowCastActive && s.FireLightMaterial != null) return true;
            }
            return false;
        }

        /// <summary>Append every registered source's fire-light injection material to <paramref name="into"/>.</summary>
        public static void CollectFireLightMaterials(List<Material> into)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.FireLightActive && s.FireLightMaterial != null) into.Add(s.FireLightMaterial);
            }
        }

        /// <summary>Append every registered source's volume-shadow injection material to <paramref name="into"/>.</summary>
        public static void CollectShadowMaterials(List<Material> into)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.ShadowCastActive && s.FireLightMaterial != null) into.Add(s.FireLightMaterial);
            }
        }
    }
}
