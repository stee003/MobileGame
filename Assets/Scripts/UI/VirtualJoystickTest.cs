using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
using MobileGame.Camera;
using MobileGame.Input;
using MobileGame.Player;

namespace MobileGame.UI
{
    /// <summary>
    /// Play Mode verification suite for the <see cref="VirtualJoystick"/>.
    ///
    /// <para>
    /// Injects real touch events through the Input System (a test Touchscreen device) and
    /// verifies every joystick requirement end to end:
    /// <list type="number">
    /// <item>Wiring: joystick, canvas and <see cref="GameInput"/> facade are present and configured.</item>
    /// <item>Touch movement: a drag inside the stick drives the shared Move input.</item>
    /// <item>Diagonal movement: diagonals keep their direction and are not faster than straight input.</item>
    /// <item>Maximum input: full deflection clamps to magnitude 1 (full run speed).</item>
    /// <item>Dead zone: inside it the output stays zero; just past it the output ramps from ~zero.</item>
    /// <item>Releasing the joystick: activity ends and the shared Move input returns to zero.</item>
    /// <item>Multi-touch safety: a second finger can neither steal nor hold the stick.</item>
    /// <item>Player integration (when a player is in the scene): touch input actually moves the
    /// character through the existing controller, and it stops on release.</item>
    /// <item>Adjustable size / position API.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// If a <see cref="PlayerControllerTest"/> suite is running, this suite waits for it to finish
    /// first (both drive the shared <c>SetMobileMove</c> seam and must not interleave). The suite
    /// runs automatically on Start in Play Mode, or on demand from the Inspector context menu.
    /// It is a diagnostic tool only: it contains no gameplay logic.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VirtualJoystickTest : MonoBehaviour
    {
        [Tooltip("Player controller used for the integration check. If unassigned, one is found automatically in the scene.")]
        [SerializeField] private ThirdPersonPlayerController player;

        [Tooltip("Automatically runs the verification suite on Start in Play Mode.")]
        [SerializeField] private bool runOnStart = true;

        private const float CheckTimeoutSeconds = 4f;
        private const float OutputToleranceSqr = 0.0025f;   // |output - expected| <= 0.05
        private const float MaxWaitForOtherSuiteSeconds = 120f;

        private VirtualJoystick m_joystick;
        private GameInput m_input;
        private Touchscreen m_touchDevice;
        private int m_nextFingerId = 100;

        private bool m_running;
        private int m_passed;
        private int m_total;

        /// <summary>True while the automated suite is in progress. Other diagnostics can defer until it finishes.</summary>
        public bool IsRunning => m_running;

        private void Start()
        {
            if (runOnStart)
                RunTestSuite();
        }

        /// <summary>Runs the complete verification suite (Play Mode only; touch injection needs frames).</summary>
        [ContextMenu("Verify: Run Virtual Joystick Test Suite")]
        public void RunTestSuite()
        {
            if (m_running)
            {
                Debug.LogWarning("[JoystickTest] A test run is already in progress.");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[JoystickTest] Touch injection requires Play Mode. Enter Play Mode and run again.");
                return;
            }

            StartCoroutine(RunAllTests());
        }

        private IEnumerator RunAllTests()
        {
            m_running = true;
            m_passed = 0;
            m_total = 0;

            Debug.Log("[JoystickTest] =========================================");
            Debug.Log("[JoystickTest] Starting Virtual Joystick Test Suite...");

            try
            {
                yield return WaitForOtherSuites();
                yield return RunChecks();
            }
            finally
            {
                Cleanup();
            }

            Debug.Log($"[JoystickTest] Test Results: {m_passed}/{m_total} checks passed.");

            if (m_passed == m_total && m_total > 0)
            {
                Debug.Log("[JoystickTest] VERIFICATION PASSED. Virtual joystick (touch movement, diagonal, release, max input, dead zone) functioning correctly with zero errors.");
            }
            else
            {
                Debug.LogError($"[JoystickTest] VERIFICATION FAILED. Only {m_passed}/{m_total} checks passed.");
            }

            Debug.Log("[JoystickTest] =========================================");
            m_running = false;
        }

        private IEnumerator RunChecks()
        {
            m_input = GameInput.Instance;
            if (player == null)
                player = FindFirstObjectByType<ThirdPersonPlayerController>();

            Debug.Log("[JoystickTest] --- Wiring ---");
            m_joystick = VirtualJoystick.Instance;
            Check(m_joystick != null, "VirtualJoystick instance exists (bootstrap or scene placement)");
            Check(m_input != null, "GameInput facade is available");

            if (m_joystick != null)
            {
                Check(m_joystick.HostCanvas != null, "joystick is hosted on a canvas");
                if (m_joystick.HostCanvas != null)
                {
                    Check(m_joystick.HostCanvas.renderMode == RenderMode.ScreenSpaceOverlay, "joystick canvas is screen-space overlay");
                    CanvasScaler scaler = m_joystick.HostCanvas.GetComponent<CanvasScaler>();
                    Check(scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize, "joystick canvas scales with screen size");
                }

                yield return WaitForCondition(() => m_joystick.IsUiBuilt, 2f, "joystick visuals are built");
                Check(!m_joystick.IsActive, "joystick starts idle");
                Check(m_joystick.Output == Vector2.zero, "joystick output starts at zero");
            }

            if (m_input == null || m_joystick == null || !m_joystick.IsUiBuilt)
            {
                Debug.LogError("[JoystickTest] Missing prerequisites; skipping input checks.");
                yield break;
            }

            // Inject a test touchscreen so the suite can drive real touch events through the
            // Input System (works in Editor and on device; removed again in Cleanup).
            m_touchDevice = InputSystem.AddDevice<Touchscreen>();
            Check(m_touchDevice != null && m_touchDevice.added, "test touchscreen device injected");

            Vector2 center = m_joystick.GetBaseCenterScreen();
            float radius = m_joystick.GetBaseScreenRadius();
            Check(radius > 1f, $"touch radius resolves in screen pixels ({radius:F0} px)");
            float deadZone = m_joystick.DeadZone;

            // 1. Touch movement ------------------------------------------------
            Debug.Log("[JoystickTest] --- Touch movement ---");
            {
                int finger = NewFinger();
                yield return TouchStep(finger, TouchPhase.Began, center);
                yield return TouchStep(finger, TouchPhase.Moved, center + Vector2.up * (radius * 0.8f));

                float expectedMag = ExpectedMagnitude(0.8f, deadZone);
                Vector2 expected = new Vector2(0f, expectedMag);
                yield return WaitForCondition(
                    () => (m_input.Move - expected).sqrMagnitude <= OutputToleranceSqr,
                    CheckTimeoutSeconds, $"dragging up 80% drives Move to ~{expected} (analog, dead-zone filtered)");

                Check(m_joystick.IsActive, "stick reports active while the finger is held");
                Check(Vector2.Angle(m_input.Move, Vector2.up) < 3f, "move direction is straight up");

                yield return TouchStep(finger, TouchPhase.Ended, center + Vector2.up * (radius * 0.8f));
                yield return Settle(2);
            }

            // 2. Diagonal movement ---------------------------------------------
            Debug.Log("[JoystickTest] --- Diagonal movement ---");
            {
                int finger = NewFinger();
                Vector2 diagonal = new Vector2(0.70710678f, 0.70710678f);
                Vector2 target = center + diagonal * (radius * 0.9f);
                yield return TouchStep(finger, TouchPhase.Began, center);
                yield return TouchStep(finger, TouchPhase.Moved, target);

                float expectedMag = ExpectedMagnitude(0.9f, deadZone);
                Vector2 expected = diagonal * expectedMag;
                yield return WaitForCondition(
                    () => (m_input.Move - expected).sqrMagnitude <= OutputToleranceSqr,
                    CheckTimeoutSeconds, $"diagonal drag outputs ~{expected} (direction preserved)");

                Check(Mathf.Abs(m_input.Move.magnitude - expectedMag) <= 0.05f, "diagonal magnitude matches straight input (no square boost)");

                yield return TouchStep(finger, TouchPhase.Ended, target);
                yield return Settle(2);
            }

            // 3. Maximum input ---------------------------------------------------
            Debug.Log("[JoystickTest] --- Maximum input ---");
            {
                int finger = NewFinger();
                Vector2 target = center + Vector2.right * (radius * 2.5f); // Far beyond the ring.
                yield return TouchStep(finger, TouchPhase.Began, center);
                yield return TouchStep(finger, TouchPhase.Moved, target);

                yield return WaitForCondition(
                    () => Mathf.Abs(m_joystick.RawInput.magnitude - 1f) <= 0.01f,
                    CheckTimeoutSeconds, "raw input clamps to the unit circle past the ring");
                Check(m_joystick.RawInput.x >= 0.99f && Mathf.Abs(m_joystick.RawInput.y) <= 0.01f, "clamped raw input keeps its direction");
                Check(Mathf.Abs(m_input.Move.magnitude - 1f) <= 0.02f, "output magnitude is exactly 1 at maximum deflection (full speed)");

                float ringEdge = m_joystick.BaseRadiusUnits;
                Vector2 pinnedKnob = new Vector2(ringEdge, 0f);
                float knobTolerance = 0.04f * ringEdge;
                Check((m_joystick.KnobVisualOffset - pinnedKnob).sqrMagnitude <= knobTolerance * knobTolerance, "knob is pinned to the ring edge");

                yield return TouchStep(finger, TouchPhase.Ended, target);
                yield return Settle(2);
            }

            // 4. Dead zone --------------------------------------------------------
            Debug.Log("[JoystickTest] --- Dead zone ---");
            {
                int finger = NewFinger();
                yield return TouchStep(finger, TouchPhase.Began, center);
                yield return TouchStep(finger, TouchPhase.Moved, center + Vector2.up * (radius * deadZone * 0.5f));
                yield return Settle(3);
                Check(m_input.Move.magnitude <= 0.02f, "input inside the dead zone produces no output");

                Vector2 target = center + Vector2.up * (radius * (deadZone + 0.06f));
                yield return TouchStep(finger, TouchPhase.Moved, target);
                float expectedMag = ExpectedMagnitude(deadZone + 0.06f, deadZone);
                yield return WaitForCondition(
                    () => Mathf.Abs(m_input.Move.magnitude - expectedMag) <= 0.03f,
                    CheckTimeoutSeconds, $"just past the dead zone the output ramps smoothly from ~zero (~{expectedMag:F3})");

                // Finger stays held for the release test.
                yield return ReleaseAndVerify(finger, target);
            }

            // 5. Multi-touch safety ------------------------------------------------
            Debug.Log("[JoystickTest] --- Multi-touch safety ---");
            {
                int fingerA = NewFinger();
                int fingerB = NewFinger();

                yield return TouchStep(fingerA, TouchPhase.Began, center);
                yield return TouchStep(fingerA, TouchPhase.Moved, center + Vector2.up * (radius * 0.8f));
                float expectedMag = ExpectedMagnitude(0.8f, deadZone);
                yield return WaitForCondition(
                    () => Mathf.Abs(m_input.Move.y - expectedMag) <= 0.05f,
                    CheckTimeoutSeconds, "first finger takes control of the stick");

                // A second finger lands on the stick and wiggles around.
                yield return TouchStep(fingerB, TouchPhase.Began, center);
                yield return TouchStep(fingerB, TouchPhase.Moved, center + Vector2.right * (radius * 1.5f));
                yield return TouchStep(fingerB, TouchPhase.Moved, center - Vector2.up * radius);
                yield return Settle(3);
                Check(Mathf.Abs(m_input.Move.y - expectedMag) <= 0.06f && Mathf.Abs(m_input.Move.x) <= 0.06f,
                    "second finger cannot steal or bend the stick");

                // The owning finger lifts: the stick must release even though finger B is still down.
                yield return TouchStep(fingerA, TouchPhase.Ended, center + Vector2.up * (radius * 0.8f));
                yield return WaitForCondition(() => !m_joystick.IsActive, CheckTimeoutSeconds, "stick releases only with its owning finger");
                Check(m_input.Move.magnitude <= 0.02f, "Move is zero while the ignored finger is still down");

                yield return TouchStep(fingerB, TouchPhase.Ended, center - Vector2.up * radius);
                yield return Settle(2);
            }

            // 6. Player integration (only when a player exists in the scene) -------
            if (player != null)
            {
                Debug.Log("[JoystickTest] --- Player integration ---");
                Vector3 startPlanar = new Vector3(player.transform.position.x, 0f, player.transform.position.z);

                int finger = NewFinger();
                yield return TouchStep(finger, TouchPhase.Began, center);
                yield return TouchStep(finger, TouchPhase.Moved, center + Vector2.up * (radius * 0.8f));
                float expectedMag = ExpectedMagnitude(0.8f, deadZone);
                yield return WaitForCondition(
                    () => Mathf.Abs(m_input.Move.magnitude - expectedMag) <= 0.05f,
                    CheckTimeoutSeconds, "touch input reaches the shared Move action");

                yield return new WaitForSeconds(1.0f);
                Vector3 nowPlanar = new Vector3(player.transform.position.x, 0f, player.transform.position.z);
                float travelled = Vector3.Distance(startPlanar, nowPlanar);
                Check(travelled > 1.0f, $"player actually moved {travelled:F2} m under joystick input (expected > 1 m)");

                ThirdPersonCamera thirdPersonCamera = FindFirstObjectByType<ThirdPersonCamera>();
                if (thirdPersonCamera != null && travelled > 0.25f)
                {
                    float alignment = Vector3.Dot((nowPlanar - startPlanar).normalized, thirdPersonCamera.CameraPlanarForward);
                    Check(alignment > 0.85f, "player travels along the camera's forward direction");
                }

                yield return TouchStep(finger, TouchPhase.Ended, center + Vector2.up * (radius * 0.8f));
                yield return WaitForCondition(() => !m_joystick.IsActive, CheckTimeoutSeconds, "stick released after integration check");
                yield return new WaitForSeconds(0.6f);
                Check(player.Speed < 0.5f, "player stops after the stick is released");
            }
            else
            {
                Debug.LogWarning("[JoystickTest] No player in scene; skipping the player integration check.", this);
            }

            // 7. Adjustable size / position ----------------------------------------
            Debug.Log("[JoystickTest] --- Size & position API ---");
            {
                float oldRadius = m_joystick.GetBaseScreenRadius();
                Vector2 oldCenter = m_joystick.GetBaseCenterScreen();

                m_joystick.SetSize(420f, 150f);
                m_joystick.SetScreenPosition(new Vector2(0f, 0f), new Vector2(520f, 300f));
                yield return Settle(2);

                float newRadius = m_joystick.GetBaseScreenRadius();
                Vector2 newCenter = m_joystick.GetBaseCenterScreen();

                Check(Mathf.Abs(newRadius / Mathf.Max(1f, oldRadius) - 420f / 340f) <= 0.05f, "SetSize scales the travel radius proportionally");
                Check((newCenter - oldCenter).sqrMagnitude > 1f, "SetScreenPosition moves the stick on screen");
                Check(Mathf.Abs(m_joystick.BaseRect.sizeDelta.x - 420f) <= 0.5f, "base rect matches the configured size");
                Check(m_joystick.Output == Vector2.zero, "stick stays idle while repositioned");

                m_joystick.SetSize(340f, 0f);
                m_joystick.SetScreenPosition(new Vector2(0f, 0f), new Vector2(300f, 320f));
                yield return Settle(1);
                Check(true, "layout restored to defaults");
            }
        }

        /// <summary>Shared helper: ends the held finger and verifies a clean release.</summary>
        private IEnumerator ReleaseAndVerify(int finger, Vector2 lastPosition)
        {
            yield return TouchStep(finger, TouchPhase.Ended, lastPosition);
            yield return WaitForCondition(() => !m_joystick.IsActive, CheckTimeoutSeconds, "releasing the finger ends stick activity");
            yield return Settle(2);
            Check(m_input.Move == Vector2.zero, "Move returns to exactly zero on release");
            Check(m_joystick.Output == Vector2.zero, "joystick output is zero after release");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private IEnumerator WaitForOtherSuites()
        {
            PlayerControllerTest playerTest = FindFirstObjectByType<PlayerControllerTest>();
            float waited = 0f;
            while (playerTest != null && playerTest.IsRunning && waited < MaxWaitForOtherSuiteSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (waited >= MaxWaitForOtherSuiteSeconds)
                Debug.LogWarning("[JoystickTest] Timed out waiting for the player controller suite; running anyway.", this);

            yield return new WaitForSeconds(0.25f);
        }

        private int NewFinger() => m_nextFingerId++;

        /// <summary>Queues a touch state change and waits a frame for the Input System to dispatch it.</summary>
        private IEnumerator TouchStep(int finger, TouchPhase phase, Vector2 screenPosition)
        {
            InputSystem.QueueStateEvent(m_touchDevice, new TouchState
            {
                touchId = finger,
                phase = phase,
                position = screenPosition
            });

            yield return null;
        }

        private IEnumerator Settle(int frames)
        {
            for (int i = 0; i < frames; i++)
                yield return null;
        }

        private IEnumerator WaitForCondition(Func<bool> predicate, float timeout, string label)
        {
            float elapsed = 0f;
            while (!predicate())
            {
                elapsed += Time.deltaTime;
                if (elapsed >= timeout)
                {
                    Check(false, $"{label} (timed out after {timeout:F0}s)");
                    yield break;
                }

                yield return null;
            }

            Check(true, label);
        }

        /// <summary>Mirrors VirtualJoystickMath.ApplyDeadZone for building expected values.</summary>
        private static float ExpectedMagnitude(float deflection, float deadZone)
        {
            return deflection <= deadZone ? 0f : (deflection - deadZone) / (1f - deadZone);
        }

        private void Check(bool condition, string label)
        {
            m_total++;
            if (condition)
            {
                m_passed++;
                Debug.Log($"[JoystickTest] PASS: {label}", this);
            }
            else
            {
                Debug.LogError($"[JoystickTest] FAIL: {label}", this);
            }
        }

        private void Cleanup()
        {
            if (m_touchDevice != null && m_touchDevice.added)
                InputSystem.RemoveDevice(m_touchDevice);
            m_touchDevice = null;

            if (m_input != null)
                m_input.SetMobileMove(Vector2.zero);

            if (player != null)
            {
                player.ResetMovementState();
                player.transform.SetPositionAndRotation(new Vector3(0f, 1f, 8f), Quaternion.Euler(0f, 180f, 0f));
            }
        }
    }
}
