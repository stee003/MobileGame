using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MobileGame.Player
{
    /// <summary>
    /// Play Mode verification suite for <see cref="HeroRig"/>.
    ///
    /// <para>
    /// It rotates the rig - nothing else - and proves that the skeleton is a proper humanoid rig
    /// wired into Unity's animation system:
    /// <list type="number">
    /// <item><b>Wiring</b> - the rig is built, the Animator sits on the skeleton root with a valid
    ///       humanoid avatar, root motion off and always-animate culling.</item>
    /// <item><b>Skeleton</b> - every bone exists with the right name and parent, and
    ///       <c>Animator.GetBoneTransform</c> resolves each of the 51 mapped bones back to the same
    ///       transform, which is the only real proof that the humanoid mapping is correct.</item>
    /// <item><b>Binding</b> - all 57 hero meshes hang from a bone, and the rigid bind reproduced
    ///       their authored world transforms exactly.</item>
    /// <item><b>T-pose</b> - the arms rotate out to horizontal and back, so the pose the avatar was
    ///       built from is reachable at runtime.</item>
    /// <item><b>Simple rotations</b> - hips, spine, chest, neck, head, shoulders, upper arms,
    ///       elbows, hands, fingers, upper legs, knees and feet are rotated one joint at a time.
    ///       Each rotation has to hold, has to carry exactly the parts below it (predicted to the
    ///       millimetre by rigid-body math), has to leave everything else untouched, has to keep the
    ///       seams between neighbouring parts closed, and has to keep the body off the floor.</item>
    /// <item><b>Stress</b> - hundreds of random multi-joint poses inside the joint limits, checking
    ///       the same invariants.</item>
    /// <item><b>Scope</b> - the Animator contains only the idle loop; there are no walking or combat
    ///       clips. The suite pauses Animator evaluation while it checks direct bone rotations.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The suite contains no gameplay logic, does not touch the player controller and restores the
    /// rest pose after every rotation. Exact per-volume seam and penetration measurements live in
    /// <c>Tools/HeroRigVerification/simulate_hero_rig.py</c>; this suite verifies the live rig.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1100)] // before the material suite (1200), which inspects the same hierarchy
    public class HeroRigTest : MonoBehaviour
    {
        private const string Tag = "[HeroRigTest]";

        [Tooltip("The rig under test. If unassigned, one is found in the scene.")]
        [SerializeField] private HeroRig rig;

        [Tooltip("Automatically runs the verification suite on Start in Play Mode.")]
        [SerializeField] private bool runOnStart = true;

        [Tooltip("Random multi-joint poses used by the stress check.")]
        [SerializeField] private int stressPoses = 150;

        [Tooltip("Seed for the stress poses, so a failure can be reproduced.")]
        [SerializeField] private int stressSeed = 20240930;

        private const float MoveEpsilon = 0.0005f;   // 0.5 mm
        private const float StillEpsilon = 1e-6f;
        private const float BindEpsilon = 1e-5f;
        private const float SeamTolerance = 0.004f;  // 4 mm between renderer bounds
        private const float SoleAllowance = 0.050f;  // rigid box soles: measured in the simulator

        // One joint at a time, both sides. Hips, spine, shoulders, elbows and knees are the joints
        // the rig has to be verified on; the rest are checked the same way.
        private static readonly RotationCase[] RotationCases =
        {
            new RotationCase("hips pitch", HeroJoint.Hips, 12f, 0f, 0f),
            new RotationCase("hips yaw", HeroJoint.Hips, 0f, 15f, 0f),
            new RotationCase("hips roll", HeroJoint.Hips, 0f, 0f, 10f),
            new RotationCase("spine pitch", HeroJoint.Spine, 15f, 0f, 0f),
            new RotationCase("spine lateral", HeroJoint.Spine, 0f, 0f, 12f),
            new RotationCase("spine yaw", HeroJoint.Spine, 0f, 15f, 0f),
            new RotationCase("chest pitch", HeroJoint.Chest, 15f, 0f, 0f),
            new RotationCase("chest yaw", HeroJoint.Chest, 0f, 15f, 0f),
            new RotationCase("neck pitch", HeroJoint.Neck, 20f, 0f, 0f),
            new RotationCase("neck yaw", HeroJoint.Neck, 0f, 30f, 0f),
            new RotationCase("head pitch", HeroJoint.Head, 20f, 0f, 0f),
            new RotationCase("head yaw", HeroJoint.Head, 0f, 40f, 0f),
            new RotationCase("left shoulder", HeroJoint.LeftShoulder, 10f, 10f, 10f),
            new RotationCase("right shoulder", HeroJoint.RightShoulder, 10f, 10f, 10f),
            new RotationCase("left upper arm forward", HeroJoint.LeftUpperArm, -40f, 0f, 0f),
            new RotationCase("left upper arm out", HeroJoint.LeftUpperArm, 0f, 0f, -60f),
            new RotationCase("right upper arm out", HeroJoint.RightUpperArm, 0f, 0f, 60f),
            new RotationCase("left elbow flex", HeroJoint.LeftLowerArm, -90f, 0f, 0f),
            new RotationCase("right elbow flex", HeroJoint.RightLowerArm, -90f, 0f, 0f),
            new RotationCase("left wrist flex", HeroJoint.LeftHand, 45f, 0f, 0f),
            new RotationCase("left index curl", HeroJoint.LeftIndexProximal, 60f, 0f, 0f),
            new RotationCase("left thumb curl", HeroJoint.LeftThumbProximal, 40f, 0f, 0f),
            new RotationCase("left hip forward", HeroJoint.LeftUpperLeg, -60f, 0f, 0f),
            new RotationCase("left hip abduct", HeroJoint.LeftUpperLeg, 0f, 0f, -35f),
            new RotationCase("right hip forward", HeroJoint.RightUpperLeg, -60f, 0f, 0f),
            new RotationCase("left knee flex", HeroJoint.LeftLowerLeg, 90f, 0f, 0f),
            new RotationCase("right knee flex", HeroJoint.RightLowerLeg, 90f, 0f, 0f),
            new RotationCase("left ankle roll", HeroJoint.LeftFoot, 0f, 0f, 20f),
            new RotationCase("left ankle pitch", HeroJoint.LeftFoot, -15f, 0f, 0f),
            new RotationCase("left toes flex", HeroJoint.LeftToes, 30f, 0f, 0f)
        };

        // Seams that are allowed to open, measured in Tools/HeroRigVerification: the armpit opens
        // when the arm is abducted, and the hip trim slides on the pelvis with the gap staying
        // inside the pelvis box.
        private static readonly string[][] SeamExceptions =
        {
            new[] { "Chest", "UpperArm_L" }, new[] { "Chest", "UpperArm_R" },
            new[] { "Chest", "Shoulder_L" }, new[] { "Chest", "Shoulder_R" },
            new[] { "HipAccent_L", "Abdomen" }, new[] { "HipAccent_R", "Abdomen" }
        };

        private bool m_running;
        private int m_passed;
        private int m_total;
        private float m_groundY;
        private readonly List<Seam> m_seams = new List<Seam>();

        /// <summary>True while the automated suite is in progress.</summary>
        public bool IsRunning => m_running;

        private void Start()
        {
            if (runOnStart)
                RunTestSuite();
        }

        /// <summary>Runs the complete rig verification suite.</summary>
        [ContextMenu("Verify: Run Hero Rig Test Suite")]
        public void RunTestSuite()
        {
            if (m_running)
            {
                Debug.LogWarning(Tag + " A test run is already in progress.");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning(Tag + " Enter Play Mode to run the rig suite.");
                return;
            }

            StartCoroutine(RunAllTests());
        }

        private IEnumerator RunAllTests()
        {
            m_running = true;
            m_passed = 0;
            m_total = 0;

            Debug.Log(Tag + " =========================================");
            Debug.Log(Tag + " Starting Hero Rig Test Suite...");

            if (rig == null)
                rig = FindFirstObjectByType<HeroRig>();

            if (rig == null || !rig.IsBuilt)
            {
                Debug.LogError(Tag + " FAILED: no built HeroRig found in the scene.");
                m_running = false;
                yield break;
            }

            rig.ApplyRestPose();
            yield return null;

            TestAnimatorSetup();
            // These rig checks author bone rotations directly. Pause the idle state machine while
            // they run so normal animation evaluation cannot overwrite the pose under test.
            Animator testAnimator = rig.Animator;
            bool restoreAnimatorEnabled = testAnimator != null && testAnimator.enabled;
            if (testAnimator != null)
                testAnimator.enabled = false;

            TestSkeleton();
            TestBinding();
            yield return TestTPose();
            yield return TestRotations();
            yield return TestStress();
            TestScope();

            rig.ApplyRestPose();
            if (testAnimator != null)
                testAnimator.enabled = restoreAnimatorEnabled;

            Debug.Log(Tag + $" Test Results: {m_passed}/{m_total} checks passed.");
            Debug.Log(m_passed == m_total
                ? Tag + " VERIFICATION PASSED. The humanoid rig is built, mapped and deforming correctly."
                : Tag + $" VERIFICATION FAILED. Only {m_passed}/{m_total} checks passed.");
            Debug.Log(Tag + " =========================================");

            m_running = false;
        }

        // ------------------------------------------------------------------
        // 1. Animator / avatar setup
        // ------------------------------------------------------------------
        private void TestAnimatorSetup()
        {
            Animator animator = rig.Animator;
            Check(animator != null, "[1] the skeleton root has an Animator");
            if (animator == null)
                return;

            Check(animator.transform == rig.RigRoot,
                  "[1] the Animator sits on the skeleton root (" + animator.transform.name +
                  "), not on the Player root the CharacterController owns");
            Check(animator.GetComponentInParent<CharacterController>() != null &&
                  animator.GetComponent<CharacterController>() == null,
                  "[1] the Animator shares no GameObject with the CharacterController");

            Avatar avatar = rig.Avatar;
            Check(avatar != null && avatar.isValid, "[1] the avatar is assigned and valid");
            Check(avatar != null && avatar.isHuman,
                  "[1] the avatar is humanoid (retargetable muscle space), kind = " + rig.AvatarKindUsed);
            Check(animator.avatar == avatar, "[1] the Animator uses the avatar the rig built");
            Check(!animator.applyRootMotion, "[1] root motion is off - the controller owns the root");
            Check(animator.cullingMode == AnimatorCullingMode.AlwaysAnimate,
                  "[1] culling is AlwaysAnimate, so the bones keep evaluating off screen");
            Check(animator.runtimeAnimatorController != null,
                  "[1] the idle-only Animator Controller is assigned");
            bool hasMovingParameter = false;
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.name == ThirdPersonPlayerController.IsMovingAnimatorParameter &&
                    parameter.type == AnimatorControllerParameterType.Bool)
                    hasMovingParameter = true;
            }
            Check(hasMovingParameter, "[1] the Animator exposes the IsMoving bool used by the player controller");
            Check(rig.BoneCount == 52, "[1] the skeleton has 52 bones (found " + rig.BoneCount + ")");
        }

        // ------------------------------------------------------------------
        // 2. Skeleton and humanoid mapping
        // ------------------------------------------------------------------
        private void TestSkeleton()
        {
            var problems = new List<string>();
            int mapped = 0;

            foreach (HeroJoint joint in HeroRig.AllJoints)
            {
                HeroRigJoint info;
                if (!HeroRig.TryGetJointInfo(joint, out info))
                {
                    problems.Add(joint + " has no joint info");
                    continue;
                }

                Transform bone = rig.GetBone(joint);
                if (bone == null)
                {
                    problems.Add(joint + " did not build");
                    continue;
                }

                if (bone.name != info.BoneName)
                    problems.Add(joint + " is named '" + bone.name + "', expected '" + info.BoneName + "'");

                if (bone.localScale != Vector3.one)
                    problems.Add(info.BoneName + " has a non-uniform scale " + bone.localScale);

                Transform expectedParent = info.Parent == HeroJoint.None
                    ? rig.RigRoot
                    : rig.GetBone(info.Parent);
                if (bone.parent != expectedParent)
                    problems.Add(info.BoneName + " is parented to " +
                                 (bone.parent != null ? bone.parent.name : "<none>"));

                if (!info.IsMapped)
                    continue;

                mapped++;
                Transform resolved;
                if (!rig.TryGetHumanBone(info.HumanBone, out resolved))
                    problems.Add(info.BoneName + " does not resolve through the avatar");
                else if (resolved != bone)
                    problems.Add(info.HumanBone + " resolves to '" + resolved.name + "' instead of '" +
                                 info.BoneName + "'");
            }

            Check(problems.Count == 0,
                  "[2] the bone chain is intact and the humanoid mapping resolves through the avatar" +
                  (problems.Count > 0 ? " | " + string.Join("; ", problems.ToArray()) : string.Empty));
            Check(mapped == 51, "[2] 51 bones are mapped to the avatar (found " + mapped + ")");

            HeroRigJoint headTop;
            HeroRig.TryGetJointInfo(HeroJoint.HeadTopEnd, out headTop);
            Check(!headTop.IsMapped && rig.GetBone(HeroJoint.HeadTopEnd) != null,
                  "[2] HeadTop_End exists as the unmapped helper that gives the head bone an axis");

            // The joints the task calls out, plus the ones a humanoid rig cannot do without.
            HeroJoint[] required =
            {
                HeroJoint.Hips, HeroJoint.Spine, HeroJoint.Chest, HeroJoint.Neck, HeroJoint.Head,
                HeroJoint.LeftShoulder, HeroJoint.RightShoulder,
                HeroJoint.LeftUpperArm, HeroJoint.RightUpperArm,
                HeroJoint.LeftLowerArm, HeroJoint.RightLowerArm,
                HeroJoint.LeftHand, HeroJoint.RightHand,
                HeroJoint.LeftUpperLeg, HeroJoint.RightUpperLeg,
                HeroJoint.LeftLowerLeg, HeroJoint.RightLowerLeg,
                HeroJoint.LeftFoot, HeroJoint.RightFoot
            };
            var missing = new List<string>();
            foreach (HeroJoint joint in required)
            {
                if (rig.GetBone(joint) == null)
                    missing.Add(joint.ToString());
            }

            Check(missing.Count == 0,
                  "[2] hips, spine, chest, neck, head, shoulders, upper arms, forearms, hands, " +
                  "upper legs, lower legs and feet all exist" +
                  (missing.Count > 0 ? " | missing " + string.Join(", ", missing.ToArray()) : string.Empty));

            Transform thumb = rig.GetBone(HeroJoint.LeftThumbDistal);
            Transform little = rig.GetBone(HeroJoint.RightLittleDistal);
            Check(thumb != null && little != null &&
                  rig.GetBone(HeroJoint.LeftIndexProximal) != null &&
                  rig.GetBone(HeroJoint.RightRingIntermediate) != null,
                  "[2] both hands carry the five finger chains (thumb to little, three segments each)");
        }

        // ------------------------------------------------------------------
        // 3. Mesh binding
        // ------------------------------------------------------------------
        private void TestBinding()
        {
            Renderer[] renderers = rig.RigRoot.GetComponentsInChildren<Renderer>(true);
            var problems = new List<string>();
            int bound = 0;

            foreach (Renderer renderer in renderers)
            {
                bool onBone = false;
                for (Transform node = renderer.transform.parent; node != null && node != rig.RigRoot; node = node.parent)
                {
                    if (rig.IsBone(node))
                    {
                        onBone = true;
                        break;
                    }
                }

                if (onBone)
                    bound++;
                else
                    problems.Add(renderer.name + " hangs from no bone");
            }

            Check(renderers.Length == 57,
                  "[3] all 57 hero meshes live under the skeleton (found " + renderers.Length + ")");
            Check(problems.Count == 0, "[3] every mesh is a descendant of a bone" +
                  (problems.Count > 0 ? " | " + string.Join("; ", problems.ToArray()) : string.Empty));
            Check(bound == rig.BoundPartCount,
                  "[3] the rig reports " + rig.BoundPartCount + " bound meshes, the hierarchy has " + bound);
            Check(rig.UnboundParts.Count == 0,
                  "[3] no mesh was left unbound (it would stay frozen during animation)");

            float worst = 0f;
            string worstName = "";
            foreach (HeroRigPart part in rig.Parts)
            {
                float delta = Vector3.Distance(part.Part.position, part.RestWorldPosition);
                float angle = Quaternion.Angle(part.Part.rotation, part.RestWorldRotation);
                float combined = delta + angle * 0.001f;
                if (combined > worst)
                {
                    worst = combined;
                    worstName = part.Name;
                }
            }

            Check(worst < BindEpsilon,
                  $"[3] the rigid bind is lossless - no mesh moved while it was reparented (worst {worst:E2} on " +
                  worstName + ")");

            // The ground reference for the rotation checks: the lowest point of the hero at rest.
            m_groundY = float.MaxValue;
            foreach (Renderer renderer in renderers)
                m_groundY = Mathf.Min(m_groundY, renderer.bounds.min.y);

            BuildSeams(renderers);
            Check(m_seams.Count >= 20,
                  "[3] " + m_seams.Count + " seams between neighbouring parts on different bones are " +
                  "watched for tearing");
        }

        // ------------------------------------------------------------------
        // 4. T-pose
        // ------------------------------------------------------------------
        private IEnumerator TestTPose()
        {
            Transform leftArm = rig.GetBone(HeroJoint.LeftUpperArm);
            Transform rightArm = rig.GetBone(HeroJoint.RightUpperArm);
            if (leftArm == null || rightArm == null)
            {
                Check(false, "[4] the upper arms are missing, the T-pose cannot be checked");
                yield break;
            }

            Quaternion restLeft = leftArm.localRotation;
            Quaternion restRight = rightArm.localRotation;

            leftArm.localRotation = Quaternion.Euler(0f, 0f, -HeroRig.TPoseArmAngle);
            rightArm.localRotation = Quaternion.Euler(0f, 0f, HeroRig.TPoseArmAngle);
            yield return null;

            Transform chest = rig.GetBone(HeroJoint.Chest);
            Vector3 left = rig.RigRoot.InverseTransformPoint(rig.GetBone(HeroJoint.LeftHand).position) -
                           rig.RigRoot.InverseTransformPoint(leftArm.position);
            Vector3 right = rig.RigRoot.InverseTransformPoint(rig.GetBone(HeroJoint.RightHand).position) -
                            rig.RigRoot.InverseTransformPoint(rightArm.position);
            left.Normalize();
            right.Normalize();

            Check(left.x < -0.9f && Mathf.Abs(left.y) < 0.02f,
                  "[4] the left arm rotates out to horizontal, pointing -x (" + F(left) + ")");
            Check(right.x > 0.9f && Mathf.Abs(right.y) < 0.02f,
                  "[4] the right arm rotates out to horizontal, pointing +x (" + F(right) + ")");
            Check(chest != null,
                  "[4] the T-pose the avatar was built from is reachable at runtime, and the chest " +
                  "stays on the mirror plane");

            leftArm.localRotation = restLeft;
            rightArm.localRotation = restRight;
            yield return null;

            Check(Quaternion.Angle(leftArm.localRotation, rig.GetRestLocalRotation(HeroJoint.LeftUpperArm)) < 0.001f &&
                  Quaternion.Angle(rightArm.localRotation, rig.GetRestLocalRotation(HeroJoint.RightUpperArm)) < 0.001f,
                  "[4] the arms return to the authored resting pose exactly");
        }

        // ------------------------------------------------------------------
        // 5. Simple rotations
        // ------------------------------------------------------------------
        private IEnumerator TestRotations()
        {
            var heldProblems = new List<string>();
            var travelProblems = new List<string>();
            var isolationProblems = new List<string>();
            var seamProblems = new List<string>();
            var groundProblems = new List<string>();
            var nanProblems = new List<string>();
            var driftProblems = new List<string>();
            var exercised = new HashSet<HeroJoint>();
            float worstSole = 0f;

            foreach (RotationCase rotationCase in RotationCases)
            {
                HeroJoint joint = rotationCase.Joint;
                Transform bone = rig.GetBone(joint);
                if (bone == null)
                {
                    heldProblems.Add(rotationCase.Label + ": bone missing");
                    continue;
                }

                Quaternion restRotation = rig.GetRestLocalRotation(joint);
                Quaternion target = restRotation * Quaternion.Euler(rotationCase.Euler);
                Vector3 pivot = bone.position;
                Vector3 axis = bone.rotation * AxisOf(Quaternion.Euler(rotationCase.Euler));
                float angle = rotationCase.Euler.magnitude == 0f
                    ? 0f
                    : Quaternion.Angle(Quaternion.identity, Quaternion.Euler(rotationCase.Euler));

                rig.SetJointLocalRotation(joint, target);
                yield return null; // let the Animator run once: it must not fight the rig

                if (Quaternion.Angle(bone.localRotation, target) > 0.01f)
                    heldProblems.Add(rotationCase.Label + ": the bone holds " +
                                     F(bone.localRotation.eulerAngles) + " instead of " + F(rotationCase.Euler));

                exercised.Add(joint);
                HashSet<Transform> chain = ChainOf(joint);

                foreach (HeroRigPart part in rig.Parts)
                {
                    Vector3 position = part.Part.position;
                    if (!IsFinite(position))
                    {
                        nanProblems.Add(rotationCase.Label + ": " + part.Name);
                        continue;
                    }

                    float moved = Vector3.Distance(position, part.RestWorldPosition);
                    if (chain.Contains(part.Bone))
                    {
                        Vector3 offset = part.RestWorldPosition - pivot;
                        float radius = (offset - Vector3.Project(offset, axis)).magnitude;
                        float expected = 2f * radius * Mathf.Sin(angle * Mathf.Deg2Rad * 0.5f);
                        if (Mathf.Abs(moved - expected) > Mathf.Max(MoveEpsilon, 0.01f * expected))
                            travelProblems.Add(rotationCase.Label + ": " + part.Name + " moved " +
                                               (moved * 1000f).ToString("F1") + " mm, a rigid rotation " +
                                               "predicts " + (expected * 1000f).ToString("F1") + " mm");
                    }
                    else if (moved > StillEpsilon)
                    {
                        isolationProblems.Add(rotationCase.Label + ": " + part.Name + " moved " +
                                              (moved * 1000f).ToString("F2") + " mm but is not below the joint");
                    }
                }

                CheckSeams(rotationCase.Label, seamProblems);
                CheckGround(rotationCase.Label, groundProblems, ref worstSole);

                rig.ApplyRestPose();
                yield return null;

                foreach (HeroRigPart part in rig.Parts)
                {
                    if (Vector3.Distance(part.Part.position, part.RestWorldPosition) > BindEpsilon)
                    {
                        driftProblems.Add(rotationCase.Label + ": " + part.Name + " did not return to rest");
                        break;
                    }
                }
            }

            Check(heldProblems.Count == 0, "[5] every joint holds the rotation it was given" + List(heldProblems));
            Check(nanProblems.Count == 0, "[5] no rotation produces a NaN transform" + List(nanProblems));
            Check(travelProblems.Count == 0,
                  "[5] everything below a rotated joint travels exactly as a rigid rotation" + List(travelProblems));
            Check(isolationProblems.Count == 0, "[5] nothing outside the rotated chain moves" + List(isolationProblems));
            Check(driftProblems.Count == 0, "[5] returning to the rest pose is exact - the rig does not drift" +
                  List(driftProblems));
            Check(seamProblems.Count == 0, "[5] no seam tears while the joints rotate" + List(seamProblems));
            Check(groundProblems.Count == 0, "[5] nothing sinks through the floor" + List(groundProblems));

            HeroJoint[] required =
            {
                HeroJoint.Hips, HeroJoint.Spine, HeroJoint.Chest,
                HeroJoint.LeftShoulder, HeroJoint.RightShoulder,
                HeroJoint.LeftLowerArm, HeroJoint.RightLowerArm,
                HeroJoint.LeftLowerLeg, HeroJoint.RightLowerLeg
            };
            var missing = new List<string>();
            foreach (HeroJoint joint in required)
            {
                if (!exercised.Contains(joint))
                    missing.Add(joint.ToString());
            }

            Check(missing.Count == 0,
                  "[5] the required joints were all rotated: hips, spine, chest, both shoulders, both " +
                  "elbows and both knees" + (missing.Count > 0 ? " | missing " + string.Join(", ", missing.ToArray()) : ""));
            Check(worstSole < SoleAllowance,
                  "[5] deepest sole contact " + (worstSole * 1000f).ToString("F1") +
                  " mm - inside the measured rigid-box allowance of " + (SoleAllowance * 1000f).ToString("F0") +
                  " mm (foot planting is animation/IK work)");
        }

        // ------------------------------------------------------------------
        // 6. Stress: random multi-joint poses
        // ------------------------------------------------------------------
        private IEnumerator TestStress()
        {
            var problems = new List<string>();
            var seamsTorn = new List<string>();
            var random = new System.Random(stressSeed);
            int poses = Mathf.Max(1, stressPoses);

            for (int i = 0; i < poses; i++)
            {
                foreach (HeroJoint joint in HeroRig.AllJoints)
                {
                    HeroRigJoint info;
                    if (!HeroRig.TryGetJointInfo(joint, out info))
                        continue;

                    Vector3 euler = new Vector3(
                        Mathf.Lerp(info.LimitMin.x, info.LimitMax.x, (float)random.NextDouble()),
                        Mathf.Lerp(info.LimitMin.y, info.LimitMax.y, (float)random.NextDouble()),
                        Mathf.Lerp(info.LimitMin.z, info.LimitMax.z, (float)random.NextDouble()));
                    rig.SetJointLocalRotation(joint, Quaternion.Euler(euler));
                }

                foreach (HeroRigPart part in rig.Parts)
                {
                    if (!IsFinite(part.Part.position))
                        problems.Add("pose " + i + ": " + part.Name + " is not finite");
                }

                CheckSeams("pose " + i, seamsTorn, 3);

                if (problems.Count > 2 || seamsTorn.Count > 2)
                    break;

                if (i % 10 == 9)
                    yield return null;
            }

            rig.ApplyRestPose();
            yield return null;

            Check(problems.Count == 0,
                  "[6] " + poses + " random multi-joint poses inside the joint limits produce no NaN " +
                  "transform" + List(problems));
            Check(seamsTorn.Count == 0,
                  "[6] no seam tears in the random poses" + List(seamsTorn));
        }

        // ------------------------------------------------------------------
        // 7. Scope
        // ------------------------------------------------------------------
        private void TestScope()
        {
            Animator animator = rig.Animator;
            bool idleOnlyController = animator != null && animator.runtimeAnimatorController != null &&
                                      animator.runtimeAnimatorController.animationClips.Length == 1 &&
                                      animator.runtimeAnimatorController.animationClips[0].name == "Hero_Idle";
            Check(idleOnlyController,
                  "[7] the Animator contains only Hero_Idle (no walking or combat clips)");
            Check(rig.GetComponent<CharacterController>() != null && rig.GetComponent<CharacterController>().enabled,
                  "[7] the CharacterController on the Player is untouched and enabled");
            Check(rig.GetComponent<ThirdPersonPlayerController>() != null &&
                  rig.GetComponent<ThirdPersonPlayerController>().enabled,
                  "[7] the player controller is untouched and enabled");
            Check(rig.RigRoot.GetComponentsInChildren<Collider>(true).Length == 0,
                  "[7] the rig adds no colliders");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>Pairs of meshes on different bones whose bounds touch at rest - the seams.</summary>
        private void BuildSeams(Renderer[] renderers)
        {
            m_seams.Clear();
            for (int i = 0; i < renderers.Length; i++)
            {
                for (int j = i + 1; j < renderers.Length; j++)
                {
                    Renderer a = renderers[i];
                    Renderer b = renderers[j];
                    if (NearestBone(a.transform) == NearestBone(b.transform))
                        continue;

                    Bounds boundsA = a.bounds;
                    Bounds boundsB = b.bounds;
                    boundsA.Expand(SeamTolerance);
                    if (boundsA.Intersects(boundsB))
                        m_seams.Add(new Seam(a, b));
                }
            }
        }

        private void CheckSeams(string label, List<string> problems, int keep = 4)
        {
            foreach (Seam seam in m_seams)
            {
                Bounds boundsA = seam.A.bounds;
                Bounds boundsB = seam.B.bounds;
                boundsA.Expand(SeamTolerance);
                if (boundsA.Intersects(boundsB))
                    continue;

                if (IsSeamException(seam.A.name, seam.B.name))
                    continue;

                if (problems.Count < keep)
                    problems.Add(label + ": " + seam.A.name + " <-> " + seam.B.name + " came apart");
                else
                    return;
            }
        }

        private static bool IsSeamException(string a, string b)
        {
            foreach (string[] pair in SeamExceptions)
            {
                if ((pair[0] == a && pair[1] == b) || (pair[0] == b && pair[1] == a))
                    return true;
            }

            return false;
        }

        private void CheckGround(string label, List<string> problems, ref float worstSole)
        {
            foreach (HeroRigPart part in rig.Parts)
            {
                float bottom = part.Part.GetComponent<Renderer>().bounds.min.y - m_groundY;
                bool isFoot = part.Joint == HeroJoint.LeftFoot || part.Joint == HeroJoint.RightFoot ||
                              part.Joint == HeroJoint.LeftToes || part.Joint == HeroJoint.RightToes;
                if (isFoot)
                {
                    worstSole = Mathf.Max(worstSole, -bottom);
                    if (bottom < -SoleAllowance && problems.Count < 4)
                        problems.Add(label + ": " + part.Name + " sank " + (-bottom * 1000f).ToString("F1") + " mm");
                }
                else if (bottom < -0.001f && problems.Count < 4)
                {
                    problems.Add(label + ": " + part.Name + " sank " + (-bottom * 1000f).ToString("F1") +
                                 " mm below the rest floor");
                }
            }
        }

        private HashSet<Transform> ChainOf(HeroJoint joint)
        {
            var chain = new HashSet<Transform>();
            Transform bone = rig.GetBone(joint);
            if (bone == null)
                return chain;

            var stack = new Stack<Transform>();
            stack.Push(bone);
            while (stack.Count > 0)
            {
                Transform node = stack.Pop();
                chain.Add(node);
                foreach (Transform child in node)
                    stack.Push(child);
            }

            return chain;
        }

        private Transform NearestBone(Transform transform)
        {
            for (Transform node = transform; node != null && node != rig.RigRoot; node = node.parent)
            {
                if (rig.IsBone(node))
                    return node;
            }

            return null;
        }

        private static Vector3 AxisOf(Quaternion rotation)
        {
            Vector3 axis = new Vector3(rotation.x, rotation.y, rotation.z);
            return axis.sqrMagnitude > 1e-12f ? axis.normalized : Vector3.up;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
                   !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
        }

        private static string F(Vector3 value)
        {
            return "(" + value.x.ToString("F2") + ", " + value.y.ToString("F2") + ", " + value.z.ToString("F2") + ")";
        }

        private static string List(List<string> problems)
        {
            return problems.Count == 0 ? string.Empty : " | " + string.Join("; ", problems.ToArray());
        }

        private void Check(bool condition, string description)
        {
            m_total++;
            if (condition)
            {
                m_passed++;
                Debug.Log(Tag + " PASSED: " + description);
            }
            else
            {
                Debug.LogError(Tag + " FAILED: " + description);
            }
        }

        private struct RotationCase
        {
            public readonly string Label;
            public readonly HeroJoint Joint;
            public readonly Vector3 Euler;

            public RotationCase(string label, HeroJoint joint, float x, float y, float z)
            {
                Label = label;
                Joint = joint;
                Euler = new Vector3(x, y, z);
            }
        }

        private struct Seam
        {
            public readonly Renderer A;
            public readonly Renderer B;

            public Seam(Renderer a, Renderer b)
            {
                A = a;
                B = b;
            }
        }
    }
}
