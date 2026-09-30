using UnityEngine;
using MobileGame.Camera;
using MobileGame.Core;
using MobileGame.Input;

namespace MobileGame.Player
{
    /// <summary>
    /// Basic third-person player controller for the temporary capsule placeholder.
    ///
    /// <para>
    /// Scope is deliberately limited to locomotion only:
    /// <list type="bullet">
    /// <item>Camera-relative forward / backward / left / right movement.</item>
    /// <item>Acceleration and deceleration of the planar velocity.</item>
    /// <item>Rotation toward the current movement direction.</item>
    /// <item>Gravity with terminal fall speed.</item>
    /// <item>Ground detection (sphere probe + slope filtering) and landing detection.</item>
    /// </list>
    /// No combat, attacks, abilities, dodge, stamina or health is implemented here.
    /// </para>
    ///
    /// <para>
    /// The controller is driven by <see cref="MobileGame.Input.GameInput"/> (keyboard/mouse bindings
    /// today, virtual-stick values later) and orients its movement against
    /// <see cref="MobileGame.Camera.ThirdPersonCamera"/>, so it works with any camera rig that
    /// exposes planar forward/right vectors.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class ThirdPersonPlayerController : MonoBehaviour
    {
        [Header("Camera Reference")]
        [Tooltip("Third-person camera used to orient movement. If unassigned, one is found automatically in the scene.")]
        [SerializeField] private ThirdPersonCamera targetCamera;

        [Header("Locomotion")]
        [Tooltip("Maximum planar movement speed in meters per second.")]
        [SerializeField] private float moveSpeed = 6.0f;

        [Tooltip("Planar acceleration in meters per second squared while movement input is applied.")]
        [SerializeField] private float acceleration = 30.0f;

        [Tooltip("Planar deceleration in meters per second squared when movement input is released.")]
        [SerializeField] private float deceleration = 45.0f;

        [Tooltip("Rotation speed toward the movement direction in degrees per second.")]
        [SerializeField] private float rotationSpeed = 720.0f;

        [Tooltip("Minimum planar speed (m/s) required before the player rotates toward the movement direction.")]
        [SerializeField] private float minSpeedToRotate = 0.1f;

        [Header("Gravity")]
        [Tooltip("Downward gravity acceleration in meters per second squared.")]
        [SerializeField] private float gravity = 20.0f;

        [Tooltip("Maximum downward fall speed in meters per second (terminal velocity).")]
        [SerializeField] private float maxFallSpeed = 30.0f;

        [Tooltip("Downward speed applied while grounded so the capsule stays glued to slopes and stairs.")]
        [SerializeField] private float groundStickForce = 2.0f;

        [Header("Ground Detection")]
        [Tooltip("Layers treated as ground (arena uses Default, Environment and Ground).")]
        [SerializeField] private LayerMask groundLayers = (1 << 0) | (1 << 14) | (1 << 17);

        [Tooltip("Extra distance probed below the capsule bottom for ground. Larger values detect landings earlier.")]
        [SerializeField] private float groundCheckDistance = 0.15f;

        [Tooltip("Maximum slope angle in degrees still considered walkable ground. Should match CharacterController.slopeLimit.")]
        [SerializeField] private float maxSlopeAngle = 45.0f;

        // Internal state
        private CharacterController m_controller;
        private ThirdPersonCamera m_camera;
        private Camera m_mainCameraFallback;

        private Vector3 m_planarVelocity;      // Horizontal velocity (y is always zero).
        private float m_verticalVelocity;      // Vertical velocity in m/s (negative while falling).

        private bool m_isGrounded;
        private bool m_wasGrounded;
        private bool m_justLanded;
        private bool m_justBecameAirborne;

        private Vector3 m_groundNormal = Vector3.up;
        private float m_groundDistance;
        private float m_lastLandingImpactSpeed;

        private long m_skippedMotionFrames;    // Frames whose motion was too small to give the engine.

        // Public read-only state
        /// <summary>Full world-space velocity of the player (planar + vertical).</summary>
        public Vector3 Velocity => m_planarVelocity + Vector3.up * m_verticalVelocity;

        /// <summary>Horizontal velocity only; magnitude is the current ground speed.</summary>
        public Vector3 PlanarVelocity => m_planarVelocity;

        /// <summary>Current horizontal speed in meters per second.</summary>
        public float Speed => m_planarVelocity.magnitude;

        /// <summary>Current vertical speed in meters per second (negative while falling).</summary>
        public float VerticalSpeed => m_verticalVelocity;

        /// <summary>True while the capsule is standing on walkable ground.</summary>
        public bool IsGrounded => m_isGrounded;

        /// <summary>Grounded state of the previous frame.</summary>
        public bool WasGrounded => m_wasGrounded;

        /// <summary>True only on the frame the player touched down after falling.</summary>
        public bool JustLanded => m_justLanded;

        /// <summary>True only on the frame the player walked or fell off ground.</summary>
        public bool JustBecameAirborne => m_justBecameAirborne;

        /// <summary>Normal of the surface below the player (up vector when no ground was probed).</summary>
        public Vector3 GroundNormal => m_groundNormal;

        /// <summary>Distance from the bottom of the capsule to the probed ground, in meters.</summary>
        public float GroundDistance => m_groundDistance;

        /// <summary>Slope angle of the current ground in degrees (0 = flat).</summary>
        public float SlopeAngle => Vector3.Angle(m_groundNormal, Vector3.up);

        /// <summary>Fall speed on impact, in meters per second, at the last landing.</summary>
        public float LastLandingImpactSpeed => m_lastLandingImpactSpeed;

        /// <summary>
        /// Number of frames whose motion was too small (or not finite) to hand to
        /// <see cref="CharacterController.Move"/>. While idle that is the zero-length motion which
        /// triggers Unity's <c>IsNormalized(dir, 0.001f)</c> assertion, so the engine call is skipped
        /// instead. Exposed for the verification suite.
        /// </summary>
        public long SkippedMotionFrames => m_skippedMotionFrames;

        /// <summary>The CharacterController driving this player.</summary>
        public CharacterController Controller => m_controller;

        // Public configuration accessors (mirror the Inspector values)
        public float MoveSpeed => moveSpeed;
        public float Acceleration => acceleration;
        public float Deceleration => deceleration;
        public float RotationSpeed => rotationSpeed;
        public float Gravity => gravity;
        public float MaxFallSpeed => maxFallSpeed;
        public float GroundStickForce => groundStickForce;
        public float MinSpeedToRotate => minSpeedToRotate;
        public float GroundCheckDistance => groundCheckDistance;
        public float MaxSlopeAngle => maxSlopeAngle;
        public LayerMask GroundLayers => groundLayers;

        private void Awake()
        {
            m_controller = GetComponent<CharacterController>();
            ResolveCamera();
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
                return;

            m_justLanded = false;
            m_justBecameAirborne = false;

            // 1. Input -> camera-relative planar direction (zero when no input).
            if (m_camera == null && targetCamera == null)
            {
                // The camera may not have been awake when this component resolved it.
                ResolveCamera();
            }

            Vector2 moveInput = ReadMoveInput();
            Vector3 moveDirection = ResolveMoveDirection(moveInput);

            // 2. Acceleration / deceleration of the planar velocity.
            UpdatePlanarVelocity(moveDirection, deltaTime);

            // 3. Rotate toward the movement direction.
            RotateTowardsMovement(deltaTime);

            // 4. Ground detection and gravity.
            m_wasGrounded = m_isGrounded;
            m_isGrounded = ProbeGround(out m_groundNormal, out m_groundDistance);

            if (m_isGrounded)
            {
                if (!m_wasGrounded)
                {
                    m_justLanded = true;
                    m_lastLandingImpactSpeed = Mathf.Max(0f, -m_verticalVelocity);
                }

                // Keep a small downward push so the capsule follows slopes instead of bouncing.
                // Re-armed from any fall (including the first grounded frame after spawning) and
                // from every landing. It stays exactly zero only in the state ResetMovementState()
                // leaves behind (teleports, tests), where the motion guard in step 5 skips the
                // engine call instead of handing Move a zero-length vector.
                if (m_verticalVelocity < 0f)
                {
                    m_verticalVelocity = -Mathf.Abs(groundStickForce);
                }
            }
            else
            {
                if (m_wasGrounded)
                {
                    m_justBecameAirborne = true;
                }

                m_verticalVelocity = Mathf.Max(m_verticalVelocity - Mathf.Abs(gravity) * deltaTime, -Mathf.Abs(maxFallSpeed));
            }

            // 5. Apply the motion.
            Vector3 velocity = m_planarVelocity + Vector3.up * m_verticalVelocity;
            Vector3 motion = velocity * deltaTime;

            // CharacterController.Move normalizes its motion internally, so a zero-length motion
            // makes Unity assert "IsNormalized(dir, 0.001f)" (it is also shorter than
            // minMoveDistance, which the controller ignores anyway). Never make the call: skip the
            // frame and let the guard be observable for the verification suite.
            float minMoveDistance = m_controller != null ? m_controller.minMoveDistance : 0f;
            if (!PhysicsQueryGuard.IsUsableMotion(motion, minMoveDistance))
            {
                m_skippedMotionFrames++;
                return;
            }

            if (m_controller != null && m_controller.enabled)
            {
                m_controller.Move(motion);
            }
            else
            {
                transform.position += motion;
            }
        }

        /// <summary>Reads the shared move vector from the input facade.</summary>
        private Vector2 ReadMoveInput()
        {
            if (GameInput.Instance == null)
                return Vector2.zero;

            return Vector2.ClampMagnitude(GameInput.Instance.Move, 1f);
        }

        /// <summary>
        /// Converts raw stick input into a planar world-space direction relative to the camera.
        /// Returns a zero vector when there is no input.
        /// </summary>
        private Vector3 ResolveMoveDirection(Vector2 moveInput)
        {
            if (moveInput.sqrMagnitude < 0.0001f)
                return Vector3.zero;

            Vector3 direction = PlanarCameraForward() * moveInput.y + PlanarCameraRight() * moveInput.x;

            // Clamp diagonals to unit length so they are not faster than straight movement.
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            return direction;
        }

        /// <summary>Planar (XZ) forward of the camera, falling back to the main camera, then to this transform.</summary>
        private Vector3 PlanarCameraForward()
        {
            if (m_camera != null)
                return m_camera.CameraPlanarForward;

            Transform source = m_mainCameraFallback != null ? m_mainCameraFallback.transform : transform;
            Vector3 forward = Vector3.ProjectOnPlane(source.forward, Vector3.up);
            return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        }

        /// <summary>Planar (XZ) right of the camera, falling back to the main camera, then to this transform.</summary>
        private Vector3 PlanarCameraRight()
        {
            if (m_camera != null)
                return m_camera.CameraPlanarRight;

            Transform source = m_mainCameraFallback != null ? m_mainCameraFallback.transform : transform;
            Vector3 right = Vector3.ProjectOnPlane(source.right, Vector3.up);
            return right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;
        }

        /// <summary>
        /// Accelerates the planar velocity toward the target velocity, or decelerates it toward zero
        /// when no movement input is present. Never overshoots the target.
        /// </summary>
        private void UpdatePlanarVelocity(Vector3 moveDirection, float deltaTime)
        {
            Vector3 targetVelocity = moveDirection * moveSpeed;
            float rate = moveDirection.sqrMagnitude > 0.0001f ? Mathf.Abs(acceleration) : Mathf.Abs(deceleration);
            m_planarVelocity = Vector3.MoveTowards(m_planarVelocity, targetVelocity, rate * deltaTime);
        }

        /// <summary>Smoothly rotates the player toward its current movement direction.</summary>
        private void RotateTowardsMovement(float deltaTime)
        {
            float minSpeedSqr = minSpeedToRotate * minSpeedToRotate;
            if (m_planarVelocity.sqrMagnitude <= minSpeedSqr)
                return;

            Quaternion targetRotation = Quaternion.LookRotation(m_planarVelocity.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, Mathf.Abs(rotationSpeed) * deltaTime);
        }

        /// <summary>
        /// Probes for walkable ground directly below the capsule using a sphere cast and rejects
        /// surfaces steeper than <see cref="maxSlopeAngle"/>. Falls back to the CharacterController's
        /// own grounded flag when the probe finds nothing (for example ground on an unmasked layer).
        /// </summary>
        private bool ProbeGround(out Vector3 normal, out float distance)
        {
            normal = Vector3.up;
            distance = 0f;

            if (m_controller == null)
                return false;

            // The CharacterController's collision volume is the capsule shrunk by the skin width, so
            // the probe starts at that inset bottom and is lifted clear of the resting surface: a
            // sphere cast that begins already touching geometry is unreliable.
            float skinWidth = Mathf.Max(0f, m_controller.skinWidth);
            float radius = Mathf.Max(0.01f, m_controller.radius - skinWidth);
            float lift = skinWidth + 0.01f;
            Vector3 bottom = transform.position + m_controller.center - Vector3.up * (m_controller.height * 0.5f - skinWidth);
            Vector3 origin = bottom + Vector3.up * (radius + lift);
            float maxDistance = Mathf.Max(0.01f, groundCheckDistance + lift);

            if (Physics.SphereCast(origin, radius, Vector3.down, out RaycastHit hit, maxDistance, groundLayers, QueryTriggerInteraction.Ignore))
            {
                float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
                if (slopeAngle <= maxSlopeAngle + 0.5f)
                {
                    normal = hit.normal;
                    distance = Mathf.Max(0f, hit.distance - lift);
                    return true;
                }
            }

            // Safety net: trust the controller when the sphere probe cannot see the surface.
            if (m_controller.isGrounded)
            {
                normal = Vector3.up;
                distance = 0f;
                return true;
            }

            return false;
        }

        /// <summary>Clears the current motion without touching the transform.</summary>
        public void ResetMovementState()
        {
            m_planarVelocity = Vector3.zero;
            m_verticalVelocity = 0f;
        }

        /// <summary>Assigns the camera used for camera-relative movement.</summary>
        public void SetCamera(ThirdPersonCamera camera)
        {
            targetCamera = camera;
            ResolveCamera();
        }

        private void ResolveCamera()
        {
            if (targetCamera != null)
            {
                m_camera = targetCamera;
                return;
            }

            m_camera = FindFirstObjectByType<ThirdPersonCamera>();
            if (m_camera == null)
            {
                m_mainCameraFallback = Camera.main;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (m_controller == null)
                return;

            float skinWidth = Mathf.Max(0f, m_controller.skinWidth);
            float radius = Mathf.Max(0.01f, m_controller.radius - skinWidth);
            float lift = skinWidth + 0.01f;
            Vector3 bottom = transform.position + m_controller.center - Vector3.up * (m_controller.height * 0.5f - skinWidth);
            Vector3 origin = bottom + Vector3.up * (radius + lift);

            Gizmos.color = m_isGrounded ? Color.green : Color.red;
            Gizmos.DrawWireSphere(origin, radius);
            Gizmos.DrawLine(origin, origin + Vector3.down * Mathf.Max(0.01f, groundCheckDistance + lift));
        }
    }
}
