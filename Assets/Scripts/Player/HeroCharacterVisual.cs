using UnityEngine;
using UnityEngine.Rendering;

namespace MobileGame.Player
{
    /// <summary>
    /// Builds a completely original low-poly superhero character to replace the
    /// temporary capsule placeholder.
    ///
    /// Design notes (original, not copied from any existing franchise):
    /// - Athletic broad-shoulder, tapered waist silhouette for instant hero read
    /// - Deep midnight-teal bodysuit with electric-cyan chest chevron and a brass belt
    /// - Short half-cape, angular shoulder armor, light boots/gloves, cyan visor
    /// - Compact swept-crest hairstyle (fade sides, single forward peak)
    /// - Humanoid proportions, separate head/hands/feet, low-poly mobile friendly
    ///
    /// Materials (see <see cref="HeroMaterials"/>):
    /// - Seven PBR roles - skin, hair, primary costume, secondary costume, boots, gloves, metal -
    ///   each with base colour + normal + channel-packed metallic/occlusion/smoothness maps.
    /// - Assigned from this component's Inspector fields, or a <c>Resources/HeroMaterials</c> set,
    ///   and only if both are missing generated procedurally, so the hero never renders as
    ///   flat-shaded colour blocks.
    ///
    /// Optimization for mobile:
    /// - Primitive-based, ~3.5k triangles (one sphere, eight capsules, one cylinder, boxes)
    /// - Seven materials, SRP Batcher and GPU instancing compatible
    /// - No SkinnedMeshRenderer, no blendshapes, no extra colliders
    /// - Shadows cast and received, per-object motion vectors
    /// - Visual root is a simple child; the CharacterController remains the sole physics shape
    /// - Feet soles sit exactly at model origin so they rest on ground (no floating)
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)] // Build before PlayerController Awake
    public class HeroCharacterVisual : MonoBehaviour
    {
        [Header("Hero Materials - 7 PBR roles (optional)")]
        [Tooltip("Skin: head, neck, jaw, ears. Authored as Assets/Art/Materials/Hero/M_Hero_Skin.mat.")]
        [SerializeField] private Material skinMaterial;
        [Tooltip("Hair: hair pieces plus brows and dark eye slits. M_Hero_Hair.mat.")]
        [SerializeField] private Material hairMaterial;
        [Tooltip("Primary costume: bodysuit, leggings, sleeves, shoulder armour, cape. M_Hero_Suit.mat.")]
        [SerializeField] private Material suitMaterial;
        [Tooltip("Secondary costume: cyan trim, visor, hip accents, belt gem, emblem inlay. M_Hero_Accent.mat.")]
        [SerializeField] private Material accentMaterial;
        [Tooltip("Boots and boot cuffs (warm bone leather). M_Hero_Boots.mat.")]
        [SerializeField] private Material bootsMaterial;
        [Tooltip("Gloves, cuffs and knuckle pads (cool light grip leather). M_Hero_Gloves.mat.")]
        [SerializeField] private Material glovesMaterial;
        [Tooltip("Metallic details: belt, buckle, emblem ring, visor bolts, boot straps. M_Hero_Metal.mat.")]
        [SerializeField] private Material metalMaterial;

        [Header("Visual Tuning")]
        [Tooltip("Vertical offset of the visual root relative to the CharacterController center. -1 places feet at ground when controller center is 0.")]
        [SerializeField] private float verticalOffset = -1f;

        [Tooltip("Logs which material set the hero resolved to (authored, Resources or procedural).")]
        [SerializeField] private bool logMaterialResolution = true;

        private const string VisualRootName = "HeroVisual";
        private bool m_FallbackLogged;
        private Transform m_visualRoot;
        private HeroMaterials.Set m_materials;
        private bool m_built;

        /// <summary>The seven resolved hero materials, indexed by role.</summary>
        public HeroMaterials.Set Materials => m_materials;

        /// <summary>Root of the generated visual hierarchy (null before the hero is built).</summary>
        public Transform VisualRoot => m_visualRoot;

        /// <summary>Material of one role, or null when the hero has not been built yet.</summary>
        public Material GetMaterial(HeroMaterialRole role)
        {
            return m_materials != null ? m_materials[role] : null;
        }

        private void Awake()
        {
            BuildIfNeeded();
        }

#if UNITY_EDITOR
        // Also build in edit mode so the scene preview shows the hero without entering Play.
        private void OnEnable()
        {
            // In edit mode OnEnable is called often; guard against duplicate builds.
            if (!Application.isPlaying)
            {
                BuildIfNeeded();
                HidePlaceholderIfNeeded();
            }
        }
#endif

        private void BuildIfNeeded()
        {
            if (m_built && m_visualRoot != null)
                return;

            // Prevent duplicate visual root (for example when the scene was saved from edit mode,
            // where the hierarchy already exists).
            Transform existing = transform.Find(VisualRootName);
            if (existing != null)
            {
                m_visualRoot = existing;
                EnsureMaterials(); // keep the role set valid for diagnostics even when nothing is rebuilt
                m_built = true;
                HidePlaceholderIfNeeded();
                return;
            }

            HidePlaceholderIfNeeded();
            EnsureMaterials();
            CreateVisualHierarchy();
            m_built = true;
        }

        private void HidePlaceholderIfNeeded()
        {
            // The original placeholder is a MeshFilter + MeshRenderer directly on the Player.
            // We disable them so only the hero is visible, but keep them for reference.
            var mr = GetComponent<MeshRenderer>();
            if (mr != null)
                mr.enabled = false;
        }

        private void EnsureMaterials()
        {
            var sources = new Material[HeroMaterials.RoleCount];
            sources[(int)HeroMaterialRole.Skin] = skinMaterial;
            sources[(int)HeroMaterialRole.Hair] = hairMaterial;
            sources[(int)HeroMaterialRole.Suit] = suitMaterial;
            sources[(int)HeroMaterialRole.Accent] = accentMaterial;
            sources[(int)HeroMaterialRole.Boots] = bootsMaterial;
            sources[(int)HeroMaterialRole.Gloves] = glovesMaterial;
            sources[(int)HeroMaterialRole.Metal] = metalMaterial;

            int assigned = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    assigned++;
            }

            bool usedFallback;
            m_materials = HeroMaterials.Resolve(sources, out usedFallback);

            // Resolved materials are deliberately NOT written back into the serialized fields: a
            // procedurally generated material has no asset behind it, so storing it in the scene
            // would only create a fake reference that is null again on the next load.
            if (usedFallback && logMaterialResolution && !m_FallbackLogged)
            {
                m_FallbackLogged = true;
                Debug.Log("[HeroCharacterVisual] " + assigned + "/" + HeroMaterials.RoleCount +
                          " hero materials came from the Inspector or Resources/HeroMaterials; the rest were " +
                          "generated procedurally. Assign the authored set from " + HeroMaterials.AssetFolder +
                          " for full PBR fidelity.", this);
            }
        }

        private void CreateVisualHierarchy()
        {
            GameObject root = new GameObject(VisualRootName);
            root.layer = 8; // Player layer - ignored by camera collision
            m_visualRoot = root.transform;
            m_visualRoot.SetParent(transform, false);
            m_visualRoot.localPosition = new Vector3(0f, verticalOffset, 0f);
            m_visualRoot.localRotation = Quaternion.identity;
            m_visualRoot.localScale = Vector3.one;

            // ------------------------------------------------------------------
            // Lower body - feet & legs (feet soles at y=0)
            // ------------------------------------------------------------------
            // Boots (bone leather) - cubes, bottom at 0
            Part(PrimitiveType.Cube, "Foot_L", new Vector3(-0.13f, 0.06f, 0.05f), new Vector3(0.14f, 0.12f, 0.28f), Quaternion.identity, HeroMaterialRole.Boots);
            Part(PrimitiveType.Cube, "Foot_R", new Vector3(0.13f, 0.06f, 0.05f), new Vector3(0.14f, 0.12f, 0.28f), Quaternion.identity, HeroMaterialRole.Boots);

            // Boot cuffs (wrap around ankle)
            Part(PrimitiveType.Cube, "BootCuff_L", new Vector3(-0.13f, 0.18f, 0f), new Vector3(0.18f, 0.14f, 0.20f), Quaternion.identity, HeroMaterialRole.Boots);
            Part(PrimitiveType.Cube, "BootCuff_R", new Vector3(0.13f, 0.18f, 0f), new Vector3(0.18f, 0.14f, 0.20f), Quaternion.identity, HeroMaterialRole.Boots);

            // Brass straps holding the cuffs - the metal read at boot height
            Part(PrimitiveType.Cube, "BootStrap_L", new Vector3(-0.13f, 0.245f, 0f), new Vector3(0.19f, 0.022f, 0.21f), Quaternion.identity, HeroMaterialRole.Metal);
            Part(PrimitiveType.Cube, "BootStrap_R", new Vector3(0.13f, 0.245f, 0f), new Vector3(0.19f, 0.022f, 0.21f), Quaternion.identity, HeroMaterialRole.Metal);

            // Lower legs (shin) - capsules (primary costume)
            Part(PrimitiveType.Capsule, "Shin_L", new Vector3(-0.13f, 0.36f, 0f), new Vector3(0.18f, 0.20f, 0.18f), Quaternion.identity, HeroMaterialRole.Suit);
            Part(PrimitiveType.Capsule, "Shin_R", new Vector3(0.13f, 0.36f, 0f), new Vector3(0.18f, 0.20f, 0.18f), Quaternion.identity, HeroMaterialRole.Suit);

            // Upper legs (thigh) - capsules
            Part(PrimitiveType.Capsule, "Thigh_L", new Vector3(-0.13f, 0.72f, 0f), new Vector3(0.22f, 0.20f, 0.22f), Quaternion.identity, HeroMaterialRole.Suit);
            Part(PrimitiveType.Capsule, "Thigh_R", new Vector3(0.13f, 0.72f, 0f), new Vector3(0.22f, 0.20f, 0.22f), Quaternion.identity, HeroMaterialRole.Suit);

            // Hips / pelvis
            Part(PrimitiveType.Cube, "Hips", new Vector3(0f, 0.97f, 0f), new Vector3(0.42f, 0.10f, 0.24f), Quaternion.identity, HeroMaterialRole.Suit);

            // Costume trim down the hip sides (secondary costume)
            Part(PrimitiveType.Cube, "HipAccent_L", new Vector3(-0.19f, 0.97f, 0.02f), new Vector3(0.04f, 0.10f, 0.25f), Quaternion.identity, HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "HipAccent_R", new Vector3(0.19f, 0.97f, 0.02f), new Vector3(0.04f, 0.10f, 0.25f), Quaternion.identity, HeroMaterialRole.Accent);

            // ------------------------------------------------------------------
            // Torso
            // ------------------------------------------------------------------
            // Lower torso / abdomen
            Part(PrimitiveType.Cube, "Abdomen", new Vector3(0f, 1.13f, 0f), new Vector3(0.36f, 0.22f, 0.22f), Quaternion.identity, HeroMaterialRole.Suit);
            // Chest (broader)
            Part(PrimitiveType.Cube, "Chest", new Vector3(0f, 1.37f, 0f), new Vector3(0.52f, 0.26f, 0.28f), Quaternion.identity, HeroMaterialRole.Suit);

            // Shoulder pads (angular, part of silhouette)
            Part(PrimitiveType.Cube, "Shoulder_L", new Vector3(-0.31f, 1.50f, 0f), new Vector3(0.16f, 0.10f, 0.22f), Quaternion.Euler(0f, 0f, -6f), HeroMaterialRole.Suit);
            Part(PrimitiveType.Cube, "Shoulder_R", new Vector3(0.31f, 1.50f, 0f), new Vector3(0.16f, 0.10f, 0.22f), Quaternion.Euler(0f, 0f, 6f), HeroMaterialRole.Suit);
            // Shoulder accent strips (secondary costume)
            Part(PrimitiveType.Cube, "ShoulderAccent_L", new Vector3(-0.31f, 1.52f, 0.02f), new Vector3(0.14f, 0.03f, 0.23f), Quaternion.identity, HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "ShoulderAccent_R", new Vector3(0.31f, 1.52f, 0.02f), new Vector3(0.14f, 0.03f, 0.23f), Quaternion.identity, HeroMaterialRole.Accent);

            // Belt - brass (metal role)
            Part(PrimitiveType.Cube, "Belt", new Vector3(0f, 1.02f, 0f), new Vector3(0.44f, 0.07f, 0.26f), Quaternion.identity, HeroMaterialRole.Metal);
            Part(PrimitiveType.Cube, "BeltBuckle", new Vector3(0f, 1.02f, 0.15f), new Vector3(0.12f, 0.07f, 0.03f), Quaternion.identity, HeroMaterialRole.Metal);
            // Buckle inlay (cyan - secondary costume)
            Part(PrimitiveType.Cube, "BeltGem", new Vector3(0f, 1.02f, 0.17f), new Vector3(0.06f, 0.04f, 0.015f), Quaternion.identity, HeroMaterialRole.Accent);

            // Chest emblem - distinctive inverted chevron + diamond (original design)
            Part(PrimitiveType.Cube, "Emblem_Ring", new Vector3(0f, 1.38f, 0.16f), new Vector3(0.20f, 0.20f, 0.02f), Quaternion.Euler(0f, 0f, 45f), HeroMaterialRole.Metal);
            Part(PrimitiveType.Cube, "Emblem_Inlay", new Vector3(0f, 1.38f, 0.175f), new Vector3(0.11f, 0.11f, 0.02f), Quaternion.Euler(0f, 0f, 45f), HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "Emblem_Core", new Vector3(0f, 1.38f, 0.19f), new Vector3(0.04f, 0.04f, 0.015f), Quaternion.Euler(0f, 0f, 45f), HeroMaterialRole.Suit);

            // ------------------------------------------------------------------
            // Arms
            // ------------------------------------------------------------------
            Part(PrimitiveType.Capsule, "UpperArm_L", new Vector3(-0.31f, 1.32f, 0f), new Vector3(0.16f, 0.15f, 0.16f), Quaternion.identity, HeroMaterialRole.Suit);
            Part(PrimitiveType.Capsule, "UpperArm_R", new Vector3(0.31f, 1.32f, 0f), new Vector3(0.16f, 0.15f, 0.16f), Quaternion.identity, HeroMaterialRole.Suit);
            Part(PrimitiveType.Capsule, "LowerArm_L", new Vector3(-0.31f, 1.03f, 0f), new Vector3(0.14f, 0.15f, 0.14f), Quaternion.identity, HeroMaterialRole.Suit);
            Part(PrimitiveType.Capsule, "LowerArm_R", new Vector3(0.31f, 1.03f, 0f), new Vector3(0.14f, 0.15f, 0.14f), Quaternion.identity, HeroMaterialRole.Suit);

            // Gloves (hands) - light grip leather, deliberately not the same material as the boots
            Part(PrimitiveType.Cube, "Hand_L", new Vector3(-0.31f, 0.80f, 0f), new Vector3(0.11f, 0.15f, 0.11f), Quaternion.identity, HeroMaterialRole.Gloves);
            Part(PrimitiveType.Cube, "Hand_R", new Vector3(0.31f, 0.80f, 0f), new Vector3(0.11f, 0.15f, 0.11f), Quaternion.identity, HeroMaterialRole.Gloves);
            // Knuckle plates (raised pads on the front of each glove)
            Part(PrimitiveType.Cube, "Knuckle_L", new Vector3(-0.31f, 0.815f, 0.062f), new Vector3(0.10f, 0.09f, 0.03f), Quaternion.identity, HeroMaterialRole.Gloves);
            Part(PrimitiveType.Cube, "Knuckle_R", new Vector3(0.31f, 0.815f, 0.062f), new Vector3(0.10f, 0.09f, 0.03f), Quaternion.identity, HeroMaterialRole.Gloves);
            // Glove cuff trim (secondary costume)
            Part(PrimitiveType.Cube, "GloveCuff_L", new Vector3(-0.31f, 0.88f, 0f), new Vector3(0.13f, 0.04f, 0.13f), Quaternion.identity, HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "GloveCuff_R", new Vector3(0.31f, 0.88f, 0f), new Vector3(0.13f, 0.04f, 0.13f), Quaternion.identity, HeroMaterialRole.Accent);

            // ------------------------------------------------------------------
            // Head & Neck
            // ------------------------------------------------------------------
            Part(PrimitiveType.Cylinder, "Neck", new Vector3(0f, 1.53f, 0f), new Vector3(0.16f, 0.03f, 0.16f), Quaternion.identity, HeroMaterialRole.Skin);
            Part(PrimitiveType.Sphere, "Head", new Vector3(0f, 1.70f, 0f), new Vector3(0.28f, 0.32f, 0.28f), Quaternion.identity, HeroMaterialRole.Skin);
            Part(PrimitiveType.Cube, "Jaw", new Vector3(0f, 1.62f, 0.06f), new Vector3(0.18f, 0.10f, 0.10f), Quaternion.identity, HeroMaterialRole.Skin);
            Part(PrimitiveType.Cube, "Ear_L", new Vector3(-0.15f, 1.70f, 0f), new Vector3(0.03f, 0.07f, 0.04f), Quaternion.identity, HeroMaterialRole.Skin);
            Part(PrimitiveType.Cube, "Ear_R", new Vector3(0.15f, 1.70f, 0f), new Vector3(0.03f, 0.07f, 0.04f), Quaternion.identity, HeroMaterialRole.Skin);

            // Visor / Mask - cyan band across the eyes (secondary costume) with brass bolts
            Part(PrimitiveType.Cube, "Visor", new Vector3(0f, 1.72f, 0.15f), new Vector3(0.24f, 0.06f, 0.04f), Quaternion.identity, HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "VisorWing_L", new Vector3(-0.13f, 1.72f, 0.14f), new Vector3(0.06f, 0.04f, 0.03f), Quaternion.Euler(0f, 25f, 0f), HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "VisorWing_R", new Vector3(0.13f, 1.72f, 0.14f), new Vector3(0.06f, 0.04f, 0.03f), Quaternion.Euler(0f, -25f, 0f), HeroMaterialRole.Accent);
            Part(PrimitiveType.Cube, "VisorBolt_L", new Vector3(-0.16f, 1.715f, 0.115f), new Vector3(0.03f, 0.03f, 0.02f), Quaternion.Euler(0f, 25f, 0f), HeroMaterialRole.Metal);
            Part(PrimitiveType.Cube, "VisorBolt_R", new Vector3(0.16f, 1.715f, 0.115f), new Vector3(0.03f, 0.03f, 0.02f), Quaternion.Euler(0f, -25f, 0f), HeroMaterialRole.Metal);

            // Dark eye slits reading through the visor, and brows above it (hair material)
            Part(PrimitiveType.Cube, "EyeSlit_L", new Vector3(-0.065f, 1.724f, 0.172f), new Vector3(0.055f, 0.022f, 0.012f), Quaternion.identity, HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "EyeSlit_R", new Vector3(0.065f, 1.724f, 0.172f), new Vector3(0.055f, 0.022f, 0.012f), Quaternion.identity, HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Brow_L", new Vector3(-0.07f, 1.772f, 0.144f), new Vector3(0.062f, 0.016f, 0.02f), Quaternion.Euler(-12f, 0f, 0f), HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Brow_R", new Vector3(0.07f, 1.772f, 0.144f), new Vector3(0.062f, 0.016f, 0.02f), Quaternion.Euler(-12f, 0f, 0f), HeroMaterialRole.Hair);

            // Hair - original style: cropped fade sides, elevated angular crest peak forward
            Part(PrimitiveType.Cube, "Hair_Base", new Vector3(0f, 1.84f, -0.02f), new Vector3(0.30f, 0.10f, 0.30f), Quaternion.identity, HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Hair_Crest", new Vector3(0f, 1.90f, 0.04f), new Vector3(0.20f, 0.12f, 0.26f), Quaternion.Euler(-8f, 0f, 0f), HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Hair_Peak", new Vector3(0f, 1.88f, 0.16f), new Vector3(0.14f, 0.10f, 0.14f), Quaternion.Euler(22f, 0f, 0f), HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Hair_Back", new Vector3(0f, 1.86f, -0.14f), new Vector3(0.26f, 0.10f, 0.12f), Quaternion.identity, HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Hair_Side_L", new Vector3(-0.16f, 1.78f, 0f), new Vector3(0.03f, 0.12f, 0.20f), Quaternion.identity, HeroMaterialRole.Hair);
            Part(PrimitiveType.Cube, "Hair_Side_R", new Vector3(0.16f, 1.78f, 0f), new Vector3(0.03f, 0.12f, 0.20f), Quaternion.identity, HeroMaterialRole.Hair);

            // ------------------------------------------------------------------
            // Cape - short half-cape from shoulders to mid-back (adds silhouette without heavy cloth sim)
            // ------------------------------------------------------------------
            Part(PrimitiveType.Cube, "Cape", new Vector3(0f, 1.18f, -0.17f), new Vector3(0.46f, 0.50f, 0.02f), Quaternion.Euler(4f, 0f, 0f), HeroMaterialRole.Suit);
            Part(PrimitiveType.Cube, "CapeCollar", new Vector3(0f, 1.44f, -0.16f), new Vector3(0.42f, 0.04f, 0.025f), Quaternion.identity, HeroMaterialRole.Accent);
        }

        // ------------------------------------------------------------------
        // Primitive helpers (optimized: remove colliders, set material, shadows)
        // ------------------------------------------------------------------
        private GameObject Part(PrimitiveType type, string name, Vector3 localPos, Vector3 localScale,
                                Quaternion localRot, HeroMaterialRole role)
        {
            Material material = m_materials != null ? m_materials[role] : null;
            if (material == null && m_materials != null)
                material = m_materials[HeroMaterialRole.Suit]; // never leave a renderer unassigned

            GameObject go = GameObject.CreatePrimitive(type);
            CleanupPrimitive(go, name, localPos, localScale, localRot, material);
            EnsureTangents(go);
            return go;
        }

        private void CleanupPrimitive(GameObject go, string name, Vector3 localPos, Vector3 localScale,
                                      Quaternion localRot, Material mat)
        {
            go.name = name;
            // Remove all colliders (CapsuleCollider, BoxCollider, SphereCollider, MeshCollider)
            foreach (var col in go.GetComponents<Collider>())
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(col);
                else
                    Destroy(col);
#else
                Destroy(col);
#endif
            }

            go.transform.SetParent(m_visualRoot, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.transform.localRotation = localRot;

            var rend = go.GetComponent<MeshRenderer>();
            if (rend != null)
            {
                rend.sharedMaterial = mat;
                rend.shadowCastingMode = ShadowCastingMode.On;
                rend.receiveShadows = true;
                rend.allowOcclusionWhenDynamic = true;
                rend.lightProbeUsage = LightProbeUsage.BlendProbes;
                rend.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                // Enable SRP batcher compatibility (no per-object motion vectors needed)
                rend.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            }

            // Put hero meshes on Player layer (8) so camera collision (Default/Environment/Ground only)
            // ignores the hero itself. Otherwise the third-person camera's SphereCast would hit the
            // hero's own body and snap inward.
            go.layer = 8;
        }

        /// <summary>
        /// Normal maps are sampled in tangent space, and the built-in primitive meshes ship without
        /// tangents. Without them URP falls back to a derived frame, which flattens the detail on
        /// curved parts, so the (per-instance) primitive mesh gets tangents once at build time.
        /// </summary>
        private static void EnsureTangents(GameObject go)
        {
            var filter = go.GetComponent<MeshFilter>();
            if (filter == null)
                return;

            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
                return;

            Vector4[] existing = mesh.tangents;
            if (existing != null && existing.Length == mesh.vertexCount)
                return;

            mesh.RecalculateTangents();
        }
    }
}
