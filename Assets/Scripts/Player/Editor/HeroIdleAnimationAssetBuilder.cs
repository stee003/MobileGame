using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MobileGame.Player.EditorTools
{
    /// <summary>
    /// Authors the hero's small, original idle loop and its two-state Animator Controller.
    /// Assets are generated through Unity's animation APIs rather than hand-authored serialized
    /// curve data, which keeps the transform bindings valid across Unity editor versions.
    /// </summary>
    [InitializeOnLoad]
    public static class HeroIdleAnimationAssetBuilder
    {
        private const string Folder = "Assets/Resources/Animations/HeroIdle";
        private const string ClipPath = Folder + "/Hero_Idle.anim";
        private const string ControllerPath = Folder + "/Hero_Idle.controller";
        private const string MovingParameter = "IsMoving";
        private const float LoopDuration = 4.2f;

        static HeroIdleAnimationAssetBuilder()
        {
            EditorApplication.delayCall += EnsureAssets;
        }

        [MenuItem("Tools/MobileGame/Animations/Rebuild Hero Idle Assets", priority = 0)]
        public static void RebuildAssets()
        {
            EnsureFolder();
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            if (clip == null)
            {
                clip = new AnimationClip { name = "Hero_Idle", frameRate = 30f };
                AssetDatabase.CreateAsset(clip, ClipPath);
            }

            AuthorIdleClip(clip);

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            AuthorController(controller, clip);
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[HeroIdle] Rebuilt Hero_Idle.anim and the idle-only Hero_Idle.controller.");
        }

        private static void EnsureAssets()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += EnsureAssets;
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath) != null &&
                AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                return;

            RebuildAssets();
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder("Assets/Resources/Animations"))
                AssetDatabase.CreateFolder("Assets/Resources", "Animations");
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/Resources/Animations", "HeroIdle");
        }

        private static void AuthorIdleClip(AnimationClip clip)
        {
            clip.name = "Hero_Idle";
            clip.frameRate = 30f;
            clip.legacy = false;
            clip.wrapMode = WrapMode.Loop;

            // Curves are deliberately limited to small, in-place joint rotations and a few
            // millimetres of pelvis sway. No root translation, walk cycle, attack, or combat pose.
            var curves = new List<CurveSpec>
            {
                new CurveSpec("Hips", "m_LocalPosition.x", 0.006f, 0.00f),
                new CurveSpec("Hips", "localEulerAnglesRaw.x", 0.35f, 0.20f),
                new CurveSpec("Hips", "localEulerAnglesRaw.y", 0.35f, 0.85f),
                new CurveSpec("Hips", "localEulerAnglesRaw.z", 1.00f, 0.00f),

                // Slow chest expansion and gentle counter-motion through the neck and head.
                new CurveSpec("Spine", "localEulerAnglesRaw.x", 0.60f, -0.55f),
                new CurveSpec("Chest", "localEulerAnglesRaw.x", 1.10f, -0.35f),
                new CurveSpec("Chest", "localEulerAnglesRaw.y", 0.22f, 0.50f),
                new CurveSpec("Chest", "localEulerAnglesRaw.z", 0.28f, 0.20f),
                new CurveSpec("Neck", "localEulerAnglesRaw.x", 0.22f, 2.30f),
                new CurveSpec("Head", "localEulerAnglesRaw.x", 0.26f, 2.45f),
                new CurveSpec("Head", "localEulerAnglesRaw.y", 0.18f, 1.20f),

                // Asymmetric shoulder, elbow and wrist drift gives the hands a living, relaxed
                // silhouette without turning the stance into a gesture.
                new CurveSpec("LeftUpperArm", "localEulerAnglesRaw.x", 0.65f, 0.10f),
                new CurveSpec("LeftUpperArm", "localEulerAnglesRaw.z", 1.25f, 0.35f),
                new CurveSpec("LeftLowerArm", "localEulerAnglesRaw.x", 0.55f, 0.70f, -3.0f),
                new CurveSpec("LeftHand", "localEulerAnglesRaw.x", 1.00f, 1.30f),
                new CurveSpec("LeftHand", "localEulerAnglesRaw.y", 0.70f, 0.40f),
                new CurveSpec("RightUpperArm", "localEulerAnglesRaw.x", 0.55f, 2.35f),
                new CurveSpec("RightUpperArm", "localEulerAnglesRaw.z", 1.10f, 2.85f),
                new CurveSpec("RightLowerArm", "localEulerAnglesRaw.x", 0.48f, 3.00f, -3.5f),
                new CurveSpec("RightHand", "localEulerAnglesRaw.x", 0.85f, 3.65f),
                new CurveSpec("RightHand", "localEulerAnglesRaw.y", 0.65f, 2.70f),

                // Counterbalance through the upper legs is barely perceptible; the feet remain
                // planted and the capsule/root never moves as part of this animation.
                new CurveSpec("LeftUpperLeg", "localEulerAnglesRaw.z", 0.45f, 0.45f),
                new CurveSpec("RightUpperLeg", "localEulerAnglesRaw.z", 0.45f, 3.59f),
            };

            foreach (CurveSpec spec in curves)
            {
                AnimationCurve curve = CreateLoopCurve(spec);
                EditorCurveBinding binding = EditorCurveBinding.FloatCurve(
                    spec.Path, typeof(Transform), spec.Property);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            settings.loopBlend = true;
            settings.loopBlendOrientation = false;
            settings.loopBlendPositionY = false;
            settings.loopBlendPositionXZ = false;
            settings.keepOriginalOrientation = false;
            settings.keepOriginalPositionY = false;
            settings.keepOriginalPositionXZ = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        private static AnimationCurve CreateLoopCurve(CurveSpec spec)
        {
            const int intervals = 12;
            float omega = Mathf.PI * 2f / LoopDuration;
            var keys = new Keyframe[intervals + 1];
            for (int i = 0; i <= intervals; i++)
            {
                float time = LoopDuration * i / intervals;
                float wave = Mathf.Sin(omega * time + spec.Phase);
                float value = spec.Bias + spec.Amplitude * wave;
                float slope = spec.Amplitude * omega * Mathf.Cos(omega * time + spec.Phase);
                keys[i] = new Keyframe(time, value, slope, slope);
            }

            // Identical endpoint values and tangents make the loop seam continuous.
            keys[intervals].value = keys[0].value;
            keys[intervals].inTangent = keys[0].inTangent;
            keys[intervals].outTangent = keys[0].outTangent;
            var curve = new AnimationCurve(keys);
            curve.preWrapMode = WrapMode.Loop;
            curve.postWrapMode = WrapMode.Loop;
            return curve;
        }

        private static void AuthorController(AnimatorController controller, AnimationClip clip)
        {
            controller.name = "Hero_Idle";
            controller.parameters = Array.Empty<AnimatorControllerParameter>();
            controller.AddParameter(MovingParameter, AnimatorControllerParameterType.Bool);

            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
                stateMachine.RemoveState(states[i].state);

            AnimatorState idle = stateMachine.AddState("Idle");
            idle.motion = clip;
            idle.writeDefaultValues = true;

            // A neutral state exists only to exit idle while the player is in motion. It has no
            // motion assigned: this pass intentionally creates no walking or combat animation.
            AnimatorState moving = stateMachine.AddState("Moving");
            moving.motion = null;
            moving.writeDefaultValues = true;
            stateMachine.defaultState = idle;

            AnimatorStateTransition leaveIdle = idle.AddTransition(moving);
            leaveIdle.hasExitTime = false;
            leaveIdle.duration = 0.12f;
            leaveIdle.AddCondition(AnimatorConditionMode.If, 0f, MovingParameter);

            AnimatorStateTransition returnToIdle = moving.AddTransition(idle);
            returnToIdle.hasExitTime = false;
            returnToIdle.duration = 0.16f;
            returnToIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, MovingParameter);

            EditorUtility.SetDirty(stateMachine);
        }

        private struct CurveSpec
        {
            public readonly string Path;
            public readonly string Property;
            public readonly float Amplitude;
            public readonly float Phase;
            public readonly float Bias;

            public CurveSpec(string path, string property, float amplitude, float phase, float bias = 0f)
            {
                Path = path;
                Property = property;
                Amplitude = amplitude;
                Phase = phase;
                Bias = bias;
            }
        }
    }
}
