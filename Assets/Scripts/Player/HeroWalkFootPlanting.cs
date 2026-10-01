using UnityEngine;

namespace MobileGame.Player
{
    /// <summary>
    /// Pins the hero's humanoid feet to the ground during the planted part of Hero_Walk. The
    /// animation remains in-place; the CharacterController moves the root and the Animator's foot
    /// IK prevents the shoes from skating as that root advances or turns.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeroWalkFootPlanting : MonoBehaviour
    {
        /// <summary>Share of each foot's gait cycle spent in ground contact.</summary>
        public const float StanceFraction = 0.56f;

        private const float AnkleToSoleHeight = 0.15f;
        private const float GroundRayStartHeight = 0.25f;
        private const float GroundRayDistance = 1.25f;

        private Animator m_animator;
        private ThirdPersonPlayerController m_player;
        private Transform m_leftFoot;
        private Transform m_rightFoot;
        private FootPlant m_leftPlant;
        private FootPlant m_rightPlant;

        /// <summary>Largest ankle-to-plant-target error observed since the last diagnostic reset.</summary>
        public float MaxGroundedPlantError { get; private set; }

        /// <summary>Number of grounded left-foot contacts observed since the last diagnostic reset.</summary>
        public int LeftGroundContactCount { get; private set; }

        /// <summary>Number of grounded right-foot contacts observed since the last diagnostic reset.</summary>
        public int RightGroundContactCount { get; private set; }

        /// <summary>True while either shoe is being held to a grounded IK target.</summary>
        public bool HasGroundedPlant =>
            (m_leftPlant.IsPlanted && m_leftPlant.HasGroundTarget) ||
            (m_rightPlant.IsPlanted && m_rightPlant.HasGroundTarget);

        /// <summary>Clears measured error and contact counts without disturbing the current pose.</summary>
        public void ResetDiagnostics()
        {
            MaxGroundedPlantError = 0f;
            LeftGroundContactCount = 0;
            RightGroundContactCount = 0;
        }

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            ResolveReferences();
            if (layerIndex != 0 || m_animator == null || !m_animator.isActiveAndEnabled ||
                !m_animator.isInitialized || !m_animator.isHuman)
            {
                return;
            }

            AnimatorStateInfo state = m_animator.GetCurrentAnimatorStateInfo(0);
            if (!state.IsName("Walk") || m_player == null || !m_player.IsGrounded ||
                m_player.Speed <= ThirdPersonPlayerController.AnimationMoveThreshold)
            {
                ReleaseAllGoals();
                return;
            }

            float phase = Mathf.Repeat(state.normalizedTime, 1f);
            ApplyFoot(AvatarIKGoal.LeftFoot, phase, 0f, m_leftFoot, ref m_leftPlant);
            ApplyFoot(AvatarIKGoal.RightFoot, phase, 0.5f, m_rightFoot, ref m_rightPlant);
        }

        private void LateUpdate()
        {
            // Animator IK has been applied by LateUpdate. Measuring the solved ankle against its
            // world-space plant target gives the Play Mode suite a direct no-skating diagnostic.
            if (m_leftPlant.IsPlanted && m_leftPlant.HasGroundTarget && m_leftFoot != null)
            {
                MaxGroundedPlantError = Mathf.Max(MaxGroundedPlantError,
                    Vector3.Distance(m_leftFoot.position, m_leftPlant.Position));
            }

            if (m_rightPlant.IsPlanted && m_rightPlant.HasGroundTarget && m_rightFoot != null)
            {
                MaxGroundedPlantError = Mathf.Max(MaxGroundedPlantError,
                    Vector3.Distance(m_rightFoot.position, m_rightPlant.Position));
            }
        }

        private void ApplyFoot(AvatarIKGoal goal, float cyclePhase, float phaseOffset,
                               Transform foot, ref FootPlant plant)
        {
            if (foot == null || Mathf.Repeat(cyclePhase + phaseOffset, 1f) >= StanceFraction)
            {
                plant.IsPlanted = false;
                SetGoalWeights(goal, 0f);
                return;
            }

            if (!plant.IsPlanted)
            {
                plant.HasGroundTarget = CaptureGroundTarget(foot, out plant.Position, out plant.Rotation);
                plant.IsPlanted = true;
                if (plant.HasGroundTarget)
                {
                    if (goal == AvatarIKGoal.LeftFoot)
                        LeftGroundContactCount++;
                    else
                        RightGroundContactCount++;
                }
            }

            // If a ground surface could not be sampled, keep the animated ankle where it was at
            // contact rather than moving it with the root. It will be reacquired at the next step.
            SetGoalWeights(goal, plant.IsPlanted ? 1f : 0f);
            m_animator.SetIKPosition(goal, plant.Position);
            m_animator.SetIKRotation(goal, plant.Rotation);
        }

        private bool CaptureGroundTarget(Transform foot, out Vector3 position, out Quaternion rotation)
        {
            Vector3 animatedPosition = foot.position;
            Vector3 normal = Vector3.up;
            bool foundGround = false;
            LayerMask groundLayers = m_player != null ? m_player.GroundLayers : Physics.DefaultRaycastLayers;
            Vector3 rayOrigin = animatedPosition + Vector3.up * GroundRayStartHeight;

            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, GroundRayDistance,
                                groundLayers, QueryTriggerInteraction.Ignore))
            {
                normal = hit.normal;
                position = hit.point + normal * AnkleToSoleHeight;
                foundGround = true;
            }
            else
            {
                position = animatedPosition;
            }

            Vector3 forward = m_player != null ? m_player.transform.forward : transform.forward;
            forward = Vector3.ProjectOnPlane(forward, normal);
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.ProjectOnPlane(Vector3.forward, normal);
            rotation = Quaternion.LookRotation(forward.normalized, normal);
            return foundGround;
        }

        private void SetGoalWeights(AvatarIKGoal goal, float weight)
        {
            m_animator.SetIKPositionWeight(goal, weight);
            m_animator.SetIKRotationWeight(goal, weight);
        }

        private void ReleaseAllGoals()
        {
            m_leftPlant.IsPlanted = false;
            m_rightPlant.IsPlanted = false;
            if (m_animator != null && m_animator.isActiveAndEnabled)
            {
                SetGoalWeights(AvatarIKGoal.LeftFoot, 0f);
                SetGoalWeights(AvatarIKGoal.RightFoot, 0f);
            }
        }

        private void ResolveReferences()
        {
            if (m_animator == null)
                m_animator = GetComponent<Animator>();
            if (m_player == null)
                m_player = GetComponentInParent<ThirdPersonPlayerController>();

            if (m_animator == null || !m_animator.isInitialized || !m_animator.isHuman || m_animator.avatar == null)
                return;

            if (m_leftFoot == null)
                m_leftFoot = m_animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            if (m_rightFoot == null)
                m_rightFoot = m_animator.GetBoneTransform(HumanBodyBones.RightFoot);
        }

        private struct FootPlant
        {
            public bool IsPlanted;
            public bool HasGroundTarget;
            public Vector3 Position;
            public Quaternion Rotation;
        }
    }
}
