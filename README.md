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
| `Assets/Scenes/TestScene.unity` | 1 | Empty |

Both scenes are registered and enabled in `ProjectSettings/EditorBuildSettings.asset`.
`Assets/Scenes` is the home for future gameplay scenes.

## Rendering (URP 17.0.4)

Mobile-oriented pipeline assets live in `Assets/Settings`:

* `Mobile_RPAsset.asset` — Render Scale **0.8**, MSAA **2×**, HDR 32-bit, depth/opaque texture off,
  main light shadows **1024 / 1 cascade / 50 m**, up to **4 additional lights without shadows**,
  soft shadows off, SRP Batcher on, dynamic batching off, GPU Resident Drawer off, Render Graph on.
* `Mobile_Renderer.asset` — Forward renderer, native render pass on, no renderer features.
* `UniversalRenderPipelineGlobalSettings.asset` + `DefaultVolumeProfile.asset` — registered in
  `GraphicsSettings` (`m_RenderPipelineGlobalSettingsMap`).

Project-wide rendering settings: **Linear** color space, lights use linear intensity and color
temperature, Android = **Vulkan (preferred) + GLES3 fallback**, iOS = **Metal**, graphics jobs off.

## Quality levels

`Low`, `Medium`, `High` (current level **Medium**). Per-level values scale blend weights, LOD bias,
anisotropic filtering, MSAA fallback, particle raycast budget and shadow distance; all three levels
reference the single `Mobile_RPAsset` (swap in extra URP assets later if you want per-tier pipelines).

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
legacy Input Manager both work). No `.inputactions` asset is shipped — input maps are gameplay work.

## Player settings

Product `MobileGame`, company `DefaultCompany`, version `0.1.0`, bundle id
`com.DefaultCompany.MobileGame` (Android + iOS), landscape auto-rotation, IL2CPP scripting backend
for Android/iOS, Android min API **24**, target API **35**, **ARM64** only.

## Verification checklist

1. Open the project with Unity 6 (6000.0.x) — the Console must be empty after the first import.
2. Open `Assets/Scenes/BootScene.unity` and press **Play** — skybox, camera and directional light
   are visible, no errors or warnings.
3. Open `Assets/Scenes/TestScene.unity` and press **Play** — empty scene loads without errors.
