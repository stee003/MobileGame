using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MobileGame.Player.EditorTools
{
    /// <summary>
    /// Authors the hero's idle and in-place walking clips, plus their two-state locomotion
    /// controller. Assets are generated through Unity's animation APIs so transform bindings stay
    /// valid across Editor versions.
    /// </summary>
    [InitializeOnLoad]
    public static class HeroIdleAnimationAssetBuilder
    {
        private const string Folder = "Assets/Resources/Animations/HeroIdle";
        private const string IdleClipPath = Folder + "/Hero_Idle.anim";
        private const string WalkClipPath = Folder + "/Hero_Walk.anim";
        private const string ControllerPath = Folder + "/Hero_Idle.controller";
        private const string MovingParameter = "IsMoving";
        private const string WalkRateParameter = "WalkCycleRate";
        private const float IdleLoopDuration = 4.2f;
        private const float WalkLoopDuration = 0.9f;
        private const int WalkCurveIntervals = 54;

        static HeroIdleAnimationAssetBuilder()
        {
            EditorApplication.delayCall += EnsureAssets;
        }

        [MenuItem("Tools/MobileGame/Animations/Rebuild Hero Walk Assets", priority = 0)]
        public static void RebuildAssets()
        {
            EnsureFolder();

            AnimationClip idleClip = LoadOrCreateClip(IdleClipPath, "Hero_Idle");
            AnimationClip walkClip = LoadOrCreateClip(WalkClipPath, "Hero_Walk");
            AuthorIdleClip(idleClip);
            AuthorWalkClip(walkClip);

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            AuthorController(controller, idleClip, walkClip);
            EditorUtility.SetDirty(idleClip);
            EditorUtility.SetDirty(walkClip);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[HeroLocomotion] Rebuilt Hero_Idle.anim, Hero_Walk.anim, and the idle/walk controller.");
        }

        private static void EnsureAssets()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += EnsureAssets;
                return;
            }

            AnimationClip idleClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(IdleClipPath);
            AnimationClip walkClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(WalkClipPath);
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (idleClip != null && walkClip != null && controller != null && IsCurrentController(controller))
                return;

            RebuildAssets();
        }

        private static bool IsCurrentController(AnimatorController controller)
        {
            AnimatorControllerLayer[] layers = controller.layers;
            if (layers == null || layers.Length == 0 || !layers[0].iKPass)
                return false;

            AnimatorStateMachine stateMachine = layers[0].stateMachine;
            bool hasIdleState = false;
            bool hasWalkState = false;
            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState state = states[i].state;
                if (state.name == "Idle" && state.motion != null && state.motion.name == "Hero_Idle")
                    hasIdleState = true;
                if (state.name == "Walk" && state.motion != null && state.motion.name == "Hero_Walk" &&
                    state.speedParameterActive && state.speedParameter == WalkRateParameter && state.iKOnFeet)
                    hasWalkState = true;
            }

            bool hasMovingParameter = false;
            bool hasWalkRateParameter = false;
            foreach (AnimatorControllerParameter parameter in controller.parameters)
            {
                if (parameter.name == MovingParameter && parameter.type == AnimatorControllerParameterType.Bool)
                    hasMovingParameter = true;
                if (parameter.name == WalkRateParameter && parameter.type == AnimatorControllerParameterType.Float)
                    hasWalkRateParameter = true;
            }

            bool hasIdleClip = false;
            bool hasWalkClip = false;
            bool hasOnlyTwoClips = controller.animationClips.Length == 2;
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip.name == "Hero_Idle")
                    hasIdleClip = true;
                else if (clip.name == "Hero_Walk")
                    hasWalkClip = true;
                else
                    hasOnlyTwoClips = false;
            }

            return hasIdleState && hasWalkState && hasMovingParameter && hasWalkRateParameter &&
                   hasIdleClip && hasWalkClip && hasOnlyTwoClips;
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

        private static AnimationClip LoadOrCreateClip(string path, string clipName)
        {
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                clip = new AnimationClip { name = clipName, frameRate = 30f };
                AssetDatabase.CreateAsset(clip, path);
            }

            return clip;
        }

        private static void AuthorIdleClip(AnimationClip clip)
        {
            clip.name = "Hero_Idle";
            clip.frameRate = 30f;
            clip.legacy = false;
            clip.wrapMode = WrapMode.Loop;

            // Small, in-place joint rotations and a few millimetres of pelvis sway. Root translation,
            // walking, running, and combat are authored separately or remain deliberately absent.
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

                // Asymmetric shoulder, elbow and wrist drift keeps the idle stance relaxed.
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

                // Barely perceptible counterbalance; the feet stay planted in the idle pose.
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

            SetLoopingClipSettings(clip);
        }

        private static void AuthorWalkClip(AnimationClip clip)
        {
            clip.name = "Hero_Walk";
            clip.frameRate = 60f;
            clip.legacy = false;
            clip.wrapMode = WrapMode.Loop;

            const float tau = Mathf.PI * 2f;
            const float stanceFraction = HeroWalkFootPlanting.StanceFraction;
            var curves = new List<PeriodicCurveSpec>
            {
                // The pelvis sits only slightly lower than the idle pose, with a restrained weight
                // shift. The spine, chest and head counter the gait so the torso remains composed.
                new PeriodicCurveSpec("Hips", "m_LocalPosition.y",
                    phase => 0.90f + 0.008f * Mathf.Cos(tau * 2f * phase)),
                new PeriodicCurveSpec("Hips", "m_LocalPosition.x",
                    phase => 0.010f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Hips", "localEulerAnglesRaw.x",
                    phase => 1.2f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Hips", "localEulerAnglesRaw.y",
                    phase => 1.4f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Hips", "localEulerAnglesRaw.z",
                    phase => 1.0f * Mathf.Sin(tau * phase + Mathf.PI * 0.5f)),
                new PeriodicCurveSpec("Spine", "localEulerAnglesRaw.x",
                    phase => -0.9f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Spine", "localEulerAnglesRaw.y",
                    phase => -0.8f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Spine", "localEulerAnglesRaw.z",
                    phase => -0.55f * Mathf.Sin(tau * phase + Mathf.PI * 0.5f)),
                new PeriodicCurveSpec("Chest", "localEulerAnglesRaw.x",
                    phase => -0.45f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Chest", "localEulerAnglesRaw.y",
                    phase => -0.65f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Chest", "localEulerAnglesRaw.z",
                    phase => -0.35f * Mathf.Sin(tau * phase + Mathf.PI * 0.5f)),
                new PeriodicCurveSpec("Neck", "localEulerAnglesRaw.x",
                    phase => 0.12f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Neck", "localEulerAnglesRaw.y",
                    phase => 0.25f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Head", "localEulerAnglesRaw.x",
                    phase => 0.10f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("Head", "localEulerAnglesRaw.y",
                    phase => -0.18f * Mathf.Sin(tau * phase)),

                // Legs swing in opposition. Knee lift and toe roll are confined to swing; humanoid
                // foot IK then holds each shoe in world space for the entire planted portion.
                new PeriodicCurveSpec("LeftUpperLeg", "localEulerAnglesRaw.x",
                    phase => UpperLegSwing(phase, 0f, stanceFraction)),
                new PeriodicCurveSpec("RightUpperLeg", "localEulerAnglesRaw.x",
                    phase => UpperLegSwing(phase, 0.5f, stanceFraction)),
                new PeriodicCurveSpec("LeftUpperLeg", "localEulerAnglesRaw.z",
                    phase => -1.2f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("RightUpperLeg", "localEulerAnglesRaw.z",
                    phase => 1.2f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("LeftLowerLeg", "localEulerAnglesRaw.x",
                    phase => KneeFlex(phase, 0f, stanceFraction)),
                new PeriodicCurveSpec("RightLowerLeg", "localEulerAnglesRaw.x",
                    phase => KneeFlex(phase, 0.5f, stanceFraction)),
                new PeriodicCurveSpec("LeftFoot", "localEulerAnglesRaw.x",
                    phase => AnklePitch(phase, 0f, stanceFraction)),
                new PeriodicCurveSpec("RightFoot", "localEulerAnglesRaw.x",
                    phase => AnklePitch(phase, 0.5f, stanceFraction)),
                new PeriodicCurveSpec("LeftToes", "localEulerAnglesRaw.x",
                    phase => ToeFlex(phase, 0f, stanceFraction)),
                new PeriodicCurveSpec("RightToes", "localEulerAnglesRaw.x",
                    phase => ToeFlex(phase, 0.5f, stanceFraction)),

                // Natural counter-swing: the opposite arm advances with each leg. Elbows stay
                // softly bent and the wrists follow through without exaggerated pumping.
                new PeriodicCurveSpec("LeftUpperArm", "localEulerAnglesRaw.x",
                    phase => 20f * Mathf.Cos(tau * phase)),
                new PeriodicCurveSpec("RightUpperArm", "localEulerAnglesRaw.x",
                    phase => -20f * Mathf.Cos(tau * phase)),
                new PeriodicCurveSpec("LeftUpperArm", "localEulerAnglesRaw.z",
                    phase => -1.4f + 1.0f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("RightUpperArm", "localEulerAnglesRaw.z",
                    phase => 1.4f - 1.0f * Mathf.Sin(tau * phase)),
                new PeriodicCurveSpec("LeftLowerArm", "localEulerAnglesRaw.x",
                    phase => -14f + 2.2f * Mathf.Sin(tau * phase + 0.25f)),
                new PeriodicCurveSpec("RightLowerArm", "localEulerAnglesRaw.x",
                    phase => -14f - 2.2f * Mathf.Sin(tau * phase + 0.25f)),
                new PeriodicCurveSpec("LeftHand", "localEulerAnglesRaw.x",
                    phase => 1.8f * Mathf.Sin(tau * phase + 0.5f)),
                new PeriodicCurveSpec("RightHand", "localEulerAnglesRaw.x",
                    phase => -1.8f * Mathf.Sin(tau * phase + 0.5f)),
            };

            foreach (PeriodicCurveSpec spec in curves)
                AddPeriodicCurve(clip, spec.Path, spec.Property, spec.Evaluate);

            SetLoopingClipSettings(clip);
        }

        private static float UpperLegSwing(float cyclePhase, float phaseOffset, float stanceFraction)
        {
            float phase = Mathf.Repeat(cyclePhase + phaseOffset, 1f);
            if (phase < stanceFraction)
            {
                float stance = Smooth01(phase / stanceFraction);
                return Mathf.Lerp(-22f, 24f, stance);
            }

            float swing = Smooth01((phase - stanceFraction) / (1f - stanceFraction));
            return Mathf.Lerp(24f, -22f, swing);
        }

        private static float KneeFlex(float cyclePhase, float phaseOffset, float stanceFraction)
        {
            float phase = Mathf.Repeat(cyclePhase + phaseOffset, 1f);
            if (phase < stanceFraction)
                return Mathf.Lerp(6f, 9f, Smooth01(phase / stanceFraction));

            float swing = (phase - stanceFraction) / (1f - stanceFraction);
            return 9f + 38f * Mathf.Sin(Mathf.PI * swing);
        }

        private static float AnklePitch(float cyclePhase, float phaseOffset, float stanceFraction)
        {
            float phase = Mathf.Repeat(cyclePhase + phaseOffset, 1f);
            if (phase < stanceFraction)
            {
                float stance = Smooth01(phase / stanceFraction);
                return Mathf.Lerp(-7f, 10f, stance);
            }

            float swing = Smooth01((phase - stanceFraction) / (1f - stanceFraction));
            return Mathf.Lerp(10f, -7f, swing) - 3f * Mathf.Sin(Mathf.PI * swing);
        }

        private static float ToeFlex(float cyclePhase, float phaseOffset, float stanceFraction)
        {
            float phase = Mathf.Repeat(cyclePhase + phaseOffset, 1f);
            if (phase < stanceFraction)
                return 8f * Smooth01(phase / stanceFraction);

            return 8f * (1f - Smooth01((phase - stanceFraction) / (1f - stanceFraction)));
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static void AddPeriodicCurve(AnimationClip clip, string path, string property,
                                             Func<float, float> evaluate)
        {
            AnimationCurve curve = CreatePeriodicCurve(WalkLoopDuration, WalkCurveIntervals, evaluate);
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), property);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static AnimationCurve CreatePeriodicCurve(float duration, int intervals,
                                                           Func<float, float> evaluate)
        {
            var keys = new Keyframe[intervals + 1];
            float phaseStep = 1f / intervals;
            float timeStep = duration / intervals;
            for (int i = 0; i <= intervals; i++)
            {
                float phase = i == intervals ? 0f : i * phaseStep;
                float previous = evaluate(Mathf.Repeat(phase - phaseStep, 1f));
                float next = evaluate(Mathf.Repeat(phase + phaseStep, 1f));
                float tangent = (next - previous) / (2f * timeStep);
                keys[i] = new Keyframe(i * timeStep, evaluate(phase), tangent, tangent);
            }

            // Explicitly duplicate the first value and slope to make the seam C1-continuous.
            keys[intervals].value = keys[0].value;
            keys[intervals].inTangent = keys[0].inTangent;
            keys[intervals].outTangent = keys[0].outTangent;
            var curve = new AnimationCurve(keys)
            {
                preWrapMode = WrapMode.Loop,
                postWrapMode = WrapMode.Loop
            };
            return curve;
        }

        private static AnimationCurve CreateLoopCurve(CurveSpec spec)
        {
            const int intervals = 12;
            float omega = Mathf.PI * 2f / IdleLoopDuration;
            var keys = new Keyframe[intervals + 1];
            for (int i = 0; i <= intervals; i++)
            {
                float time = IdleLoopDuration * i / intervals;
                float wave = Mathf.Sin(omega * time + spec.Phase);
                float value = spec.Bias + spec.Amplitude * wave;
                float slope = spec.Amplitude * omega * Mathf.Cos(omega * time + spec.Phase);
                keys[i] = new Keyframe(time, value, slope, slope);
            }

            // Identical endpoint values and tangents make the loop seam continuous.
            keys[intervals].value = keys[0].value;
            keys[intervals].inTangent = keys[0].inTangent;
            keys[intervals].outTangent = keys[0].outTangent;
            var curve = new AnimationCurve(keys)
            {
                preWrapMode = WrapMode.Loop,
                postWrapMode = WrapMode.Loop
            };
            return curve;
        }

        private static void SetLoopingClipSettings(AnimationClip clip)
        {
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

        private static void AuthorController(AnimatorController controller, AnimationClip idleClip,
                                             AnimationClip walkClip)
        {
            controller.name = "Hero_Idle";
            controller.parameters = Array.Empty<AnimatorControllerParameter>();
            controller.AddParameter(MovingParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(WalkRateParameter, AnimatorControllerParameterType.Float);

            AnimatorControllerLayer[] layers = controller.layers;
            if (layers == null || layers.Length == 0)
            {
                controller.AddLayer("Base Layer");
                layers = controller.layers;
            }
            layers[0].iKPass = true;
            controller.layers = layers;

            AnimatorStateMachine stateMachine = layers[0].stateMachine;
            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
                stateMachine.RemoveState(states[i].state);

            AnimatorState idle = stateMachine.AddState("Idle");
            idle.motion = idleClip;
            idle.writeDefaultValues = true;
            idle.position = new Vector3(250f, 80f, 0f);

            AnimatorState walk = stateMachine.AddState("Walk");
            walk.motion = walkClip;
            walk.writeDefaultValues = true;
            walk.speed = 1f;
            walk.speedParameterActive = true;
            walk.speedParameter = WalkRateParameter;
            walk.iKOnFeet = true;
            walk.position = new Vector3(500f, 80f, 0f);
            stateMachine.defaultState = idle;

            AnimatorStateTransition leaveIdle = idle.AddTransition(walk);
            leaveIdle.hasExitTime = false;
            leaveIdle.hasFixedDuration = true;
            leaveIdle.duration = 0.18f;
            leaveIdle.AddCondition(AnimatorConditionMode.If, 0f, MovingParameter);

            AnimatorStateTransition returnToIdle = walk.AddTransition(idle);
            returnToIdle.hasExitTime = false;
            returnToIdle.hasFixedDuration = true;
            returnToIdle.duration = 0.20f;
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

        private struct PeriodicCurveSpec
        {
            public readonly string Path;
            public readonly string Property;
            public readonly Func<float, float> Evaluate;

            public PeriodicCurveSpec(string path, string property, Func<float, float> evaluate)
            {
                Path = path;
                Property = property;
                Evaluate = evaluate;
            }
        }
    }
}
