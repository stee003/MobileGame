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

Both scenes are registered and enabled in `ProjectSettings/EditorBuildSettings.asset`.
`Assets/Scenes` is the home for future gameplay scenes.

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
