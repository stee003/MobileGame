using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MobileGame.Graphics
{
    /// <summary>
    /// The three graphics quality tiers. Values match the indices in
    /// Project Settings &gt; Quality (0 = Low, 1 = Medium, 2 = High).
    /// </summary>
    public enum GraphicsQualityTier
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    /// <summary>
    /// Single authoring point for the runtime-applied half of the graphics quality tiers.
    ///
    /// Where each setting lives (to avoid editing the wrong place):
    /// <list type="bullet">
    /// <item>Render scale, MSAA, shadow resolution/cascades, HDR, LUT size,
    /// reflection blending, additional-light limits → the three URP assets in
    /// <c>Assets/Settings</c> (<c>Mobile_RPAsset_Low/Medium/High</c>). Unity swaps
    /// them automatically when the quality level changes.</item>
    /// <item>Shadow distance, texture mip limits, anisotropic filtering, LOD bias,
    /// particle raycast budget, pixel lights, vsync → the matching tier in
    /// Project Settings &gt; Quality. Also applied automatically.</item>
    /// <item>Everything in <see cref="TierSettings"/> below (volume profile, bloom /
    /// vignette / grain tuning, reflection resolution, particle budget multiplier,
    /// target framerate) → this asset, applied at runtime by
    /// <see cref="GraphicsQualityManager"/>.</item>
    /// </list>
    ///
    /// The shipped instance lives at <c>Assets/Settings/Resources/</c> so the manager
    /// can load it without any scene setup. Create additional instances via
    /// <c>Assets &gt; Create &gt; MobileGame &gt; Graphics Quality Settings</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "MobileGame/Graphics Quality Settings", fileName = "GraphicsQualitySettings")]
    public sealed class GraphicsQualitySettings : ScriptableObject
    {
        /// <summary>
        /// Per-tier values applied at runtime. Post-processing intentionally stays
        /// <b>on</b> for every tier — tiers scale effect quality, they never strip
        /// the look down to nothing.
        /// </summary>
        [Serializable]
        public sealed class TierSettings
        {
            [Header("Identity")]
            [Tooltip("Display name. Keep in sync with the matching entry in Project Settings > Quality.")]
            public string displayName = "Medium";

            [Header("Post-processing")]
            [Tooltip("Base volume profile for this tier. It is instantiated at runtime (the asset is never modified) and lightly tuned with the values below. If null, a sensible mobile default profile is built in code instead.")]
            public VolumeProfile volumeProfile;

            [Tooltip("Bloom strength applied on top of the volume profile. 0 keeps bloom off for the tier; the rest of post (tonemapping, color, vignette) stays on.")]
            [Range(0f, 2f)]
            public float bloomIntensity = 0.45f;

            [Tooltip("Use high-quality (bicubic) bloom upsampling on this tier. Slightly more expensive, smoother result.")]
            public bool bloomHighQualityFiltering;

            [Tooltip("Vignette strength. Subtle on all tiers; same look, cheap everywhere.")]
            [Range(0f, 1f)]
            public float vignetteIntensity = 0.25f;

            [Tooltip("Enable a subtle film grain on this tier (flagship devices).")]
            public bool enableFilmGrain;

            [Header("Reflections")]
            [Tooltip("Default reflection-probe cubemap resolution applied via RenderSettings. 128 / 256 / 512 is the Low / Medium / High ladder.")]
            public int reflectionResolution = 256;

            [Header("Particles (for future VFX)")]
            [Tooltip("Multiplier applied by VFX systems to particle emission budgets. Query via GraphicsQualityManager.ParticleBudgetMultiplier.")]
            [Range(0.25f, 2f)]
            public float particleBudgetMultiplier = 1f;

            [Header("Framerate")]
            [Tooltip("Application.targetFrameRate for this tier. All tiers target 60 on modern phones; lower Low to 30 only if thermals demand it.")]
            public int targetFrameRate = 60;
        }

        [Tooltip("PlayerPrefs key used to persist the player's chosen quality tier.")]
        public string prefsKey = "MobileGame.GraphicsQuality";

        [Tooltip("Tier used on first launch (no saved preference yet). Must match the Android/iPhone default in Quality Settings.")]
        public GraphicsQualityTier defaultTier = GraphicsQualityTier.Medium;

        [Tooltip("Weak devices: hard shadows, no MSAA, lighter bloom. Nothing essential is disabled.")]
        public TierSettings low = new TierSettings
        {
            displayName = "Low",
            bloomIntensity = 0f,
            bloomHighQualityFiltering = false,
            vignetteIntensity = 0.25f,
            enableFilmGrain = false,
            reflectionResolution = 128,
            particleBudgetMultiplier = 0.75f,
            targetFrameRate = 60,
        };

        [Tooltip("Mainstream devices (default): soft shadows, 2x MSAA, gentle bloom.")]
        public TierSettings medium = new TierSettings
        {
            displayName = "Medium",
            bloomIntensity = 0.45f,
            bloomHighQualityFiltering = false,
            vignetteIntensity = 0.25f,
            enableFilmGrain = false,
            reflectionResolution = 256,
            particleBudgetMultiplier = 1f,
            targetFrameRate = 60,
        };

        [Tooltip("Flagship devices: 4x MSAA, full-res rendering, richer bloom + subtle grain.")]
        public TierSettings high = new TierSettings
        {
            displayName = "High",
            bloomIntensity = 0.65f,
            bloomHighQualityFiltering = true,
            vignetteIntensity = 0.3f,
            enableFilmGrain = true,
            reflectionResolution = 512,
            particleBudgetMultiplier = 1.25f,
            targetFrameRate = 60,
        };

        /// <summary>Number of tiers defined by this asset (always 3).</summary>
        public int TierCount => 3;

        /// <summary>Returns the tier at <paramref name="index"/> (clamped to 0..2).</summary>
        public TierSettings GetTier(int index)
        {
            switch (Mathf.Clamp(index, 0, 2))
            {
                case 0: return low;
                case 2: return high;
                default: return medium;
            }
        }

        /// <summary>Returns the settings for <paramref name="tier"/>.</summary>
        public TierSettings GetTier(GraphicsQualityTier tier) => GetTier((int)tier);
    }
}
