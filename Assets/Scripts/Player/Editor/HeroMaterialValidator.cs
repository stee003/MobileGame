using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MobileGame.Player.EditorTools
{
    /// <summary>
    /// Editor-side checks and helpers for the hero material pass.
    ///
    /// <para>
    /// The Play Mode suite (<see cref="HeroMaterialTest"/>) proves the materials work at runtime; this
    /// window proves the *assets* are well formed: every role has its three maps, the import settings
    /// are right per map type, the smoothness workflow is authored correctly, and the hero instances in
    /// the open scenes reference all seven roles.
    /// </para>
    ///
    /// Menus: <b>Tools/MobileGame/Hero Materials/...</b>
    /// </summary>
    public static class HeroMaterialValidator
    {
        [MenuItem("Tools/MobileGame/Hero Materials/Validate PBR Setup", priority = 0)]
        public static void Validate()
        {
            var problems = new List<string>();
            var summary = new StringBuilder();

            // 1. The seven authored materials.
            var materials = new Material[HeroMaterials.RoleCount];
            for (int i = 0; i < HeroMaterials.RoleCount; i++)
            {
                HeroMaterialRole role = (HeroMaterialRole)i;
                string path = HeroMaterials.AssetPath(role);
                materials[i] = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (materials[i] == null)
                {
                    problems.Add("missing material asset: " + path);
                    continue;
                }

                ValidateMaterial(role, materials[i], path, problems);
            }

            // 2. Texture import settings.
            ValidateTextureSettings(problems);

            // 3. Hero instances in the currently open scenes.
            int heroes = 0;
            int fullyAssigned = 0;
            foreach (Scene scene in GetOpenScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (HeroCharacterVisual visual in root.GetComponentsInChildren<HeroCharacterVisual>(true))
                    {
                        heroes++;
                        var serialized = new SerializedObject(visual);
                        var missing = new List<string>();
                        for (int i = 0; i < HeroMaterials.RoleCount; i++)
                        {
                            SerializedProperty property = serialized.FindProperty(FieldName((HeroMaterialRole)i));
                            if (property == null || property.objectReferenceValue == null)
                                missing.Add(HeroMaterials.RoleName((HeroMaterialRole)i));
                        }

                        if (missing.Count == 0)
                        {
                            fullyAssigned++;
                            summary.Append($"{scene.name}:{visual.name}=7/7  ");
                        }
                        else
                        {
                            problems.Add($"hero '{visual.name}' in scene '{scene.name}' has no material assigned for: " +
                                         string.Join(", ", missing));
                        }
                    }
                }
            }

            if (heroes == 0)
                summary.Append("no HeroCharacterVisual in the open scenes  ");

            // 4. Report.
            var report = new StringBuilder();
            report.AppendLine("[HeroMaterials] PBR validation");
            report.AppendLine("  Materials: " + string.Join(", ", System.Array.ConvertAll(materials,
                m => m != null ? m.name : "<missing>")));
            report.AppendLine($"  Heroes in open scenes: {heroes} ({fullyAssigned} fully assigned) {summary}".TrimEnd());
            report.AppendLine("  Texture folder: Assets/Art/Textures/Hero (" + CountTextures() + " maps)");

            if (problems.Count == 0)
            {
                report.AppendLine("  RESULT: PASSED");
                Debug.Log(report.ToString());
                return;
            }

            report.AppendLine("  RESULT: FAILED (" + problems.Count + " problem(s))");
            foreach (string problem in problems)
                report.AppendLine("    - " + problem);

            Debug.LogError(report.ToString());
        }

        [MenuItem("Tools/MobileGame/Hero Materials/Assign Authored Materials To Open Scenes", priority = 1)]
        public static void AssignToOpenScenes()
        {
            int assigned = 0;
            foreach (Scene scene in GetOpenScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (HeroCharacterVisual visual in root.GetComponentsInChildren<HeroCharacterVisual>(true))
                    {
                        var serialized = new SerializedObject(visual);
                        for (int i = 0; i < HeroMaterials.RoleCount; i++)
                        {
                            HeroMaterialRole role = (HeroMaterialRole)i;
                            Material material = AssetDatabase.LoadAssetAtPath<Material>(HeroMaterials.AssetPath(role));
                            SerializedProperty property = serialized.FindProperty(FieldName(role));
                            if (property != null && material != null)
                                property.objectReferenceValue = material;
                        }

                        serialized.ApplyModifiedProperties();
                        EditorUtility.SetDirty(visual);
                        assigned++;
                    }
                }
            }

            if (assigned == 0)
            {
                Debug.LogWarning("[HeroMaterials] No HeroCharacterVisual found in the open scenes; nothing to assign.");
                return;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[HeroMaterials] Assigned the seven authored materials to {assigned} hero instance(s) in the open scenes.");
        }

        [MenuItem("Tools/MobileGame/Hero Materials/Select Hero Texture Folder", priority = 20)]
        public static void SelectTextures()
        {
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<Object>("Assets/Art/Textures/Hero");
        }

        // ------------------------------------------------------------------
        private static void ValidateMaterial(HeroMaterialRole role, Material material, string path, List<string> problems)
        {
            string name = HeroMaterials.RoleName(role);

            if (material.GetTexture("_BaseMap") == null && material.GetTexture("_MainTex") == null)
                problems.Add(name + ": no base colour map (" + path + ")");
            if (material.GetTexture("_BumpMap") == null)
                problems.Add(name + ": no normal map (" + path + ")");
            if (material.GetTexture("_MetallicGlossMap") == null)
                problems.Add(name + ": no metallic/smoothness mask (" + path + ")");
            if (material.GetTexture("_OcclusionMap") == null)
                problems.Add(name + ": no occlusion map (" + path + ")");

            if (!material.IsKeywordEnabled("_NORMALMAP"))
                problems.Add(name + ": _NORMALMAP keyword is off");
            if (!material.IsKeywordEnabled("_METALLICGLOSSMAP"))
                problems.Add(name + ": _METALLICGLOSSMAP keyword is off");
            if (!material.IsKeywordEnabled("_OCCLUSIONMAP"))
                problems.Add(name + ": _OCCLUSIONMAP keyword is off");

            if (Mathf.Abs(material.GetFloat("_Smoothness") - 1f) > 0.001f)
                problems.Add(name + ": _Smoothness must be 1 so the mask alpha stays authoritative");
            if (material.GetFloat("_SmoothnessTextureChannel") > 0.5f)
                problems.Add(name + ": smoothness source should be the metallic alpha channel");

            float metallic = material.GetFloat("_Metallic");
            if (role == HeroMaterialRole.Metal)
            {
                if (metallic < 0.5f)
                    problems.Add(name + ": the metallic-details role should be set up as a metal");
            }
            else if (metallic > 0.2f)
            {
                problems.Add(name + ": should be a dielectric but _Metallic=" + metallic.ToString("0.00"));
            }

            Color tint = material.GetColor("_BaseColor");
            if (tint.r + tint.g + tint.b < 0.02f)
                problems.Add(name + ": base colour tint is essentially black");
            if (tint.r + tint.g + tint.b > 2.99f)
                problems.Add(name + ": base colour tint is bleached white");

            if (material.shader == null || material.shader.name != "Universal Render Pipeline/Lit")
                problems.Add(name + ": shader is '" + (material.shader != null ? material.shader.name : "null") +
                             "' instead of Universal Render Pipeline/Lit");
        }

        private static void ValidateTextureSettings(List<string> problems)
        {
            string[] stems = { "Skin", "Hair", "Suit", "Accent", "Boots", "Gloves", "Metal" };
            foreach (string stem in stems)
            {
                CheckTexture("Assets/Art/Textures/Hero/T_Hero_" + stem + "_BaseColor.png", true, false, problems);
                CheckTexture("Assets/Art/Textures/Hero/T_Hero_" + stem + "_Normal.png", false, true, problems);
                CheckTexture("Assets/Art/Textures/Hero/T_Hero_" + stem + "_Mask.png", false, false, problems);
            }
        }

        private static void CheckTexture(string path, bool srgb, bool normalMap, List<string> problems)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                problems.Add("missing texture: " + path);
                return;
            }

            string fileName = System.IO.Path.GetFileName(path);
            if (importer.sRGBTexture != srgb)
                problems.Add(fileName + ": sRGB should be " + (srgb ? "on" : "off"));
            if (normalMap && importer.textureType != TextureImporterType.NormalMap)
                problems.Add(fileName + ": texture type should be NormalMap");
            if (!normalMap && importer.textureType != TextureImporterType.Default)
                problems.Add(fileName + ": texture type should be Default");
            if (importer.wrapMode != TextureWrapMode.Repeat)
                problems.Add(fileName + ": wrap mode should be Repeat (the maps are tileable)");
            if (!importer.mipmapEnabled)
                problems.Add(fileName + ": mipmaps should be enabled for the mobile target");
        }

        private static int CountTextures()
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Art/Textures/Hero" });
            return guids.Length;
        }

        private static string FieldName(HeroMaterialRole role)
        {
            switch (role)
            {
                case HeroMaterialRole.Skin: return "skinMaterial";
                case HeroMaterialRole.Hair: return "hairMaterial";
                case HeroMaterialRole.Suit: return "suitMaterial";
                case HeroMaterialRole.Accent: return "accentMaterial";
                case HeroMaterialRole.Boots: return "bootsMaterial";
                case HeroMaterialRole.Gloves: return "glovesMaterial";
                default: return "metalMaterial";
            }
        }

        private static IEnumerable<Scene> GetOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    yield return scene;
            }
        }
    }
}
