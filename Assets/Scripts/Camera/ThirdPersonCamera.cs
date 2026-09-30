using System;
using UnityEngine;
using UnityEngine.InputSystem;
using MobileGame.Input;

namespace MobileGame.Camera
{
    /// <summary>
    /// Reusable third-person camera system tailored for modern 3D action RPGs.
    ///
    /// <para>
    /// Features:
    /// <list type="bullet">
    /// <item>Follows any target transform with adjustable pivot height and framing.</item>
    /// <item>Smooth orbital horizontal (yaw) and vertical (pitch) rotation.</item>
    /// <item>Vertical pitch clamping between configurable minimum and maximum angles.</item>
    /// <item>Adjustable camera distance with interactive scroll-wheel zoom.</item>
    /// <item>Critically damped smooth movement and smooth rotation.</item>
    /// <item>SphereCast-based obstacle collision avoidance with fast pull-in and smooth release.</item>
    /// <item>Configurable horizontal and vertical sensitivity and optional pitch inversion.</item>
    /// <item>Planar forward/right accessors for camera-relative character movement.</item>
    /// </list>
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public class ThirdPersonCamera : MonoBehaviour
    {
        [Header("Target & Framing")]
        [Tooltip("Target transform to follow. If null, attempts to find an object tagged 'Player' or 'SpawnPoint'.")]
        [SerializeField] private Transform target;

        [Tooltip("Offset applied to the target position (camera focus pivot). Default y=1.6m frames character at chest/shoulder level.")]
        [SerializeField] private Vector3 targetOffset = new Vector3(0f, 1.6f, 0f);

        [Tooltip("Default orbit distance behind the pivot.")]
        [SerializeField] private float defaultDistance = 5.0f;

        [Tooltip("Minimum allowable camera distance (limits zoom-in and collision push-in).")]
        [SerializeField] private float minDistance = 1.5f;

        [Tooltip("Maximum allowable camera distance (limits zoom-out).")]
        [SerializeField] private float maxDistance = 10.0f;

        [Header("Vertical Angle Limits")]
        [Tooltip("Minimum vertical angle in degrees. Negative values tilt the camera upward (looking up at target).")]
        [SerializeField] private float minVerticalAngle = -35.0f;

        [Tooltip("Maximum vertical angle in degrees. Positive values tilt the camera downward (looking down from above).")]
        [SerializeField] private float maxVerticalAngle = 70.0f;

        [Header("Sensitivity & Controls")]
        [Tooltip("Horizontal look sensitivity (yaw degrees per pixel of look input).")]
        [SerializeField] private float horizontalSensitivity = 0.15f;

        [Tooltip("Vertical look sensitivity (pitch degrees per pixel of look input).")]
        [SerializeField] private float verticalSensitivity = 0.15f;

        [Tooltip("Inverts vertical pitch look direction.")]
        [SerializeField] private bool invertPitch = false;

        [Tooltip("Distance adjustment step per mouse scroll notch.")]
        [SerializeField] private float zoomSensitivity = 0.5f;

        [Tooltip("Automatically locks cursor into the game window on click in Play Mode (disabled by default for mobile touch controls).")]
        [SerializeField] private bool lockCursor = false;

        [Header("Smoothing")]
        [Tooltip("Enables critically damped spring smoothing for camera follow and rotation.")]
        [SerializeField] private bool enableSmoothing = true;

        [Tooltip("Time in seconds to smoothly follow target movement.")]
        [SerializeField] private float positionSmoothTime = 0.08f;

        [Tooltip("Time in seconds to smoothly track rotation input.")]
        [SerializeField] private float rotationSmoothTime = 0.02f;

        [Header("Camera Collision")]
        [Tooltip("Enables camera collision prevention against obstacles.")]
        [SerializeField] private bool enableCollision = true;

        [Tooltip("Radius of the collision spherecast.")]
        [SerializeField] private float collisionRadius = 0.25f;

        [Tooltip("Cushion distance kept between camera and obstacle surface.")]
        [SerializeField] private float collisionOffset = 0.15f;

        [Tooltip("Layers the camera collides with (e.g. Default, Environment, Ground).")]
        [SerializeField] private LayerMask collisionLayers = (1 << 0) | (1 << 14) | (1 << 17);

        [Tooltip("Speed at which camera pulls inward when an obstruction appears.")]
        [SerializeField] private float collisionPushSpeed = 25.0f;

        [Tooltip("Speed at which camera recovers outward after obstruction clears.")]
        [SerializeField] private float collisionReleaseSpeed = 4.0f;

        // Internal State
        private float m_targetYaw;
        private float m_targetPitch = 15.0f;
        private float m_currentYaw;
        private float m_currentPitch = 15.0f;
        private float m_yawVelocity;
        private float m_pitchVelocity;

        private Vector3 m_smoothedPivot;
        private Vector3 m_pivotVelocity;

        private float m_desiredDistance;
        private float m_currentDistance;

        // Public Properties
        public Transform Target => target;
        public Vector3 TargetOffset => targetOffset;
        public float DefaultDistance => defaultDistance;
        public float MinDistance => minDistance;
        public float MaxDistance => maxDistance;
        public float MinVerticalAngle => minVerticalAngle;
        public float MaxVerticalAngle => maxVerticalAngle;
        public float HorizontalSensitivity => horizontalSensitivity;
        public float VerticalSensitivity => verticalSensitivity;
        public bool InvertPitch => invertPitch;
        public bool EnableSmoothing => enableSmoothing;
        public bool EnableCollision => enableCollision;
        public LayerMask CollisionLayers => collisionLayers;
        public float CurrentYaw => m_currentYaw;
        public float CurrentPitch => m_currentPitch;
        public float TargetYaw => m_targetYaw;
        public float TargetPitch => m_targetPitch;
        public float DesiredDistance => m_desiredDistance;
        public float CurrentDistance => m_currentDistance;
        public Vector3 SmoothedPivot => m_smoothedPivot;

        /// <summary>
        /// Planar camera forward vector projected onto the horizontal XZ plane.
        /// Ideal for driving camera-relative player movement.
        /// </summary>
        public Vector3 CameraPlanarForward
        {
            get
            {
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                return fwd.sqrMagnitude > 0.0001f ? fwd.normalized : Vector3.forward;
            }
        }

        /// <summary>
        /// Planar camera right vector projected onto the horizontal XZ plane.
        /// Ideal for driving camera-relative player movement.
        /// </summary>
        public Vector3 CameraPlanarRight
        {
            get
            {
                Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up);
                return right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;
            }
        }

        private void Awake()
        {
            InitializeAngles();
            InitializeTarget();
            m_desiredDistance = Mathf.Clamp(defaultDistance, minDistance, maxDistance);
            m_currentDistance = m_desiredDistance;

            if (target != null)
            {
                m_smoothedPivot = target.position + targetOffset;
            }
            else
            {
                m_smoothedPivot = transform.position + transform.forward * m_currentDistance;
            }
        }

        private void Start()
        {
            if (!Application.isPlaying)
                return;

            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void Update()
        {
            HandleCursorLock();
            HandleInput();
        }

        private void LateUpdate()
        {
            UpdateCamera(Time.deltaTime);
        }

        /// <summary>
        /// Updates camera position, rotation, and collision resolution for the given delta time.
        /// </summary>
        public void UpdateCamera(float deltaTime)
        {
            if (target == null)
                return;

            Vector3 targetPivot = target.position + targetOffset;

            // 1. Smooth pivot follow
            if (enableSmoothing && positionSmoothTime > 0.0001f && deltaTime > 0f)
            {
                m_smoothedPivot = Vector3.SmoothDamp(m_smoothedPivot, targetPivot, ref m_pivotVelocity, positionSmoothTime, Mathf.Infinity, deltaTime);
            }
            else
            {
                m_smoothedPivot = targetPivot;
                m_pivotVelocity = Vector3.zero;
            }

            // 2. Smooth rotation
            if (enableSmoothing && rotationSmoothTime > 0.0001f && deltaTime > 0f)
            {
                m_currentYaw = Mathf.SmoothDampAngle(m_currentYaw, m_targetYaw, ref m_yawVelocity, rotationSmoothTime, Mathf.Infinity, deltaTime);
                m_currentPitch = Mathf.SmoothDamp(m_currentPitch, m_targetPitch, ref m_pitchVelocity, rotationSmoothTime, Mathf.Infinity, deltaTime);
            }
            else
            {
                m_currentYaw = m_targetYaw;
                m_currentPitch = m_targetPitch;
                m_yawVelocity = 0f;
                m_pitchVelocity = 0f;
            }

            // Clamp pitch strictly
            m_currentPitch = Mathf.Clamp(m_currentPitch, minVerticalAngle, maxVerticalAngle);

            Quaternion rotation = Quaternion.Euler(m_currentPitch, m_currentYaw, 0f);
            Vector3 backwardDir = -(rotation * Vector3.forward);

            // 3. Collision avoidance
            float targetCollisionDistance = m_desiredDistance;
            if (enableCollision)
            {
                if (Physics.SphereCast(m_smoothedPivot, collisionRadius, backwardDir, out RaycastHit hit, m_desiredDistance, collisionLayers, QueryTriggerInteraction.Ignore))
                {
                    float unobstructed = hit.distance - collisionOffset;
                    targetCollisionDistance = Mathf.Clamp(unobstructed, minDistance, m_desiredDistance);
                }
            }

            if (deltaTime > 0f)
            {
                if (m_currentDistance > targetCollisionDistance)
                {
                    // Rapidly push in to prevent clipping through obstacle
                    m_currentDistance = Mathf.MoveTowards(m_currentDistance, targetCollisionDistance, collisionPushSpeed * deltaTime);
                }
                else if (m_currentDistance < targetCollisionDistance)
                {
                    // Smoothly ease back out when clear
                    m_currentDistance = Mathf.MoveTowards(m_currentDistance, targetCollisionDistance, collisionReleaseSpeed * deltaTime);
                }
            }
            else
            {
                m_currentDistance = targetCollisionDistance;
            }

            // 4. Apply final transform
            transform.position = m_smoothedPivot + backwardDir * m_currentDistance;
            transform.rotation = rotation;
        }

        private void HandleInput()
        {
            Vector2 lookInput = Vector2.zero;
            if (GameInput.Instance != null)
            {
                lookInput = GameInput.Instance.Look;
            }

            ProcessLook(lookInput);
            ProcessZoom();
        }

        /// <summary>
        /// Processes horizontal and vertical look deltas.
        /// </summary>
        public void ProcessLook(Vector2 lookDelta)
        {
            if (lookDelta.sqrMagnitude < 0.0001f)
                return;

            // Horizontal rotation (yaw)
            m_targetYaw += lookDelta.x * horizontalSensitivity;
            if (m_targetYaw > 360f) m_targetYaw -= 360f;
            else if (m_targetYaw < 0f) m_targetYaw += 360f;

            // Vertical rotation (pitch)
            // Mouse UP gives positive delta.y -> tilts camera UP (decreases pitch towards minVerticalAngle)
            float pitchDelta = lookDelta.y * verticalSensitivity * (invertPitch ? 1f : -1f);
            m_targetPitch = Mathf.Clamp(m_targetPitch + pitchDelta, minVerticalAngle, maxVerticalAngle);
        }

        private void ProcessZoom()
        {
            float scroll = 0f;

#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                scroll = Mouse.current.scroll.ReadValue().y;
            }
#endif
            if (Mathf.Abs(scroll) < 0.001f)
            {
                scroll = UnityEngine.Input.mouseScrollDelta.y;
            }

            if (Mathf.Abs(scroll) > 0.001f)
            {
                float step = Mathf.Sign(scroll) * zoomSensitivity;
                SetDistance(m_desiredDistance - step);
            }
        }

        private void HandleCursorLock()
        {
            if (!lockCursor)
                return;

            bool lockTriggered = false;
            bool unlockTriggered = false;

#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                lockTriggered = true;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                unlockTriggered = true;
#endif
            if (!lockTriggered && UnityEngine.Input.GetMouseButtonDown(0))
                lockTriggered = true;
            if (!unlockTriggered && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                unlockTriggered = true;

            if (lockTriggered)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (unlockTriggered)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void InitializeAngles()
        {
            Vector3 euler = transform.eulerAngles;
            m_targetYaw = euler.y;
            m_currentYaw = euler.y;

            float pitch = euler.x;
            if (pitch > 180f) pitch -= 360f;
            m_targetPitch = Mathf.Clamp(pitch, minVerticalAngle, maxVerticalAngle);
            m_currentPitch = m_targetPitch;
        }

        private void InitializeTarget()
        {
            if (target != null)
                return;

            GameObject playerObj = GameObject.FindWithTag("Player");
            if (playerObj != null)
            {
                target = playerObj.transform;
                return;
            }

            GameObject spawnObj = GameObject.FindWithTag("SpawnPoint");
            if (spawnObj != null)
            {
                target = spawnObj.transform;
            }
        }

        /// <summary>Assigns a new follow target.</summary>
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            if (target != null)
            {
                TeleportToTarget();
            }
        }

        /// <summary>Configures camera distance clamped between min and max bounds.</summary>
        public void SetDistance(float distance)
        {
            m_desiredDistance = Mathf.Clamp(distance, minDistance, maxDistance);
        }

        /// <summary>Sets the vertical camera pivot height above the target.</summary>
        public void SetHeight(float height)
        {
            targetOffset.y = height;
        }

        /// <summary>Sets the camera look sensitivity multipliers.</summary>
        public void SetSensitivity(float horizontal, float vertical)
        {
            horizontalSensitivity = Mathf.Max(0.001f, horizontal);
            verticalSensitivity = Mathf.Max(0.001f, vertical);
        }

        /// <summary>Sets vertical pitch limits in degrees.</summary>
        public void SetPitchLimits(float minAngle, float maxAngle)
        {
            minVerticalAngle = minAngle;
            maxVerticalAngle = Mathf.Max(minAngle, maxAngle);
            m_targetPitch = Mathf.Clamp(m_targetPitch, minVerticalAngle, maxVerticalAngle);
            m_currentPitch = Mathf.Clamp(m_currentPitch, minVerticalAngle, maxVerticalAngle);
        }

        /// <summary>Sets the camera yaw and pitch angles (clamped to vertical limits).</summary>
        public void SetAngles(float yaw, float pitch, bool snap = false)
        {
            m_targetYaw = yaw % 360f;
            if (m_targetYaw < 0f) m_targetYaw += 360f;
            m_targetPitch = Mathf.Clamp(pitch, minVerticalAngle, maxVerticalAngle);

            if (snap)
            {
                m_currentYaw = m_targetYaw;
                m_currentPitch = m_targetPitch;
                m_yawVelocity = 0f;
                m_pitchVelocity = 0f;
                UpdateCamera(0f);
            }
        }

        /// <summary>Enables or disables smoothing for movement and rotation.</summary>
        public void SetSmoothing(bool enable)
        {
            enableSmoothing = enable;
        }

        /// <summary>Enables or disables obstacle collision resolution.</summary>
        public void SetCollision(bool enable)
        {
            enableCollision = enable;
        }

        /// <summary>Snaps camera immediately to target without smoothing interpolation.</summary>
        public void TeleportToTarget()
        {
            if (target == null)
                return;

            m_smoothedPivot = target.position + targetOffset;
            m_pivotVelocity = Vector3.zero;
            m_currentYaw = m_targetYaw;
            m_currentPitch = m_targetPitch;
            m_yawVelocity = 0f;
            m_pitchVelocity = 0f;
            m_currentDistance = m_desiredDistance;
            UpdateCamera(0f);
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 pivot = target != null ? target.position + targetOffset : transform.position + transform.forward * m_currentDistance;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(pivot, 0.2f);
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(pivot, transform.position);
            Gizmos.DrawWireSphere(transform.position, collisionRadius);
        }
    }
}
