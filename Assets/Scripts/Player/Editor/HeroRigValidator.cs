using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MobileGame.Player.EditorTools
{
    /// <summary>
    /// Editor-side checks and helpers for the hero rig.
    ///
    /// <para>
    /// The Play Mode suite (<see cref="HeroRigTest"/>) proves the rig deforms correctly at runtime and
    /// <c>Tools/HeroRigVerification</c> measures the geometry headlessly; this window checks the
    /// <i>setup</i> in the open scenes without entering Play Mode: is the skeleton complete, is the
    /// humanoid avatar valid, is every hero mesh bound to a bone, and is the Animator configured so it
    /// cannot fight the <c>CharacterController</c>.
    /// </para>
    ///
    /// Menus: <b>Tools/MobileGame/Hero Rig/...</b>
    /// </summary>
    public static class HeroRigValidator
    {
        [MenuItem("Tools/MobileGame/Hero Rig/Validate Rig Setup", priority = 0)]
        public static void Validate()
        {
            var problems = new List<string>();
            var report = new StringBuilder();
            int heroes = 0;

            report.AppendLine("[HeroRig] Rig validation");

            foreach (Scene scene in GetOpenScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (HeroRig rig in root.GetComponentsInChildren<HeroRig>(true))
                    {
                        heroes++;
                        ValidateRig(rig, scene.name, problems, report);
                    }
                }
            }

            if (heroes == 0)
                report.AppendLine("  No HeroRig in the open scenes.");

            report.AppendLine("  Joint table: " + CountJoints() + " bones declared, " +
                              CountMapped() + " mapped to the humanoid avatar");

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

        [MenuItem("Tools/MobileGame/Hero Rig/Rebuild Rig In Open Scenes", priority = 1)]
        public static void RebuildInOpenScenes()
        {
            int rebuilt = 0;
            foreach (Scene scene in GetOpenScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (HeroRig rig in root.GetComponentsInChildren<HeroRig>(true))
                    {
                        rig.Rebuild();
                        rebuilt++;
                        EditorUtility.SetDirty(rig);
                    }
                }
            }

            Debug.Log("[HeroRig] Rebuilt " + rebuilt + " rig(s). The skeleton is generated at runtime and " +
                      "is not meant to be saved into the scene.");
        }

        [MenuItem("Tools/MobileGame/Hero Rig/Apply Rest Pose In Open Scenes", priority = 2)]
        public static void ApplyRestPoseInOpenScenes()
        {
            int posed = 0;
            foreach (Scene scene in GetOpenScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (HeroRig rig in root.GetComponentsInChildren<HeroRig>(true))
                    {
                        if (!rig.IsBuilt)
                            continue;

                        rig.ApplyRestPose();
                        posed++;
                    }
                }
            }

            Debug.Log("[HeroRig] Applied the rest pose to " + posed + " rig(s).");
        }

        // ------------------------------------------------------------------

        private static void ValidateRig(HeroRig rig, string sceneName, List<string> problems, StringBuilder report)
        {
            if (!rig.IsBuilt)
            {
                rig.BuildIfNeeded();
                if (!rig.IsBuilt)
                {
                    problems.Add(sceneName + ": '" + rig.name + "' could not build the rig (is the hero visual built?)");
                    return;
                }
            }

            report.AppendLine("  " + sceneName + ": " + rig.name + " -> " + rig.RigRoot.name + " with " +
                              rig.BoneCount + " bones, " + rig.BoundPartCount + " meshes bound, avatar " +
                              rig.AvatarKindUsed);

            if (rig.BoneCount != 52)
                problems.Add(sceneName + ": expected 52 bones, found " + rig.BoneCount);

            var missing = new List<string>();
            foreach (HeroJoint joint in HeroRig.AllJoints)
            {
                HeroRigJoint info;
                HeroRig.TryGetJointInfo(joint, out info);
                Transform bone = rig.GetBone(joint);
                if (bone == null)
                {
                    missing.Add(info.BoneName);
                    continue;
                }

                if (bone.name != info.BoneName)
                    problems.Add(sceneName + ": bone " + joint + " is named '" + bone.name + "'");
                if (bone.localScale != Vector3.one)
                    problems.Add(sceneName + ": bone " + info.BoneName + " has scale " + bone.localScale);
            }

            if (missing.Count > 0)
                problems.Add(sceneName + ": missing bones: " + string.Join(", ", missing.ToArray()));

            Avatar avatar = rig.Avatar;
            if (avatar == null || !avatar.isValid)
                problems.Add(sceneName + ": the avatar is missing or invalid");
            else if (!avatar.isHuman)
                problems.Add(sceneName + ": the avatar is not humanoid (retargeting and IK unavailable)");

            Animator animator = rig.Animator;
            if (animator == null)
                problems.Add(sceneName + ": the skeleton root has no Animator");
            else
            {
                if (animator.transform != rig.RigRoot)
                    problems.Add(sceneName + ": the Animator is not on the skeleton root");
                if (animator.applyRootMotion)
                    problems.Add(sceneName + ": root motion is on - it would fight the CharacterController");
                if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    problems.Add(sceneName + ": culling is " + animator.cullingMode +
                                 ", the skeleton stops updating off screen");

                RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                if (controller == null)
                {
                    problems.Add(sceneName + ": the hero locomotion Animator Controller is missing");
                }
                else
                {
                    bool hasIdleClip = false;
                    bool hasWalkClip = false;
                    bool hasUnexpectedClip = controller.animationClips.Length != 2;
                    foreach (AnimationClip clip in controller.animationClips)
                    {
                        if (clip.name == "Hero_Idle")
                            hasIdleClip = true;
                        else if (clip.name == "Hero_Walk")
                            hasWalkClip = true;
                        else
                            hasUnexpectedClip = true;
                    }

                    if (!hasIdleClip || !hasWalkClip || hasUnexpectedClip)
                        problems.Add(sceneName + ": the Animator must contain Hero_Idle and Hero_Walk only (no running or combat clips)");
                }

                bool hasMovingParameter = false;
                bool hasWalkCycleRateParameter = false;
                foreach (AnimatorControllerParameter parameter in animator.parameters)
                {
                    if (parameter.name == ThirdPersonPlayerController.IsMovingAnimatorParameter &&
                        parameter.type == AnimatorControllerParameterType.Bool)
                        hasMovingParameter = true;
                    if (parameter.name == ThirdPersonPlayerController.WalkCycleRateAnimatorParameter &&
                        parameter.type == AnimatorControllerParameterType.Float)
                        hasWalkCycleRateParameter = true;
                }
                if (!hasMovingParameter)
                    problems.Add(sceneName + ": the Animator is missing its IsMoving bool parameter");
                if (!hasWalkCycleRateParameter)
                    problems.Add(sceneName + ": the Animator is missing its WalkCycleRate float parameter");
            }

            if (rig.GetComponent<CharacterController>() == null)
                problems.Add(sceneName + ": HeroRig is not on the GameObject that carries the CharacterController");

            if (rig.RigRoot != null && rig.RigRoot.GetComponent<HeroWalkFootPlanting>() == null)
                problems.Add(sceneName + ": the Animator rig root has no grounded walk foot-planting driver");

            if (rig.UnboundParts.Count > 0)
                problems.Add(sceneName + ": unbound meshes: " + string.Join(", ", rig.UnboundParts.ToArray()));
        }

        private static IEnumerable<Scene> GetOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                yield return SceneManager.GetSceneAt(i);
        }

        private static int CountJoints()
        {
            int count = 0;
            foreach (HeroJoint joint in HeroRig.AllJoints)
            {
                HeroRigJoint info;
                if (HeroRig.TryGetJointInfo(joint, out info))
                    count++;
            }

            return count;
        }

        private static int CountMapped()
        {
            int count = 0;
            foreach (HeroJoint joint in HeroRig.AllJoints)
            {
                HeroRigJoint info;
                if (HeroRig.TryGetJointInfo(joint, out info) && info.IsMapped)
                    count++;
            }

            return count;
        }
    }
}
