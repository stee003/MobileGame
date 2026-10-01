# Hero rig verification tools

Headless tooling for the hero rig pass: `Assets/Scripts/Player/HeroRig.cs`, its Play Mode suite
`Assets/Scripts/Player/HeroRigTest.cs` and the Editor validator
`Assets/Scripts/Player/Editor/HeroRigValidator.cs`.

```
rig_model.py             parses the hero meshes and the C# bone/bind/fit tables, then does
                         forward kinematics plus exact box/box, capsule/capsule, capsule/box and
                         sphere separation math
simulate_hero_rig.py     drives that model the way HeroRigTest drives the real rig: 51 checks in
                         4 sections (rig data, T-pose and rest pose, simple rotations, stress)
verify_hero_rig.py       static verification of the files on disk: 66 checks in 6 sections
                         (components and GUIDs, skeleton contents, mesh binding, Idle/Walk Animator
                         setup, walk-only scope, scene wiring)
```

Both scripts are standard library only and CI friendly; `rig_model.py` needs nothing but the
standard library too.

```bash
python3 Tools/HeroRigVerification/verify_hero_rig.py     # static, ~0.1 s
python3 Tools/HeroRigVerification/simulate_hero_rig.py   # geometric, ~3 s
```

Exit code 0 means every check passed. `REPORT.md` records the measured results, the deformation
problems that were found and fixed, and the two seam openings that are accepted on purpose.

This folder is deliberately outside `Assets/`: Unity does not import it, and it is not part of any
build. The authoritative test for the project is the Play Mode suite `HeroRigTest`, which drives the
real `HeroRig` component, the real `Animator` and the real renderers in the Editor.

## What the simulator is, and what it is not

`rig_model.py` reads the same tables the runtime reads - `HeroCharacterVisual`'s 57 `Part(...)`
calls and `HeroRig`'s `JointTable`, `BindTable` and `FitTable` - and reproduces the transform
hierarchy, the quaternion math (Unity's ZXY Euler order, parent-first composition) and the rigid
binding exactly. It therefore answers the questions the rig actually depends on:

* is the skeleton complete, mirrored and correctly parented;
* does the T-pose really look like a humanoid bind pose;
* does the rest pose put both soles on the ground with nothing else below it;
* does rotating one bone carry its whole subtree, and only its subtree;
* do neighbouring meshes that touch at rest stay together when the joints bend;
* does anything end up below the floor.

What it cannot do is call Unity: it does not build an `Avatar`, so `isValid` / `isHuman` and the
`Humanoid` retargeting are checked statically (`verify_hero_rig.py`) and confirmed by
`HeroRigTest` in the Editor. Its geometry is exact for the primitives involved, but the Play Mode
suite measures axis-aligned renderer bounds instead - a coarser, deliberately independent metric.
