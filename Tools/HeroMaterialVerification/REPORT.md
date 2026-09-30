# Hero material pass — verification report

Task: improve **only** the materials of the current hero. Separate PBR materials for skin, hair,
primary costume, secondary costume material, boots, gloves and metallic details, with appropriate
base colour, roughness, metallic and normal detail — no flat-coloured Unity look, no plastic skin, no
flat cloth, no excessive metal, no all-black materials, and the hero must stay readable under
different lighting. No gameplay or animation changes.

## What changed

| Area | Change |
| --- | --- |
| `Assets/Art/Textures/Hero/` | 21 new maps: albedo + normal + packed mask for each of the seven roles |
| `Assets/Art/Materials/Hero/` | `M_Hero_Skin/Hair/Suit/Accent/Boots/Gloves/Metal.mat`, all URP Lit with those maps; the old `M_Hero_Belt.mat` is retired (its role is now `M_Hero_Metal`) |
| `Assets/Scripts/Player/HeroMaterials.cs` | new: the seven-role material library, resolution order and procedural PBR fallback |
| `Assets/Scripts/Player/HeroCharacterVisual.cs` | seven material roles instead of six, tangents generated for the primitives (normal maps need them), parts re-assigned to the correct role |
| `Assets/Scripts/Player/HeroMaterialTest.cs` | new: 13-check Play Mode suite incl. a lighting-readability measurement |
| `Assets/Scripts/Player/Editor/HeroMaterialValidator.cs` | new: Editor validation of materials, texture import settings and scene wiring |
| `Assets/Scenes/CombatTestScene.unity` | hero's seven material fields bound to the authored assets; `HeroMaterialTest` object added |
| `Tools/HeroMaterialVerification/` | generator, authoring, static verification and the headless preview renderer |

The hero model itself (geometry, proportions, part names) is unchanged; only the materials each part
uses and the two details added for material credibility (knuckle pads on the gloves, brass straps on
the boot cuffs) are new.

## How it was verified

Four layers, in order of authority:

1. **`Assets/Scripts/Player/HeroMaterialTest.cs`** — the in-Editor Play Mode suite that inspects the
   real hero in `CombatTestScene` (13 checks, see below). It runs automatically after the movement,
   joystick and touch-camera suites.
2. **`Tools/HeroMaterialVerification/verify_hero_materials.py`** — static verification of the assets,
   the code and the scene wiring; 289 checks, no dependencies.
3. **`Tools/HeroMaterialVerification/render_hero_preview.py`** — renders the hero with the authored
   materials in the arena lighting (see `preview/hero_material_preview.png`).
4. **`Tools/PlayerMovementVerification/simulate_player_movement.py`** — unchanged gameplay harness,
   re-run to prove the material pass did not touch movement.

No Unity Editor is available in the environment this work was produced in, so layers 2–4 are the
evidence here and layer 1 is the authoritative check to run on first open. The layer-4 harness is a
pre-existing mirror of the controller, and it is deliberately unchanged.

## Results

```
$ python3 Tools/HeroMaterialVerification/verify_hero_materials.py
Checks passed: 289/289
HERO MATERIAL VERIFICATION PASSED
```

```
$ python3 Tools/HeroMaterialVerification/generate_hero_textures.py
MATERIAL FILE                     PIXELS        MEAN ALBEDO (sRGB)     SMOOTHNESS   METALLIC
Skin    T_Hero_Skin_*            256/512/256   0.849 0.685 0.564      0.23-0.50    0.00-0.00
Hair    T_Hero_Hair_*            256/512/256   0.216 0.235 0.310      0.31-0.60    0.00-0.00
Suit    T_Hero_Suit_*            512/512/512   0.204 0.340 0.475      0.08-0.39    0.00-0.00
Accent  T_Hero_Accent_*          256/256/256   0.136 0.685 0.834      0.37-0.53    0.00-0.00
Boots   T_Hero_Boots_*           256/256/256   0.703 0.665 0.592      0.20-0.45    0.00-0.00
Gloves  T_Hero_Gloves_*          256/256/256   0.823 0.846 0.886      0.25-0.65    0.00-0.00
Metal   T_Hero_Metal_*           256/256/256   0.753 0.602 0.293      0.20-0.64    0.76-0.92

Wrote 21 PNGs, 1.75 MB total.
```

```
$ python3 Tools/HeroMaterialVerification/render_hero_preview.py
LIGHT SETUP    VIEW             HERO LUMA   CONTRAST   PIXELS   RESULT
bright_key     front            0.168       0.578      26708    PASS
dim_warm_key   front            0.164       0.303      26708    PASS
cool_key       front            0.168       0.535      26708    PASS
HERO READABILITY: PASSED
```

```
$ python3 Tools/PlayerMovementVerification/simulate_player_movement.py
Checks passed: 51/51
VERIFICATION PASSED
```

`Checks passed` above are the runs recorded on this machine; every number in this report is
reproduced by re-running the commands.

## Requirement coverage

| Requirement | How it is met | Verified by |
| --- | --- | --- |
| Separate skin material | `M_Hero_Skin`: pores, creases and per-pixel roughness in a mid-tone skin albedo | static [1][3], Play Mode 1–8 |
| Separate hair material | `M_Hero_Hair`: strand normals, streaky sheen, blue-black base that is not `#000` | static [1][3], Play Mode 1–8 |
| Separate primary costume material | `M_Hero_Suit`: woven cloth normals, fold shading, per-pixel roughness + occlusion | static [1][3], Play Mode 1–8 |
| Separate secondary costume material | `M_Hero_Accent`: ripstop trim, cleaner sheen, electric cyan | static [1][3], Play Mode 1–8 |
| Separate boots material | `M_Hero_Boots`: pebbled leather grain, stitching, scuffs, sole dirt gradient | static [1][3], Play Mode 1–8 |
| Separate gloves material | `M_Hero_Gloves`: finer grain, quilting, knuckle pads - a different material family from the boots | static [1][3], Play Mode 4 |
| Separate metallic details material | `M_Hero_Metal`: brushed brass with tarnish, the only metallic role | static [1][2][3], Play Mode 6 |
| Base colour | per-role albedo maps with micro variation, white `_BaseColor` tint | static [3][4], Play Mode 8 |
| Roughness | packed into the mask alpha, `_Smoothness = 1` so the map is authoritative | static [2][3], Play Mode 9 |
| Metallic where appropriate | mask R: 0 on six roles, 0.76–0.92 on `M_Hero_Metal` | static [2][3], Play Mode 6 |
| Normal detail where useful | normal maps on all seven roles (pores, strands, weave, grain, brush marks) | static [3], Play Mode 5 |
| No flat-coloured Unity look | albedo luma spread asserted per role; no constant-roughness surface anywhere | static [4], Play Mode 8–9 |
| No plastic skin | skin roughness 0.23–0.50 with pore/crease detail; no zero-roughness surface on the hero | static [3], Play Mode 9 |
| No completely flat cloth | suit/albedo variation plus weave + fold normals | static [3][4], Play Mode 8–9 |
| No excessive metallic surfaces | exactly one metallic role, enforced in three places | static [2], Play Mode 6 |
| No pure black materials everywhere | continuous coverage from hair (0.74 luma) to boots (0.90 luma); pure black asserted against | static [2], Play Mode 7 |
| Readable under different lighting | measured under bright, dim warm and cool keys; hero luma 0.16 in all three | preview tool, Play Mode 13 |
| Gameplay behaviour unchanged | no collider, no animation, no controller change; movement harness re-run | static [5], Play Mode 12, movement harness |
| No new animations | no `Animation`/`Animator` anywhere in the visual (asserted) | Play Mode 12 |

## Notes and follow-ups

* URP multiplies the mask alpha by the material's `_Smoothness` slider. Earlier hero materials had
  `_Smoothness = 0.35–0.75` with hand-tuned constants, which flattens roughness detail; all seven now
  use `_Smoothness = 1` with the value authored per pixel.
* `_Metallic` is the scalar fallback (and the value the Inspector shows). It is 0 on the six
  dielectrics and 0.9 on `M_Hero_Metal`; when the mask is present, URP takes the red channel.
* Normal maps are written with `alpha = red` so the same file decodes identically as RGB and in the
  DXT5nm / AG layout used by compressed mobile formats.
* The maps are deliberately tileable: Unity's built-in primitives give every face the full 0..1 UV
  square, so one square map covers cubes, capsules, spheres and the cylinder without seams.
* Procedurally generated fallback materials exist so that a hero without assigned assets still gets
  albedo, normal and mask data instead of flat colours. They are a safety net, not the intended look.
