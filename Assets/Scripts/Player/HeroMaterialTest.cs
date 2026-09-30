using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MobileGame.Player
{
    /// <summary>
    /// Play Mode verification suite for the hero material pass.
    ///
    /// <para>
    /// It proves, on the hero that is actually standing in the test arena, that:
    /// <list type="number">
    /// <item>All seven material roles exist and are separate material instances.</item>
    /// <item>Every renderer of the hero uses one of the seven roles, and every role is bound to geometry.</item>
    /// <item>Every role carries a base colour map, a normal map and a packed metallic/occlusion/smoothness mask.</item>
    /// <item>Skin, hair, costume, boots and gloves are dielectric; only the metallic-details role is metal.</item>
    /// <item>No role is a pure-black or a bleached-white material.</item>
    /// <item>Every albedo varies per pixel - no flat-coloured parts.</item>
    /// <item>Roughness is authored per pixel in the mask (<c>_Smoothness = 1</c>), not a single plastic value.</item>
    /// <item>All roles share one shader keyword set, so the hero stays in a single SRP Batcher variant.</item>
    /// <item>The hero reads under three lighting setups - bright key, dim warm key and cool key - measured by
    ///       rendering the hero off-screen and comparing hero-pixel luminance and contrast.</item>
    /// <item>Nothing gameplay-related changed: the visual carries no colliders and no gameplay components,
    ///       the rig's Animator applies no root motion, and the CharacterController and controller script
    ///       are still enabled.</item>
    /// </list>
    /// </para>
    ///
    /// Runs automatically on Start in Play Mode, after the movement, joystick and touch camera suites, or on
    /// demand from the component's context menu. All lighting state is restored when the suite finishes.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1200)]
    public class HeroMaterialTest : MonoBehaviour
    {
        [Tooltip("The hero visual to verify. If unassigned, one is found in the scene.")]
        [SerializeField] private HeroCharacterVisual heroVisual;

        [Tooltip("Automatically runs the verification suite on Start in Play Mode.")]
        [SerializeField] private bool runOnStart = true;

        [Tooltip("Renders the hero off-screen for the lighting readability check.")]
        [SerializeField] private bool verifyLightingReadability = true;

        [Tooltip("Resolution of the off-screen readability capture.")]
        [SerializeField] private int captureSize = 256;

        private const float MaxWaitForOtherSuiteSeconds = 180f;

        private bool m_running;
        private int m_passed;
        private int m_total;

        /// <summary>True while the automated suite is in progress.</summary>
        public bool IsRunning => m_running;

        private void Start()
        {
            if (runOnStart)
                RunTestSuite();
        }

        /// <summary>Runs the hero material verification suite in Play Mode.</summary>
        [ContextMenu("Verify: Run Hero Material Test Suite")]
        public void RunTestSuite()
        {
            if (m_running)
            {
                Debug.LogWarning("[HeroMaterialTest] A test run is already in progress.");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[HeroMaterialTest] Enter Play Mode to run the hero material suite.");
                return;
            }

            StartCoroutine(RunAllTests());
        }

        private IEnumerator RunAllTests()
        {
            m_running = true;
            m_passed = 0;
            m_total = 0;

            yield return WaitForOtherSuites();

            Debug.Log("[HeroMaterialTest] =========================================");
            Debug.Log("[HeroMaterialTest] Starting Hero Material Test Suite...");

            HeroCharacterVisual visual = heroVisual != null ? heroVisual : FindFirstObjectByType<HeroCharacterVisual>();
            if (visual == null)
            {
                Debug.LogError("[HeroMaterialTest] FAILED: No HeroCharacterVisual found in the scene.");
                m_running = false;
                yield break;
            }

            Transform root = visual.VisualRoot;
            HeroMaterials.Set materials = visual.Materials;
            if (root == null || materials == null)
            {
                Debug.LogError("[HeroMaterialTest] FAILED: The hero visual has not been built (no visual root or " +
                               "no material set).");
                m_running = false;
                yield break;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            TestRolesExist(materials);
            TestRoleInstancesDistinct(materials);
            TestRenderersUseRoleMaterials(materials, renderers);
            TestEveryRoleIsUsed(materials, renderers);
            TestTextureChannels(materials);
            TestMetallicPolicy(materials);
            TestBaseColourRange(materials);
            TestAlbedoIsNotFlat(materials);
            TestRoughnessAuthority(materials);
            TestSharedKeywordSet(materials);
            TestSurfaceSanity(renderers);
            TestGameplayUntouched(visual, root);

            if (verifyLightingReadability)
                yield return TestReadabilityUnderLighting(root, renderers, visual);

            Debug.Log($"[HeroMaterialTest] Test Results: {m_passed}/{m_total} checks passed.");

            if (m_passed == m_total)
            {
                Debug.Log("[HeroMaterialTest] VERIFICATION PASSED. All seven hero material roles carry base colour, " +
                          "normal and packed metallic/occlusion/smoothness data, and the hero stays readable under " +
                          "bright, dim warm and cool lighting.");
                Debug.Log("[HeroMaterialTest] =========================================");
            }
            else
            {
                Debug.LogError($"[HeroMaterialTest] VERIFICATION FAILED. Only {m_passed}/{m_total} checks passed.");
                Debug.Log("[HeroMaterialTest] =========================================");
            }

            m_running = false;
        }

        private IEnumerator WaitForOtherSuites()
        {
            // Yield one frame so the other suites' Start() runs first.
            yield return null;

            PlayerControllerTest playerTest = FindFirstObjectByType<PlayerControllerTest>();
            UI.VirtualJoystickTest joystickTest = FindFirstObjectByType<UI.VirtualJoystickTest>();
            UI.TouchCameraControlTest touchTest = FindFirstObjectByType<UI.TouchCameraControlTest>();
            HeroRigTest rigTest = FindFirstObjectByType<HeroRigTest>();

            float waited = 0f;
            while (((playerTest != null && playerTest.IsRunning) ||
                    (joystickTest != null && joystickTest.IsRunning) ||
                    (rigTest != null && rigTest.IsRunning) ||
                    (touchTest != null && touchTest.IsRunning)) &&
                   waited < MaxWaitForOtherSuiteSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (waited >= MaxWaitForOtherSuiteSeconds)
                Debug.LogWarning("[HeroMaterialTest] Timed out waiting for earlier suites; running anyway.", this);

            yield return new WaitForSeconds(0.25f);
        }

        // ------------------------------------------------------------------
        // Checks
        // ------------------------------------------------------------------
        private void TestRolesExist(HeroMaterials.Set materials)
        {
            var missing = new List<string>();
            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                if (materials[(HeroMaterialRole)i] == null)
                    missing.Add(HeroMaterials.RoleName((HeroMaterialRole)i));
            }

            Check(missing.Count == 0,
                $"[1/13] Seven material roles (7 = skin, hair, primary costume, secondary costume, boots, " +
                $"gloves, metallic details); missing: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
        }

        private void TestRoleInstancesDistinct(HeroMaterials.Set materials)
        {
            var seen = new Dictionary<Material, string>();
            var duplicates = new List<string>();
            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                if (material == null)
                    continue;

                if (seen.TryGetValue(material, out string other))
                    duplicates.Add($"{HeroMaterials.RoleName(role)} reuses the material of {other}");
                else
                    seen[material] = HeroMaterials.RoleName(role);
            }

            Check(duplicates.Count == 0,
                "[2/13] Roles are separate materials - no body part borrows another role's material" +
                (duplicates.Count > 0 ? " | " + string.Join("; ", duplicates) : string.Empty));
        }

        private void TestRenderersUseRoleMaterials(HeroMaterials.Set materials, Renderer[] renderers)
        {
            var foreign = new List<string>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer.sharedMaterial == null)
                {
                    foreign.Add(renderer.name + " has no material");
                    continue;
                }

                bool known = false;
                for (int i = 0; i < HeroMaterials.RoleCount; i++)
                {
                    if (materials[(HeroMaterialRole)i] == renderer.sharedMaterial)
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                    foreign.Add(renderer.name + " -> " + renderer.sharedMaterial.name);
            }

            Check(foreign.Count == 0,
                $"[3/13] All {renderers.Length} hero renderers use a role material" +
                (foreign.Count > 0 ? " | " + string.Join("; ", foreign) : " - no placeholder material left behind"));
        }

        private void TestEveryRoleIsUsed(HeroMaterials.Set materials, Renderer[] renderers)
        {
            var used = new bool[HeroMaterials.RoleCount];
            var counts = new int[HeroMaterials.RoleCount];
            foreach (Renderer renderer in renderers)
            {
                for (int i = 0; i < HeroMaterials.RoleCount; i++)
                {
                    if (renderer.sharedMaterial == materials[(HeroMaterialRole)i])
                    {
                        used[i] = true;
                        counts[i]++;
                    }
                }
            }

            var unused = new List<string>();
            var summary = new List<string>();
            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                if (!used[i])
                    unused.Add(HeroMaterials.RoleName((HeroMaterialRole)i));
                else
                    summary.Add($"{HeroMaterials.RoleName((HeroMaterialRole)i)}x{counts[i]}");
            }

            Check(unused.Count == 0,
                "[4/13] Every role is bound to geometry - " + string.Join(", ", summary) +
                (unused.Count > 0 ? " | unused: " + string.Join(", ", unused) : string.Empty));
        }

        private void TestTextureChannels(HeroMaterials.Set materials)
        {
            var problems = new List<string>();
            var summary = new StringBuilder();
            int complete = 0;

            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                if (material == null)
                    continue;

                string name = HeroMaterials.RoleName(role);
                Texture baseMap = GetBaseMap(material);
                Texture normalMap = material.GetTexture("_BumpMap");
                Texture maskMap = material.GetTexture("_MetallicGlossMap");
                Texture occlusionMap = material.GetTexture("_OcclusionMap");

                if (baseMap == null) problems.Add(name + " has no base colour map");
                if (normalMap == null) problems.Add(name + " has no normal map");
                if (maskMap == null) problems.Add(name + " has no metallic/smoothness mask");
                if (occlusionMap == null) problems.Add(name + " has no occlusion map");
                if (normalMap != null && !material.IsKeywordEnabled("_NORMALMAP"))
                    problems.Add(name + " does not enable _NORMALMAP");
                if (maskMap != null && !material.IsKeywordEnabled("_METALLICGLOSSMAP"))
                    problems.Add(name + " does not enable _METALLICGLOSSMAP");
                if (occlusionMap != null && !material.IsKeywordEnabled("_OCCLUSIONMAP"))
                    problems.Add(name + " does not enable _OCCLUSIONMAP");

                if (baseMap != null && normalMap != null && maskMap != null && occlusionMap != null)
                    complete++;

                summary.Append($"{name}[{(baseMap != null ? 'a' : '-')}{(normalMap != null ? 'n' : '-')}{(maskMap != null ? 'm' : '-')}] ");
            }

            Check(problems.Count == 0 && complete == HeroMaterials.RoleCount,
                $"[5/13] Base colour + normal + packed mask on all roles ({complete}/{HeroMaterials.RoleCount}) " +
                summary.ToString().Trim() +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : string.Empty));
        }

        private void TestMetallicPolicy(HeroMaterials.Set materials)
        {
            var problems = new List<string>();
            var values = new List<string>();
            int metallicRoles = 0;

            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                if (material == null)
                    continue;

                float metallic = material.GetFloat("_Metallic");
                string name = HeroMaterials.RoleName(role);
                values.Add($"{name}={metallic:0.00}");
                if (metallic > 0.5f)
                    metallicRoles++;

                if (role == HeroMaterialRole.Metal)
                {
                    if (metallic < 0.5f)
                        problems.Add("the metallic-details role is not set up as a metal");
                }
                else if (metallic > 0.2f)
                {
                    problems.Add(name + " is metallic (" + metallic.ToString("0.00") + ") but must be a dielectric");
                }
            }

            if (metallicRoles > 1)
                problems.Add(metallicRoles + " roles are metallic; only the metallic-details role should be");

            Check(problems.Count == 0,
                "[6/13] Metallic policy - one metal role and six dielectrics (" + string.Join(" ", values) + ")" +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : " - no excessive metallic surfaces"));
        }

        private void TestBaseColourRange(HeroMaterials.Set materials)
        {
            var problems = new List<string>();
            var values = new List<string>();

            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                if (material == null)
                    continue;

                Color tint = material.GetColor("_BaseColor");
                float luma = Luminance(tint);
                string name = HeroMaterials.RoleName(role);
                values.Add($"{name}={luma:0.00}");

                if (luma < 0.005f)
                    problems.Add(name + " uses a pure black tint");
                if (tint.r + tint.g + tint.b > 2.99f)
                    problems.Add(name + " uses a fully bleached white tint");
            }

            Check(problems.Count == 0,
                "[7/13] Base colour tints stay away from pure black and bleached white (" + string.Join(" ", values) + ")" +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : " - no all-black material anywhere"));
        }

        private void TestAlbedoIsNotFlat(HeroMaterials.Set materials)
        {
            var problems = new List<string>();
            var ranges = new List<string>();
            int sampled = 0;

            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                string name = HeroMaterials.RoleName(role);
                var texture = material != null ? GetBaseMap(material) as Texture2D : null;
                if (texture == null)
                {
                    problems.Add(name + " has no readable base colour texture");
                    continue;
                }

                if (!TrySampleLuma(texture, out float min, out float max))
                {
                    problems.Add(name + " base colour texture could not be sampled (" + texture.name + ")");
                    continue;
                }

                sampled++;
                ranges.Add($"{name}={min:0.00}-{max:0.00}");
                float spread = max - min;
                if (spread < 0.04f)
                    problems.Add(name + " albedo varies by only " + spread.ToString("0.000") + " (flat colour)");
                if (max < 0.02f)
                    problems.Add(name + " albedo is effectively black");
            }

            Check(problems.Count == 0 && sampled == HeroMaterials.RoleCount,
                "[8/13] Albedo is textured, not flat (luma range per role) " + string.Join(" ", ranges) +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : string.Empty));
        }

        private void TestRoughnessAuthority(HeroMaterials.Set materials)
        {
            var problems = new List<string>();
            var ranges = new List<string>();

            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                if (material == null)
                    continue;

                string name = HeroMaterials.RoleName(role);

                // URP multiplies _Smoothness into the mask alpha; anything below 1 silently dims every
                // authored roughness value, which is what makes a character look plastic.
                float smoothness = material.GetFloat("_Smoothness");
                if (Mathf.Abs(smoothness - 1f) > 0.001f)
                    problems.Add(name + " has _Smoothness=" + smoothness.ToString("0.00") + " instead of 1");

                if (material.GetFloat("_SmoothnessTextureChannel") > 0.5f)
                    problems.Add(name + " takes smoothness from albedo alpha instead of the mask");

                if (material.GetTexture("_MetallicGlossMap") is Texture2D mask &&
                    TrySampleMaskSpread(mask, out float maskMin, out float maskMax))
                {
                    ranges.Add($"{name}={maskMin:0.00}-{maskMax:0.00}");
                    if (maskMax - maskMin < 0.03f)
                        problems.Add(name + " has one constant roughness value over the whole surface");
                }
                else
                {
                    problems.Add(name + " roughness mask could not be sampled");
                }
            }

            Check(problems.Count == 0,
                "[9/13] Per-pixel roughness from the packed mask (smoothness range per role) " +
                string.Join(" ", ranges) +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : " - no constant plastic highlight"));
        }

        private void TestSharedKeywordSet(HeroMaterials.Set materials)
        {
            string[] reference = null;
            string referenceName = null;
            var problems = new List<string>();

            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = materials[role];
                if (material == null)
                    continue;

                var keywords = new List<string>();
                foreach (string keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_OCCLUSIONMAP", "_EMISSION" })
                {
                    if (material.IsKeywordEnabled(keyword))
                        keywords.Add(keyword);
                }

                keywords.Sort();
                if (reference == null)
                {
                    reference = keywords.ToArray();
                    referenceName = HeroMaterials.RoleName(role);
                }
                else if (!ArraysEqual(reference, keywords.ToArray()))
                {
                    problems.Add(HeroMaterials.RoleName(role) + " differs from " + referenceName);
                }
            }

            Check(problems.Count == 0,
                "[10/13] One shared shader keyword set across all roles (" +
                (reference != null ? string.Join(", ", reference) : "none") + ")" +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : " - single SRP Batcher variant"));
        }

        private void TestSurfaceSanity(Renderer[] renderers)
        {
            var problems = new List<string>();
            int shadowCasters = 0;
            int receivers = 0;

            foreach (Renderer renderer in renderers)
            {
                if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On)
                    shadowCasters++;
                else
                    problems.Add(renderer.name + " does not cast shadows");

                if (renderer.receiveShadows)
                    receivers++;
                else
                    problems.Add(renderer.name + " does not receive shadows");

                if (!renderer.allowOcclusionWhenDynamic)
                    problems.Add(renderer.name + " is not dynamic-occlusion aware");
            }

            Check(problems.Count == 0,
                $"[11/13] All {renderers.Length} hero renderers cast ({shadowCasters}) and receive ({receivers}) shadows" +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : " - the materials respond to scene lighting"));
        }

        private void TestGameplayUntouched(HeroCharacterVisual visual, Transform root)
        {
            var problems = new List<string>();

            // Scope the checks to the generated visual hierarchy: the Player root legitimately owns the
            // CharacterController and the gameplay components.
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 0)
                problems.Add(colliders.Length + " colliders inside the visual would change physics");

            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null)
                    continue;

                problems.Add("unexpected component in the visual: " + behaviour.GetType().Name);
            }

            // The only animation component allowed inside the visual is the rig's own Animator, and it
            // must never drive the root transform - the CharacterController owns that.
            foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.transform.parent != root)
                    problems.Add("the rig Animator is not on the visual's skeleton root");
                else if (animator.applyRootMotion)
                    problems.Add("the rig Animator applies root motion, fighting the CharacterController");
            }

            var controller = visual.GetComponent<CharacterController>();
            if (controller == null || !controller.enabled)
                problems.Add("the CharacterController is missing or disabled");

            if (!visual.enabled)
                problems.Add("HeroCharacterVisual is disabled");

            var player = visual.GetComponent<ThirdPersonPlayerController>();
            if (player == null || !player.enabled)
                problems.Add("ThirdPersonPlayerController is missing or disabled");

            Check(problems.Count == 0,
                "[12/13] Gameplay untouched - no colliders and no gameplay components in the visual, the rig " +
                "Animator sits on the skeleton root with root motion off, CharacterController and controller " +
                "script intact" + (problems.Count > 0 ? " | " + string.Join("; ", problems) : string.Empty));
        }

        private IEnumerator TestReadabilityUnderLighting(Transform heroRoot, Renderer[] renderers, HeroCharacterVisual visual)
        {
            Light key = FindKeyLight();
            if (key == null)
            {
                Check(false, "[13/13] Lighting readability - no directional light found in the scene");
                yield break;
            }

            Quaternion savedRotation = key.transform.rotation;
            Color savedColor = key.color;
            float savedIntensity = key.intensity;
            LightShadows savedShadows = key.shadows;

            string[] names = { "bright key", "dim warm key", "cool key" };
            Color[] colors = { new Color(1f, 1f, 1f), new Color(1f, 0.78f, 0.55f), new Color(0.6f, 0.72f, 1f) };
            float[] intensities = { 1f, 0.35f, 1.2f };

            var problems = new List<string>();
            var results = new List<string>();

            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    key.color = colors[i];
                    key.intensity = intensities[i];
                    yield return null; // let the light change take effect before rendering
                    yield return null;

                    yield return CaptureHeroStats(heroRoot, renderers, stats =>
                    {
                        results.Add($"{names[i]}={stats.MeanLuma:0.000}(contrast {stats.Contrast:0.000})");

                        if (stats.PixelCount < 64)
                            problems.Add(names[i] + " could not see the hero in the capture");
                        if (stats.MeanLuma < 0.02f)
                            problems.Add(names[i] + " renders the hero at luma " + stats.MeanLuma.ToString("0.000") + " (unreadable)");
                        if (stats.MeanLuma > 0.98f)
                            problems.Add(names[i] + " blows the hero out to luma " + stats.MeanLuma.ToString("0.000") + " (no form left)");
                        if (stats.Contrast < 0.05f)
                            problems.Add(names[i] + " leaves the hero flat (frame contrast " + stats.Contrast.ToString("0.000") + ")");
                    });
                }
            }
            finally
            {
                key.color = savedColor;
                key.intensity = savedIntensity;
                key.shadows = savedShadows;
                key.transform.rotation = savedRotation;
            }

            Check(problems.Count == 0,
                "[13/13] Readable under three lighting setups (hero mean luma and in-frame contrast) " +
                string.Join("  ", results) +
                (problems.Count > 0 ? " | " + string.Join("; ", problems) : " - form and shading survive all three"));
        }

        // ------------------------------------------------------------------
        // Off-screen capture
        // ------------------------------------------------------------------
        private struct HeroStats
        {
            public int PixelCount;
            public float MeanLuma;
            public float Contrast;
        }

        private IEnumerator CaptureHeroStats(Transform heroRoot, Renderer[] renderers, System.Action<HeroStats> onDone)
        {
            int size = Mathf.Clamp(captureSize, 64, 1024);
            var cameraObject = new GameObject("HeroMaterialTestCamera");
            cameraObject.hideFlags = HideFlags.DontSave;
            var camera = cameraObject.AddComponent<UnityEngine.Camera>();
            var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);

            HeroStats stats = new HeroStats();
            var withHero = new Color32[size * size];
            var withoutHero = new Color32[size * size];

            try
            {
                Bounds bounds = ComputeBounds(renderers);
                Vector3 focus = bounds.center;
                Vector3 direction = Vector3.ProjectOnPlane(heroRoot.root.forward, Vector3.up).normalized;
                if (direction.sqrMagnitude < 0.5f)
                    direction = Vector3.forward;

                camera.transform.position = focus + direction * (bounds.extents.magnitude * 2.6f + 1f);
                camera.transform.LookAt(focus);
                camera.fieldOfView = 40f;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 200f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.targetTexture = target;
                camera.allowHDR = false;

                RenderTexture.active = target;
                camera.Render();
                ReadPixels(withHero, size);

                // Second pass with the hero hidden: pixels that changed belong to the hero, which makes
                // the measurement independent of how the platform handles the alpha channel.
                var savedStates = new bool[renderers.Length];
                for (int i = 0; i < renderers.Length; i++)
                {
                    savedStates[i] = renderers[i].enabled;
                    renderers[i].enabled = false;
                }

                try
                {
                    camera.Render();
                    ReadPixels(withoutHero, size);
                }
                finally
                {
                    for (int i = 0; i < renderers.Length; i++)
                        renderers[i].enabled = savedStates[i];
                }
            }
            finally
            {
                RenderTexture.active = null;
                camera.targetTexture = null;
                Destroy(target);
                Destroy(cameraObject);
            }

            float sum = 0f;
            float min = 1f;
            float max = 0f;
            int counted = 0;

            for (int i = 0; i < withHero.Length; i++)
            {
                Color32 heroPixel = withHero[i];
                Color32 backgroundPixel = withoutHero[i];
                int difference = Mathf.Abs(heroPixel.r - backgroundPixel.r) +
                                 Mathf.Abs(heroPixel.g - backgroundPixel.g) +
                                 Mathf.Abs(heroPixel.b - backgroundPixel.b);
                if (difference < 6)
                    continue;

                float luma = Luminance(heroPixel);
                sum += luma;
                min = Mathf.Min(min, luma);
                max = Mathf.Max(max, luma);
                counted++;
            }

            stats.PixelCount = counted;
            stats.MeanLuma = counted > 0 ? sum / counted : 0f;
            stats.Contrast = counted > 0 ? max - min : 0f;

            onDone?.Invoke(stats);
            yield return null;
        }

        private static void ReadPixels(Color32[] destination, int size)
        {
            var readable = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                readable.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                readable.Apply(false, false);
                destination.CopyTo(readable.GetPixels32(), 0);
            }
            finally
            {
                Destroy(readable);
            }
        }

        private static Light FindKeyLight()
        {
            Light best = null;
            foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type != LightType.Directional || !light.enabled || !light.gameObject.activeInHierarchy)
                    continue;
                if (best == null || light.intensity > best.intensity)
                    best = light;
            }

            return best;
        }

        private static Bounds ComputeBounds(Renderer[] renderers)
        {
            bool hasBounds = false;
            Bounds bounds = new Bounds(Vector3.zero, Vector3.one);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !(renderer is MeshRenderer))
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }

        private static Texture GetBaseMap(Material material)
        {
            Texture map = material.GetTexture("_BaseMap");
            return map != null ? map : material.GetTexture("_MainTex");
        }

        /// <summary>Min/max linear luminance of an albedo texture.</summary>
        private static bool TrySampleLuma(Texture2D texture, out float min, out float max)
        {
            return TrySample(texture, out min, out max, false);
        }

        /// <summary>Min/max of the packed mask (red = metallic, alpha = smoothness).</summary>
        private static bool TrySampleMaskSpread(Texture2D texture, out float min, out float max)
        {
            return TrySample(texture, out min, out max, true);
        }

        private static bool TrySample(Texture2D texture, out float min, out float max, bool maskChannels)
        {
            min = 0f;
            max = 1f;

            Color32[] pixels = ReadablePixels(texture);
            if (pixels == null || pixels.Length == 0)
                return false;

            min = 1f;
            max = 0f;
            foreach (Color32 pixel in pixels)
            {
                float value = maskChannels
                    ? Mathf.Max(pixel.r / 255f, pixel.a / 255f)
                    : Luminance(((Color)pixel).linear);
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            return true;
        }

        /// <summary>Pixel source that works for both readable and GPU-compressed textures.</summary>
        private static Color32[] ReadablePixels(Texture2D texture)
        {
            try
            {
                if (texture.isReadable)
                    return texture.GetPixels32();
            }
            catch (UnityException)
            {
                // Fall through to the render-texture path below.
            }

            int size = Mathf.Min(64, Mathf.Max(2, Mathf.Min(texture.width, texture.height)));
            var readable = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var scratch = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                UnityEngine.Graphics.Blit(texture, scratch);
                RenderTexture.active = scratch;
                readable.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                readable.Apply(false, false);
                return readable.GetPixels32();
            }
            catch (UnityException)
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(scratch);
                Destroy(readable);
            }
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }

        private static bool ArraysEqual(string[] a, string[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        private void Check(bool condition, string description)
        {
            m_total++;
            if (condition)
            {
                m_passed++;
                Debug.Log("[HeroMaterialTest] PASSED: " + description);
            }
            else
            {
                Debug.LogError("[HeroMaterialTest] FAILED: " + description);
            }
        }
    }
}
