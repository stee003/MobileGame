using UnityEngine;
using UnityEngine.InputSystem;
using MobileGame.Input;

namespace MobileGame.Camera
{
    /// <summary>
    /// Temporary placeholder movement controller for testing the third-person camera system.
    /// This is NOT the final player character controller.
    ///
    /// <para>
    /// Provides simple camera-relative WASD movement, smooth character orientation,
    /// gravity, and jump in the greybox arena so that follow, rotation, angles,
    /// and camera collision can be tested against the arena geometry.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class PlaceholderPlayerController : MonoBehaviour
    {
        [Header("Camera Reference")]
        [Tooltip("The third-person camera used to orient movement. If unassigned, automatically finds one in the scene.")]
        [SerializeField] private ThirdPersonCamera targetCamera;

        [Header("Locomotion")]
        [Tooltip("Movement speed in meters per second.")]
        [SerializeField] private float moveSpeed = 6.0f;

        [Tooltip("Turn rotation speed in degrees per second.")]
        [SerializeField] private float turnSpeed = 720.0f;

        [Header("Jumping & Physics")]
        [Tooltip("Jump apex height in meters.")]
        [SerializeField] private float jumpHeight = 1.2f;

        [Tooltip("Downward gravity acceleration in meters per second squared.")]
        [SerializeField] private float gravity = 20.0f;

        private CharacterController m_controller;
        private float m_verticalVelocity;

        public CharacterController Controller => m_controller;
        public float MoveSpeed => moveSpeed;
        public Vector3 Velocity => m_controller != null ? m_controller.velocity : Vector3.zero;

        private void Awake()
        {
            m_controller = GetComponent<CharacterController>();
            if (targetCamera == null)
            {
                targetCamera = FindFirstObjectByType<ThirdPersonCamera>();
            }
        }

        private void Update()
        {
            Vector2 input = Vector2.zero;
            if (GameInput.Instance != null)
            {
                input = GameInput.Instance.Move;
            }

            Vector3 cameraForward = targetCamera != null ? targetCamera.CameraPlanarForward : Vector3.forward;
            Vector3 cameraRight = targetCamera != null ? targetCamera.CameraPlanarRight : Vector3.right;

            Vector3 moveDirection = cameraForward * input.y + cameraRight * input.x;
            if (moveDirection.sqrMagnitude > 1f)
            {
                moveDirection.Normalize();
            }

            // Smoothly rotate placeholder capsule toward movement direction
            if (moveDirection.sqrMagnitude > 0.001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);
            }

            // Grounding & Gravity
            bool isGrounded = m_controller != null && m_controller.isGrounded;
            if (isGrounded)
            {
                if (m_verticalVelocity < 0f)
                {
                    m_verticalVelocity = -2f; // Small grounding stick force
                }

                bool jumpPressed = (GameInput.Instance != null && GameInput.Instance.WasPressedThisFrame(GameInputAction.Dodge));
#if ENABLE_INPUT_SYSTEM
                if (!jumpPressed && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                {
                    jumpPressed = true;
                }
#endif
                if (!jumpPressed && UnityEngine.Input.GetKeyDown(KeyCode.Space))
                {
                    jumpPressed = true;
                }

                if (jumpPressed)
                {
                    m_verticalVelocity = Mathf.Sqrt(2f * jumpHeight * gravity);
                }
            }
            else
            {
                m_verticalVelocity -= gravity * Time.deltaTime;
            }

            // Apply motion
            Vector3 motion = moveDirection * moveSpeed + Vector3.up * m_verticalVelocity;
            if (m_controller != null)
            {
                m_controller.Move(motion * Time.deltaTime);
            }
            else
            {
                transform.position += motion * Time.deltaTime;
            }
        }
    }
}
