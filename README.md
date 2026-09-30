# MobileGame

Foundation for an original **mobile 3D superhero action RPG** built with Unity.

This commit intentionally contains **no gameplay, no characters, no city and no combat** — it is
only the configured project skeleton: folder structure, scenes, render pipeline, quality levels,
physics/tag/layer setup and input architecture.

## Opening the project

* **Unity 6 LTS — 6000.0.40f1** (`ProjectSettings/ProjectVersion.txt`).
  Any newer `6000.0.x` patch works too. For public builds use **6000.0.58f2 or newer**, which
  contains the Unity Runtime security fix (CVE-2025-59489).
* No manual setup is required: package resolution and asset import happen automatically on first
  open, and the URP assets are already assigned in Graphics / Quality settings.

## Packages

| Package | Version | Reason |
| --- | --- | --- |
| `com.unity.render-pipelines.universal` | 17.0.4 | URP — the render pipeline for the project |
| `com.unity.inputsystem` | 1.11.1 | New Input System (Active Input Handling = *Both*) |
| `com.unity.ugui` | 2.0.0 | Built-in UI package (ships with every Unity 6 project) |
| `com.unity.ide.visualstudio` | 2.0.22 | IDE integration (default project tooling) |

Everything else in the manifest is a built-in engine module — nothing else was added.

## Folder structure

```
Assets/
  Art/
    Characters/    Environments/  Animations/
    Materials/     Textures/      VFX/
  Audio/  Data/  Prefabs/  Scenes/  Scripts/  UI/
  Settings/        URP pipeline assets (see below)
```

## Scenes

| Scene | Build index | Content |
| --- | --- | --- |
| `Assets/Scenes/BootScene.unity` | 0 (startup) | Main Camera (skybox clear, HDR, post-processing on) + Directional Light |
| `Assets/Scenes/TestScene.unity` | 1 | Input architecture diagnostics only (no player or gameplay) |
| `Assets/Scenes/CombatTestScene.unity` | 2 | Minimal outdoor greybox combat test arena (no gameplay yet) |

All scenes are registered and enabled in `ProjectSettings/EditorBuildSettings.asset`.
`Assets/Scenes` is the home for future gameplay scenes.

## Combat test arena (greybox) & third-person camera

`CombatTestScene` is a development-only outdoor arena used to develop and verify **movement,
camera, combat and animations** before any city content exists.

The scene features the reusable **third-person camera system** and a temporary capsule player:

* **Player** — a temporary capsule object (`Player`, tagged `Player`, on the `Player` layer) at
  (0, 1, 8) with a `CharacterController` (height 2 m, radius 0.5 m, skin width 0.08 m, slope limit
  45°, step offset 0.3 m) and `ThirdPersonPlayerController`. This is explicitly a temporary
  placeholder for verifying movement and camera behaviour, not the final player character — see
  *Player controller* below.
* **ThirdPersonCamera** (`Assets/Scripts/Camera/ThirdPersonCamera.cs`) — reusable, decoupled camera
  rig attached to `Main Camera`:
  * Third-person target follow with adjustable pivot height/offset (default 1.6 m).
  * 360° orbital horizontal rotation (yaw).
  * Vertical rotation (pitch) strictly clamped between configurable min (-35°) and max (+70°) angles.
  * Adjustable camera distance (1.5 m to 10 m, default 5 m) with interactive mouse scroll-wheel zoom.
  * Critically damped spring smoothing for follow movement (`positionSmoothTime = 0.08s`) and
    rotation (`rotationSmoothTime = 0.02s`).
  * SphereCast obstacle collision avoidance (`collisionRadius = 0.25m`, `collisionOffset = 0.15m`)
    against `Default`, `Environment`, and `Ground` layers with fast pull-in and smooth release.
  * Configurable sensitivity and optional pitch inversion.
  * Planar forward/right accessors for camera-relative character movement.
  * Automatic cursor lock in Play Mode with Escape toggle.
* **CameraSystemTest** (`Assets/Scripts/Camera/CameraSystemTest.cs`) — diagnostic suite verifying
  target tracking, horizontal rotation, looking up/down clamping, distance/height adjustments,
  and collision detection in Play Mode.
* **Arena geometry**:
  * **Ground** — 44 × 44 m flat slab (top at y = 0).
  * **Walls** — 4 m perimeter walls plus two interior half-walls for cover.
  * **Architecture** — four pillars, one tall monolith block, an archway and three low cover blocks.
  * **Height variation** — stepped platform (east), ramp-up platform (west), and three-tier ziggurat (north).
  * **PlayerSpawn** — empty marker tagged `SpawnPoint` at (0, 0, 8), facing the arena center.

## Player controller

`ThirdPersonPlayerController` (`Assets/Scripts/Player/ThirdPersonPlayerController.cs`) is the basic
third-person locomotion controller. Its scope is deliberately limited to movement: no combat,
attacks, abilities, dodge, stamina or health.

* **Camera-relative movement** — input is mapped through the planar forward/right vectors of
  `ThirdPersonCamera` (auto-found, or `Camera.main` / this transform as a fallback), so W/A/S/D and
  the arrow keys always move relative to where the camera looks. Diagonals are clamped to unit
  length so they are never faster than straight movement.
* **Acceleration and deceleration** — the planar velocity moves toward the target velocity at
  `acceleration` m/s² while input is held and toward zero at `deceleration` m/s² when it is
  released. `Vector3.MoveTowards` guarantees the speed never overshoots the target or reverses.
* **Rotation toward movement direction** — the capsule turns toward its current horizontal velocity
  at `rotationSpeed` degrees per second (rate limited, never snapping) once it moves faster than
  `minSpeedToRotate`.
* **Gravity** — downward acceleration with a `maxFallSpeed` terminal velocity, plus a small
  `groundStickForce` while grounded so the capsule follows slopes and stairs instead of bouncing.
* **Ground detection** — a sphere probe below the capsule (inset by the CharacterController's skin
  width so it never starts inside geometry) against configurable `groundLayers`, rejecting surfaces
  steeper than `maxSlopeAngle` and falling back to `CharacterController.isGrounded` when the probe
  cannot see the surface. Landing and leaving-the-ground are exposed as `JustLanded` /
  `JustBecameAirborne`, and `LastLandingImpactSpeed` reports the impact speed.
* **Inspector configuration** — movement speed, acceleration, deceleration, rotation speed,
  gravity, terminal fall speed, ground stick force, ground layers, ground check distance, maximum
  slope angle and the camera reference. Read-only state (`Speed`, `VerticalSpeed`, `IsGrounded`,
  `GroundNormal`, `SlopeAngle`, `GroundDistance`) is exposed as properties for animation, audio and
  diagnostics.

Input comes from the shared `MobileGame.Input.GameInput` facade, so the future mobile virtual stick
feeds the same controller through `SetMobileMove` without any change to this script.

`PlayerControllerTest` (`Assets/Scripts/Player/PlayerControllerTest.cs`) is the Play Mode
verification suite for the controller: it drives the player through the input facade and checks
wiring, grounded-at-spawn, forward/backward/left/right movement, camera-relative movement,
acceleration, deceleration, rotation toward movement, gravity/falling/landing and slope traversal.
It runs automatically on Start in Play Mode (or via its **Verify: Run Player Controller Test
Suite** context menu) and reports `VERIFICATION PASSED` for all 14 checks. The suite teleports the
capsule around the arena while it runs (about 20 s of game time) and returns it to the spawn point
at (0, 1, 8) when it finishes; set `Run On Start` to false on the component if you would rather
trigger it by hand.

A headless mirror of the same suite lives in
`Tools/PlayerMovementVerification/simulate_player_movement.py`; see
`Tools/PlayerMovementVerification/REPORT.md` for the recorded results.

## Rendering (URP 17.0.4)

Mobile-oriented pipeline assets live in `Assets/Settings` — one URP asset per quality tier, all
sharing a single forward renderer:

| Asset | Render scale | MSAA | Main-light shadows | Shadow distance | Soft shadows | Additional lights | Color grading |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Mobile_RPAsset_Low.asset` | 0.8 | Off | 1024 / 1 cascade | 30 m | Hard | 2/object, no shadows | LDR, LUT 16 |
| `Mobile_RPAsset_Medium.asset` | 0.9 | 2× | 2048 / 1 cascade | 50 m | Soft (medium) | 4/object, no shadows | LDR, LUT 32 |
| `Mobile_RPAsset_High.asset` | 1.0 | 4× | 2048 / 2 cascades | 70 m | Soft (high) | 4/object, no shadows | HDR, LUT 32 |

Common to all tiers: HDR 32-bit on, SRP Batcher on, dynamic batching off, depth/opaque texture off,
reflection-probe blending on (box projection off on Low only), LOD cross-fade on, mixed lighting on,
GPU Resident Drawer off.

* `Mobile_Renderer.asset` — Forward renderer, native render pass on, no renderer features.
* `UniversalRenderPipelineGlobalSettings.asset` + `DefaultVolumeProfile.asset` — registered in
  `GraphicsSettings` (`m_RenderPipelineGlobalSettingsMap`); the default pipeline is the Medium asset.
* `VolumeProfile_Low/Medium/High.asset` — per-tier post-processing bases (see Quality levels).

Project-wide rendering settings: **Linear** color space, lights use linear intensity and color
temperature, Android = **Vulkan (preferred) + GLES3 fallback**, iOS = **Metal**, graphics jobs off.

## Quality levels

`Low`, `Medium`, `High` (current level **Medium**), each bound to its matching URP asset. Tiers scale
— but never strip — the look:

| Setting | Low (weak devices) | Medium (mainstream, default) | High (flagship) |
| --- | --- | --- | --- |
| Shadow quality | 1024, 1 cascade, hard | 2048, 1 cascade, soft | 2048, 2 cascades, soft |
| Shadow distance | 30 m | 50 m | 70 m |
| Texture quality | Half res, per-texture aniso | Full res, per-texture aniso | Full res, forced aniso |
| Anti-aliasing | Off | 2× MSAA | 4× MSAA |
| Render scale | 0.8 | 0.9 | 1.0 |
| Particle quality | Raycast budget 128, 0.75× emission | Budget 256, 1.0× | Budget 512 + soft particles, 1.25× |
| Post-processing | On: tonemap + color + vignette | + gentle bloom | + richer bloom, HQ filter, subtle grain |
| LOD quality | Bias 0.8, cross-fade on | Bias 1.0, cross-fade on | Bias 1.5, cross-fade on |
| Reflection quality | Realtime on, 128, box-proj off | Realtime on, 256 | Realtime on, 512 |
| Pixel lights / skinning | 1 light, 4 bones | 2 lights, 4 bones | 4 lights, 4 bones |

Runtime control lives in `Assets/Scripts/Graphics/`:

* `GraphicsQualityManager` — self-bootstrapping singleton (`DontDestroyOnLoad`, no scene setup):
  `SetQuality(i)` / `SetQualityByName(name)` / `CycleQuality()`, persists to `PlayerPrefs`, swaps the
  global volume profile, reflection resolution, particle multiplier
  (`GraphicsQualityManager.ParticleBudgetMultiplier`, for future VFX) and target framerate
  (60 on all tiers).
* `GraphicsQualitySettings` — one ScriptableObject
  (`Assets/Settings/Resources/GraphicsQualitySettings.asset`, also the `Resources` fallback) holding
  the per-tier runtime values. Edit the URP assets for pipeline values, Quality Settings for engine
  values, this asset for everything else — each setting has exactly one home.

Per-platform default quality: **Android / iPhone → Medium**, Standalone → High, WebGL → Low.

## Layers and tags

Layers 8–17 reserved for gameplay code:
`Player`, `Enemy`, `NPC`, `Interactable`, `Projectile`, `Hitbox`, `Environment`, `VFX`,
`PostProcessing`, `Ground`.

Tags: `Enemy`, `NPC`, `Interactable`, `Pickup`, `Checkpoint`, `SpawnPoint` (plus Unity's built-ins).

## Physics

Unity 6 defaults with mobile-friendly settings: gravity `-9.81`, fixed timestep `0.02` (50 Hz),
solver iterations `6`, contact offset `0.01`, auto-sync transforms off, collision callbacks reused,
all 32 layers colliding (the collision matrix is intentionally untouched — gameplay decides it later).

## Input

`com.unity.inputsystem` is installed and **Active Input Handling = Both** (new Input System and the
legacy Input Manager both work). `Assets/Resources/Input/GameInputActions.inputactions` defines the
reusable `Gameplay` action map: `Move`, `Look`, `LightAttack`, `HeavyAttack`, `Dodge`, `Ability1`,
`Ability2`, `Ability3`, `Ultimate`, and `Interact`. Keyboard/mouse test bindings use WASD/arrows,
mouse delta, left/right mouse buttons, Space, 1/2/3, R, and E.

`MobileGame.Input.GameInput` self-bootstraps and exposes the shared move/look vectors, button state,
and performed/released events. Future mobile controls can provide virtual-stick values and button
edges through `SetMobileMove`, `SetMobileLook`, and `SetMobileButton`; no mobile UI or gameplay is
included. `TestScene` contains only `InputArchitectureTest`: enter Play Mode, run its
**Verify: Gameplay Input Actions** context menu, and use the mapped controls to see detected input
in Console. The monitor is diagnostic only; it does not move a character or implement combat.

## Player settings

Product `MobileGame`, company `DefaultCompany`, version `0.1.0`, bundle id
`com.DefaultCompany.MobileGame` (Android + iOS), landscape auto-rotation, IL2CPP scripting backend
for Android/iOS, Android min API **24**, target API **35**, **ARM64** only.

## Verification checklist

1. Open the project with Unity 6 (6000.0.x) — the Console must be empty after the first import.
2. Open `Assets/Scenes/BootScene.unity` and press **Play** — skybox, camera and directional light
   are visible, no errors or warnings.
3. Open `Assets/Scenes/TestScene.unity` and press **Play** — the input diagnostic reports that it is listening; run **Verify: Gameplay Input Actions** on `InputArchitectureTest`, then try every mapped keyboard/mouse control. No errors should appear.
4. With `BootScene` open, press **Play**, select the auto-created `GraphicsQualityManager` object
   and run its **Verify: Cycle Tiers + Reload Scenes** context menu — the Console must show the
   Low → Medium → High cycle plus both scene loads with no errors, ending in `VERIFICATION PASSED`.
5. Open `Assets/Scenes/CombatTestScene.unity` and press **Play**:
   * The third-person camera frames the temporary capsule player at the default distance (5 m) and height (1.6 m) with zero Console errors.
   * `CameraSystemTest` runs automatically on Start (or via its **Verify: Run Camera Test Suite** context menu), reporting `VERIFICATION PASSED`.
   * `PlayerControllerTest` runs automatically on Start (or via its **Verify: Run Player Controller Test Suite** context menu), reporting `VERIFICATION PASSED` for all 14 checks.
   * **Moving around the arena**: WASD / arrow keys move the capsule around the arena, up the ramp, and onto platforms; the camera follows smoothly without jitter.
   * **Acceleration and stopping**: hold a direction and the capsule ramps up to 6 m/s in about 0.2 s; release the key and it comes to rest in about 0.13 s without sliding past the intended stop point.
   * **Facing**: the capsule turns toward the direction it is walking in at 720 deg/s.
   * **Rotating horizontally**: move the mouse left/right; the camera orbits smoothly around the capsule.
   * **Looking up**: push mouse forward/up; view tilts up and clamps smoothly at -35°.
   * **Looking down**: pull mouse backward/down; view tilts down and clamps smoothly at +70°.
   * **Adjusting distance**: roll the mouse scroll wheel to zoom the camera smoothly between 1.5 m and 10 m.
   * **Camera collision**: walk behind the perimeter walls, monolith, pillars, or archway; the camera pushes forward smoothly to prevent clipping into geometry, and eases back out when clear.
   * **Slopes**: walk up `Ramp_West` (13.5°) and onto `Platform_West`, then back down; the capsule stays glued to the ramp the whole way.
   * **Falling and landing**: walk off `Platform_West` (1.2 m) or the east platform (1.6 m); the capsule falls, reports `JustLanded`, and settles on the floor.
   * **Collisions**: walk into the monolith, pillars or perimeter walls; the capsule stops at the surface instead of passing through.
   * **Cursor lock**: click into the game view to lock the mouse; press Escape to unlock.

Note: the ziggurat's 0.6 m tier steps are taller than the `CharacterController`'s 0.3 m step
offset, so the capsule cannot climb them; the stepped platform on the east side (0.4 m steps) has
the same limitation. Both are properties of the temporary `CharacterController` on the placeholder
capsule, not of the player controller script.
