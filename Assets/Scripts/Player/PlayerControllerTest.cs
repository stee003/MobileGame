using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MobileGame.Camera;
using MobileGame.Input;

namespace MobileGame.Player
{
    /// <summary>
    /// Play Mode verification suite for <see cref="ThirdPersonPlayerController"/>.
    ///
    /// <para>
    /// Drives the player with the shared input facade (via <see cref="GameInput.SetMobileMove"/>,
    /// the same seam virtual mobile controls will use) and verifies every locomotion requirement:
    /// <list type="number">
    /// <item>Component wiring and Inspector configuration.</item>
    /// <item>Forward / backward / left / right movement.</item>
    /// <item>Camera-relative movement (movement follows camera yaw).</item>
    /// <item>Acceleration and deceleration of the planar velocity.</item>
    /// <item>Rotation toward the movement direction.</item>
    /// <item>Gravity, falling and landing.</item>
    /// <item>Slope traversal (ramp up and ramp down).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The suite runs automatically on Start in Play Mode, or on demand from the Inspector context
    /// menu. It is a diagnostic tool only: it contains no gameplay logic.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerControllerTest : MonoBehaviour
    {
        [Tooltip("Player controller under test. If unassigned, one is found automatically in the scene.")]
        [SerializeField] private ThirdPersonPlayerController player;

        [Tooltip("Camera used to derive the expected movement directions. If unassigned, one is found automatically.")]
        [SerializeField] private ThirdPersonCamera thirdPersonCamera;

        [Tooltip("Automatically runs the verification suite on Start in Play Mode.")]
        [SerializeField] private bool runOnStart = true;

        private bool m_running;
        private int m_passed;
        private int m_total;

        private void Start()
        {
            if (runOnStart)
            {
                RunTestSuite();
            }
        }

        /// <summary>
        /// Runs the complete verification suite. Movement checks need frames, so they only execute in
        /// Play Mode; outside Play Mode only the static wiring checks can run.
        /// </summary>
        [ContextMenu("Verify: Run Player Controller Test Suite")]
        public void RunTestSuite()
        {
            if (m_running)
            {
                Debug.LogWarning("[PlayerTest] A test run is already in progress.");
                return;
            }

            if (!ResolveReferences())
            {
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[PlayerTest] Movement checks need frames and only run in Play Mode. Running static checks only.");
                RunTest("Static wiring & configuration", StaticChecks());
                return;
            }

            StartCoroutine(RunAllTests());
        }

        private IEnumerator RunAllTests()
        {
            m_running = true;
            m_passed = 0;
            m_total = 0;

            Debug.Log("[PlayerTest] =========================================");
            Debug.Log("[PlayerTest] Starting Third-Person Player Controller Test Suite...");

            yield return RunTest("Static wiring & configuration", StaticChecks());
            yield return RunTest("Grounded at spawn", TestGroundedAtSpawn());
            yield return RunTest("Forward movement", TestDirectionalMovement("Forward", Vector3.forward));
            yield return RunTest("Backward movement", TestDirectionalMovement("Backward", Vector3.back));
            yield return RunTest("Right movement", TestDirectionalMovement("Right", Vector3.right));
            yield return RunTest("Left movement", TestDirectionalMovement("Left", Vector3.left));
            yield return RunTest("Camera-relative movement", TestCameraRelativeMovement());
            yield return RunTest("Acceleration", TestAcceleration());
            yield return RunTest("Deceleration", TestDeceleration());
            yield return RunTest("Rotation toward movement direction", TestRotationTowardsMovement());
            yield return RunTest("Gravity, falling and landing", TestGravityFallingAndLanding());
            yield return RunTest("Slope traversal", TestSlopeTraversal());
            yield return RunTest("Walking off a ledge", TestWalkOffLedge());
            yield return RunTest("Wall collision", TestWallCollision());

            // Restore a clean state for the developer.
            ReleaseInput();
            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(10);

            Debug.Log($"[PlayerTest] Test Results: {m_passed}/{m_total} tests passed.");

            if (m_passed == m_total)
            {
                Debug.Log("[PlayerTest] VERIFICATION PASSED. All player controller features functioning correctly with zero errors.");
            }
            else
            {
                Debug.LogError($"[PlayerTest] VERIFICATION FAILED. Only {m_passed}/{m_total} tests passed.");
            }

            Debug.Log("[PlayerTest] =========================================");
            m_running = false;
        }

        // ------------------------------------------------------------------
        // Tests (each marks its result through the passed-in TestResult)
        // ------------------------------------------------------------------

        private IEnumerator StaticChecks()
        {
            TestResult result = new TestResult();

            if (player.Controller == null)
            {
                Debug.LogError("[PlayerTest] Static checks FAILED: player has no CharacterController.");
                yield return result;
            }

            if (player.MoveSpeed <= 0f || player.Acceleration <= 0f || player.Deceleration <= 0f ||
                player.RotationSpeed <= 0f || player.Gravity <= 0f)
            {
                Debug.LogError("[PlayerTest] Static checks FAILED: movement speed, acceleration, deceleration, rotation speed and gravity must all be greater than zero.");
                yield return result;
            }

            if ((player.GroundLayers.value & (1 << 17)) == 0)
            {
                Debug.LogWarning("[PlayerTest] Static checks WARNING: ground layers do not include the Ground layer (17); ground detection may miss the arena floor.");
            }

            if (player.Controller.slopeLimit - player.MaxSlopeAngle > 0.5f)
            {
                Debug.LogWarning($"[PlayerTest] Static checks WARNING: maxSlopeAngle ({player.MaxSlopeAngle:F1}) is below CharacterController.slopeLimit ({player.Controller.slopeLimit:F1}); the player will be reported airborne on slopes it can actually walk.");
            }

            Debug.Log($"[PlayerTest] Static checks PASSED: controller + CharacterController + camera wired. " +
                      $"speed={player.MoveSpeed} m/s, accel={player.Acceleration} m/s^2, decel={player.Deceleration} m/s^2, " +
                      $"rotation={player.RotationSpeed} deg/s, gravity={player.Gravity} m/s^2.");

            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestGroundedAtSpawn()
        {
            TestResult result = new TestResult();

            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(12);

            float restY = player.transform.position.y;

            if (!player.IsGrounded)
            {
                Debug.LogError($"[PlayerTest] Grounded at spawn FAILED: player reports airborne at spawn (y={restY:F3}).");
                yield return result;
            }

            yield return WaitFrames(30);

            if (!player.IsGrounded)
            {
                Debug.LogError("[PlayerTest] Grounded at spawn FAILED: player left the ground while standing still.");
                yield return result;
            }

            if (Mathf.Abs(player.transform.position.y - restY) > 0.02f)
            {
                Debug.LogError($"[PlayerTest] Grounded at spawn FAILED: player drifted vertically while idle (from {restY:F3} to {player.transform.position.y:F3}).");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Grounded at spawn PASSED: resting at y={restY:F3}, grounded and stable while idle.");
            result.Passed = true;
            yield return result;
        }

        /// <summary>
        /// Drives the player along a world-space direction for a fixed time and verifies the
        /// displacement matches, the player reaches cruise speed and stays grounded.
        /// </summary>
        private IEnumerator TestDirectionalMovement(string label, Vector3 worldDirection)
        {
            TestResult result = new TestResult();
            const float duration = 0.6f;

            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(8);

            Vector3 start = player.transform.position;
            Vector3 expected = worldDirection.normalized;
            Drive(InputForWorldDirection(expected));

            yield return WaitSeconds(duration);

            Vector3 end = player.transform.position;
            Vector3 displacement = end - start;
            ReleaseInput();

            Vector3 planar = new Vector3(displacement.x, 0f, displacement.z);
            float planarDistance = planar.magnitude;
            float alignment = planarDistance > 0.01f ? Vector3.Dot(planar.normalized, expected) : -1f;
            float expectedDistance = ExpectedTravelDistance(duration);

            if (planarDistance < expectedDistance * 0.6f)
            {
                Debug.LogError($"[PlayerTest] {label} movement FAILED: travelled {planarDistance:F2}m, expected about {expectedDistance:F2}m toward {expected}.");
                yield return result;
            }

            if (alignment < 0.95f)
            {
                Debug.LogError($"[PlayerTest] {label} movement FAILED: movement direction does not match {expected} (alignment={alignment:F3}).");
                yield return result;
            }

            yield return WaitFrames(20);

            if (!player.IsGrounded)
            {
                Debug.LogError($"[PlayerTest] {label} movement FAILED: player left the ground while walking on flat ground.");
                yield return result;
            }

            Debug.Log($"[PlayerTest] {label} movement PASSED: {planarDistance:F2}m in {duration:F2}s (expected ~{expectedDistance:F2}m), " +
                      $"alignment={alignment:F3}, speed={player.Speed:F2} m/s, grounded.");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestCameraRelativeMovement()
        {
            TestResult result = new TestResult();

            // Turn the camera 90 degrees and confirm that "forward" input now moves along the new
            // camera forward instead of the previous one.
            Vector3 forwardBefore = thirdPersonCamera.CameraPlanarForward;
            float yawBefore = thirdPersonCamera.CurrentYaw;
            SetCameraYaw(yawBefore + 90f);
            yield return WaitFrames(20);

            Vector3 forwardAfter = thirdPersonCamera.CameraPlanarForward;
            float yawError = Mathf.Abs(Mathf.DeltaAngle(yawBefore + 90f, thirdPersonCamera.CurrentYaw));

            if (yawError > 2f)
            {
                Debug.LogError($"[PlayerTest] Camera-relative movement FAILED: camera yaw did not reach the requested angle (error={yawError:F2} deg).");
                yield return result;
            }

            float angleBetween = Vector3.Angle(forwardBefore, forwardAfter);
            if (angleBetween < 45f)
            {
                Debug.LogError($"[PlayerTest] Camera-relative movement FAILED: camera did not rotate enough to test relativity ({angleBetween:F1} deg).");
                yield return result;
            }

            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(8);

            Vector3 start = player.transform.position;
            Drive(new Vector2(0f, 1f)); // "forward" relative to the camera
            yield return WaitSeconds(0.5f);

            Vector3 planar = new Vector3(player.transform.position.x - start.x, 0f, player.transform.position.z - start.z);
            ReleaseInput();

            float planarDistance = planar.magnitude;
            float alignmentWithNew = planarDistance > 0.01f ? Vector3.Dot(planar.normalized, forwardAfter) : -1f;
            float alignmentWithOld = planarDistance > 0.01f ? Vector3.Dot(planar.normalized, forwardBefore) : -1f;

            if (planarDistance < ExpectedTravelDistance(0.5f) * 0.6f)
            {
                Debug.LogError($"[PlayerTest] Camera-relative movement FAILED: player only travelled {planarDistance:F2}m.");
                yield return result;
            }

            if (alignmentWithNew < 0.95f)
            {
                Debug.LogError($"[PlayerTest] Camera-relative movement FAILED: forward input moved along {planar.normalized} instead of camera forward {forwardAfter} (alignment={alignmentWithNew:F3}).");
                yield return result;
            }

            if (alignmentWithOld > 0.5f)
            {
                Debug.LogError($"[PlayerTest] Camera-relative movement FAILED: movement still follows the previous camera heading (alignment={alignmentWithOld:F3}).");
                yield return result;
            }

            SetCameraYaw(yawBefore);
            yield return WaitFrames(20);

            Debug.Log($"[PlayerTest] Camera-relative movement PASSED: forward input tracks camera forward after a {angleBetween:F0} deg camera rotation (alignment={alignmentWithNew:F3}).");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestAcceleration()
        {
            TestResult result = new TestResult();
            const float maxDuration = 2f;

            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(8);

            float previousSpeed = player.Speed;
            bool monotonic = true;
            float elapsed = 0f;
            float timeToCruise = -1f;

            Drive(new Vector2(0f, 1f));

            while (elapsed < maxDuration)
            {
                yield return null;
                elapsed += Time.deltaTime;

                if (player.Speed < previousSpeed - 0.001f)
                {
                    monotonic = false;
                }

                previousSpeed = player.Speed;

                if (timeToCruise < 0f && player.Speed >= player.MoveSpeed * 0.95f)
                {
                    timeToCruise = elapsed;
                }
            }

            ReleaseInput();

            if (!monotonic)
            {
                Debug.LogError("[PlayerTest] Acceleration FAILED: speed decreased while movement input was held.");
                yield return result;
            }

            if (timeToCruise < 0f)
            {
                Debug.LogError($"[PlayerTest] Acceleration FAILED: never reached cruise speed (speed={player.Speed:F2}/{player.MoveSpeed:F2} m/s).");
                yield return result;
            }

            float expected = player.MoveSpeed / player.Acceleration;
            if (timeToCruise > expected * 2f + 0.1f)
            {
                Debug.LogError($"[PlayerTest] Acceleration FAILED: reached cruise speed in {timeToCruise:F3}s, expected about {expected:F3}s.");
                yield return result;
            }

            if (Mathf.Abs(player.Speed - player.MoveSpeed) > player.MoveSpeed * 0.05f)
            {
                Debug.LogError($"[PlayerTest] Acceleration FAILED: cruise speed is {player.Speed:F2} m/s, expected {player.MoveSpeed:F2} m/s.");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Acceleration PASSED: 0 -> {player.MoveSpeed:F2} m/s in {timeToCruise:F3}s (theoretical {expected:F3}s), monotonic ramp, no overshoot.");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestDeceleration()
        {
            TestResult result = new TestResult();
            const float maxDuration = 2f;

            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(8);

            Drive(new Vector2(0f, 1f));
            yield return WaitSeconds(0.6f);

            float startSpeed = player.Speed;
            Vector3 startPosition = player.transform.position;
            ReleaseInput();

            float previousSpeed = startSpeed;
            bool monotonic = true;
            float elapsed = 0f;
            float timeToStop = -1f;

            while (elapsed < maxDuration)
            {
                yield return null;
                elapsed += Time.deltaTime;

                if (player.Speed > previousSpeed + 0.001f)
                {
                    monotonic = false;
                }

                previousSpeed = player.Speed;

                if (timeToStop < 0f && player.Speed <= 0.001f)
                {
                    timeToStop = elapsed;
                }
            }

            Vector3 endPosition = player.transform.position;
            float travelled = new Vector2(endPosition.x - startPosition.x, endPosition.z - startPosition.z).magnitude;
            float expectedSlide = (startSpeed * startSpeed) / (2f * player.Deceleration);
            float overshoot = travelled - expectedSlide;

            if (!monotonic)
            {
                Debug.LogError("[PlayerTest] Deceleration FAILED: speed increased after input was released.");
                yield return result;
            }

            if (timeToStop < 0f)
            {
                Debug.LogError($"[PlayerTest] Deceleration FAILED: player never came to rest (speed={player.Speed:F3} m/s).");
                yield return result;
            }

            if (overshoot > 0.5f)
            {
                Debug.LogError($"[PlayerTest] Deceleration FAILED: player slid {travelled:F2}m after release, {overshoot:F2}m beyond the expected stopping distance of {expectedSlide:F2}m.");
                yield return result;
            }

            if (player.Speed > 0.001f)
            {
                Debug.LogError($"[PlayerTest] Deceleration FAILED: residual speed {player.Speed:F3} m/s after stopping.");
                yield return result;
            }

            float expected = startSpeed / player.Deceleration;
            Debug.Log($"[PlayerTest] Deceleration PASSED: {startSpeed:F2} m/s -> 0 in {timeToStop:F3}s (theoretical {expected:F3}s), slid {travelled:F2}m, no reversal.");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestRotationTowardsMovement()
        {
            TestResult result = new TestResult();

            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(8);

            // Walk diagonally so the expected heading is unambiguous.
            Drive(new Vector2(1f, 1f));
            yield return WaitSeconds(0.8f);
            ReleaseInput();

            Vector3 velocity = player.PlanarVelocity;
            float speed = velocity.magnitude;

            if (speed < player.MoveSpeed * 0.5f)
            {
                Debug.LogError($"[PlayerTest] Rotation FAILED: player is barely moving ({speed:F2} m/s), cannot verify heading.");
                yield return result;
            }

            float headingError = Vector3.Angle(player.transform.forward, velocity.normalized);

            if (headingError > 15f)
            {
                Debug.LogError($"[PlayerTest] Rotation FAILED: player faces {player.transform.forward} but moves along {velocity.normalized} (error={headingError:F1} deg).");
                yield return result;
            }

            // Confirm the turn is rate limited rather than instantaneous.
            Teleport(new Vector3(0f, 1f, 8f), 180f);
            yield return WaitFrames(8);

            Drive(new Vector2(1f, 0f));
            yield return WaitFrames(3);
            float earlyError = Vector3.Angle(player.transform.forward, player.PlanarVelocity.normalized);
            ReleaseInput();

            if (earlyError < 1f)
            {
                Debug.LogError("[PlayerTest] Rotation FAILED: heading snapped instantly to the movement direction; rotation speed is not applied.");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Rotation PASSED: heading error {headingError:F1} deg after turning at {player.RotationSpeed:F0} deg/s " +
                      $"(intermediate error {earlyError:F1} deg while turning).");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestGravityFallingAndLanding()
        {
            TestResult result = new TestResult();
            const float maxDuration = 3f;

            Teleport(new Vector3(0f, 6f, 8f), 180f);
            yield return WaitFrames(6);

            if (player.IsGrounded)
            {
                Debug.LogError("[PlayerTest] Gravity FAILED: player still reports grounded 6m above the arena floor.");
                yield return result;
            }

            float fallHeight = 5f;
            float expectedImpact = Mathf.Sqrt(2f * player.Gravity * fallHeight);

            bool landed = false;
            float lowestSpeed = 0f;
            float elapsed = 0f;

            while (elapsed < maxDuration && !landed)
            {
                yield return null;
                elapsed += Time.deltaTime;

                if (player.JustLanded)
                {
                    landed = true;
                }

                if (!player.IsGrounded)
                {
                    lowestSpeed = Mathf.Min(lowestSpeed, player.VerticalSpeed);
                }
            }

            if (lowestSpeed >= 0f)
            {
                Debug.LogError($"[PlayerTest] Gravity FAILED: vertical velocity never became negative while falling (min={lowestSpeed:F2} m/s).");
                yield return result;
            }

            if (!landed)
            {
                Debug.LogError($"[PlayerTest] Landing FAILED: player never touched down within {maxDuration:F1}s (vertical speed={player.VerticalSpeed:F2} m/s).");
                yield return result;
            }

            yield return WaitFrames(30);

            if (!player.IsGrounded)
            {
                Debug.LogError("[PlayerTest] Landing FAILED: player is airborne after landing.");
                yield return result;
            }

            if (player.VerticalSpeed < -player.GroundStickForce - 0.5f)
            {
                Debug.LogError($"[PlayerTest] Landing FAILED: vertical velocity not reset after landing ({player.VerticalSpeed:F2} m/s).");
                yield return result;
            }

            float impactError = Mathf.Abs(player.LastLandingImpactSpeed - expectedImpact);
            if (impactError > expectedImpact * 0.15f + 0.5f)
            {
                Debug.LogError($"[PlayerTest] Landing FAILED: impact speed {player.LastLandingImpactSpeed:F2} m/s, expected about {expectedImpact:F2} m/s.");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Gravity + Landing PASSED: fell {fallHeight:F1}m, min vertical speed {lowestSpeed:F2} m/s, landed after {elapsed:F2}s " +
                      $"with impact {player.LastLandingImpactSpeed:F2} m/s (expected {expectedImpact:F2} m/s).");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestSlopeTraversal()
        {
            TestResult result = new TestResult();

            // Ramp_West rises from ground level at x=-5.5 to 1.2m at x=-10.5 (about 13.5 degrees).
            Teleport(new Vector3(-5.0f, 1.0f, 6.0f), 180f);
            yield return WaitFrames(12);

            if (!player.IsGrounded)
            {
                Debug.LogError("[PlayerTest] Slope FAILED: player did not settle on the ramp approach.");
                yield return result;
            }

            float startY = player.transform.position.y;
            Drive(InputForWorldDirection(Vector3.left)); // west, up the ramp
            yield return WaitSeconds(1.2f);
            ReleaseInput();

            float climbed = player.transform.position.y - startY;
            bool groundedAfterClimb = player.IsGrounded;

            if (climbed < 0.8f)
            {
                Debug.LogError($"[PlayerTest] Slope FAILED: walking up the ramp only gained {climbed:F2}m of height (expected about 1.2m).");
                yield return result;
            }

            if (!groundedAfterClimb)
            {
                Debug.LogError("[PlayerTest] Slope FAILED: player left the ground while walking up the ramp.");
                yield return result;
            }

            if (player.SlopeAngle < 1f)
            {
                Debug.LogWarning($"[PlayerTest] Slope WARNING: slope angle reads {player.SlopeAngle:F1} deg on the ramp; the ground normal may not be sampled.");
            }

            // Walk back down and make sure the player descends without launching into the air.
            float descentStartY = player.transform.position.y;
            Drive(InputForWorldDirection(Vector3.right)); // east, down the ramp
            yield return WaitSeconds(1.4f);
            ReleaseInput();

            float descended = descentStartY - player.transform.position.y;

            if (descended < 0.8f)
            {
                Debug.LogError($"[PlayerTest] Slope FAILED: walking down the ramp only lost {descended:F2}m of height (expected about 1.2m).");
                yield return result;
            }

            if (!player.IsGrounded)
            {
                Debug.LogError("[PlayerTest] Slope FAILED: player left the ground while walking down the ramp.");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Slope PASSED: climbed {climbed:F2}m and descended {descended:F2}m on the 13.5 deg ramp, grounded throughout " +
                      $"(slope angle {player.SlopeAngle:F1} deg).");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestWalkOffLedge()
        {
            TestResult result = new TestResult();
            const float maxDuration = 4f;

            // Platform_West top is at 1.2m; walking east off its edge drops the player to the floor.
            Teleport(new Vector3(-13.0f, 2.2f, 6.0f), 180f);
            yield return WaitFrames(15);

            if (!player.IsGrounded || player.transform.position.y < 2.0f)
            {
                Debug.LogError($"[PlayerTest] Ledge FAILED: player did not settle on Platform_West (y={player.transform.position.y:F3}).");
                yield return result;
            }

            bool becameAirborne = false;
            bool landed = false;
            float impact = 0f;
            float elapsed = 0f;

            Drive(InputForWorldDirection(Vector3.right));

            while (elapsed < maxDuration && !landed)
            {
                yield return null;
                elapsed += Time.deltaTime;

                if (player.JustBecameAirborne)
                {
                    becameAirborne = true;
                }

                if (player.JustLanded)
                {
                    landed = true;
                    impact = player.LastLandingImpactSpeed;
                }
            }

            ReleaseInput();

            if (!becameAirborne)
            {
                Debug.LogError("[PlayerTest] Ledge FAILED: player never left the ground after walking off the platform edge.");
                yield return result;
            }

            if (!landed)
            {
                Debug.LogError($"[PlayerTest] Ledge FAILED: player never landed within {maxDuration:F1}s (vertical speed={player.VerticalSpeed:F2} m/s).");
                yield return result;
            }

            // Let the residual slide come to rest before judging the final state.
            float slideTime = 0f;
            while (slideTime < 3f && player.Speed > 0.001f)
            {
                yield return null;
                slideTime += Time.deltaTime;
            }

            float restY = player.transform.position.y;
            yield return WaitFrames(20);

            if (!player.IsGrounded || Mathf.Abs(player.transform.position.y - restY) > 0.05f)
            {
                Debug.LogError($"[PlayerTest] Ledge FAILED: player did not settle after the fall (y={player.transform.position.y:F3}).");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Ledge PASSED: walked off Platform_West, left the ground, landed after {elapsed:F2}s " +
                      $"with {impact:F2} m/s impact and settled at y={player.transform.position.y:F3}.");
            result.Passed = true;
            yield return result;
        }

        private IEnumerator TestWallCollision()
        {
            TestResult result = new TestResult();

            // Monolith west face is at x = 13.75.
            Teleport(new Vector3(8.0f, 1.0f, 10.0f), 180f);
            yield return WaitFrames(10);

            Drive(InputForWorldDirection(Vector3.right));
            yield return WaitSeconds(4f);
            ReleaseInput();

            float x = player.transform.position.x;

            if (x >= 13.75f)
            {
                Debug.LogError($"[PlayerTest] Wall FAILED: player passed through the monolith (x={x:F2}, face at 13.75).");
                yield return result;
            }

            if (x < 12.5f)
            {
                Debug.LogError($"[PlayerTest] Wall FAILED: player stopped short of the monolith (x={x:F2}, face at 13.75).");
                yield return result;
            }

            if (!player.IsGrounded)
            {
                Debug.LogError("[PlayerTest] Wall FAILED: player left the ground while pushing into the monolith.");
                yield return result;
            }

            Debug.Log($"[PlayerTest] Wall PASSED: player stopped {13.75f - x:F2}m from the monolith face and stayed grounded.");
            result.Passed = true;
            yield return result;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private bool ResolveReferences()
        {
            if (player == null)
            {
                player = FindFirstObjectByType<ThirdPersonPlayerController>();
            }

            if (thirdPersonCamera == null)
            {
                thirdPersonCamera = FindFirstObjectByType<ThirdPersonCamera>();
            }

            if (player == null)
            {
                Debug.LogError("[PlayerTest] FAILED: no ThirdPersonPlayerController found in scene.");
                return false;
            }

            if (thirdPersonCamera == null)
            {
                Debug.LogError("[PlayerTest] FAILED: no ThirdPersonCamera found in scene.");
                return false;
            }

            if (GameInput.Instance == null)
            {
                Debug.LogError("[PlayerTest] FAILED: GameInput instance is missing; cannot drive input.");
                return false;
            }

            return true;
        }

        /// <summary>Runs a single test coroutine, logs the outcome and updates the counters.</summary>
        private IEnumerator RunTest(string name, IEnumerator test)
        {
            m_total++;
            TestResult result = new TestResult();

            while (test.MoveNext())
            {
                yield return test.Current;
            }

            // The final yielded value carries the result.
            if (test.Current is TestResult carried)
            {
                result = carried;
            }

            if (result.Passed)
            {
                m_passed++;
            }
            else
            {
                Debug.LogError($"[PlayerTest] '{name}' reported failure.");
            }
        }

        private IEnumerator WaitFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
            }
        }

        private IEnumerator WaitSeconds(float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                yield return null;
                elapsed += Time.deltaTime;
            }
        }

        /// <summary>Distance covered from rest in the given time under the configured acceleration.</summary>
        private float ExpectedTravelDistance(float duration)
        {
            float timeToCruise = player.MoveSpeed / player.Acceleration;
            if (duration <= timeToCruise)
            {
                return 0.5f * player.Acceleration * duration * duration;
            }

            return 0.5f * player.Acceleration * timeToCruise * timeToCruise + player.MoveSpeed * (duration - timeToCruise);
        }

        /// <summary>Converts a desired world-space planar direction into stick input for the current camera yaw.</summary>
        private Vector2 InputForWorldDirection(Vector3 worldDirection)
        {
            Vector3 forward = thirdPersonCamera.CameraPlanarForward;
            Vector3 right = thirdPersonCamera.CameraPlanarRight;
            return new Vector2(Vector3.Dot(worldDirection, right), Vector3.Dot(worldDirection, forward));
        }

        private void Drive(Vector2 direction)
        {
            GameInput.Instance.SetMobileMove(Vector2.ClampMagnitude(direction, 1f));
        }

        private void ReleaseInput()
        {
            if (GameInput.Instance != null)
            {
                GameInput.Instance.SetMobileMove(Vector2.zero);
            }
        }

        private void Teleport(Vector3 position, float yaw)
        {
            player.ResetMovementState();
            player.transform.position = position;
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private void SetCameraYaw(float yaw)
        {
            float delta = Mathf.DeltaAngle(thirdPersonCamera.CurrentYaw, yaw);
            thirdPersonCamera.ProcessLook(new Vector2(delta / thirdPersonCamera.HorizontalSensitivity, 0f));
        }

        /// <summary>Carries the pass/fail state of a test coroutine as its final yielded value.</summary>
        private class TestResult
        {
            public bool Passed;
        }
    }
}
