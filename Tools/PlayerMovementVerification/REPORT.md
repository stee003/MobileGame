# Player controller verification report

Task: create the basic third-person player controller only (camera-relative movement,
acceleration, deceleration, rotation toward movement, gravity, ground detection) on a temporary
capsule, and verify walking in all directions, rotating, stopping, slopes, falling and landing.

## How it was verified

Two layers of verification exist:

1. **`Assets/Scripts/Player/PlayerControllerTest.cs`** — the in-Editor Play Mode suite that ships
   with the project. It drives the real `ThirdPersonPlayerController` through the shared
   `GameInput` facade (the same seam the future mobile virtual stick uses) and checks the same
   scenarios, including idle motion integrity (no zero-length motion ever reaches
   `CharacterController.Move`). It runs automatically on Start in Play Mode, or on demand from the
   Inspector context menu (**Verify: Run Player Controller Test Suite**).
2. **`simulate_player_movement.py`** (this folder) — a headless mirror of the controller's
   `Update()` loop plus a model of the `CharacterController` semantics the controller relies on
   (skin-width collision volume, slide-along-surface, depenetration, ground snapping, step offset).
   The arena geometry is read straight out of `Assets/Scenes/CombatTestScene.unity`, so the slopes,
   platforms and walls under test are the real ones. It exists because no Unity Editor is available
   in the environment this work was produced in.

The harness is a faithful mirror, not a physics engine: it reproduces the controller's math
statement by statement and the CharacterController behaviour the controller depends on, but final
sign-off must come from running `PlayerControllerTest` in the Unity Editor.

## Results

```
$ python3 Tools/PlayerMovementVerification/simulate_player_movement.py
Loaded 23 colliders from Assets/Scenes/CombatTestScene.unity
...
Checks passed: 51/51
VERIFICATION PASSED
```

| Requirement | Checks | Result |
| --- | --- | --- |
| Inspector configuration | 2 | PASS — speed 6 m/s, acceleration 30 m/s², deceleration 45 m/s², rotation 720 deg/s, gravity 20 m/s² |
| Ground detection at spawn | 2 | PASS — grounded and stable while idle (no vertical drift) |
| Idle motion integrity | 3 | PASS — 60 idle frames after a movement reset never handed `CharacterController.Move` a zero-length vector; the 60 degenerate motions are detected and skipped instead (0 engine calls) |
| Forward / backward / left / right | 12 | PASS — 3.05 m in 0.60 s per direction (expected ≈ 3.00 m), alignment 1.000, grounded throughout |
| Camera-relative movement | 4 | PASS — after a 90° camera yaw change, "forward" input follows the new camera forward (alignment 1.000) and no longer follows the old heading (alignment 0.000) |
| Acceleration | 4 | PASS — 0 → 6.00 m/s in 0.200 s (theoretical 0.200 s), monotonic, no overshoot |
| Deceleration | 4 | PASS — 6.00 → 0 m/s in 0.133 s (theoretical 0.133 s), slid 0.35 m against a 0.40 m stopping distance, no reversal, no residual speed |
| Rotation toward movement | 2 | PASS — heading error 0.0° at cruise speed, turn is rate limited (54° remaining 3 frames in) |
| Gravity / falling / landing | 6 | PASS — airborne 5 m up, −14.00 m/s terminal fall for that height, touchdown after 0.62 s, impact 14.00 m/s vs 14.14 m/s theoretical, vertical velocity reset to the −2 m/s ground stick |
| Slopes (`Ramp_West`, 13.5°) | 4 | PASS — climbed 1.20 m and descended 1.28 m, grounded throughout, ground normal sampled at 13.5° |
| Walking off a ledge | 4 | PASS — leaves the ground at the platform edge, lands after 0.75 s with a 3.00 m/s impact, settles grounded |
| Wall collision | 2 | PASS — stops 0.42 m (the capsule radius) from the monolith face and stays grounded |

## Issues found and fixed

1. **Zero-length motion reached `CharacterController.Move` (controller fix).** Every teleport in the
   Play Mode suite calls `ResetMovementState()`, which leaves the planar *and* vertical velocity at
   exactly zero. Unity's `CharacterController.Move` normalizes its motion internally, and on a
   zero-length motion that normalization produces a zero direction, so the engine reported
   `Assertion failed on expression: 'IsNormalized(dir, 0.001f)'` on **every frame** the player idled
   in that state (the assertion is stripped from release builds, but it marks a degenerate vector
   being handed to the engine, and it spammed the Editor console). The controller now checks the
   motion with `MobileGame.Core.PhysicsQueryGuard` before the call and skips the frame when it is
   zero-length, non-finite or shorter than `minMoveDistance` (which the controller ignores anyway);
   `SkippedMotionFrames` exposes the counter. The same guard is applied to the camera's collision
   `Physics.SphereCast` (positive radius/distance and a unit-length direction only), and
   `ProcessLook` now ignores non-finite look deltas so a NaN can never reach the orbit angles.
   `PlayerControllerTest` gained the *Idle motion integrity* check, which fails if a zero-length
   motion is ever handed to the engine or if the console logs the `IsNormalized` assertion.
2. **Ground probe started inside the floor (controller fix).** `ProbeGround` placed its sphere at
   the *visual* capsule bottom, which sits one skin width (0.08 m) below the CharacterController's
   collision volume. Resting on the ground therefore meant the sphere began already overlapping
   the floor, and `Physics.SphereCast` from an overlapping position is unreliable — the player
   could report "airborne" while standing still, which fights the ground stick force and produces
   a visible bob. The probe now starts at the skin-inset collision bottom and lifts the sphere by
   `skinWidth + 0.01` so it always begins clear of geometry; `GroundDistance` now reports the true
   clearance.
3. **Capsule-vs-slope geometry (controller behaviour confirmed).** Walking into a slope requires
   the capsule to ride *above* the surface height sampled under its centre, because the capsule's
   side surfaces are what touch a tilted plane. Verified with the harness: the capsule rests
   tangent to the ramp plane (a 13.5° ramp lifts the capsule by an extra ~1 cm versus flat ground)
   and climbs at exactly the slope's gradient. The controller relies on `CharacterController`'s own
   collision resolution for this, which is why the ground stick force is applied while grounded.
4. **Harness modelling bugs (fixed, not controller bugs).** Successive harness revisions modelled the
   ground probe as a centre ray, treated the capsule as a radius-inflated box, froze the capsule on
   contact instead of sliding, and — most subtly — treated the scene's empty grouping transforms
   (`Walls`, `Architecture`, `HeightVariation`) as 1×1×1 colliders at the origin, which made the
   player "stick" to an invisible box in the middle of the arena. Each of those produced false
   failures (the player sinking through the floor, being unable to move, never landing, freezing
   mid-arena). They were corrected to mirror the CharacterController semantics listed above and to
   only import objects that actually own a `BoxCollider`; the controller math itself needed no
   change beyond issues 1 and 2.

## Not verified here

* Console cleanliness and rendering in the Unity Editor — requires opening the project. The
  *Idle motion integrity* check in `PlayerControllerTest` watches `Application.logMessageReceived`
  for the `IsNormalized` assertion, so running the suite in the Editor confirms the fix there.
* Camera framing, smoothing and obstacle push-in while the player moves (covered by
  `CameraSystemTest` and by eye in Play Mode).
* Animation, audio and VFX hooks — the controller only exposes the state properties
  (`Speed`, `VerticalSpeed`, `IsGrounded`, `JustLanded`, `SlopeAngle`, …) those systems need.

## Known limitation (not a controller bug)

The ziggurat's 0.6 m tier steps and the east stepped platform's 0.4 m steps are taller than the
temporary capsule's `CharacterController` step offset (0.3 m), so the capsule cannot climb them.
That is a property of the placeholder's `CharacterController`, not of
`ThirdPersonPlayerController`; raising `Step Offset` on that component (to ~0.7 m) makes them
walkable without touching the script.
