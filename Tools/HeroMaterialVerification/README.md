# Hero material verification tools

Headless tooling for the hero material pass: `Assets/Scripts/Player/HeroMaterials.cs`,
`Assets/Scripts/Player/HeroCharacterVisual.cs`, `Assets/Art/Materials/Hero/M_Hero_*.mat` and
`Assets/Art/Textures/Hero/*.png`.

```
generate_hero_textures.py     writes the 21 PBR maps (albedo / normal / packed mask x 7 roles)
author_hero_materials.py      writes the seven URP Lit materials and the texture import settings
verify_hero_materials.py      static verification of materials, textures, code and scene wiring
render_hero_preview.py        renders the hero wearing those materials, in the test arena lighting
preview/                      the rendered result (see preview/hero_material_preview.png)
```

Requirements: `python3 -m pip install --user -r requirements.txt` (numpy + Pillow).
`verify_hero_materials.py` needs nothing but the standard library.

## The seven materials

| Role | Material | Body parts | Base colour | Metallic | Roughness (1 - smoothness) |
| --- | --- | --- | --- | --- | --- |
| Skin | `M_Hero_Skin` | head, neck, jaw, ears | warm mid-tone skin | 0 | 0.50 – 0.77 |
| Hair | `M_Hero_Hair` | hair, brows, eye slits | blue-black, streaky | 0 | 0.40 – 0.70 |
| Primary costume | `M_Hero_Suit` | bodysuit, leggings, sleeves, shoulder armour, cape | deep teal-blue | 0 | 0.61 – 0.92 |
| Secondary costume | `M_Hero_Accent` | visor, trims, hip accents, belt gem, emblem inlay | electric cyan | 0 | 0.38 – 0.63 |
| Boots | `M_Hero_Boots` | boots and cuffs | warm bone leather | 0 | 0.55 – 0.80 |
| Gloves | `M_Hero_Gloves` | gloves, cuffs, knuckle pads | cool light grip leather | 0 | 0.30 – 0.75 |
| Metallic details | `M_Hero_Metal` | belt, buckle, emblem ring, visor bolts, boot straps | brushed brass | 0.76 – 0.92 | 0.34 – 0.80 |

Every material is URP Lit with the same three maps and the same keyword set
(`_NORMALMAP`, `_METALLICGLOSSMAP`, `_OCCLUSIONMAP`), so the hero stays in a single SRP Batcher
variant. `_Smoothness = 1` on all seven because URP multiplies that slider into the metallic-gloss
alpha - the authored roughness lives in the mask's alpha channel, which is what keeps the surfaces
from reading as one uniform plastic.

The maps themselves are packed the way URP expects:

| Map | Colour space | Channels |
| --- | --- | --- |
| `T_Hero_<Role>_BaseColor.png` | sRGB | colour + baked micro variation (never a flat fill) |
| `T_Hero_<Role>_Normal.png` | linear | tangent-space normal, alpha mirrors red (DXT5nm-safe) |
| `T_Hero_<Role>_Mask.png` | linear | R metallic, G occlusion, A smoothness, B unused |

## How the hero gets its materials at runtime

`HeroCharacterVisual` resolves its seven roles in `HeroMaterials.Resolve()`:

1. materials assigned on the component (this is what `CombatTestScene` uses),
2. else a complete `Resources/HeroMaterials/M_Hero_*` set (for prefabs and builds),
3. else procedurally generated URP Lit materials with tileable albedo / normal / packed-mask
   textures, so a missing asset never degrades the hero to flat colours.

## Running the tools

```bash
python3 Tools/HeroMaterialVerification/generate_hero_textures.py   # regenerate the maps
python3 Tools/HeroMaterialVerification/author_hero_materials.py    # (re)write materials + .meta
python3 Tools/HeroMaterialVerification/author_hero_materials.py --check   # verify they are current
python3 Tools/HeroMaterialVerification/verify_hero_materials.py    # static verification, no deps
python3 Tools/HeroMaterialVerification/render_hero_preview.py      # render the previews
```

`verify_hero_materials.py` is the headless mirror of the in-Editor checks; it exits non-zero on any
failure, so it can be wired into CI. See `REPORT.md` for the recorded results of every tool.

## In-Editor verification

* **`Assets/Scripts/Player/HeroMaterialTest.cs`** - Play Mode suite (13 checks) that inspects the
  hero standing in `CombatTestScene`: role coverage, per-role maps, metallic policy, albedo and
  roughness variation, shadow flags, gameplay untouched, and a readability measurement that renders
  the hero off-screen under a bright, a dim warm and a cool key light. It waits for the movement,
  joystick and touch camera suites, then runs automatically on Start (or from its context menu
  **Verify: Run Hero Material Test Suite**).
* **`Assets/Scripts/Player/Editor/HeroMaterialValidator.cs`** - Editor checks for the assets
  themselves (material properties, texture import settings, hero instances in the open scenes):
  **Tools/MobileGame/Hero Materials/Validate PBR Setup**, plus
  **Assign Authored Materials To Open Scenes** if a scene's hero fields ever come up empty.

## Renderer caveats

`render_hero_preview.py` is deliberately faithful but is not Unity: it implements a Cook-Torrance
shading path and the same map/channel semantics, and it reads the model, the materials and the arena
lighting out of the repository, but it cannot reproduce URP's exact BRDF terms, its cubemap
reflection probe contents or its post-processing. The printed readability numbers are therefore an
indication, not a substitute for running `HeroMaterialTest` in the Editor.
