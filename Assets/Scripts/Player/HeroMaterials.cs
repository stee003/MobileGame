using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileGame.Player
{
    /// <summary>
    /// The seven material roles of the hero. One role = one body part family = one material, so the
    /// hero never falls back to a single flat "character" colour.
    /// </summary>
    public enum HeroMaterialRole
    {
        /// <summary>Face, neck, ears, jaw.</summary>
        Skin = 0,
        /// <summary>Hair pieces and darker facial details.</summary>
        Hair = 1,
        /// <summary>Primary costume: bodysuit, leggings, shoulder armour, cape, sleeves.</summary>
        Suit = 2,
        /// <summary>Secondary costume: cyan trim, visor, belt gem, hip and shoulder accents.</summary>
        Accent = 3,
        /// <summary>Boots and boot cuffs (warm bone leather).</summary>
        Boots = 4,
        /// <summary>Gloves, cuffs and knuckle pads (cool light grip leather).</summary>
        Gloves = 5,
        /// <summary>The only metal on the hero: belt, buckle, emblem ring, visor bolts, boot straps.</summary>
        Metal = 6,
    }

    /// <summary>
    /// Resolves the hero's PBR materials at runtime.
    ///
    /// <para>
    /// Priority order (first hit wins) so the hero is never left with flat colours:
    /// <list type="number">
    /// <item>Materials assigned in the Inspector (used by the scenes in this project).</item>
    /// <item>A complete <c>Resources/HeroMaterials/M_Hero_*.mat</c> set, for prefabs and builds that
    /// cannot hold scene references.</item>
    /// <item>Procedurally generated URP Lit materials with tileable albedo / normal / packed mask
    /// textures. They are lower fidelity than the authored set but keep every PBR channel populated,
    /// which is what stops the character from looking like a flat-shaded placeholder.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <see cref="Assets/Art/Materials/Hero"/> holds the authored set; each material carries a base
    /// map, a normal map and a channel-packed mask (R metallic, G occlusion, A smoothness) with
    /// <c>_Smoothness = 1</c>, because URP multiplies that slider into the mask alpha.
    /// </para>
    /// </summary>
    public static class HeroMaterials
    {
        /// <summary>Number of material roles (matches <see cref="HeroMaterialRole"/>).</summary>
        public const int RoleCount = 7;

        /// <summary>Resources sub-folder that may hold a build-safe copy of the authored set.</summary>
        public const string ResourcesFolder = "HeroMaterials";

        /// <summary>Project-relative folder of the authored materials.</summary>
        public const string AssetFolder = "Assets/Art/Materials/Hero";

        private static readonly HeroMaterialRole[] s_Roles =
        {
            HeroMaterialRole.Skin, HeroMaterialRole.Hair, HeroMaterialRole.Suit, HeroMaterialRole.Accent,
            HeroMaterialRole.Boots, HeroMaterialRole.Gloves, HeroMaterialRole.Metal,
        };

        private static Set s_ProceduralSet;

        /// <summary>All roles, in enum order.</summary>
        public static IReadOnlyList<HeroMaterialRole> Roles => s_Roles;

        /// <summary>Asset name of a role's authored material (for example <c>M_Hero_Suit</c>).</summary>
        public static string RoleName(HeroMaterialRole role)
        {
            switch (role)
            {
                case HeroMaterialRole.Skin: return "Skin";
                case HeroMaterialRole.Hair: return "Hair";
                case HeroMaterialRole.Suit: return "Suit";
                case HeroMaterialRole.Accent: return "Accent";
                case HeroMaterialRole.Boots: return "Boots";
                case HeroMaterialRole.Gloves: return "Gloves";
                default: return "Metal";
            }
        }

        /// <summary>Inspector-facing description of what the role covers.</summary>
        public static string RolePurpose(HeroMaterialRole role)
        {
            switch (role)
            {
                case HeroMaterialRole.Skin: return "Head, neck, jaw, ears";
                case HeroMaterialRole.Hair: return "Hair, brows, eyes";
                case HeroMaterialRole.Suit: return "Primary costume (bodysuit, cape, sleeves, armour)";
                case HeroMaterialRole.Accent: return "Secondary costume (cyan trim, visor, gem)";
                case HeroMaterialRole.Boots: return "Boots and cuffs";
                case HeroMaterialRole.Gloves: return "Gloves, cuffs, knuckle pads";
                default: return "Metallic details (belt, buckle, emblem ring, bolts)";
            }
        }

        /// <summary>Project-relative path of a role's authored material.</summary>
        public static string AssetPath(HeroMaterialRole role)
        {
            return AssetFolder + "/M_Hero_" + RoleName(role) + ".mat";
        }

        /// <summary>The seven materials of one hero.</summary>
        public sealed class Set
        {
            private readonly Material[] m_materials = new Material[RoleCount];

            /// <summary>Material of a role.</summary>
            public Material this[HeroMaterialRole role]
            {
                get { return m_materials[(int)role]; }
                set { m_materials[(int)role] = value; }
            }

            /// <summary>True when all seven roles are assigned.</summary>
            public bool IsComplete
            {
                get
                {
                    for (int i = 0; i < RoleCount; i++)
                    {
                        if (m_materials[i] == null)
                            return false;
                    }

                    return true;
                }
            }

            /// <summary>Number of assigned roles.</summary>
            public int AssignedCount
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < RoleCount; i++)
                    {
                        if (m_materials[i] != null)
                            count++;
                    }

                    return count;
                }
            }

            /// <summary>Material of a role, by index.</summary>
            public Material Get(int roleIndex)
            {
                return m_materials[Mathf.Clamp(roleIndex, 0, RoleCount - 1)];
            }

            /// <summary>Enumerates the assigned materials in role order.</summary>
            public IEnumerable<Material> All()
            {
                for (int i = 0; i < RoleCount; i++)
                {
                    if (m_materials[i] != null)
                        yield return m_materials[i];
                }
            }

            /// <summary>Copies any missing roles from another set.</summary>
            public void FillFrom(Set other)
            {
                if (other == null)
                    return;

                for (int i = 0; i < RoleCount; i++)
                {
                    if (m_materials[i] == null)
                        m_materials[i] = other.m_materials[i];
                }
            }
        }

        /// <summary>
        /// Builds the hero's material set: Inspector assignments first, then a complete Resources set,
        /// then procedural materials for whatever is still missing.
        /// </summary>
        /// <param name="sources">Materials assigned in the Inspector, in <see cref="HeroMaterialRole"/> order. Nulls are allowed.</param>
        /// <param name="usedFallback">True when at least one role had to be generated procedurally.</param>
        public static Set Resolve(Material[] sources, out bool usedFallback)
        {
            Set set = new Set();
            usedFallback = false;

            Set resources = TryLoadResourcesSet();
            for (int i = 0; i < RoleCount; i++)
            {
                Material material = sources != null && i < sources.Length ? sources[i] : null;
                if (material == null && resources != null)
                    material = resources[(HeroMaterialRole)i];

                set[(HeroMaterialRole)i] = material;
            }

            if (!set.IsComplete)
            {
                set.FillFrom(GetOrCreateProceduralSet());
                usedFallback = true;
            }

            return set;
        }

        /// <summary>Loads a complete authored set from <c>Resources/HeroMaterials</c>, or null.</summary>
        public static Set TryLoadResourcesSet()
        {
            Set set = new Set();
            for (int i = 0; i < RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                Material material = Resources.Load<Material>(ResourcesFolder + "/M_Hero_" + RoleName(role));
                if (material == null)
                    return null;

                set[role] = material;
            }

            return set;
        }

        /// <summary>
        /// Procedural URP Lit materials with tileable colour, normal and packed-mask textures.
        /// Cached: the textures are generated once per session, not once per hero instance.
        /// </summary>
        public static Set GetOrCreateProceduralSet()
        {
            if (s_ProceduralSet != null && s_ProceduralSet.IsComplete)
                return s_ProceduralSet;

            s_ProceduralSet = CreateProceduralSet();
            return s_ProceduralSet;
        }

        private static Set CreateProceduralSet()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                lit = Shader.Find("Standard");
            if (lit == null)
            {
                Debug.LogError("[HeroMaterials] No usable lit shader found; the hero cannot fall back to procedural PBR materials.");
                return new Set();
            }

            var set = new Set();
            Texture2D organicNormal = BuildDetailNormal("HeroFallback_Organic", 3, 6, 0.55f, 7);
            Texture2D clothNormal = BuildDetailNormal("HeroFallback_Cloth", 8, 8, 0.42f, 11);
            Texture2D metalNormal = BuildDetailNormal("HeroFallback_Metal", 4, 24, 0.28f, 13);

            for (int i = 0; i < RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                var rng = new System.Random(1000 + i * 37);
                Texture2D albedo = BuildFallbackAlbedo(role, 128, rng);
                Texture2D mask = BuildFallbackMask(role, 128, rng);
                Texture2D normal = role == HeroMaterialRole.Suit || role == HeroMaterialRole.Accent
                    ? clothNormal
                    : role == HeroMaterialRole.Metal ? metalNormal : organicNormal;

                var material = new Material(lit) { name = "M_Hero_" + RoleName(role) + "_Runtime" };
                material.SetTexture("_BaseMap", albedo);
                material.SetTexture("_MainTex", albedo);
                material.SetColor("_BaseColor", Color.white);
                material.SetColor("_Color", Color.white);
                material.SetTexture("_BumpMap", normal);
                material.SetFloat("_BumpScale", role == HeroMaterialRole.Hair ? 1.1f : 0.9f);
                material.SetTexture("_MetallicGlossMap", mask);
                material.SetTexture("_OcclusionMap", mask);
                // URP reads metallic from the mask's red channel; this scalar is the fallback and the
                // value the Inspector shows, so it matches the role's intent.
                material.SetFloat("_Metallic", role == HeroMaterialRole.Metal ? 0.9f : 0f);
                material.SetFloat("_Smoothness", 1f);
                material.SetFloat("_OcclusionStrength", 0.85f);
                material.SetFloat("_WorkflowMode", 1f);
                material.SetFloat("_Surface", 0f);
                material.SetFloat("_SpecularHighlights", 1f);
                material.SetFloat("_EnvironmentReflections", 1f);
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_METALLICGLOSSMAP");
                material.EnableKeyword("_OCCLUSIONMAP");
                material.enableInstancing = true;
                set[role] = material;
            }

            return set;
        }

        // ------------------------------------------------------------------
        // Procedural fallback textures (small, tileable, seeded)
        // ------------------------------------------------------------------
        private static Texture2D BuildFallbackAlbedo(HeroMaterialRole role, int size, System.Random rng)
        {
            Color baseColor;
            switch (role)
            {
                case HeroMaterialRole.Skin: baseColor = new Color(0.876f, 0.700f, 0.588f); break;
                case HeroMaterialRole.Hair: baseColor = new Color(0.216f, 0.235f, 0.310f); break;
                case HeroMaterialRole.Suit: baseColor = new Color(0.212f, 0.353f, 0.494f); break;
                case HeroMaterialRole.Accent: baseColor = new Color(0.129f, 0.663f, 0.808f); break;
                case HeroMaterialRole.Boots: baseColor = new Color(0.902f, 0.886f, 0.835f); break;
                case HeroMaterialRole.Gloves: baseColor = new Color(0.836f, 0.855f, 0.878f); break;
                default: baseColor = new Color(0.867f, 0.686f, 0.325f); break;
            }

            float[] coarse = ValueNoise(size, 4, 4, rng);
            float[] fine = ValueNoise(size, 16, 16, rng);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                float vu = (y + 0.5f) / size;
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    int index = y * size + x;
                    float shade;

                    switch (role)
                    {
                        case HeroMaterialRole.Suit:
                            shade = 0.86f + 0.26f * ThreadWeave(u, vu) + 0.10f * (fine[index] - 0.5f)
                                    - 0.18f * Mathf.SmoothStep(0.35f, 0.85f, coarse[index]);
                            break;
                        case HeroMaterialRole.Accent:
                            shade = 0.90f + 0.14f * Mathf.Max(Mathf.Cos(u * 8f * Mathf.PI * 2f), Mathf.Cos(vu * 8f * Mathf.PI * 2f)) * 0.5f + 0.5f
                                    + 0.08f * (fine[index] - 0.5f);
                            break;
                        case HeroMaterialRole.Hair:
                            shade = 0.80f + 0.45f * StrandPattern(u, vu, 24, coarse[index]) + 0.20f * vu;
                            break;
                        case HeroMaterialRole.Boots:
                            shade = 0.94f + 0.12f * (fine[index] - 0.5f) + 0.10f * (coarse[index] - 0.5f)
                                    - 0.22f * Mathf.SmoothStep(0.16f, 0f, vu);
                            break;
                        case HeroMaterialRole.Gloves:
                            shade = 0.95f + 0.12f * (fine[index] - 0.5f) + 0.06f * (coarse[index] - 0.5f);
                            break;
                        case HeroMaterialRole.Metal:
                            shade = 0.92f + 0.14f * (coarse[index] - 0.5f) + 0.06f * (fine[index] - 0.5f);
                            break;
                        default:
                            shade = 0.94f + 0.10f * (coarse[index] - 0.5f) - 0.10f * Mathf.SmoothStep(0.8f, 1f, fine[index]);
                            break;
                    }

                    Color tinted = baseColor * shade;
                    tinted.a = 1f;
                    pixels[index] = tinted;
                }
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            texture.name = "T_Hero_" + RoleName(role) + "_BaseColor_Runtime";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
            return texture;
        }

        private static Texture2D BuildFallbackMask(HeroMaterialRole role, int size, System.Random rng)
        {
            bool metal = role == HeroMaterialRole.Metal;
            float smoothBase;
            switch (role)
            {
                case HeroMaterialRole.Skin: smoothBase = 0.38f; break;
                case HeroMaterialRole.Hair: smoothBase = 0.46f; break;
                case HeroMaterialRole.Suit: smoothBase = 0.26f; break;
                case HeroMaterialRole.Accent: smoothBase = 0.46f; break;
                case HeroMaterialRole.Boots: smoothBase = 0.32f; break;
                case HeroMaterialRole.Gloves: smoothBase = 0.42f; break;
                default: smoothBase = 0.44f; break;
            }

            float[] variation = ValueNoise(size, 8, 8, rng);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                float noise = variation[i] - 0.5f;
                float metallic = metal ? Mathf.Clamp01(0.88f + 0.10f * noise) : 0f;
                float occlusion = Mathf.Clamp01(0.86f + 0.14f * noise);
                float smoothness = Mathf.Clamp(smoothBase + 0.16f * noise, 0.1f, 0.75f);
                pixels[i] = new Color(metallic, occlusion, 0f, smoothness);
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true);
            texture.name = "T_Hero_" + RoleName(role) + "_Mask_Runtime";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
            return texture;
        }

        /// <summary>Shared tileable detail normal map (wraps seamlessly, tangent-space RGB).</summary>
        private static Texture2D BuildDetailNormal(string name, int cells, int detail, float strength, int seed)
        {
            const int size = 128;
            var rng = new System.Random(seed);
            float[] coarse = ValueNoise(size, cells, cells, rng);
            float[] fine = ValueNoise(size, detail, detail, rng);
            var height = new float[size * size];
            for (int i = 0; i < height.Length; i++)
                height[i] = coarse[i] * 0.7f + fine[i] * 0.3f;

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    int left = y * size + ((x - 1 + size) % size);
                    int right = y * size + ((x + 1) % size);
                    int up = ((y - 1 + size) % size) * size + x;
                    int down = ((y + 1) % size) * size + x;

                    float du = (height[right] - height[left]) * 0.5f;
                    float dv = -(height[down] - height[up]) * 0.5f;
                    Vector3 normal = new Vector3(-du * strength * 16f, -dv * strength * 16f, 1f).normalized;

                    // Alpha mirrors red so the map decodes the same as RGB and as the AG (DXT5nm) layout.
                    pixels[index] = new Color(
                        normal.x * 0.5f + 0.5f,
                        normal.y * 0.5f + 0.5f,
                        normal.z * 0.5f + 0.5f,
                        normal.x * 0.5f + 0.5f);
                }
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true);
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
            return texture;
        }

        /// <summary>Tileable smooth value noise in [0, 1] (cell indices wrap, so the tile is seamless).</summary>
        private static float[] ValueNoise(int size, int cellsX, int cellsY, System.Random rng)
        {
            var grid = new float[cellsX * cellsY];
            for (int i = 0; i < grid.Length; i++)
                grid[i] = (float)rng.NextDouble();

            var result = new float[size * size];
            for (int y = 0; y < size; y++)
            {
                float ty = (float)y / size * cellsY;
                int row0 = Mathf.FloorToInt(ty) % cellsY;
                int row1 = (row0 + 1) % cellsY;
                float fy = ty - Mathf.Floor(ty);
                fy = fy * fy * (3f - 2f * fy);

                for (int x = 0; x < size; x++)
                {
                    float tx = (float)x / size * cellsX;
                    int column0 = Mathf.FloorToInt(tx) % cellsX;
                    int column1 = (column0 + 1) % cellsX;
                    float fx = tx - Mathf.Floor(tx);
                    fx = fx * fx * (3f - 2f * fx);

                    float a = grid[row0 * cellsX + column0];
                    float b = grid[row0 * cellsX + column1];
                    float c = grid[row1 * cellsX + column0];
                    float d = grid[row1 * cellsX + column1];
                    result[y * size + x] = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
                }
            }

            return result;
        }

        private static float ThreadWeave(float u, float v)
        {
            float warp = Mathf.Cos(u * 16f * Mathf.PI * 2f) * 0.5f + 0.5f;
            float weft = Mathf.Cos(v * 16f * Mathf.PI * 2f) * 0.5f + 0.5f;
            bool over = (Mathf.FloorToInt(u * 16f) + Mathf.FloorToInt(v * 16f)) % 2 == 0;
            return over ? warp : weft;
        }

        private static float StrandPattern(float u, float v, int strands, float jitter)
        {
            float phase = u * strands + (jitter - 0.5f) * 4f;
            return Mathf.Sin(phase * Mathf.PI * 2f) * 0.5f + 0.5f;
        }
    }
}
