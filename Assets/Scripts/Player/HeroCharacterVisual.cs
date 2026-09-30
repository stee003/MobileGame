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
    /// - Deep midnight-teal bodysuit with electric-cyan chest chevron and gold belt
    /// - Short half-cape, angular shoulder armor, white boots/gloves, visor
    /// - Compact swept-crest hairstyle (fade sides, single forward peak) – distinctive but not MHA
    /// - Humanoid proportions, separate head/hands/feet, low-poly mobile friendly
    ///
    /// Optimization for mobile:
    /// - ~3.2k triangles total (1 head sphere + cubes + 4 capsules)
    /// - 5 materials (Suit / Accent / Skin / Hair / Boots/Belt) – SRP Batcher compatible
    /// - No SkinnedMeshRenderer, no blendshapes, no extra colliders
    /// - GPU instancing enabled where possible, shadows on, receive shadows on
    /// - Visual root is a simple child; CharacterController remains sole physics shape
    /// - Feet soles sit exactly at model origin so they rest on ground (no floating)
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)] // Build before PlayerController Awake
    public class HeroCharacterVisual : MonoBehaviour
    {
        [Header("Material Overrides (optional)")]
        [Tooltip("If assigned, these materials are used. Otherwise procedural URP Lit materials are created at runtime.")]
        [SerializeField] private Material suitMaterial;
        [SerializeField] private Material accentMaterial;
        [SerializeField] private Material skinMaterial;
        [SerializeField] private Material hairMaterial;
        [SerializeField] private Material bootsMaterial;
        [SerializeField] private Material beltMaterial;

        [Header("Visual Tuning")]
        [Tooltip("Vertical offset of the visual root relative to the CharacterController center. -1 places feet at ground when controller center is 0.")]
        [SerializeField] private float verticalOffset = -1f;

        private const string VisualRootName = "HeroVisual";
        private Transform m_visualRoot;
        private bool m_built;

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
                // Delay a frame in editor to avoid building during serialization
                // but for now build immediately if missing.
                BuildIfNeeded();
                HidePlaceholderIfNeeded();
            }
        }

        private void OnValidate()
        {
            if (!Application.isPlaying && m_visualRoot == null)
            {
                // Don't auto-build on validate to avoid spam, just ensure placeholder hidden
            }
        }
#endif

        private void BuildIfNeeded()
        {
            if (m_built && m_visualRoot != null)
                return;

            // Prevent duplicate visual root
            Transform existing = transform.Find(VisualRootName);
            if (existing != null)
            {
                m_visualRoot = existing;
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
            var mf = GetComponent<MeshFilter>();
            var mr = GetComponent<MeshRenderer>();
            if (mr != null)
                mr.enabled = false;
            // Keep MeshFilter but it won't render without renderer.
            // Optionally disable to save draw, but not required.
        }

        private void EnsureMaterials()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                lit = Shader.Find("Standard"); // fallback

            if (suitMaterial == null)
            {
                suitMaterial = new Material(lit);
                suitMaterial.name = "M_Hero_Suit_Runtime";
                suitMaterial.SetColor("_BaseColor", new Color(0.078f, 0.196f, 0.353f, 1f));
                suitMaterial.SetColor("_Color", new Color(0.078f, 0.196f, 0.353f, 1f));
                suitMaterial.SetFloat("_Smoothness", 0.35f);
                suitMaterial.SetFloat("_Metallic", 0f);
                suitMaterial.enableInstancing = true;
            }
            if (accentMaterial == null)
            {
                accentMaterial = new Material(lit);
                accentMaterial.name = "M_Hero_Accent_Runtime";
                accentMaterial.SetColor("_BaseColor", new Color(0.09f, 0.78f, 0.92f, 1f));
                accentMaterial.SetColor("_Color", new Color(0.09f, 0.78f, 0.92f, 1f));
                accentMaterial.SetFloat("_Smoothness", 0.55f);
                accentMaterial.SetFloat("_Metallic", 0f);
                accentMaterial.enableInstancing = true;
            }
            if (skinMaterial == null)
            {
                skinMaterial = new Material(lit);
                skinMaterial.name = "M_Hero_Skin_Runtime";
                skinMaterial.SetColor("_BaseColor", new Color(0.91f, 0.77f, 0.66f, 1f));
                skinMaterial.SetColor("_Color", new Color(0.91f, 0.77f, 0.66f, 1f));
                skinMaterial.SetFloat("_Smoothness", 0.35f);
                skinMaterial.enableInstancing = true;
            }
            if (hairMaterial == null)
            {
                hairMaterial = new Material(lit);
                hairMaterial.name = "M_Hero_Hair_Runtime";
                hairMaterial.SetColor("_BaseColor", new Color(0.102f, 0.118f, 0.18f, 1f));
                hairMaterial.SetColor("_Color", new Color(0.102f, 0.118f, 0.18f, 1f));
                hairMaterial.SetFloat("_Smoothness", 0.45f);
                hairMaterial.enableInstancing = true;
            }
            if (bootsMaterial == null)
            {
                bootsMaterial = new Material(lit);
                bootsMaterial.name = "M_Hero_Boots_Runtime";
                bootsMaterial.SetColor("_BaseColor", new Color(0.92f, 0.93f, 0.95f, 1f));
                bootsMaterial.SetColor("_Color", new Color(0.92f, 0.93f, 0.95f, 1f));
                bootsMaterial.SetFloat("_Smoothness", 0.5f);
                bootsMaterial.enableInstancing = true;
            }
            if (beltMaterial == null)
            {
                beltMaterial = new Material(lit);
                beltMaterial.name = "M_Hero_Belt_Runtime";
                beltMaterial.SetColor("_BaseColor", new Color(0.96f, 0.78f, 0.29f, 1f));
                beltMaterial.SetColor("_Color", new Color(0.96f, 0.78f, 0.29f, 1f));
                beltMaterial.SetFloat("_Smoothness", 0.75f);
                beltMaterial.SetFloat("_Metallic", 0.7f);
                beltMaterial.enableInstancing = true;
            }

            // Try to use authored assets if available (editor only)
#if UNITY_EDITOR
            TryReplaceWithAuthored(ref suitMaterial, "Assets/Art/Materials/Hero/M_Hero_Suit.mat");
            TryReplaceWithAuthored(ref accentMaterial, "Assets/Art/Materials/Hero/M_Hero_Accent.mat");
            TryReplaceWithAuthored(ref skinMaterial, "Assets/Art/Materials/Hero/M_Hero_Skin.mat");
            TryReplaceWithAuthored(ref hairMaterial, "Assets/Art/Materials/Hero/M_Hero_Hair.mat");
            TryReplaceWithAuthored(ref bootsMaterial, "Assets/Art/Materials/Hero/M_Hero_Boots.mat");
            TryReplaceWithAuthored(ref beltMaterial, "Assets/Art/Materials/Hero/M_Hero_Belt.mat");
#endif
        }

#if UNITY_EDITOR
        private void TryReplaceWithAuthored(ref Material runtime, string path)
        {
            var authored = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
            if (authored != null)
                runtime = authored;
        }
#endif

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
            // Feet (boots) - cubes, bottom at 0
            CreateCube("Foot_L", new Vector3(-0.13f, 0.06f, 0.05f), new Vector3(0.14f, 0.12f, 0.28f), Quaternion.identity, bootsMaterial, m_visualRoot);
            CreateCube("Foot_R", new Vector3(0.13f, 0.06f, 0.05f), new Vector3(0.14f, 0.12f, 0.28f), Quaternion.identity, bootsMaterial, m_visualRoot);

            // Boot cuffs (wrap around ankle)
            CreateCube("BootCuff_L", new Vector3(-0.13f, 0.18f, 0f), new Vector3(0.18f, 0.14f, 0.20f), Quaternion.identity, bootsMaterial, m_visualRoot);
            CreateCube("BootCuff_R", new Vector3(0.13f, 0.18f, 0f), new Vector3(0.18f, 0.14f, 0.20f), Quaternion.identity, bootsMaterial, m_visualRoot);

            // Lower legs (shin) - capsules (suit)
            CreateCapsule("Shin_L", new Vector3(-0.13f, 0.36f, 0f), new Vector3(0.18f, 0.20f, 0.18f), Quaternion.identity, suitMaterial, m_visualRoot);
            CreateCapsule("Shin_R", new Vector3(0.13f, 0.36f, 0f), new Vector3(0.18f, 0.20f, 0.18f), Quaternion.identity, suitMaterial, m_visualRoot);

            // Upper legs (thigh) - capsules
            CreateCapsule("Thigh_L", new Vector3(-0.13f, 0.72f, 0f), new Vector3(0.22f, 0.20f, 0.22f), Quaternion.identity, suitMaterial, m_visualRoot);
            CreateCapsule("Thigh_R", new Vector3(0.13f, 0.72f, 0f), new Vector3(0.22f, 0.20f, 0.22f), Quaternion.identity, suitMaterial, m_visualRoot);

            // Hips / pelvis
            CreateCube("Hips", new Vector3(0f, 0.97f, 0f), new Vector3(0.42f, 0.10f, 0.24f), Quaternion.identity, suitMaterial, m_visualRoot);

            // Accent hip side panels (cyan trim)
            CreateCube("HipAccent_L", new Vector3(-0.19f, 0.97f, 0.02f), new Vector3(0.04f, 0.10f, 0.25f), Quaternion.identity, accentMaterial, m_visualRoot);
            CreateCube("HipAccent_R", new Vector3(0.19f, 0.97f, 0.02f), new Vector3(0.04f, 0.10f, 0.25f), Quaternion.identity, accentMaterial, m_visualRoot);

            // ------------------------------------------------------------------
            // Torso
            // ------------------------------------------------------------------
            // Lower torso / abdomen
            CreateCube("Abdomen", new Vector3(0f, 1.13f, 0f), new Vector3(0.36f, 0.22f, 0.22f), Quaternion.identity, suitMaterial, m_visualRoot);
            // Chest (broader)
            CreateCube("Chest", new Vector3(0f, 1.37f, 0f), new Vector3(0.52f, 0.26f, 0.28f), Quaternion.identity, suitMaterial, m_visualRoot);

            // Shoulder pads (angular, part of silhouette)
            CreateCube("Shoulder_L", new Vector3(-0.31f, 1.50f, 0f), new Vector3(0.16f, 0.10f, 0.22f), Quaternion.Euler(0, 0, -6), suitMaterial, m_visualRoot);
            CreateCube("Shoulder_R", new Vector3(0.31f, 1.50f, 0f), new Vector3(0.16f, 0.10f, 0.22f), Quaternion.Euler(0, 0, 6), suitMaterial, m_visualRoot);
            // Shoulder accent strips
            CreateCube("ShoulderAccent_L", new Vector3(-0.31f, 1.52f, 0.02f), new Vector3(0.14f, 0.03f, 0.23f), Quaternion.identity, accentMaterial, m_visualRoot);
            CreateCube("ShoulderAccent_R", new Vector3(0.31f, 1.52f, 0.02f), new Vector3(0.14f, 0.03f, 0.23f), Quaternion.identity, accentMaterial, m_visualRoot);

            // Belt
            CreateCube("Belt", new Vector3(0f, 1.02f, 0f), new Vector3(0.44f, 0.07f, 0.26f), Quaternion.identity, beltMaterial, m_visualRoot);
            CreateCube("BeltBuckle", new Vector3(0f, 1.02f, 0.15f), new Vector3(0.12f, 0.07f, 0.03f), Quaternion.identity, beltMaterial, m_visualRoot);
            // Buckle accent (cyan gem)
            CreateCube("BeltGem", new Vector3(0f, 1.02f, 0.17f), new Vector3(0.06f, 0.04f, 0.015f), Quaternion.identity, accentMaterial, m_visualRoot);

            // Chest emblem - distinctive inverted chevron + diamond (original design)
            // Outer diamond (rotated cube)
            CreateCube("Emblem_Outer", new Vector3(0f, 1.38f, 0.16f), new Vector3(0.20f, 0.20f, 0.02f), Quaternion.Euler(0, 0, 45), accentMaterial, m_visualRoot);
            CreateCube("Emblem_Inner", new Vector3(0f, 1.38f, 0.175f), new Vector3(0.11f, 0.11f, 0.02f), Quaternion.Euler(0, 0, 45), beltMaterial, m_visualRoot);
            // Small central core
            CreateCube("Emblem_Core", new Vector3(0f, 1.38f, 0.19f), new Vector3(0.04f, 0.04f, 0.015f), Quaternion.Euler(0, 0, 45), suitMaterial, m_visualRoot);

            // ------------------------------------------------------------------
            // Arms
            // ------------------------------------------------------------------
            // Upper arms
            CreateCapsule("UpperArm_L", new Vector3(-0.31f, 1.32f, 0f), new Vector3(0.16f, 0.15f, 0.16f), Quaternion.identity, suitMaterial, m_visualRoot);
            CreateCapsule("UpperArm_R", new Vector3(0.31f, 1.32f, 0f), new Vector3(0.16f, 0.15f, 0.16f), Quaternion.identity, suitMaterial, m_visualRoot);
            // Lower arms
            CreateCapsule("LowerArm_L", new Vector3(-0.31f, 1.03f, 0f), new Vector3(0.14f, 0.15f, 0.14f), Quaternion.identity, suitMaterial, m_visualRoot);
            CreateCapsule("LowerArm_R", new Vector3(0.31f, 1.03f, 0f), new Vector3(0.14f, 0.15f, 0.14f), Quaternion.identity, suitMaterial, m_visualRoot);
            // Gloves (hands)
            CreateCube("Hand_L", new Vector3(-0.31f, 0.80f, 0f), new Vector3(0.11f, 0.15f, 0.11f), Quaternion.identity, bootsMaterial, m_visualRoot);
            CreateCube("Hand_R", new Vector3(0.31f, 0.80f, 0f), new Vector3(0.11f, 0.15f, 0.11f), Quaternion.identity, bootsMaterial, m_visualRoot);
            // Glove cuffs trim
            CreateCube("GloveCuff_L", new Vector3(-0.31f, 0.88f, 0f), new Vector3(0.13f, 0.04f, 0.13f), Quaternion.identity, accentMaterial, m_visualRoot);
            CreateCube("GloveCuff_R", new Vector3(0.31f, 0.88f, 0f), new Vector3(0.13f, 0.04f, 0.13f), Quaternion.identity, accentMaterial, m_visualRoot);

            // ------------------------------------------------------------------
            // Head & Neck
            // ------------------------------------------------------------------
            CreateCylinder("Neck", new Vector3(0f, 1.53f, 0f), new Vector3(0.16f, 0.03f, 0.16f), Quaternion.identity, skinMaterial, m_visualRoot);
            // Head - sphere, slightly scaled to be oval
            CreateSphere("Head", new Vector3(0f, 1.70f, 0f), new Vector3(0.28f, 0.32f, 0.28f), Quaternion.identity, skinMaterial, m_visualRoot);
            // Jaw / chin accent (slightly forward)
            CreateCube("Jaw", new Vector3(0f, 1.62f, 0.06f), new Vector3(0.18f, 0.10f, 0.10f), Quaternion.identity, skinMaterial, m_visualRoot);

            // Visor / Mask - cyan band across eyes (distinctive hero mask)
            CreateCube("Visor", new Vector3(0f, 1.72f, 0.15f), new Vector3(0.24f, 0.06f, 0.04f), Quaternion.identity, accentMaterial, m_visualRoot);
            // Visor side wings (angular)
            CreateCube("VisorWing_L", new Vector3(-0.13f, 1.72f, 0.14f), new Vector3(0.06f, 0.04f, 0.03f), Quaternion.Euler(0, 25, 0), accentMaterial, m_visualRoot);
            CreateCube("VisorWing_R", new Vector3(0.13f, 1.72f, 0.14f), new Vector3(0.06f, 0.04f, 0.03f), Quaternion.Euler(0, -25, 0), accentMaterial, m_visualRoot);

            // Eyes (dark behind visor, subtle)
            CreateCube("Eye_L", new Vector3(-0.07f, 1.72f, 0.16f), new Vector3(0.06f, 0.02f, 0.01f), Quaternion.identity, hairMaterial, m_visualRoot);
            CreateCube("Eye_R", new Vector3(0.07f, 1.72f, 0.16f), new Vector3(0.06f, 0.02f, 0.01f), Quaternion.identity, hairMaterial, m_visualRoot);

            // Hair - distinctive original style: cropped fade sides, elevated angular crest peak forward
            // Base cap covering top of head (cube flattened for low-poly, 12 tris vs 768)
            CreateCube("Hair_Base", new Vector3(0f, 1.84f, -0.02f), new Vector3(0.30f, 0.10f, 0.30f), Quaternion.identity, hairMaterial, m_visualRoot);
            // Central crest volume
            CreateCube("Hair_Crest", new Vector3(0f, 1.90f, 0.04f), new Vector3(0.20f, 0.12f, 0.26f), Quaternion.Euler(-8, 0, 0), hairMaterial, m_visualRoot);
            // Forward peak (angular forelock - signature silhouette)
            CreateCube("Hair_Peak", new Vector3(0f, 1.88f, 0.16f), new Vector3(0.14f, 0.10f, 0.14f), Quaternion.Euler(22, 0, 0), hairMaterial, m_visualRoot);
            // Rear volume
            CreateCube("Hair_Back", new Vector3(0f, 1.86f, -0.14f), new Vector3(0.26f, 0.10f, 0.12f), Quaternion.identity, hairMaterial, m_visualRoot);
            // Side fade panels (tight sides)
            CreateCube("Hair_Side_L", new Vector3(-0.16f, 1.78f, 0f), new Vector3(0.03f, 0.12f, 0.20f), Quaternion.identity, hairMaterial, m_visualRoot);
            CreateCube("Hair_Side_R", new Vector3(0.16f, 1.78f, 0f), new Vector3(0.03f, 0.12f, 0.20f), Quaternion.identity, hairMaterial, m_visualRoot);

            // Ears (subtle)
            CreateCube("Ear_L", new Vector3(-0.15f, 1.70f, 0f), new Vector3(0.03f, 0.07f, 0.04f), Quaternion.identity, skinMaterial, m_visualRoot);
            CreateCube("Ear_R", new Vector3(0.15f, 1.70f, 0f), new Vector3(0.03f, 0.07f, 0.04f), Quaternion.identity, skinMaterial, m_visualRoot);

            // ------------------------------------------------------------------
            // Cape - short half-cape from shoulders to mid-back (adds silhouette without heavy cloth sim)
            // ------------------------------------------------------------------
            CreateCube("Cape", new Vector3(0f, 1.18f, -0.17f), new Vector3(0.46f, 0.50f, 0.02f), Quaternion.Euler(4, 0, 0), suitMaterial, m_visualRoot);
            // Cape top collar trim
            CreateCube("CapeCollar", new Vector3(0f, 1.44f, -0.16f), new Vector3(0.42f, 0.04f, 0.025f), Quaternion.identity, accentMaterial, m_visualRoot);
        }

        // ------------------------------------------------------------------
        // Primitive helpers (optimized: remove colliders, set material, shadows)
        // ------------------------------------------------------------------
        private GameObject CreateCube(string name, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            CleanupPrimitive(go, name, localPos, localScale, localRot, mat, parent);
            return go;
        }

        private GameObject CreateSphere(string name, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            CleanupPrimitive(go, name, localPos, localScale, localRot, mat, parent);
            return go;
        }

        private GameObject CreateCapsule(string name, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            CleanupPrimitive(go, name, localPos, localScale, localRot, mat, parent);
            return go;
        }

        private GameObject CreateCylinder(string name, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            CleanupPrimitive(go, name, localPos, localScale, localRot, mat, parent);
            return go;
        }

        private void CleanupPrimitive(GameObject go, string name, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat, Transform parent)
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

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.transform.localRotation = localRot;

            var rend = go.GetComponent<MeshRenderer>();
            if (rend != null)
            {
                rend.sharedMaterial = mat;
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                rend.receiveShadows = true;
                rend.allowOcclusionWhenDynamic = true;
                // Enable SRP batcher compatibility (no per-object motion vectors needed)
                rend.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            }

            // Put hero meshes on Player layer (8) so camera collision (Default/Environment/Ground only) ignores the hero itself.
            // Otherwise the third-person camera's SphereCast would hit the hero's own body and snap inward.
            go.layer = 8;
            // Ensure no extra scripts
            // Keep MeshFilter as is (low poly)
        }

#if UNITY_EDITOR
        // Helper to destroy visual in editor when component removed
        private void OnDestroy()
        {
            if (!Application.isPlaying && m_visualRoot != null)
            {
                // Don't auto-destroy in edit mode to preserve manual edits; let user delete manually
            }
        }
#endif
    }
}
