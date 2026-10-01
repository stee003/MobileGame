# Hero rig pass — verification report

Task: build and verify the hero's humanoid rig, then add only its original looping idle animation.
The Animator has an Idle state and a neutral Moving state; no walking or combat clips are authored.
The player controller only supplies grounded speed state to drive those Animator transitions.

## What changed

| Area | Change |
| --- | --- |
| `Assets/Scripts/Player/HeroRig.cs` | new: the 52-bone humanoid skeleton (51 bones mapped into a `HumanDescription`, `HeadTop_End` a helper that gives the head bone an axis), the joint table with per-joint limits, the bind table for all 57 hero meshes, the fit table that extends two meshes into their neighbours, humanoid avatar construction with a generic fallback, and the `Animator` on the skeleton root |
| `Assets/Scripts/Player/HeroRigTest.cs` | Play Mode suite, now 42 checks in 7 sections, driving the real component |
| `Assets/Scripts/Player/Editor/HeroIdleAnimationAssetBuilder.cs` | authors a seamless 4.2-second breathing/weight-shift idle clip and the Idle/Moving Animator Controller through Unity APIs |
| `Assets/Scripts/Player/Editor/HeroRigValidator.cs` | **Tools/MobileGame/Hero Rig/** — validate the rig setup, rebuild it in open scenes, re-apply the rest pose |
| `Assets/Scripts/Player/ThirdPersonPlayerController.cs` | passes grounded speed to the Animator's `IsMoving` bool; no locomotion or combat animation logic |
| `Assets/Scripts/Player/HeroMaterialTest.cs` | check `[12/13]` now accepts the one `Animator` on the rig root (root motion off) instead of requiring the visual root to carry no components at all |
| `Assets/Scenes/CombatTestScene.unity` | `HeroRig` added to the `Player` object, plus a `HeroRigTest` root object wired to it |
| `Tools/HeroRigVerification/` | `rig_model.py` (C# table parser + FK + exact separation math), `simulate_hero_rig.py`, `verify_hero_rig.py` |

`HeroCharacterVisual.cs` remains unchanged. The idle clip and controller are generated into
`Assets/Resources/Animations/HeroIdle/` by the Editor asset builder on first load (or from its Tools
menu). `HeroRig` loads that controller from Resources unless an explicit override is assigned. The
controller contains one animation clip (`Hero_Idle`) and one clipless neutral movement state.

The hero is built from Unity primitives, which are shared meshes without bone weights, so the
binding is **rigid** — each of the 57 meshes follows exactly one bone. Skinning is impossible here
without replacing the meshes; the deformation work is therefore joint placement, axes, pivots,
limits, and volume overlap.

## How it was verified

Three layers, in order of authority:

1. **`HeroRigTest`** (in the project) — Play Mode suite that builds the real rig, then checks the
   Animator setup, the skeleton and `Animator.GetBoneTransform` mapping for all 51 mapped bones, the
   binding of all 57 meshes, the T-pose, 30 simple rotations (hips, spine, chest, neck, head, both
   shoulders, upper arms, elbows, wrists, fingers, hips, knees, ankles, toes) with rigid-travel,
   seam and ground checks, and 150 random multi-joint stress poses inside the joint limits. It runs
   automatically on Start in `CombatTestScene`, or from the Inspector context menu
   (**Verify: Run Hero Rig Test Suite**).
2. **`verify_hero_rig.py`** (this folder) — static verification of the files on disk: 61 checks in 6
   sections. It checks the idle clip authoring, both Animator transitions, the player movement
   signal, and the Play Mode test coverage, as well as GUIDs, bindings, root motion and scope.
3. **`simulate_hero_rig.py`** (this folder) — geometric verification: 51 checks in 4 sections, on a
   model that parses the same C# tables and reproduces the transform hierarchy, Unity's quaternion
   math and the rigid binding, with exact separation math for box/box (SAT), capsule/capsule,
   capsule/box and sphere pairs. It runs the same 30 rotations as the Play Mode suite (the two
   tables are byte-identical, checked) plus 400 random stress poses.

**What was not run here:** there is no Unity Editor and no .NET toolchain in this environment, so
`HeroRigTest` has not been executed and `HeroRig.cs` has not been compiled by Unity. The C# was
checked two ways instead: every file touched by this pass parses cleanly as C# (tree-sitter grammar,
no ERROR/MISSING nodes), and every `HeroRig` / `HeroJoint` / `HeroRigJoint` / `HeroRigPart` member
referenced by the test suite, the validator and the material suite resolves to a declaration in
`HeroRig.cs` (67 declared members; a deliberately typo'd copy reports 16 undeclared references, so
the check is not vacuous). Sign-off on the Avatar itself must come from opening the project.

## Results

```
$ python3 Tools/HeroRigVerification/verify_hero_rig.py
...
Checks passed: 61/61
HERO RIG STATIC VERIFICATION PASSED

$ python3 Tools/HeroRigVerification/simulate_hero_rig.py
...
Checks passed: 51/51
RIG VERIFICATION PASSED
```

| Section | Checks | Result |
| --- | --- | --- |
| Rig data | 19 | PASS — 52 bones, 51 mapped, unique names, parents declared before children, all 21 mandatory humanoid bones, all 30 finger bones, exact left/right mirrors, all 57 meshes bound exactly once |
| T-pose and rest pose | 16 | PASS — arms horizontal at shoulder height, legs straight down, both soles exactly at y = 0, nothing but the boots below 0.160 |
| Simple rotations | 9 | PASS — 30 rotations, every joint holds what it was given, subtrees move rigidly, nothing outside moves, returning to rest is exact, no seam tears |
| Stress | 7 | PASS — 400 random poses: no NaN, no torn seams, nothing above the boots reaches the floor |
| Static: components | 12 | PASS — scripts, unique GUIDs, namespace, execution order, menus |
| Static: skeleton | 16 | PASS — every bone the task asks for is declared |
| Static: binding | 5 | PASS — 57 meshes, no duplicates, no unknown parts |
| Static: Unity animation setup | 11 | PASS — humanoid avatar with validation and generic fallback, root motion off, `AlwaysAnimate`, idle controller resource fallback |
| Static: scope | 10 | PASS — grounded movement parameter wiring, idle-only clip/state, transition and Play Mode checks, no colliders or combat clips |
| Static: scene wiring | 7 | PASS — `CombatTestScene` has the rig and test object, humanoid kind and Resources controller fallback |

### Measured skeleton

| Measurement | Value |
| --- | --- |
| Bones | 52 (51 mapped to `HumanBodyBones`, `HeadTop_End` the only helper) |
| Bone lengths | clavicle 221 mm, upper arm 265, forearm 300, thigh 360, shin 390, neck 80 |
| Shortest bone | `LeftLittleDistal`, 20.9 mm |
| T-pose hands | y = 1.440, arms pointing to (∓1, 0, 0) |
| T-pose feet | pointing forward, (0, −0.523, 0.853) |
| Rest soles | y = 0.000000 both sides; lowest other mesh `Shin_L` at 0.160 |
| Rigid bind fidelity | worst mesh offset 0.00e+00 m, worst rotation deviation 1.71e−06° |
| Knife-edge contacts (rest) | 6 before the fixes → 2 after |

### Simple rotations

30 rotations, one joint at a time. Largest travel of a bound mesh: 841.5 mm (`left hip forward`,
`LeftUpperLeg(−60, 0, 0)`), 682.5 mm (knee flex at ±90°), 640.0 mm (upper arm abducted 60°),
530.3 mm (elbow flex at −90°), 201.3 mm (spine pitch). Travel is checked against the rigid-body
prediction `2·d·sin(θ/2)` for the perpendicular distance `d` to the rotation axis, so a mesh that
merely sits in the wrong subtree fails.

Three seams open, all of them documented and measured:

| Seam | Worst opening | Why it is accepted |
| --- | --- | --- |
| `Chest ↔ UpperArm_L` / `_R` | 13.3 mm at 60° abduction | the armpit; nothing covers it, and the opening grows with abduction |
| `HipAccent_L ↔ Abdomen` | 9.2 mm at 12° spine lateral | the hip trim slides on the pelvis; the gap stays inside the `Hips` box (x ±0.21, y 0.92–1.02, z ±0.12), so it is never visible |

Deepest ground contact over all 30 rotations: 20.0 mm into the floor (`Foot_L`, at `hips roll`) —
inside the 45 mm allowance that was measured for the rigid box soles.

### Stress (400 random multi-joint poses)

No NaN, no torn seam outside the six documented exception pairs, and no mesh above the boots reaches
the floor. Worst opening per seam:

```
Chest <-> Shoulder_L        27.8 mm        HipAccent_L <-> Abdomen    16.3 mm
Chest <-> UpperArm_R        18.7 mm        Chest <-> Shoulder_R       15.8 mm
Chest <-> UpperArm_L        17.9 mm        HipAccent_R <-> Abdomen    14.4 mm
```

The other 18 watched seams stay closed in all 400 poses. The interior extensions cut the worst hip
trim gap from 49.7 mm to 16.3 mm.

## Deformation problems found and fixed

| # | Problem | Fix |
| --- | --- | --- |
| F1 | meshes bound to no bone would silently stay behind when the rig moves | the rig logs an error listing every unbound mesh; the bind table now claims all 57 |
| F2 | the cape spans the chest and the hips, so bending the spine tore it | measured: split in two it tears up to **75.9 mm**; shipped as one sheet on `Chest` it opens **0.0 mm** |
| F3 | `Abdomen` (1.02–1.24) sat in a knife-edge gap between the pelvis and the chest | extended to **0.95–1.30**, inside both neighbours — `F("Abdomen", 0, −0.005, 0, 0, 0.130, 0)` |
| F4 | `Neck` (1.50–1.56) was a 6 cm stub that separated from the chest when the neck pitched | extended to **1.44–1.56**, the extra length hidden inside the chest box — `F("Neck", 0, −0.030, 0, 0, 0.030, 0)` |
| F5 | the ankle pivot sat at the sole, so any foot rotation dragged the boot through the floor | ankle pivot at y = 0.15 |
| F6 | `GloveCuff` on the forearm left the wrist open when the hand flexed | bound to `LeftHand` / `RightHand` |
| F7 | `BootCuff` / `BootStrap` on the shin opened the ankle when the foot pitched | bound to `LeftFoot` / `RightFoot` |
| F8 | the shoulder pads were on the clavicle, which carries no geometry, so they detached when the arm moved | `Shoulder_*` and `ShoulderAccent_*` bound to `LeftUpperArm` / `RightUpperArm` |

Both fit adjustments add volume **only** where a neighbour already covers it, so the silhouette does
not change; the simulator checks that every added point is contained by an existing mesh, using exact
per-primitive tests (box corners, sphere poles, cylinder rim samples, capsule cap poles) rather than
bounding boxes, which reported the neck's top rim as new volume before.

## Known limitations, deliberately out of scope

* **Rigid binding.** Unity primitives cannot be skinned, so shoulders, elbows, knees and hips keep
  their shape instead of deforming. Real deformation needs skinned meshes, which is an art pass.
* **Sole penetration in random poses.** A random pose can put a sole 61.1 mm below the floor while
  the ankle is still 0.147 m up — that is foot planting, not a rig fault. The check therefore asserts
  a rigid-body bound instead: every boot corner must stay inside its measured ankle radius
  (`Foot` 252 mm, `BootCuff` 168, `BootStrap` 177). The fix is humanoid foot IK in the animation
  pass.
* **Only the idle pass exists.** The controller has no walking, attack, dodge or combat clips. Foot
  planting during large poses still needs a future IK/locomotion pass.
