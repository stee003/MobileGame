using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MobileGame.Player
{
    /// <summary>
    /// Every bone of the hero's humanoid skeleton, in rig order (a parent always comes before its
    /// child). Names match Unity's humanoid bone names so the avatar mapping is unambiguous.
    /// </summary>
    public enum HeroJoint
    {
        /// <summary>Marker for "no joint" - the parent of <see cref="Hips"/> and unmapped entries.</summary>
        None = 0,
        Hips, Spine, Chest, Neck,
        Head, HeadTopEnd, LeftShoulder, LeftUpperArm,
        LeftLowerArm, LeftHand, LeftThumbProximal, LeftThumbIntermediate,
        LeftThumbDistal, LeftIndexProximal, LeftIndexIntermediate, LeftIndexDistal,
        LeftMiddleProximal, LeftMiddleIntermediate, LeftMiddleDistal, LeftRingProximal,
        LeftRingIntermediate, LeftRingDistal, LeftLittleProximal, LeftLittleIntermediate,
        LeftLittleDistal, RightShoulder, RightUpperArm, RightLowerArm,
        RightHand, RightThumbProximal, RightThumbIntermediate, RightThumbDistal,
        RightIndexProximal, RightIndexIntermediate, RightIndexDistal, RightMiddleProximal,
        RightMiddleIntermediate, RightMiddleDistal, RightRingProximal, RightRingIntermediate,
        RightRingDistal, RightLittleProximal, RightLittleIntermediate, RightLittleDistal,
        LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes,
        RightUpperLeg, RightLowerLeg, RightFoot, RightToes
    }

    /// <summary>Which <see cref="Avatar"/> the rig builds for Unity's animation system.</summary>
    public enum HeroRigAvatarKind
    {
        /// <summary>
        /// Humanoid avatar (<c>AvatarBuilder.BuildHumanAvatar</c>): muscle-space retargeting, avatar
        /// masks, hand/foot IK and clip retargeting from any humanoid source. Falls back to
        /// <see cref="Generic"/> automatically when the skeleton does not validate.
        /// </summary>
        Humanoid,

        /// <summary>Generic avatar: transform curves only, no retargeting.</summary>
        Generic,

        /// <summary>No avatar at all - a plain transform hierarchy driven by script.</summary>
        None
    }

    /// <summary>Read-only description of one rig bone, exposed for tests and Editor tooling.</summary>
    public struct HeroRigJoint
    {
        public readonly HeroJoint Joint;
        public readonly string BoneName;
        public readonly HeroJoint Parent;

        /// <summary>Rest position in <see cref="HeroRig.RigRoot"/> local space (the visual pose).</summary>
        public readonly Vector3 RestPosition;

        /// <summary>Humanoid mapping, or <see cref="HumanBodyBones.LastBone"/> for helper bones.</summary>
        public readonly HumanBodyBones HumanBone;

        /// <summary>Diagnostic rotation limits in degrees (x, y, z Euler, bone local space).</summary>
        public readonly Vector3 LimitMin;
        public readonly Vector3 LimitMax;

        public bool IsMapped => HumanBone != HumanBodyBones.LastBone;

        public HeroRigJoint(HeroJoint joint, string boneName, HeroJoint parent, Vector3 restPosition,
                            HumanBodyBones humanBone, Vector3 limitMin, Vector3 limitMax)
        {
            Joint = joint;
            BoneName = boneName;
            Parent = parent;
            RestPosition = restPosition;
            HumanBone = humanBone;
            LimitMin = limitMin;
            LimitMax = limitMax;
        }
    }

    /// <summary>One hero mesh bound to one bone (rigid bind - see <see cref="HeroRig"/>).</summary>
    public struct HeroRigPart
    {
        public readonly string Name;
        public readonly HeroJoint Joint;
        public readonly Transform Bone;
        public readonly Transform Part;

        /// <summary>World transform the part had immediately before it was parented to the bone.</summary>
        public readonly Vector3 RestWorldPosition;
        public readonly Quaternion RestWorldRotation;
        public readonly Vector3 RestWorldScale;

        public HeroRigPart(string name, HeroJoint joint, Transform bone, Transform part,
                           Vector3 restWorldPosition, Quaternion restWorldRotation, Vector3 restWorldScale)
        {
            Name = name;
            Joint = joint;
            Bone = bone;
            Part = part;
            RestWorldPosition = restWorldPosition;
            RestWorldRotation = restWorldRotation;
            RestWorldScale = restWorldScale;
        }
    }

    /// <summary>
    /// Builds a real humanoid skeleton for the primitive hero (<see cref="HeroCharacterVisual"/>) and
    /// wires it into Unity's animation system.
    ///
    /// <para><b>Skeleton</b> - 52 bones with Unity's humanoid names: Hips, Spine, Chest, Neck, Head
    /// (+ HeadTop_End helper), Left/Right Shoulder (clavicle), UpperArm, LowerArm, Hand, 15 finger
    /// bones per hand (Thumb/Index/Middle/Ring/Little x Proximal/Intermediate/Distal), and
    /// UpperLeg, LowerLeg, Foot, Toes per leg. 51 of them are mapped into the
    /// <see cref="HumanDescription"/>; only HeadTop_End is a helper.</para>
    ///
    /// <para><b>Binding</b> - the hero is built from separate primitives, so every mesh is rigidly
    /// bound to exactly one bone (its world transform is preserved while reparenting, so rigging
    /// never moves the model). Rigid binding is the right choice here: the parts are hard-surface
    /// costume pieces and boots, there is no single watertight mesh to skin, and it costs no
    /// skinning matrices on mobile. Rounded capsule joints already overlap at the hips, knees,
    /// elbows and wrists, so those joints bend without opening a gap.</para>
    ///
    /// <para><b>Deformation fixes applied while binding</b> (see
    /// <c>Tools/HeroRigVerification/REPORT.md</c> for the measurements behind them):
    /// <list type="bullet">
    /// <item>The <c>Cape</c> sheet spans Hips, Spine and Chest, so it was measured both split in two
    /// and whole. Splitting it tears the sheet by up to 72 mm as soon as the chest bends, because the
    /// two pieces pivot about different bones; whole, and bound to the Chest together with its
    /// collar, it never tears at all (its only contact is the chest it hangs from). It therefore
    /// ships as one rigid sheet on the Chest.</item>
    /// <item><c>Abdomen</c> is extended 0.07 m down and 0.06 m up so the waist seams stay closed
    /// when the pelvis or the chest pitches - the extra volume is entirely inside the Hips and Chest
    /// boxes, so the silhouette does not change.</item>
    /// <item><c>Neck</c> is extended 0.06 m down into the chest box for the same reason.</item>
    /// <item>The glove cuff is bound to the hand (it is the wrist seam cover) and the boot cuff and
    /// strap to the foot (the ankle seam cover); the shoulder pads follow the upper arm, not the
    /// chest, so they travel with the arm.</item>
    /// </list></para>
    ///
    /// <para><b>Animation setup</b> - an <see cref="Animator"/> sits on the rig root (never on the
    /// Player root, so it cannot fight <c>ThirdPersonPlayerController</c>), with
    /// <c>applyRootMotion = false</c> because the <c>CharacterController</c> owns the root motion,
    /// <c>cullingMode = AlwaysAnimate</c> so bones keep evaluating off screen, and the humanoid
    /// avatar built from a true T-pose: the arms are rotated out to the sides before
    /// <c>AvatarBuilder.BuildHumanAvatar</c> runs and returned to the resting pose straight
    /// afterwards, so the avatar's bind pose is a valid T-pose while the hero still stands relaxed.
    /// No clips are created - the rig ships without animation content.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(HeroCharacterVisual))]
    [DefaultExecutionOrder(-40)] // after HeroCharacterVisual (-50), before the player controller
    public class HeroRig : MonoBehaviour
    {
        /// <summary>Name of the generated skeleton root; it is also the avatar root.</summary>
        public const string RigRootName = "HeroRig";

        /// <summary>Player layer (8) - the same layer the hero meshes use.</summary>
        public const int PlayerLayer = 8;

        [Header("Build")]
        [Tooltip("Builds the skeleton in Awake/OnEnable. Turn off to rig by hand from the context menu.")]
        [SerializeField] private bool buildOnEnable = true;

        [Tooltip("Humanoid = retargetable muscle-space avatar (recommended). Generic = transform curves only.")]
        [SerializeField] private HeroRigAvatarKind avatarKind = HeroRigAvatarKind.Humanoid;

        [Tooltip("Extends Abdomen and Neck inside their neighbours so the waist and neck seams stay closed.")]
        [SerializeField] private bool applyFitAdjustments = true;

        [Tooltip("Logs the build report (bone count, mapping, binding, avatar state).")]
        [SerializeField] private bool logBuildReport = true;

        [Header("Animation")]
        [Tooltip("Optional controller. Left empty on purpose: the rig ships without animation clips.")]
        [SerializeField] private RuntimeAnimatorController animatorController;

        [Tooltip("Must stay false - the CharacterController owns the root motion of this hero.")]
        [SerializeField] private bool applyRootMotion = false;

        private readonly Dictionary<HeroJoint, Transform> m_bones = new Dictionary<HeroJoint, Transform>();
        private readonly Dictionary<HeroJoint, Quaternion> m_restLocalRotations = new Dictionary<HeroJoint, Quaternion>();
        private readonly List<HeroRigPart> m_parts = new List<HeroRigPart>();
        private readonly List<string> m_unboundParts = new List<string>();

        private Transform m_rigRoot;
        private Animator m_animator;
        private Avatar m_avatar;
        private HeroRigAvatarKind m_avatarKindUsed = HeroRigAvatarKind.None;
        private bool m_built;
        private bool m_attempted;
        private string m_report = string.Empty;

        // ------------------------------------------------------------------
        // Public state
        // ------------------------------------------------------------------

        /// <summary>True once the skeleton exists and the parts are bound.</summary>
        public bool IsBuilt => m_built && m_rigRoot != null;

        /// <summary>Skeleton root (avatar root). Null before the rig is built.</summary>
        public Transform RigRoot => m_rigRoot;

        /// <summary>The Animator driving the skeleton. Null before the rig is built.</summary>
        public Animator Animator => m_animator;

        /// <summary>The avatar handed to the Animator, or null for <see cref="HeroRigAvatarKind.None"/>.</summary>
        public Avatar Avatar => m_avatar;

        /// <summary>What was actually built (a Humanoid request can fall back to Generic).</summary>
        public HeroRigAvatarKind AvatarKindUsed => m_avatarKindUsed;

        /// <summary>Number of bones in the skeleton.</summary>
        public int BoneCount => m_bones.Count;

        /// <summary>Number of hero meshes bound to a bone.</summary>
        public int BoundPartCount => m_parts.Count;

        /// <summary>Hero meshes that no bone claims - they would stay frozen during animation.</summary>
        public IReadOnlyList<string> UnboundParts => m_unboundParts;

        /// <summary>Every bound part, in bind order.</summary>
        public IReadOnlyList<HeroRigPart> Parts => m_parts;

        /// <summary>Human-readable build report (also written to the console when enabled).</summary>
        public string BuildReport => m_report;

        /// <summary>Every joint of the rig, in rig order.</summary>
        public static IEnumerable<HeroJoint> AllJoints
        {
            get
            {
                for (int i = 0; i < JointTable.Length; i++)
                    yield return JointTable[i].Joint;
            }
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (buildOnEnable)
                BuildIfNeeded();
        }

        private void Start()
        {
            // Awake order is only guaranteed through DefaultExecutionOrder in Play Mode; in edit mode
            // OnEnable ordering is not, so try once more before giving up on the visual.
            if (buildOnEnable)
                BuildIfNeeded();
        }

#if UNITY_EDITOR
        private void OnEnable()
        {
            if (!Application.isPlaying && buildOnEnable)
                BuildIfNeeded();
        }
#endif

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Builds the skeleton if it does not exist yet. Safe to call repeatedly.</summary>
        public bool BuildIfNeeded()
        {
            if (m_built && m_rigRoot != null)
                return true;

            HeroCharacterVisual visual = GetComponent<HeroCharacterVisual>();
            if (visual == null)
            {
                Debug.LogError("[HeroRig] HeroCharacterVisual is missing - the rig needs the hero meshes to bind.", this);
                return false;
            }

            Transform visualRoot = visual.VisualRoot;
            if (visualRoot == null)
            {
                if (!m_attempted)
                {
                    m_attempted = true;
                    Debug.LogWarning("[HeroRig] The hero visual is not built yet; the rig will be built on the next attempt.", this);
                }

                return false;
            }

            m_attempted = true;

            Transform existing = visualRoot.Find(RigRootName);
            if (existing != null)
            {
                Adopt(existing);
                return true;
            }

            Create(visualRoot);
            return m_built;
        }

        /// <summary>Destroys the generated skeleton and builds it again from the current hero meshes.</summary>
        [ContextMenu("Rig: Rebuild")]
        public void Rebuild()
        {
            HeroCharacterVisual visual = GetComponent<HeroCharacterVisual>();
            if (visual != null && visual.VisualRoot != null)
            {
                Transform existing = visual.VisualRoot.Find(RigRootName);
                if (existing != null)
                {
                    if (Application.isPlaying)
                        Destroy(existing.gameObject);
                    else
                        DestroyImmediate(existing.gameObject);
                }
            }

            m_bones.Clear();
            m_restLocalRotations.Clear();
            m_parts.Clear();
            m_unboundParts.Clear();
            m_rigRoot = null;
            m_animator = null;
            m_avatar = null;
            m_avatarKindUsed = HeroRigAvatarKind.None;
            m_built = false;
            m_attempted = false;
            BuildIfNeeded();
        }

        /// <summary>Bone transform of a joint, or null when the rig is not built.</summary>
        public Transform GetBone(HeroJoint joint)
        {
            Transform bone;
            return m_bones.TryGetValue(joint, out bone) ? bone : null;
        }

        /// <summary>Bone transform of a joint when it exists.</summary>
        public bool TryGetBone(HeroJoint joint, out Transform bone)
        {
            return m_bones.TryGetValue(joint, out bone);
        }

        /// <summary>Finds a joint by bone name (case sensitive, exactly as in the hierarchy).</summary>
        public bool TryGetJoint(string boneName, out HeroJoint joint)
        {
            for (int i = 0; i < JointTable.Length; i++)
            {
                if (JointTable[i].BoneName == boneName)
                {
                    joint = JointTable[i].Joint;
                    return true;
                }
            }

            joint = HeroJoint.None;
            return false;
        }

        /// <summary>Rest position, parent, humanoid mapping and limits of a joint.</summary>
        public static bool TryGetJointInfo(HeroJoint joint, out HeroRigJoint info)
        {
            for (int i = 0; i < JointTable.Length; i++)
            {
                if (JointTable[i].Joint == joint)
                {
                    info = JointTable[i].ToPublic();
                    return true;
                }
            }

            info = default(HeroRigJoint);
            return false;
        }

        /// <summary>Rest local rotation of a bone (identity for this rig, kept general).</summary>
        public Quaternion GetRestLocalRotation(HeroJoint joint)
        {
            Quaternion rotation;
            return m_restLocalRotations.TryGetValue(joint, out rotation) ? rotation : Quaternion.identity;
        }

        /// <summary>Returns every bone to the pose the hero was authored in.</summary>
        [ContextMenu("Rig: Apply Rest Pose")]
        public void ApplyRestPose()
        {
            for (int i = 0; i < JointTable.Length; i++)
            {
                Transform bone = GetBone(JointTable[i].Joint);
                if (bone != null)
                    bone.localRotation = GetRestLocalRotation(JointTable[i].Joint);
            }
        }

        /// <summary>
        /// Sets one bone's local rotation. Returns false when the rig is not built or the joint is
        /// unknown; nothing else is touched, so a test can isolate a single joint.
        /// </summary>
        public bool SetJointLocalRotation(HeroJoint joint, Quaternion localRotation)
        {
            Transform bone = GetBone(joint);
            if (bone == null)
                return false;

            bone.localRotation = localRotation;
            return true;
        }

        /// <summary>Clamps a local Euler rotation (degrees) into the joint's diagnostic limits.</summary>
        public static Vector3 ClampToLimits(HeroJoint joint, Vector3 eulerDegrees)
        {
            HeroRigJoint info;
            if (!TryGetJointInfo(joint, out info))
                return eulerDegrees;

            return new Vector3(
                Mathf.Clamp(eulerDegrees.x, info.LimitMin.x, info.LimitMax.x),
                Mathf.Clamp(eulerDegrees.y, info.LimitMin.y, info.LimitMax.y),
                Mathf.Clamp(eulerDegrees.z, info.LimitMin.z, info.LimitMax.z));
        }

        /// <summary>
        /// Resolves a bone through the built avatar, which is the only proof that the humanoid
        /// mapping points at the intended transform.
        /// </summary>
        public bool TryGetHumanBone(HumanBodyBones humanBone, out Transform bone)
        {
            bone = null;
            if (m_animator == null || m_avatar == null || !m_avatar.isHuman)
                return false;

            bone = m_animator.GetBoneTransform(humanBone);
            return bone != null;
        }

        /// <summary>True when a transform is this rig's skeleton root or one of its bones.</summary>
        public bool IsBone(Transform transform)
        {
            if (transform == null)
                return false;

            if (transform == m_rigRoot)
                return true;

            foreach (KeyValuePair<HeroJoint, Transform> entry in m_bones)
            {
                if (entry.Value == transform)
                    return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------

        private void Create(Transform visualRoot)
        {
            var partsByName = new Dictionary<string, Transform>();
            var duplicates = new List<string>();
            Renderer[] renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                if (partsByName.ContainsKey(renderer.name))
                    duplicates.Add(renderer.name);
                else
                    partsByName[renderer.name] = renderer.transform;
            }

            if (duplicates.Count > 0)
                Debug.LogError("[HeroRig] Duplicate hero part names, only the first can be bound: " +
                               string.Join(", ", duplicates.ToArray()), this);

            int authoredParts = partsByName.Count;
            int fitted = applyFitAdjustments ? ApplyFits(partsByName) : 0;

            // The skeleton root is the avatar root, so it must sit at the origin of the visual and
            // carry no scale of its own.
            var rootObject = new GameObject(RigRootName);
            m_rigRoot = rootObject.transform;
            m_rigRoot.SetParent(visualRoot, false);
            m_rigRoot.localPosition = Vector3.zero;
            m_rigRoot.localRotation = Quaternion.identity;
            m_rigRoot.localScale = Vector3.one;
            m_rigRoot.gameObject.layer = PlayerLayer;

            CreateBones();

            // World transforms are snapshotted before reparenting so the verification suites can
            // prove that binding itself never moves a single mesh.
            var snapshot = SnapshotParts(partsByName);
            BindParts(partsByName, snapshot);
            RecordRestPose();

            string poseNote = PoseArmsForAvatar();
            string avatarNote;
            m_avatar = BuildAvatar(out m_avatarKindUsed, out avatarNote);
            ApplyRestPose();
            ConfigureAnimator();

            m_built = true;
            m_report = BuildReportText(authoredParts, fitted, poseNote, avatarNote);
            if (logBuildReport)
                Debug.Log(m_report, this);

            if (m_unboundParts.Count > 0)
                Debug.LogError("[HeroRig] " + m_unboundParts.Count + " hero mesh(es) are bound to no bone and would " +
                               "stay frozen during animation: " + string.Join(", ", m_unboundParts.ToArray()), this);
        }

        private void CreateBones()
        {
            m_bones.Clear();
            for (int i = 0; i < JointTable.Length; i++)
            {
                JointSpec spec = JointTable[i];
                var boneObject = new GameObject(spec.BoneName);
                boneObject.layer = PlayerLayer;

                Transform bone = boneObject.transform;
                Transform parent = m_rigRoot;
                Vector3 parentPosition = Vector3.zero;
                if (spec.Parent != HeroJoint.None)
                {
                    if (!m_bones.TryGetValue(spec.Parent, out parent))
                    {
                        Debug.LogError("[HeroRig] Bone '" + spec.BoneName + "' lists '" + spec.Parent +
                                       "' as its parent, which has not been created yet - the table is out of order.", this);
                        parent = m_rigRoot;
                    }
                    else
                    {
                        parentPosition = JointLookup[spec.Parent].Position;
                    }
                }

                bone.SetParent(parent, false);
                bone.localPosition = spec.Position - parentPosition;
                bone.localRotation = Quaternion.identity;
                bone.localScale = Vector3.one;
                m_bones[spec.Joint] = bone;
            }
        }

        private void BindParts(Dictionary<string, Transform> partsByName, Dictionary<string, PartSnapshot> snapshot)
        {
            m_parts.Clear();
            m_unboundParts.Clear();
            var claimed = new HashSet<string>();

            for (int i = 0; i < BindTable.Length; i++)
            {
                BindSpec spec = BindTable[i];
                Transform part;
                if (!partsByName.TryGetValue(spec.PartName, out part) || part == null)
                {
                    Debug.LogError("[HeroRig] The bind table references '" + spec.PartName +
                                   "', which is not in the hero visual.", this);
                    continue;
                }

                Transform bone = GetBone(spec.Joint);
                if (bone == null)
                {
                    Debug.LogError("[HeroRig] '" + spec.PartName + "' wants the unknown bone " + spec.Joint + ".", this);
                    continue;
                }

                // worldPositionStays: the mesh keeps the exact transform it was authored with.
                part.SetParent(bone, true);
                claimed.Add(spec.PartName);

                PartSnapshot rest = snapshot[spec.PartName];
                m_parts.Add(new HeroRigPart(spec.PartName, spec.Joint, bone, part,
                                            rest.Position, rest.Rotation, rest.Scale));
            }

            foreach (KeyValuePair<string, Transform> entry in partsByName)
            {
                if (!claimed.Contains(entry.Key))
                    m_unboundParts.Add(entry.Key);
            }
        }

        private static Dictionary<string, PartSnapshot> SnapshotParts(Dictionary<string, Transform> partsByName)
        {
            var snapshot = new Dictionary<string, PartSnapshot>();
            foreach (KeyValuePair<string, Transform> entry in partsByName)
            {
                Transform part = entry.Value;
                snapshot[entry.Key] = new PartSnapshot(part.position, part.rotation, part.lossyScale);
            }

            return snapshot;
        }

        private void RecordRestPose()
        {
            m_restLocalRotations.Clear();
            for (int i = 0; i < JointTable.Length; i++)
            {
                Transform bone = GetBone(JointTable[i].Joint);
                if (bone != null)
                    m_restLocalRotations[JointTable[i].Joint] = bone.localRotation;
            }
        }

        // ------------------------------------------------------------------
        // Deformation fix: interior-only volume extensions (see FitTable at the bottom)
        // ------------------------------------------------------------------

        private int ApplyFits(Dictionary<string, Transform> partsByName)
        {
            int applied = 0;
            for (int i = 0; i < FitTable.Length; i++)
            {
                FitSpec spec = FitTable[i];
                Transform part;
                if (!partsByName.TryGetValue(spec.PartName, out part) || part == null)
                {
                    Debug.LogError("[HeroRig] The fit table references '" + spec.PartName +
                                   "', which is not in the hero visual.", this);
                    continue;
                }

                part.localPosition += spec.PositionDelta;
                part.localScale += spec.ScaleDelta;
                applied++;
            }

            return applied;
        }

        // ------------------------------------------------------------------
        // Avatar
        // ------------------------------------------------------------------

        /// <summary>Rotation applied to each upper arm to reach the T-pose the avatar is built from.</summary>
        public const float TPoseArmAngle = 90f;

        private string PoseArmsForAvatar()
        {
            if (avatarKind == HeroRigAvatarKind.None)
                return "T-pose skipped (no avatar)";

            // The hero is authored with the arms hanging down. A humanoid avatar has to be created
            // from a T-pose, so the upper arms are rotated out to the sides first - in the rig's own
            // space, never world space, because the Player root is yawed 180 degrees in the arena -
            // and ApplyRestPose() puts them back immediately after the avatar exists.
            SetJointLocalRotation(HeroJoint.LeftUpperArm, Quaternion.Euler(0f, 0f, -TPoseArmAngle));
            SetJointLocalRotation(HeroJoint.RightUpperArm, Quaternion.Euler(0f, 0f, TPoseArmAngle));

            Vector3 left = ArmDirection(HeroJoint.LeftUpperArm, HeroJoint.LeftHand);
            Vector3 right = ArmDirection(HeroJoint.RightUpperArm, HeroJoint.RightHand);
            bool valid = left.x < -0.9f && right.x > 0.9f &&
                         Mathf.Abs(left.y) < 0.1f && Mathf.Abs(right.y) < 0.1f;

            return valid
                ? "T-pose arms out (" + F(left) + " / " + F(right) + ")"
                : "T-POSE INVALID - arms are not horizontal (" + F(left) + " / " + F(right) + ")";
        }

        private Vector3 ArmDirection(HeroJoint shoulder, HeroJoint hand)
        {
            Transform shoulderBone = GetBone(shoulder);
            Transform handBone = GetBone(hand);
            if (shoulderBone == null || handBone == null)
                return Vector3.zero;

            Vector3 from = m_rigRoot.InverseTransformPoint(shoulderBone.position);
            Vector3 to = m_rigRoot.InverseTransformPoint(handBone.position);
            Vector3 direction = to - from;
            return direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.zero;
        }

        private HumanDescription BuildHumanDescription()
        {
            var human = new List<HumanBone>(JointTable.Length);
            for (int i = 0; i < JointTable.Length; i++)
            {
                JointSpec spec = JointTable[i];
                if (!spec.IsMapped)
                    continue;

                human.Add(new HumanBone
                {
                    boneName = spec.BoneName,
                    humanName = HumanTrait.BoneName[(int)spec.HumanBone],
                    limit = new HumanLimit { useDefaultValues = true }
                });
            }

            // The skeleton must list every transform under the avatar root, root first.
            Transform[] transforms = m_rigRoot.GetComponentsInChildren<Transform>(true);
            var skeleton = new SkeletonBone[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                skeleton[i] = new SkeletonBone
                {
                    name = transforms[i].name,
                    position = transforms[i].localPosition,
                    rotation = transforms[i].localRotation,
                    scale = transforms[i].localScale
                };
            }

            return new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };
        }

        private Avatar BuildAvatar(out HeroRigAvatarKind used, out string note)
        {
            used = HeroRigAvatarKind.None;
            note = "no avatar (script-driven skeleton)";

            if (avatarKind == HeroRigAvatarKind.None)
                return null;

            if (avatarKind == HeroRigAvatarKind.Humanoid)
            {
                try
                {
                    HumanDescription description = BuildHumanDescription();
                    Avatar avatar = AvatarBuilder.BuildHumanAvatar(m_rigRoot.gameObject, description);
                    if (avatar != null && avatar.isValid && avatar.isHuman)
                    {
                        used = HeroRigAvatarKind.Humanoid;
                        note = "humanoid avatar (valid), " + description.human.Length + " mapped bones";
                        return avatar;
                    }

                    Debug.LogWarning("[HeroRig] AvatarBuilder did not accept the humanoid description" +
                                     (avatar == null ? " (returned null)" : " (isValid=" + avatar.isValid +
                                      ", isHuman=" + avatar.isHuman) + ") - falling back to a generic avatar.", this);
                }
                catch (System.Exception exception)
                {
                    Debug.LogWarning("[HeroRig] Building the humanoid avatar threw " + exception.GetType().Name +
                                     ": " + exception.Message + " - falling back to a generic avatar.", this);
                }
            }

            try
            {
                Avatar generic = AvatarBuilder.BuildGenericAvatar(m_rigRoot.gameObject, string.Empty);
                if (generic != null && generic.isValid)
                {
                    used = HeroRigAvatarKind.Generic;
                    note = "generic avatar (no humanoid retargeting)";
                    return generic;
                }

                Debug.LogWarning("[HeroRig] AvatarBuilder did not accept the generic skeleton either - the rig " +
                                 "stays script driven.", this);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning("[HeroRig] Building the generic avatar threw " + exception.GetType().Name +
                                 ": " + exception.Message + " - the rig stays script driven.", this);
            }

            return null;
        }

        private void ConfigureAnimator()
        {
            m_animator = m_rigRoot.GetComponent<Animator>();
            if (m_animator == null)
                m_animator = m_rigRoot.gameObject.AddComponent<Animator>();

            m_animator.avatar = m_avatar;
            // The CharacterController (and ThirdPersonPlayerController) owns the root transform, so
            // root motion must never be applied by the Animator.
            m_animator.applyRootMotion = applyRootMotion;
            // The hero is a plain MeshRenderer hierarchy: without this the skeleton stops updating as
            // soon as the renderers leave the frustum, which silently breaks animation and tests.
            m_animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            m_animator.updateMode = AnimatorUpdateMode.Normal;
            m_animator.runtimeAnimatorController = animatorController;
            m_animator.logWarnings = true;
        }

        // ------------------------------------------------------------------
        // Adoption (a hierarchy that was saved with a scene) and reporting
        // ------------------------------------------------------------------

        private void Adopt(Transform existing)
        {
            m_rigRoot = existing;
            m_bones.Clear();
            m_parts.Clear();
            m_unboundParts.Clear();

            Transform[] transforms = existing.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < JointTable.Length; i++)
            {
                for (int t = 0; t < transforms.Length; t++)
                {
                    if (transforms[t].name == JointTable[i].BoneName)
                    {
                        m_bones[JointTable[i].Joint] = transforms[t];
                        break;
                    }
                }
            }

            RecordRestPose();

            foreach (Transform transform in transforms)
            {
                Renderer renderer = transform.GetComponent<Renderer>();
                if (renderer == null)
                    continue;

                Transform bone = transform.parent;
                HeroJoint joint = HeroJoint.None;
                while (bone != null && joint == HeroJoint.None)
                {
                    HeroJoint candidate;
                    if (TryGetJoint(bone.name, out candidate))
                        joint = candidate;
                    else
                        bone = bone.parent;
                }

                m_parts.Add(new HeroRigPart(transform.name, joint, bone, transform,
                                            transform.position, transform.rotation, transform.lossyScale));
                if (joint == HeroJoint.None)
                    m_unboundParts.Add(transform.name);
            }

            m_animator = existing.GetComponent<Animator>();
            m_avatar = m_animator != null ? m_animator.avatar : null;
            m_avatarKindUsed = m_avatar == null
                ? HeroRigAvatarKind.None
                : (m_avatar.isHuman ? HeroRigAvatarKind.Humanoid : HeroRigAvatarKind.Generic);
            m_built = true;
            m_report = "[HeroRig] adopted the existing '" + RigRootName + "' hierarchy: " + m_bones.Count +
                       " bones, " + m_parts.Count + " meshes, avatar " + m_avatarKindUsed;
            if (logBuildReport)
                Debug.Log(m_report, this);
        }

        private string BuildReportText(int authoredParts, int fitted, string poseNote, string avatarNote)
        {
            int mapped = 0;
            for (int i = 0; i < JointTable.Length; i++)
            {
                if (JointTable[i].IsMapped)
                    mapped++;
            }

            var report = new StringBuilder();
            report.Append("[HeroRig] built ").Append(m_bones.Count).Append(" bones (")
                  .Append(mapped).Append(" mapped to the avatar), bound ").Append(m_parts.Count)
                  .Append('/').Append(authoredParts).Append(" hero meshes. Avatar: ").Append(avatarNote)
                  .Append(". ").Append(poseNote).Append('.')
                  .Append(" Root motion ").Append(applyRootMotion ? "ON (check this!)" : "off")
                  .Append(", culling AlwaysAnimate.")
                  .Append(" Deformation fixes: ").Append(fitted)
                  .Append(" interior extension(s), cape bound whole to the chest.");
            return report.ToString();
        }

        private static string F(Vector3 value)
        {
            return "(" + value.x.ToString("F2") + ", " + value.y.ToString("F2") + ", " + value.z.ToString("F2") + ")";
        }

        private struct PartSnapshot
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly Vector3 Scale;

            public PartSnapshot(Vector3 position, Quaternion rotation, Vector3 scale)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
            }
        }

        // ------------------------------------------------------------------
        // Rig data - the single source of truth, also parsed by
        // Tools/HeroRigVerification/*.py so the headless checks cannot drift.
        //
        // J(joint, bone name, parent, x, y, z, humanoid mapping,
        //   x min, x max, y min, y max, z min, z max)   <- diagnostic limits in degrees
        //
        // Positions are in HeroVisual space: the hero faces +Z, feet soles at y = 0, and the
        // character's left side is -X. Limits are bone-local Euler ranges used by the verification
        // sweeps; they are deliberately generous but stay inside what the rigid geometry survives.
        // ------------------------------------------------------------------

        private struct JointSpec
        {
            public readonly HeroJoint Joint;
            public readonly string BoneName;
            public readonly HeroJoint Parent;
            public readonly Vector3 Position;
            public readonly HumanBodyBones HumanBone;
            public readonly Vector3 LimitMin;
            public readonly Vector3 LimitMax;

            public bool IsMapped => HumanBone != HumanBodyBones.LastBone;

            public HeroRigJoint ToPublic()
            {
                return new HeroRigJoint(Joint, BoneName, Parent, Position, HumanBone, LimitMin, LimitMax);
            }
        }

        private static JointSpec J(HeroJoint joint, string boneName, HeroJoint parent,
                                   float x, float y, float z, HumanBodyBones humanBone,
                                   float xMin, float xMax, float yMin, float yMax, float zMin, float zMax)
        {
            return new JointSpec
            {
                Joint = joint,
                BoneName = boneName,
                Parent = parent,
                Position = new Vector3(x, y, z),
                HumanBone = humanBone,
                LimitMin = new Vector3(xMin, yMin, zMin),
                LimitMax = new Vector3(xMax, yMax, zMax)
            };
        }

        private static readonly JointSpec[] JointTable =
        {
            J(HeroJoint.Hips, "Hips", HeroJoint.None, 0.000f, 0.970f, 0.000f, HumanBodyBones.Hips, -25.0f, 25.0f, -30.0f, 30.0f, -20.0f, 20.0f),
            J(HeroJoint.Spine, "Spine", HeroJoint.Hips, 0.000f, 1.130f, 0.000f, HumanBodyBones.Spine, -30.0f, 30.0f, -25.0f, 25.0f, -20.0f, 20.0f),
            J(HeroJoint.Chest, "Chest", HeroJoint.Spine, 0.000f, 1.370f, 0.000f, HumanBodyBones.Chest, -25.0f, 25.0f, -25.0f, 25.0f, -18.0f, 18.0f),
            J(HeroJoint.Neck, "Neck", HeroJoint.Chest, 0.000f, 1.520f, 0.000f, HumanBodyBones.Neck, -30.0f, 25.0f, -40.0f, 40.0f, -25.0f, 25.0f),
            J(HeroJoint.Head, "Head", HeroJoint.Neck, 0.000f, 1.600f, 0.000f, HumanBodyBones.Head, -25.0f, 25.0f, -50.0f, 50.0f, -25.0f, 25.0f),
            J(HeroJoint.HeadTopEnd, "HeadTop_End", HeroJoint.Head, 0.000f, 1.920f, 0.000f, HumanBodyBones.LastBone, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f),
            J(HeroJoint.LeftShoulder, "LeftShoulder", HeroJoint.Chest, -0.090f, 1.460f, 0.000f, HumanBodyBones.LeftShoulder, -15.0f, 15.0f, -15.0f, 15.0f, -15.0f, 25.0f),
            J(HeroJoint.LeftUpperArm, "LeftUpperArm", HeroJoint.LeftShoulder, -0.310f, 1.440f, 0.000f, HumanBodyBones.LeftUpperArm, -75.0f, 75.0f, -80.0f, 80.0f, -150.0f, 20.0f),
            J(HeroJoint.LeftLowerArm, "LeftLowerArm", HeroJoint.LeftUpperArm, -0.310f, 1.175f, 0.000f, HumanBodyBones.LeftLowerArm, -145.0f, 0.0f, -80.0f, 80.0f, -10.0f, 10.0f),
            J(HeroJoint.LeftHand, "LeftHand", HeroJoint.LeftLowerArm, -0.310f, 0.875f, 0.000f, HumanBodyBones.LeftHand, -70.0f, 70.0f, -25.0f, 25.0f, -20.0f, 20.0f),
            J(HeroJoint.LeftThumbProximal, "LeftThumbProximal", HeroJoint.LeftHand, -0.265f, 0.835f, 0.030f, HumanBodyBones.LeftThumbProximal, -30.0f, 60.0f, -40.0f, 40.0f, -50.0f, 30.0f),
            J(HeroJoint.LeftThumbIntermediate, "LeftThumbIntermediate", HeroJoint.LeftThumbProximal, -0.255f, 0.805f, 0.052f, HumanBodyBones.LeftThumbIntermediate, 0.0f, 60.0f, -25.0f, 25.0f, -25.0f, 25.0f),
            J(HeroJoint.LeftThumbDistal, "LeftThumbDistal", HeroJoint.LeftThumbIntermediate, -0.250f, 0.780f, 0.070f, HumanBodyBones.LeftThumbDistal, 0.0f, 50.0f, -20.0f, 20.0f, -20.0f, 20.0f),
            J(HeroJoint.LeftIndexProximal, "LeftIndexProximal", HeroJoint.LeftHand, -0.345f, 0.800f, 0.020f, HumanBodyBones.LeftIndexProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.LeftIndexIntermediate, "LeftIndexIntermediate", HeroJoint.LeftIndexProximal, -0.345f, 0.770f, 0.030f, HumanBodyBones.LeftIndexIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftIndexDistal, "LeftIndexDistal", HeroJoint.LeftIndexIntermediate, -0.345f, 0.745f, 0.037f, HumanBodyBones.LeftIndexDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftMiddleProximal, "LeftMiddleProximal", HeroJoint.LeftHand, -0.322f, 0.800f, 0.022f, HumanBodyBones.LeftMiddleProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.LeftMiddleIntermediate, "LeftMiddleIntermediate", HeroJoint.LeftMiddleProximal, -0.322f, 0.768f, 0.032f, HumanBodyBones.LeftMiddleIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftMiddleDistal, "LeftMiddleDistal", HeroJoint.LeftMiddleIntermediate, -0.322f, 0.742f, 0.039f, HumanBodyBones.LeftMiddleDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftRingProximal, "LeftRingProximal", HeroJoint.LeftHand, -0.300f, 0.800f, 0.020f, HumanBodyBones.LeftRingProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.LeftRingIntermediate, "LeftRingIntermediate", HeroJoint.LeftRingProximal, -0.300f, 0.770f, 0.030f, HumanBodyBones.LeftRingIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftRingDistal, "LeftRingDistal", HeroJoint.LeftRingIntermediate, -0.300f, 0.745f, 0.037f, HumanBodyBones.LeftRingDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftLittleProximal, "LeftLittleProximal", HeroJoint.LeftHand, -0.281f, 0.795f, 0.018f, HumanBodyBones.LeftLittleProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.LeftLittleIntermediate, "LeftLittleIntermediate", HeroJoint.LeftLittleProximal, -0.281f, 0.772f, 0.026f, HumanBodyBones.LeftLittleIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftLittleDistal, "LeftLittleDistal", HeroJoint.LeftLittleIntermediate, -0.281f, 0.752f, 0.032f, HumanBodyBones.LeftLittleDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightShoulder, "RightShoulder", HeroJoint.Chest, 0.090f, 1.460f, 0.000f, HumanBodyBones.RightShoulder, -15.0f, 15.0f, -15.0f, 15.0f, -25.0f, 15.0f),
            J(HeroJoint.RightUpperArm, "RightUpperArm", HeroJoint.RightShoulder, 0.310f, 1.440f, 0.000f, HumanBodyBones.RightUpperArm, -75.0f, 75.0f, -80.0f, 80.0f, -20.0f, 150.0f),
            J(HeroJoint.RightLowerArm, "RightLowerArm", HeroJoint.RightUpperArm, 0.310f, 1.175f, 0.000f, HumanBodyBones.RightLowerArm, -145.0f, 0.0f, -80.0f, 80.0f, -10.0f, 10.0f),
            J(HeroJoint.RightHand, "RightHand", HeroJoint.RightLowerArm, 0.310f, 0.875f, 0.000f, HumanBodyBones.RightHand, -70.0f, 70.0f, -25.0f, 25.0f, -20.0f, 20.0f),
            J(HeroJoint.RightThumbProximal, "RightThumbProximal", HeroJoint.RightHand, 0.265f, 0.835f, 0.030f, HumanBodyBones.RightThumbProximal, -30.0f, 60.0f, -40.0f, 40.0f, -30.0f, 50.0f),
            J(HeroJoint.RightThumbIntermediate, "RightThumbIntermediate", HeroJoint.RightThumbProximal, 0.255f, 0.805f, 0.052f, HumanBodyBones.RightThumbIntermediate, 0.0f, 60.0f, -25.0f, 25.0f, -25.0f, 25.0f),
            J(HeroJoint.RightThumbDistal, "RightThumbDistal", HeroJoint.RightThumbIntermediate, 0.250f, 0.780f, 0.070f, HumanBodyBones.RightThumbDistal, 0.0f, 50.0f, -20.0f, 20.0f, -20.0f, 20.0f),
            J(HeroJoint.RightIndexProximal, "RightIndexProximal", HeroJoint.RightHand, 0.345f, 0.800f, 0.020f, HumanBodyBones.RightIndexProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.RightIndexIntermediate, "RightIndexIntermediate", HeroJoint.RightIndexProximal, 0.345f, 0.770f, 0.030f, HumanBodyBones.RightIndexIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightIndexDistal, "RightIndexDistal", HeroJoint.RightIndexIntermediate, 0.345f, 0.745f, 0.037f, HumanBodyBones.RightIndexDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightMiddleProximal, "RightMiddleProximal", HeroJoint.RightHand, 0.322f, 0.800f, 0.022f, HumanBodyBones.RightMiddleProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.RightMiddleIntermediate, "RightMiddleIntermediate", HeroJoint.RightMiddleProximal, 0.322f, 0.768f, 0.032f, HumanBodyBones.RightMiddleIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightMiddleDistal, "RightMiddleDistal", HeroJoint.RightMiddleIntermediate, 0.322f, 0.742f, 0.039f, HumanBodyBones.RightMiddleDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightRingProximal, "RightRingProximal", HeroJoint.RightHand, 0.300f, 0.800f, 0.020f, HumanBodyBones.RightRingProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.RightRingIntermediate, "RightRingIntermediate", HeroJoint.RightRingProximal, 0.300f, 0.770f, 0.030f, HumanBodyBones.RightRingIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightRingDistal, "RightRingDistal", HeroJoint.RightRingIntermediate, 0.300f, 0.745f, 0.037f, HumanBodyBones.RightRingDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightLittleProximal, "RightLittleProximal", HeroJoint.RightHand, 0.281f, 0.795f, 0.018f, HumanBodyBones.RightLittleProximal, -15.0f, 90.0f, -8.0f, 8.0f, -8.0f, 8.0f),
            J(HeroJoint.RightLittleIntermediate, "RightLittleIntermediate", HeroJoint.RightLittleProximal, 0.281f, 0.772f, 0.026f, HumanBodyBones.RightLittleIntermediate, 0.0f, 100.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightLittleDistal, "RightLittleDistal", HeroJoint.RightLittleIntermediate, 0.281f, 0.752f, 0.032f, HumanBodyBones.RightLittleDistal, 0.0f, 70.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftUpperLeg, "LeftUpperLeg", HeroJoint.Hips, -0.130f, 0.900f, 0.000f, HumanBodyBones.LeftUpperLeg, -120.0f, 30.0f, -40.0f, 40.0f, -45.0f, 20.0f),
            J(HeroJoint.LeftLowerLeg, "LeftLowerLeg", HeroJoint.LeftUpperLeg, -0.130f, 0.540f, 0.000f, HumanBodyBones.LeftLowerLeg, -5.0f, 140.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.LeftFoot, "LeftFoot", HeroJoint.LeftLowerLeg, -0.130f, 0.150f, 0.000f, HumanBodyBones.LeftFoot, -20.0f, 5.0f, -10.0f, 10.0f, -25.0f, 25.0f),
            J(HeroJoint.LeftToes, "LeftToes", HeroJoint.LeftFoot, -0.130f, 0.055f, 0.155f, HumanBodyBones.LeftToes, -5.0f, 40.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightUpperLeg, "RightUpperLeg", HeroJoint.Hips, 0.130f, 0.900f, 0.000f, HumanBodyBones.RightUpperLeg, -120.0f, 30.0f, -40.0f, 40.0f, -20.0f, 45.0f),
            J(HeroJoint.RightLowerLeg, "RightLowerLeg", HeroJoint.RightUpperLeg, 0.130f, 0.540f, 0.000f, HumanBodyBones.RightLowerLeg, -5.0f, 140.0f, -5.0f, 5.0f, -5.0f, 5.0f),
            J(HeroJoint.RightFoot, "RightFoot", HeroJoint.RightLowerLeg, 0.130f, 0.150f, 0.000f, HumanBodyBones.RightFoot, -20.0f, 5.0f, -10.0f, 10.0f, -25.0f, 25.0f),
            J(HeroJoint.RightToes, "RightToes", HeroJoint.RightFoot, 0.130f, 0.055f, 0.155f, HumanBodyBones.RightToes, -5.0f, 40.0f, -5.0f, 5.0f, -5.0f, 5.0f)
        };

        // Declared after JointTable on purpose: C# runs static initializers in textual order.
        private static readonly Dictionary<HeroJoint, JointSpec> JointLookup = BuildJointLookup();

        private static Dictionary<HeroJoint, JointSpec> BuildJointLookup()
        {
            var lookup = new Dictionary<HeroJoint, JointSpec>(JointTable.Length);
            for (int i = 0; i < JointTable.Length; i++)
                lookup[JointTable[i].Joint] = JointTable[i];
            return lookup;
        }

        // B(hero mesh name, bone it is rigidly bound to).
        private struct BindSpec
        {
            public readonly string PartName;
            public readonly HeroJoint Joint;

            public BindSpec(string partName, HeroJoint joint)
            {
                PartName = partName;
                Joint = joint;
            }
        }

        private static BindSpec B(string partName, HeroJoint joint)
        {
            return new BindSpec(partName, joint);
        }

        private static readonly BindSpec[] BindTable =
        {
            // Pelvis - the belt and its hardware travel with the hips, not with the spine.
            B("Hips", HeroJoint.Hips),
            B("HipAccent_L", HeroJoint.Hips),
            B("HipAccent_R", HeroJoint.Hips),
            B("Belt", HeroJoint.Hips),
            B("BeltBuckle", HeroJoint.Hips),
            B("BeltGem", HeroJoint.Hips),

            // Torso - the abdomen is the spine segment and reaches into both neighbours so the
            // waist seams stay closed when the pelvis or the chest pitches.
            B("Abdomen", HeroJoint.Spine),
            B("Chest", HeroJoint.Chest),
            B("Emblem_Ring", HeroJoint.Chest),
            B("Emblem_Inlay", HeroJoint.Chest),
            B("Emblem_Core", HeroJoint.Chest),
            B("CapeCollar", HeroJoint.Chest),
            // One rigid sheet: measured, a two piece cape tears by up to 72 mm when the chest
            // bends, while the whole sheet on the chest never opens a seam.
            B("Cape", HeroJoint.Chest),

            // Neck and everything on the skull.
            B("Neck", HeroJoint.Neck),
            B("Head", HeroJoint.Head),
            B("Jaw", HeroJoint.Head),
            B("Ear_L", HeroJoint.Head),
            B("Ear_R", HeroJoint.Head),
            B("Visor", HeroJoint.Head),
            B("VisorWing_L", HeroJoint.Head),
            B("VisorWing_R", HeroJoint.Head),
            B("VisorBolt_L", HeroJoint.Head),
            B("VisorBolt_R", HeroJoint.Head),
            B("EyeSlit_L", HeroJoint.Head),
            B("EyeSlit_R", HeroJoint.Head),
            B("Brow_L", HeroJoint.Head),
            B("Brow_R", HeroJoint.Head),
            B("Hair_Base", HeroJoint.Head),
            B("Hair_Crest", HeroJoint.Head),
            B("Hair_Peak", HeroJoint.Head),
            B("Hair_Back", HeroJoint.Head),
            B("Hair_Side_L", HeroJoint.Head),
            B("Hair_Side_R", HeroJoint.Head),

            // Arms - the pads follow the upper arm (the clavicle bone carries no geometry), the
            // glove cuff is the wrist seam cover and therefore belongs to the hand.
            B("Shoulder_L", HeroJoint.LeftUpperArm),
            B("ShoulderAccent_L", HeroJoint.LeftUpperArm),
            B("UpperArm_L", HeroJoint.LeftUpperArm),
            B("LowerArm_L", HeroJoint.LeftLowerArm),
            B("Hand_L", HeroJoint.LeftHand),
            B("Knuckle_L", HeroJoint.LeftHand),
            B("GloveCuff_L", HeroJoint.LeftHand),
            B("Shoulder_R", HeroJoint.RightUpperArm),
            B("ShoulderAccent_R", HeroJoint.RightUpperArm),
            B("UpperArm_R", HeroJoint.RightUpperArm),
            B("LowerArm_R", HeroJoint.RightLowerArm),
            B("Hand_R", HeroJoint.RightHand),
            B("Knuckle_R", HeroJoint.RightHand),
            B("GloveCuff_R", HeroJoint.RightHand),

            // Legs - the boot cuff and strap are the ankle seam cover, so they follow the foot.
            B("Thigh_L", HeroJoint.LeftUpperLeg),
            B("Shin_L", HeroJoint.LeftLowerLeg),
            B("Foot_L", HeroJoint.LeftFoot),
            B("BootCuff_L", HeroJoint.LeftFoot),
            B("BootStrap_L", HeroJoint.LeftFoot),
            B("Thigh_R", HeroJoint.RightUpperLeg),
            B("Shin_R", HeroJoint.RightLowerLeg),
            B("Foot_R", HeroJoint.RightFoot),
            B("BootCuff_R", HeroJoint.RightFoot),
            B("BootStrap_R", HeroJoint.RightFoot)
        };

        // F(hero mesh name, position delta, scale delta) - interior-only extensions. Both grow a
        // part into the volume of its neighbour, so nothing of the silhouette changes; they exist so
        // the seams do not open when the joint rotates. Measured in
        // Tools/HeroRigVerification/simulate_hero_rig.py.
        private struct FitSpec
        {
            public readonly string PartName;
            public readonly Vector3 PositionDelta;
            public readonly Vector3 ScaleDelta;

            public FitSpec(string partName, Vector3 positionDelta, Vector3 scaleDelta)
            {
                PartName = partName;
                PositionDelta = positionDelta;
                ScaleDelta = scaleDelta;
            }
        }

        private static FitSpec F(string partName, float dx, float dy, float dz, float sx, float sy, float sz)
        {
            return new FitSpec(partName, new Vector3(dx, dy, dz), new Vector3(sx, sy, sz));
        }

        private static readonly FitSpec[] FitTable =
        {
            // Abdomen: 1.02..1.24 becomes 0.95..1.30 - inside the Hips box (0.92..1.02) below and
            // inside the wider Chest box (1.24..1.50) above.
            F("Abdomen", 0.000f, -0.005f, 0.000f, 0.000f, 0.130f, 0.000f),
            // Neck: 1.50..1.56 becomes 1.44..1.56 - the extra 0.06 sits inside the chest box.
            F("Neck", 0.000f, -0.030f, 0.000f, 0.000f, 0.030f, 0.000f)
        };

    }
}
