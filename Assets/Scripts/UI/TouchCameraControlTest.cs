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
    /// Play Mode verification suite for <see cref="TouchCameraControl"/>.
    ///
    /// <para>
    /// Injects touch events through a test <see cref="Touchscreen"/> device and verifies all
    /// mobile touch camera control requirements end to end:
    /// <list type="number">
    /// <item>Wiring: <see cref="TouchCameraControl"/>, shared <c>MobileControlsCanvas</c>,
    /// <see cref="GameInput"/>, <see cref="ThirdPersonCamera"/>, and <see cref="VirtualJoystick"/>.</item>
    /// <item>Touch detection &amp; horizontal rotation (left and right yaw orbiting + release).</item>
    /// <item>Vertical rotation &amp; vertical camera limits (pitch clamping at min/max angles).</item>
    /// <item>Smooth rotation (exponential displacement smoothing + camera damping).</item>
    /// <item>Adjustable sensitivity &amp; axis inversion.</item>
    /// <item>UI touch separation — dragging the movement joystick (even across the screen into
    /// the camera zone) must NEVER rotate the camera.</item>
    /// <item>Simultaneous dual-thumb multi-touch (moving with the joystick while rotating the camera
    /// with a second finger, and ignoring a third finger).</item>
    /// <item>UI touch separation — touching or dragging on future UI buttons inside the camera
    /// touch zone must NEVER rotate the camera.</item>
    /// <item>Multi-aspect-ratio verification across 16:9, 18:9, 19.5:9, 20:9, 21:9, 16:10, 4:3,
    /// and the current Unity Game view resolution.</item>
    /// </list>
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TouchCameraControlTest : MonoBehaviour
    {
        [Tooltip("Camera under test. If unassigned, found automatically in the scene.")]
        [SerializeField] private ThirdPersonCamera thirdPersonCamera;

        [Tooltip("Player controller used to restore spawn state after dual-thumb checks.")]
        [SerializeField] private ThirdPersonPlayerController player;

        [Tooltip("Automatically runs the verification suite on Start in Play Mode.")]
        [SerializeField] private bool runOnStart = true;

        private const float MaxWaitForOtherSuiteSeconds = 120f;

        private TouchCameraControl m_cameraControl;
        private VirtualJoystick m_joystick;
        private GameInput m_input;
        private Touchscreen m_touchDevice;
        private int m_nextFingerId = 300;

        private bool m_running;
        private int m_passed;
        private int m_total;

        /// <summary>True while the automated suite is in progress.</summary>
        public bool IsRunning => m_running;

        private void Start()
        {
            if (runOnStart)
                RunTestSuite();
        }

        /// <summary>Runs the complete mobile touch camera verification suite in Play Mode.</summary>
        [ContextMenu("Verify: Run Touch Camera Control Test Suite")]
        public void RunTestSuite()
        {
            if (m_running)
            {
                Debug.LogWarning("[TouchCameraTest] A test run is already in progress.");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[TouchCameraTest] Touch injection requires Play Mode. Enter Play Mode and run again.");
                return;
            }

            StartCoroutine(RunAllTests());
        }

        private IEnumerator RunAllTests()
        {
            m_running = true;
            m_passed = 0;
            m_total = 0;

            Debug.Log("[TouchCameraTest] =========================================");
            Debug.Log("[TouchCameraTest] Starting Mobile Touch Camera Control Test Suite...");

            try
            {
                yield return WaitForOtherSuites();
                yield return RunChecks();
            }
            finally
            {
                Cleanup();
            }

            Debug.Log($"[TouchCameraTest] Test Results: {m_passed}/{m_total} checks passed.");

            if (m_passed == m_total && m_total > 0)
            {
                Debug.Log("[TouchCameraTest] VERIFICATION PASSED. Mobile touch camera control (smooth rotation, adjustable sensitivity, vertical limits, horizontal rotation, touch detection, joystick & UI button separation, multi-aspect-ratio layout) functioning correctly with zero errors.");
            }
            else
            {
                Debug.LogError($"[TouchCameraTest] VERIFICATION FAILED. Only {m_passed}/{m_total} checks passed.");
            }

            Debug.Log("[TouchCameraTest] =========================================");
            m_running = false;
        }

        private IEnumerator RunChecks()
        {
            m_input = GameInput.Instance;
            m_cameraControl = TouchCameraControl.Instance;
            m_joystick = VirtualJoystick.Instance;
            if (thirdPersonCamera == null)
                thirdPersonCamera = FindFirstObjectByType<ThirdPersonCamera>();
            if (player == null)
                player = FindFirstObjectByType<ThirdPersonPlayerController>();

            // 1. Wiring & Initialization ---------------------------------------
            Debug.Log("[TouchCameraTest] --- 1. Wiring & initialization ---");
            Check(m_cameraControl != null, "TouchCameraControl instance exists (bootstrap or scene placement)");
            Check(m_input != null, "GameInput facade is available");
            Check(thirdPersonCamera != null, "ThirdPersonCamera is available in scene");
            Check(m_joystick != null, "VirtualJoystick instance is available for separation checks");

            if (m_cameraControl == null || m_input == null || thirdPersonCamera == null)
            {
                Debug.LogError("[TouchCameraTest] Missing prerequisites; aborting checks.");
                yield break;
            }

            yield return WaitForCondition(() => m_cameraControl.IsUiBuilt, 2f, "TouchCameraControl UI is built");
            Check(m_cameraControl.HostCanvas != null, "TouchCameraControl is hosted on MobileControlsCanvas");
            if (m_cameraControl.HostCanvas != null)
            {
                Check(m_cameraControl.HostCanvas.GetComponent<GraphicRaycaster>() != null,
                    "MobileControlsCanvas has a GraphicRaycaster for UI touch separation");
            }
            Check(!m_cameraControl.IsActive, "TouchCameraControl starts idle");
            Check(m_cameraControl.LookOutput == Vector2.zero, "TouchCameraControl look output starts at zero");

            m_touchDevice = InputSystem.AddDevice<Touchscreen>();
            Check(m_touchDevice != null && m_touchDevice.added, "test touchscreen device injected");

            Vector2 camZoneCenter = m_cameraControl.GetTouchZoneCenterScreen();
            float scaleFactor = m_cameraControl.GetCanvasScaleFactor();
            Check(m_cameraControl.ContainsScreenPoint(camZoneCenter),
                $"camera touch zone center {camZoneCenter} is valid at Game view {Screen.width}x{Screen.height}");

            // 2. Touch Detection & Horizontal Rotation -------------------------
            Debug.Log("[TouchCameraTest] --- 2. Touch detection & horizontal rotation ---");
            {
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                float startYaw = thirdPersonCamera.TargetYaw;
                float dragRefUnits = 200f;
                Vector2 dragPixelsRight = new Vector2(dragRefUnits * scaleFactor, 0f);

                int finger = NewFinger();
                yield return TouchStep(finger, TouchPhase.Began, camZoneCenter);
                Check(m_cameraControl.IsActive && m_cameraControl.ActiveTouchId == finger,
                    "touch-down inside camera zone claims the finger");

                // Drag horizontally to the right over several frames and let smoothing converge.
                const int steps = 5;
                for (int s = 1; s <= steps; s++)
                {
                    Vector2 pos = camZoneCenter + dragPixelsRight * (s / (float)steps);
                    yield return TouchStep(finger, TouchPhase.Moved, pos);
                }
                yield return Settle(15);

                float expectedYawDelta = dragRefUnits * m_cameraControl.Sensitivity *
                                         m_cameraControl.HorizontalSensitivity *
                                         thirdPersonCamera.HorizontalSensitivity;
                float actualRightYawDelta = Mathf.DeltaAngle(startYaw, thirdPersonCamera.TargetYaw);
                Check(Mathf.Abs(actualRightYawDelta - expectedYawDelta) <= 1.5f,
                    $"dragging right rotates yaw by +{actualRightYawDelta:F2} deg (expected +{expectedYawDelta:F2} deg)");
                Check(Mathf.Abs(Mathf.DeltaAngle(thirdPersonCamera.CurrentYaw, thirdPersonCamera.TargetYaw)) <= 1.5f,
                    "smoothed CurrentYaw tracks TargetYaw smoothly");

                // Drag back to the left to the starting position.
                for (int s = steps - 1; s >= 0; s--)
                {
                    Vector2 pos = camZoneCenter + dragPixelsRight * (s / (float)steps);
                    yield return TouchStep(finger, TouchPhase.Moved, pos);
                }
                yield return Settle(15);

                float returnedError = Mathf.Abs(Mathf.DeltaAngle(startYaw, thirdPersonCamera.TargetYaw));
                Check(returnedError <= 1.5f,
                    $"dragging left rotates yaw back to start (error={returnedError:F2} deg)");

                yield return TouchStep(finger, TouchPhase.Ended, camZoneCenter);
                yield return Settle(2);
                Check(!m_cameraControl.IsActive, "releasing the finger ends camera touch activity");
                Check(m_input.Look == Vector2.zero, "GameInput.Look returns to zero on release");
            }

            // 3. Vertical Rotation & Vertical Camera Limits --------------------
            Debug.Log("[TouchCameraTest] --- 3. Vertical rotation & vertical camera limits ---");
            {
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                float minPitch = m_cameraControl.MinVerticalAngle;
                float maxPitch = m_cameraControl.MaxVerticalAngle;

                // Drag strongly upward (positive screen Y -> tilts view up -> clamps at minVerticalAngle).
                int fingerUp = NewFinger();
                Vector2 bigUp = camZoneCenter + new Vector2(0f, 900f * scaleFactor);
                yield return TouchStep(fingerUp, TouchPhase.Began, camZoneCenter);
                yield return TouchStep(fingerUp, TouchPhase.Moved, bigUp);
                yield return Settle(15);

                Check(Mathf.Abs(thirdPersonCamera.TargetPitch - minPitch) <= 0.5f &&
                      Mathf.Abs(thirdPersonCamera.CurrentPitch - minPitch) <= 1.0f,
                    $"vertical upward drag clamps pitch at minVerticalAngle ({thirdPersonCamera.CurrentPitch:F1} deg == {minPitch:F1} deg)");

                yield return TouchStep(fingerUp, TouchPhase.Ended, bigUp);
                yield return Settle(2);

                // Drag strongly downward (negative screen Y -> tilts view down -> clamps at maxVerticalAngle).
                int fingerDown = NewFinger();
                Vector2 bigDown = camZoneCenter - new Vector2(0f, 1200f * scaleFactor);
                yield return TouchStep(fingerDown, TouchPhase.Began, camZoneCenter);
                yield return TouchStep(fingerDown, TouchPhase.Moved, bigDown);
                yield return Settle(15);

                Check(Mathf.Abs(thirdPersonCamera.TargetPitch - maxPitch) <= 0.5f &&
                      Mathf.Abs(thirdPersonCamera.CurrentPitch - maxPitch) <= 1.0f,
                    $"vertical downward drag clamps pitch at maxVerticalAngle ({thirdPersonCamera.CurrentPitch:F1} deg == {maxPitch:F1} deg)");

                yield return TouchStep(fingerDown, TouchPhase.Ended, bigDown);
                yield return Settle(2);

                // Verify runtime SetVerticalLimits synchronization with ThirdPersonCamera.
                m_cameraControl.SetVerticalLimits(-20f, 45f);
                Check(Mathf.Abs(thirdPersonCamera.MinVerticalAngle - (-20f)) <= 0.01f &&
                      Mathf.Abs(thirdPersonCamera.MaxVerticalAngle - 45f) <= 0.01f &&
                      thirdPersonCamera.CurrentPitch <= 45.01f,
                    "SetVerticalLimits(-20, 45) synchronizes with ThirdPersonCamera and clamps pitch");

                m_cameraControl.SetVerticalLimits(-35f, 70f);
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);
            }

            // 4. Smooth Rotation -----------------------------------------------
            Debug.Log("[TouchCameraTest] --- 4. Smooth rotation ---");
            {
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                float startYaw = thirdPersonCamera.CurrentYaw;
                float dragRefUnits = 240f;
                float fullExpectedYaw = dragRefUnits * m_cameraControl.Sensitivity *
                                        m_cameraControl.HorizontalSensitivity *
                                        thirdPersonCamera.HorizontalSensitivity;

                int finger = NewFinger();
                Vector2 endPos = camZoneCenter + new Vector2(dragRefUnits * scaleFactor, 0f);
                yield return TouchStep(finger, TouchPhase.Began, camZoneCenter);
                yield return TouchStep(finger, TouchPhase.Moved, endPos);

                float firstFrameYawChange = Mathf.DeltaAngle(startYaw, thirdPersonCamera.CurrentYaw);
                Check(firstFrameYawChange > 0.1f && firstFrameYawChange < fullExpectedYaw * 0.85f,
                    $"single-frame drag step is smoothed (first frame rotated {firstFrameYawChange:F2} deg of {fullExpectedYaw:F2} deg total)");

                // Hold finger stationary and verify monotonic smooth convergence to full angle.
                float prevYawChange = firstFrameYawChange;
                bool monotonic = true;
                for (int i = 0; i < 18; i++)
                {
                    yield return null;
                    float curYawChange = Mathf.DeltaAngle(startYaw, thirdPersonCamera.CurrentYaw);
                    if (curYawChange < prevYawChange - 0.05f)
                        monotonic = false;
                    prevYawChange = curYawChange;
                }

                Check(monotonic, "camera yaw glides monotonically during smoothing with zero jitter or reversal");
                Check(Mathf.Abs(prevYawChange - fullExpectedYaw) <= 1.0f,
                    $"smoothed rotation converges to exact total angle ({prevYawChange:F2} deg vs {fullExpectedYaw:F2} deg)");

                yield return TouchStep(finger, TouchPhase.Ended, endPos);
                yield return Settle(2);
            }

            // 5. Adjustable Sensitivity ----------------------------------------
            Debug.Log("[TouchCameraTest] --- 5. Adjustable sensitivity ---");
            {
                float originalSens = m_cameraControl.Sensitivity;
                float originalH = m_cameraControl.HorizontalSensitivity;
                float originalV = m_cameraControl.VerticalSensitivity;
                bool origInvH = m_cameraControl.InvertHorizontal;
                bool origInvV = m_cameraControl.InvertVertical;

                Vector2 testDrag = new Vector2(120f * scaleFactor, 0f);

                // Measure rotation at sensitivity = 1.0.
                m_cameraControl.SetSensitivity(1.0f);
                m_cameraControl.SetSensitivity(1.0f, 1.0f);
                m_cameraControl.SetInvert(false, false);
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                int finger1 = NewFinger();
                yield return TouchStep(finger1, TouchPhase.Began, camZoneCenter);
                yield return TouchStep(finger1, TouchPhase.Moved, camZoneCenter + testDrag);
                yield return Settle(15);
                float yawAt1x = Mathf.DeltaAngle(180f, thirdPersonCamera.TargetYaw);
                yield return TouchStep(finger1, TouchPhase.Ended, camZoneCenter + testDrag);
                yield return Settle(2);

                // Measure rotation at sensitivity = 2.5.
                m_cameraControl.SetSensitivity(2.5f);
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                int finger2 = NewFinger();
                yield return TouchStep(finger2, TouchPhase.Began, camZoneCenter);
                yield return TouchStep(finger2, TouchPhase.Moved, camZoneCenter + testDrag);
                yield return Settle(15);
                float yawAt2_5x = Mathf.DeltaAngle(180f, thirdPersonCamera.TargetYaw);
                yield return TouchStep(finger2, TouchPhase.Ended, camZoneCenter + testDrag);
                yield return Settle(2);

                float ratio = yawAt2_5x / Mathf.Max(0.001f, yawAt1x);
                Check(Mathf.Abs(ratio - 2.5f) <= 0.08f,
                    $"sensitivity 2.5x scales rotation proportionally ({yawAt2_5x:F2} deg vs {yawAt1x:F2} deg, ratio={ratio:F2})");

                // Restore defaults.
                m_cameraControl.SetSensitivity(originalSens);
                m_cameraControl.SetSensitivity(originalH, originalV);
                m_cameraControl.SetInvert(origInvH, origInvV);
            }

            // 6. UI Touch Separation: Movement Joystick Must NOT Rotate Camera -
            Debug.Log("[TouchCameraTest] --- 6. Movement joystick touch separation ---");
            if (m_joystick != null)
            {
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                float initialYaw = thirdPersonCamera.TargetYaw;
                float initialPitch = thirdPersonCamera.TargetPitch;

                Vector2 joyCenter = m_joystick.GetBaseCenterScreen();
                float joyRadius = m_joystick.GetBaseScreenRadius();

                Check(!m_cameraControl.CanClaimScreenPoint(joyCenter),
                    "camera touch control rejects touch-down at joystick center");

                int joyFinger = NewFinger();
                yield return TouchStep(joyFinger, TouchPhase.Began, joyCenter);
                yield return TouchStep(joyFinger, TouchPhase.Moved, joyCenter + Vector2.up * (joyRadius * 0.9f));
                yield return TouchStep(joyFinger, TouchPhase.Moved, joyCenter + new Vector2(joyRadius, joyRadius));

                // Even drag the joystick finger all the way into the right-side camera touch zone!
                yield return TouchStep(joyFinger, TouchPhase.Moved, camZoneCenter);
                yield return TouchStep(joyFinger, TouchPhase.Moved, camZoneCenter + new Vector2(150f, 100f));
                yield return Settle(4);

                Check(m_joystick.IsActive && m_input.Move.sqrMagnitude > 0.1f,
                    "movement joystick is active and driving Move input");
                Check(!m_cameraControl.IsActive,
                    "camera touch control stays inactive while dragging the movement joystick (even into the right side)");
                Check(m_input.Look == Vector2.zero,
                    "GameInput.Look stays exactly zero while dragging the movement joystick");

                float yawDrift = Mathf.Abs(Mathf.DeltaAngle(initialYaw, thirdPersonCamera.TargetYaw));
                float pitchDrift = Mathf.Abs(thirdPersonCamera.TargetPitch - initialPitch);
                Check(yawDrift <= 0.001f && pitchDrift <= 0.001f,
                    $"camera did NOT rotate while using movement joystick (yawDrift={yawDrift:F4} deg, pitchDrift={pitchDrift:F4} deg)");

                yield return TouchStep(joyFinger, TouchPhase.Ended, camZoneCenter + new Vector2(150f, 100f));
                yield return Settle(2);
            }

            // 7. Simultaneous Dual-Thumb Multi-Touch (Joystick + Camera) -------
            Debug.Log("[TouchCameraTest] --- 7. Simultaneous dual-thumb multi-touch ---");
            if (m_joystick != null)
            {
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                Vector2 joyCenter = m_joystick.GetBaseCenterScreen();
                float joyRadius = m_joystick.GetBaseScreenRadius();
                float startYaw = thirdPersonCamera.TargetYaw;

                int moveFinger = NewFinger();
                int camFinger = NewFinger();
                int extraFinger = NewFinger();

                // Thumb 1 grabs the movement joystick.
                yield return TouchStep(moveFinger, TouchPhase.Began, joyCenter);
                yield return TouchStep(moveFinger, TouchPhase.Moved, joyCenter + Vector2.up * (joyRadius * 0.85f));

                // Thumb 2 simultaneously grabs the camera touch zone and rotates.
                Vector2 camTarget = camZoneCenter + new Vector2(180f * scaleFactor, 0f);
                yield return TouchStep(camFinger, TouchPhase.Began, camZoneCenter);
                yield return TouchStep(camFinger, TouchPhase.Moved, camTarget);
                yield return Settle(12);

                Check(m_joystick.IsActive && m_cameraControl.IsActive,
                    "joystick and camera touch control operate simultaneously with two thumbs");
                float dualYawDelta = Mathf.DeltaAngle(startYaw, thirdPersonCamera.TargetYaw);
                Check(dualYawDelta > 15f && m_input.Move.y > 0.5f,
                    $"simultaneous move ({m_input.Move.y:F2}) and camera yaw (+{dualYawDelta:F1} deg) both succeed");

                // A third finger touches inside the camera zone and tries to pull left: must be ignored.
                float yawBeforeExtra = thirdPersonCamera.TargetYaw;
                Vector2 extraStart = camZoneCenter + new Vector2(0f, 100f * scaleFactor);
                yield return TouchStep(extraFinger, TouchPhase.Began, extraStart);
                yield return TouchStep(extraFinger, TouchPhase.Moved, extraStart - new Vector2(300f * scaleFactor, 0f));
                yield return Settle(4);
                Check(Mathf.Abs(Mathf.DeltaAngle(yawBeforeExtra, thirdPersonCamera.TargetYaw)) <= 1.0f,
                    "extra finger in camera zone cannot steal or jump the active camera touch");

                yield return TouchStep(extraFinger, TouchPhase.Ended, extraStart - new Vector2(300f * scaleFactor, 0f));
                yield return TouchStep(camFinger, TouchPhase.Ended, camTarget);
                yield return Settle(2);

                Check(!m_cameraControl.IsActive && m_joystick.IsActive,
                    "releasing the camera finger leaves the movement joystick active");

                yield return TouchStep(moveFinger, TouchPhase.Ended, joyCenter + Vector2.up * (joyRadius * 0.85f));
                yield return Settle(2);
            }

            // 8. UI Touch Separation: Future UI Buttons Must NOT Rotate Camera -
            Debug.Log("[TouchCameraTest] --- 8. Future UI buttons touch separation ---");
            {
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                yield return Settle(2);

                // Create simulated future HUD buttons (e.g. Attack & Ability) directly inside the
                // right-hand camera touch zone on MobileControlsCanvas.
                RectTransform attackBtnRect = CreateSimulatedHudButton(
                    "SimulatedAttackButton",
                    new Vector2(1f, 0f),
                    new Vector2(-280f, 260f),
                    new Vector2(220f, 220f));

                RectTransform abilityBtnRect = CreateSimulatedHudButton(
                    "SimulatedAbilityButton",
                    new Vector2(1f, 0f),
                    new Vector2(-520f, 420f),
                    new Vector2(160f, 160f));

                Canvas.ForceUpdateCanvases();
                yield return null;

                Vector2 attackScreenPos = RectTransformUtility.WorldToScreenPoint(null, attackBtnRect.position);
                Vector2 abilityScreenPos = RectTransformUtility.WorldToScreenPoint(null, abilityBtnRect.position);

                // Confirm both buttons sit inside the right-half camera touch zone but are blocked as UI.
                Check(m_cameraControl.ContainsScreenPoint(attackScreenPos) &&
                      m_cameraControl.ContainsScreenPoint(abilityScreenPos),
                    "simulated future UI buttons are positioned inside the right-side camera touch area");
                Check(m_cameraControl.IsScreenPointOverInteractiveUI(attackScreenPos) &&
                      !m_cameraControl.CanClaimScreenPoint(attackScreenPos),
                    "IsScreenPointOverInteractiveUI detects Attack button and blocks camera claim");
                Check(m_cameraControl.IsScreenPointOverInteractiveUI(abilityScreenPos) &&
                      !m_cameraControl.CanClaimScreenPoint(abilityScreenPos),
                    "IsScreenPointOverInteractiveUI detects Ability button and blocks camera claim");

                float yawBeforeBtn = thirdPersonCamera.TargetYaw;
                float pitchBeforeBtn = thirdPersonCamera.TargetPitch;

                // Touch down on the Attack button and drag across the camera zone.
                int btnFinger1 = NewFinger();
                yield return TouchStep(btnFinger1, TouchPhase.Began, attackScreenPos);
                yield return TouchStep(btnFinger1, TouchPhase.Moved, attackScreenPos + new Vector2(-200f, 180f));
                yield return TouchStep(btnFinger1, TouchPhase.Moved, camZoneCenter);
                yield return Settle(4);

                Check(!m_cameraControl.IsActive && m_input.Look == Vector2.zero,
                    "touching and dragging from Attack UI button does NOT activate camera look");

                yield return TouchStep(btnFinger1, TouchPhase.Ended, camZoneCenter);
                yield return Settle(2);

                // Touch down on the Ability button and drag across the camera zone.
                int btnFinger2 = NewFinger();
                yield return TouchStep(btnFinger2, TouchPhase.Began, abilityScreenPos);
                yield return TouchStep(btnFinger2, TouchPhase.Moved, abilityScreenPos + new Vector2(180f, 220f));
                yield return Settle(4);

                Check(!m_cameraControl.IsActive && m_input.Look == Vector2.zero,
                    "touching and dragging from Ability UI button does NOT activate camera look");

                float btnYawDrift = Mathf.Abs(Mathf.DeltaAngle(yawBeforeBtn, thirdPersonCamera.TargetYaw));
                float btnPitchDrift = Mathf.Abs(thirdPersonCamera.TargetPitch - pitchBeforeBtn);
                Check(btnYawDrift <= 0.001f && btnPitchDrift <= 0.001f,
                    $"camera did NOT rotate when interacting with future UI buttons (yawDrift={btnYawDrift:F4} deg, pitchDrift={btnPitchDrift:F4} deg)");

                yield return TouchStep(btnFinger2, TouchPhase.Ended, abilityScreenPos + new Vector2(180f, 220f));
                yield return Settle(2);

                // Touching open space in the camera touch zone (upper-right, away from buttons) still rotates camera.
                Vector2 openSpace = new Vector2(Screen.width * 0.72f, Screen.height * 0.78f);
                Check(m_cameraControl.CanClaimScreenPoint(openSpace),
                    "open space in camera touch area remains claimable alongside UI buttons");

                int openFinger = NewFinger();
                yield return TouchStep(openFinger, TouchPhase.Began, openSpace);
                yield return TouchStep(openFinger, TouchPhase.Moved, openSpace + new Vector2(140f * scaleFactor, 0f));
                yield return Settle(10);

                Check(Mathf.Abs(Mathf.DeltaAngle(yawBeforeBtn, thirdPersonCamera.TargetYaw)) > 10f,
                    "dragging on open camera area next to UI buttons rotates the camera normally");

                yield return TouchStep(openFinger, TouchPhase.Ended, openSpace + new Vector2(140f * scaleFactor, 0f));
                yield return Settle(2);

                Destroy(attackBtnRect.gameObject);
                Destroy(abilityBtnRect.gameObject);
                yield return null;
            }

            // 9. Multi-Aspect-Ratio Verification -------------------------------
            Debug.Log("[TouchCameraTest] --- 9. Screen aspect ratios verification ---");
            {
                (string label, int width, int height)[] aspectRatios = new[]
                {
                    ("16:9 Standard Landscape", 1920, 1080),
                    ("18:9 Wide Phone", 2160, 1080),
                    ("19.5:9 Modern Smartphone", 2532, 1170),
                    ("20:9 Android Flagship", 2400, 1080),
                    ("21:9 Ultrawide Phone", 2520, 1080),
                    ("16:10 Tablet Landscape", 1920, 1200),
                    ("4:3 iPad / Tablet", 2048, 1536)
                };

                bool allAspectsValid = true;
                for (int i = 0; i < aspectRatios.Length; i++)
                {
                    var ar = aspectRatios[i];
                    m_cameraControl.EvaluateViewportLayout(
                        ar.width,
                        ar.height,
                        out Rect camZone,
                        out Rect joyZone,
                        out float arScale);

                    bool camCoversRightHalf = Mathf.Abs(camZone.xMin - ar.width * 0.5f) <= 1f &&
                                              Mathf.Abs(camZone.xMax - ar.width) <= 1f &&
                                              camZone.height >= ar.height - 1f;
                    bool zonesDisjoint = !camZone.Overlaps(joyZone);

                    // Verify normalized swipe of 200 canvas units produces the exact same look delta on every aspect ratio.
                    Vector2 pixelSwipe = new Vector2(200f * arScale, -100f * arScale);
                    Vector2 normalizedSwipe = TouchCameraMath.NormalizeScreenDelta(pixelSwipe, arScale, normalizeByScale: true);
                    bool scaleConsistent = Mathf.Abs(normalizedSwipe.x - 200f) <= 0.01f &&
                                           Mathf.Abs(normalizedSwipe.y - (-100f)) <= 0.01f;

                    if (!camCoversRightHalf || !zonesDisjoint || !scaleConsistent)
                    {
                        allAspectsValid = false;
                        Debug.LogError($"[TouchCameraTest] Aspect ratio {ar.label} ({ar.width}x{ar.height}) failed layout/separation check: camZone={camZone}, joyZone={joyZone}, scale={arScale:F3}");
                    }
                }

                Check(allAspectsValid,
                    $"all {aspectRatios.Length} mobile/tablet aspect ratios (16:9, 18:9, 19.5:9, 20:9, 21:9, 16:10, 4:3) maintain disjoint joystick/camera zones and consistent normalized sensitivity");
            }
        }

        private RectTransform CreateSimulatedHudButton(string name, Vector2 anchor, Vector2 anchoredPosition, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(m_cameraControl.HostCanvas.transform, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Image img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.25f);
            img.raycastTarget = true;

            return rect;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private IEnumerator WaitForOtherSuites()
        {
            // Yield one frame so Start() on PlayerControllerTest and VirtualJoystickTest runs first.
            yield return null;

            PlayerControllerTest playerTest = FindFirstObjectByType<PlayerControllerTest>();
            VirtualJoystickTest joystickTest = FindFirstObjectByType<VirtualJoystickTest>();

            float waited = 0f;
            while (((playerTest != null && playerTest.IsRunning) ||
                    (joystickTest != null && joystickTest.IsRunning)) &&
                   waited < MaxWaitForOtherSuiteSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (waited >= MaxWaitForOtherSuiteSeconds)
                Debug.LogWarning("[TouchCameraTest] Timed out waiting for earlier test suites; running anyway.", this);

            yield return new WaitForSeconds(0.25f);
        }

        private int NewFinger() => m_nextFingerId++;

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

        private void Check(bool condition, string label)
        {
            m_total++;
            if (condition)
            {
                m_passed++;
                Debug.Log($"[TouchCameraTest] PASS: {label}", this);
            }
            else
            {
                Debug.LogError($"[TouchCameraTest] FAIL: {label}", this);
            }
        }

        private void Cleanup()
        {
            if (m_touchDevice != null && m_touchDevice.added)
                InputSystem.RemoveDevice(m_touchDevice);
            m_touchDevice = null;

            if (m_input != null)
            {
                m_input.SetMobileMove(Vector2.zero);
                m_input.SetMobileLook(Vector2.zero);
            }

            if (player != null)
            {
                player.ResetMovementState();
                player.transform.SetPositionAndRotation(new Vector3(0f, 1f, 8f), Quaternion.Euler(0f, 180f, 0f));
            }

            if (thirdPersonCamera != null)
            {
                thirdPersonCamera.SetPitchLimits(-35f, 70f);
                thirdPersonCamera.SetAngles(180f, 15f, snap: true);
                thirdPersonCamera.TeleportToTarget();
            }
        }
    }
}
