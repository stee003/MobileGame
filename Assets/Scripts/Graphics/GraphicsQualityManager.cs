using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace MobileGame.Graphics
{
    /// <summary>
    /// Runtime owner of the Low / Medium / High graphics quality tiers.
    ///
    /// <para>
    /// The manager self-bootstraps via <see cref="Bootstrap"/> (no scene setup needed),
    /// survives scene loads, restores the player's saved tier, and applies each tier in
    /// two steps:
    /// <list type="number">
    /// <item><see cref="QualitySettings.SetQualityLevel"/> — swaps the per-tier URP asset
    /// and QualitySettings tier (shadow quality/distance, texture quality, MSAA, render
    /// scale, LOD bias, particle raycast budget, realtime reflections, pixel lights).</item>
    /// <item>Runtime extras from <see cref="GraphicsQualitySettings"/> — global volume
    /// profile (+ bloom / vignette / grain tuning), reflection-probe resolution, the
    /// particle-budget multiplier for future VFX, and target framerate.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Play-Mode test: press Play, select the auto-created
    /// <c>GraphicsQualityManager</c> object, and run the
    /// <c>Verify: Cycle Tiers + Reload Scenes</c> context menu. The Console should show
    /// the tier cycle plus both scene loads with no errors.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GraphicsQualityManager : MonoBehaviour
    {
        private const string SettingsResourcePath = "GraphicsQualitySettings";
        private const string VolumeObjectName = "GraphicsQuality_Volume";

        private static GraphicsQualityManager s_instance;
        private static float s_particleBudgetMultiplier = 1f;

        [SerializeField]
        [Tooltip("Per-tier runtime settings. If left empty, the manager loads Assets/Settings/Resources/GraphicsQualitySettings, and falls back to built-in defaults if that is missing too.")]
        private GraphicsQualitySettings settings;

        private Volume m_volume;
        private VolumeProfile m_runtimeProfile;
        private int m_currentTier = -1;

        /// <summary>The singleton instance (created automatically before the first scene loads).</summary>
        public static GraphicsQualityManager Instance => s_instance;

        /// <summary>
        /// Particle budget multiplier for the active tier (0.75 / 1.0 / 1.25).
        /// Future VFX systems should scale emission rates / max particles by this.
        /// </summary>
        public static float ParticleBudgetMultiplier => s_particleBudgetMultiplier;

        /// <summary>Active tier index (0 = Low, 1 = Medium, 2 = High).</summary>
        public int CurrentTier => m_currentTier;

        /// <summary>Display name of the active tier, taken from QualitySettings.</summary>
        public string CurrentTierName
        {
            get
            {
                string[] names = QualitySettings.names;
                if (names != null && m_currentTier >= 0 && m_currentTier < names.Length)
                    return names[m_currentTier];
                return $"Tier {m_currentTier}";
            }
        }

        /// <summary>Fired after a tier change with the new tier index.</summary>
        public event Action<int> QualityChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null)
                return;

            var go = new GameObject("GraphicsQualityManager");
            go.AddComponent<GraphicsQualityManager>();
            // DontDestroyOnLoad is applied in Awake, which runs right after AddComponent.
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);

            ResolveSettings();
            EnsureVolume();
            ApplyTier(LoadSavedTier(), save: false);
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        /// <summary>Switch to a tier by index (0 = Low, 1 = Medium, 2 = High) and persist it.</summary>
        public void SetQuality(int tierIndex) => ApplyTier(tierIndex, save: true);

        /// <summary>Switch to a tier by name ("Low", "Medium", "High", case-insensitive). Returns false for unknown names.</summary>
        public bool SetQualityByName(string tierName)
        {
            if (string.IsNullOrEmpty(tierName))
                return false;

            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], tierName, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyTier(i, save: true);
                    return true;
                }
            }

            Debug.LogWarning($"[GraphicsQuality] Unknown quality tier '{tierName}'. Available: {string.Join(", ", names)}.");
            return false;
        }

        /// <summary>Cycle Low → Medium → High → Low. Handy for quick manual testing.</summary>
        public void CycleQuality()
        {
            int count = Mathf.Max(1, QualitySettings.names.Length);
            ApplyTier((m_currentTier + 1) % Math.Min(3, count), save: true);
        }

        /// <summary>
        /// Play-Mode self test: cycles all tiers (one rendered frame each), reloads the
        /// Boot and Test scenes, and reports success. Run via the component context menu.
        /// </summary>
        [ContextMenu("Verify: Cycle Tiers + Reload Scenes")]
        public void VerifyInPlayMode()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[GraphicsQuality] Verification runs in Play Mode only. Press Play, then run this again.");
                return;
            }
            StartCoroutine(VerifyRoutine());
        }

        private IEnumerator VerifyRoutine()
        {
            Debug.Log("[GraphicsQuality] Verification started: cycling Low -> Medium -> High.");
            int restoreTier = m_currentTier;
            int tiers = Math.Min(3, QualitySettings.names.Length);
            for (int i = 0; i < tiers; i++)
            {
                SetQuality(i);
                yield return null; // Let one frame render on the new tier.
                string pipeline = GraphicsSettings.currentRenderPipeline != null
                    ? GraphicsSettings.currentRenderPipeline.name
                    : "<none>";
                Debug.Log($"[GraphicsQuality] Verify: tier {i} ({CurrentTierName}) active, pipeline '{pipeline}', reflection res {RenderSettings.defaultReflectionResolution}, particle x{ParticleBudgetMultiplier:F2}.");
            }

            yield return SceneManager.LoadSceneAsync(0, LoadSceneMode.Single);
            Debug.Log("[GraphicsQuality] Verify: scene index 0 (Boot) reloaded OK.");

            if (SceneManager.sceneCountInBuildSettings > 1)
            {
                yield return SceneManager.LoadSceneAsync(1, LoadSceneMode.Single);
                Debug.Log("[GraphicsQuality] Verify: scene index 1 (Test) loaded OK.");

                yield return SceneManager.LoadSceneAsync(0, LoadSceneMode.Single);
                Debug.Log("[GraphicsQuality] Verify: back to Boot scene. VERIFICATION PASSED (no errors above = healthy).");
            }
            else
            {
                Debug.Log("[GraphicsQuality] Verify: single scene in build settings. VERIFICATION PASSED.");
            }

            SetQuality(restoreTier); // Leave the saved preference exactly as it was.
        }

        private void ResolveSettings()
        {
            if (settings != null)
                return;

            settings = Resources.Load<GraphicsQualitySettings>(SettingsResourcePath);
            if (settings == null)
            {
                Debug.LogWarning("[GraphicsQuality] GraphicsQualitySettings asset not found in Resources; using built-in defaults. Create one via Assets > Create > MobileGame > Graphics Quality Settings.");
                settings = ScriptableObject.CreateInstance<GraphicsQualitySettings>();
            }
        }

        private int LoadSavedTier()
        {
            int fallback = settings != null ? Mathf.Clamp((int)settings.defaultTier, 0, 2) : (int)GraphicsQualityTier.Medium;
            string key = settings != null ? settings.prefsKey : "MobileGame.GraphicsQuality";
            return Mathf.Clamp(PlayerPrefs.GetInt(key, fallback), 0, 2);
        }

        private void ApplyTier(int tierIndex, bool save)
        {
            int maxTier = Math.Max(0, QualitySettings.names.Length - 1);
            tierIndex = Mathf.Clamp(tierIndex, 0, Math.Min(2, maxTier));

            // Step 1 — engine tiers: swaps the URP asset + QualitySettings level.
            QualitySettings.SetQualityLevel(tierIndex, applyExpensiveChanges: true);

            // Step 2 — runtime extras from our settings asset.
            GraphicsQualitySettings.TierSettings tier = settings != null ? settings.GetTier(tierIndex) : null;
            if (tier != null)
            {
                ApplyVolumeProfile(tier);
                RenderSettings.defaultReflectionResolution = Mathf.Clamp(tier.reflectionResolution, 16, 1024);
                s_particleBudgetMultiplier = Mathf.Clamp(tier.particleBudgetMultiplier, 0.25f, 2f);
                Application.targetFrameRate = Mathf.Max(0, tier.targetFrameRate);
            }

            bool changed = tierIndex != m_currentTier;
            m_currentTier = tierIndex;

            if (save && settings != null)
            {
                PlayerPrefs.SetInt(settings.prefsKey, tierIndex);
                PlayerPrefs.Save();
            }

            if (changed)
                QualityChanged?.Invoke(tierIndex);

            Debug.Log($"[GraphicsQuality] Applied tier {tierIndex} ({CurrentTierName}).");
        }

        private void EnsureVolume()
        {
            if (m_volume != null)
                return;

            GameObject go = GameObject.Find(VolumeObjectName);
            if (go == null)
            {
                go = new GameObject(VolumeObjectName);
                DontDestroyOnLoad(go);
            }

            m_volume = go.GetComponent<Volume>();
            if (m_volume == null)
                m_volume = go.AddComponent<Volume>();

            m_volume.isGlobal = true;
            m_volume.priority = 100f;
        }

        private void ApplyVolumeProfile(GraphicsQualitySettings.TierSettings tier)
        {
            EnsureVolume();

            // Instantiate: the project asset is never modified, artists stay in control.
            VolumeProfile runtime = tier.volumeProfile != null
                ? Instantiate(tier.volumeProfile)
                : ScriptableObject.CreateInstance<VolumeProfile>();

            TuneProfile(runtime, tier);

            VolumeProfile previous = m_runtimeProfile;
            m_runtimeProfile = runtime;
            m_volume.profile = runtime;
            m_volume.enabled = true;

            if (previous != null)
                Destroy(previous);
        }

        /// <summary>
        /// Enforces the per-tier post look. Tonemapping / color / vignette are identical
        /// on every tier so art direction never shifts with quality; tiers scale bloom
        /// quality and (on High) add a subtle grain. Post is never turned off.
        /// </summary>
        private static void TuneProfile(VolumeProfile profile, GraphicsQualitySettings.TierSettings tier)
        {
            Tonemapping tonemapping = GetOrAdd<Tonemapping>(profile);
            SetOverride(tonemapping.mode, TonemappingMode.Neutral);

            ColorAdjustments color = GetOrAdd<ColorAdjustments>(profile);
            SetOverride(color.postExposure, 0f);
            SetOverride(color.contrast, 5f);
            SetOverride(color.saturation, 5f);
            SetOverride(color.colorFilter, Color.white);

            Vignette vignette = GetOrAdd<Vignette>(profile);
            SetOverride(vignette.color, Color.black);
            SetOverride(vignette.center, new Vector2(0.5f, 0.5f));
            SetOverride(vignette.intensity, tier.vignetteIntensity);
            SetOverride(vignette.smoothness, 0.8f);

            Bloom bloom = GetOrAdd<Bloom>(profile);
            SetOverride(bloom.threshold, 1f);
            SetOverride(bloom.intensity, tier.bloomIntensity);
            SetOverride(bloom.scatter, 0.5f);
            SetOverride(bloom.highQualityFiltering, tier.bloomHighQualityFiltering);
            bloom.active = tier.bloomIntensity > 0f;

            FilmGrain grain = GetOrAdd<FilmGrain>(profile);
            SetOverride(grain.intensity, 0.25f);
            SetOverride(grain.response, 0.8f);
            grain.active = tier.enableFilmGrain;
        }

        private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet(out T component))
                component = profile.Add<T>(false);
            component.active = true;
            return component;
        }

        private static void SetOverride<T>(VolumeParameter<T> parameter, T value)
        {
            parameter.value = value;
            parameter.overrideState = true;
        }
    }
}
