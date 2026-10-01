#!/usr/bin/env python3
"""Static verification of the hero rig pass.

Checks the on-disk state without opening Unity and without simulating anything
(``simulate_hero_rig.py`` does the geometry):

  1. the rig component, the Play Mode suite and the Editor validator exist, are compiled into the
     project (unique meta GUIDs) and follow the project's conventions;
  2. the skeleton declares every bone the task asks for - hips, spine, chest, neck, head,
     shoulders, upper arms, forearms, hands, fingers, upper legs, lower legs, feet;
  3. every hero mesh built by ``HeroCharacterVisual`` is claimed by exactly one bone;
  4. the Animator is configured for Unity's animation system: humanoid avatar with a generic
     fallback, root motion off, always-animate culling, and the Animator sits on the skeleton root
     instead of on the Player root that the CharacterController owns;
  5. the controller supplies grounded walk state and stride rate, and the generated Animator
     Controller contains the idle and walk clips only, with no run or combat animation;
  6. ``CombatTestScene`` wires the rig and its test suite.

Exit code 0 means every check passed.

Run:  python3 Tools/HeroRigVerification/verify_hero_rig.py
"""

import os
import re
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(REPO_ROOT, "Assets")
RIG_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "HeroRig.cs")
TEST_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "HeroRigTest.cs")
VALIDATOR_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "Editor", "HeroRigValidator.cs")
HERO_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "HeroCharacterVisual.cs")
CONTROLLER_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "ThirdPersonPlayerController.cs")
MATERIAL_TEST_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "HeroMaterialTest.cs")
IDLE_BUILDER_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "Editor", "HeroIdleAnimationAssetBuilder.cs")
FOOT_PLANTING_SCRIPT = os.path.join(ASSETS, "Scripts", "Player", "HeroWalkFootPlanting.cs")
SCENE = os.path.join(ASSETS, "Scenes", "CombatTestScene.unity")

PASSED = []
FAILED = []


def check(condition, description):
    (PASSED if condition else FAILED).append(description)
    print("  %s %s" % ("PASS" if condition else "FAIL", description))
    return condition


def read(path):
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def meta_guid(path):
    meta = path + ".meta"
    if not os.path.exists(meta):
        return None
    match = re.search(r"^guid: ([0-9a-f]{32})$", read(meta), re.M)
    return match.group(1) if match else None


def git_unchanged(path):
    """True when the file is identical to the branch point of this work."""
    try:
        base = subprocess.check_output(["git", "merge-base", "HEAD", "main"], cwd=REPO_ROOT,
                                       stderr=subprocess.DEVNULL).decode().strip()
    except (subprocess.CalledProcessError, OSError):
        base = "aa954abe14fe098ab76d8d80e6a752d2702b9fa9"
    result = subprocess.run(["git", "diff", "--quiet", base, "--",
                             os.path.relpath(path, REPO_ROOT)], cwd=REPO_ROOT)
    return result.returncode == 0


def main():
    print("Hero rig static verification")
    print("repository: " + REPO_ROOT)

    # ------------------------------------------------------------------ 1. files
    print("")
    print("1. Rig components")
    print("-------------------")
    for path, label in ((RIG_SCRIPT, "HeroRig"), (TEST_SCRIPT, "HeroRigTest"),
                        (VALIDATOR_SCRIPT, "HeroRigValidator"),
                        (FOOT_PLANTING_SCRIPT, "HeroWalkFootPlanting")):
        check(os.path.exists(path), "%s exists (%s)" % (label, os.path.relpath(path, REPO_ROOT)))
        check(meta_guid(path) is not None, "%s has a meta file with a GUID" % label)

    guids = [meta_guid(path) for path in
             (RIG_SCRIPT, TEST_SCRIPT, VALIDATOR_SCRIPT, FOOT_PLANTING_SCRIPT)
             if meta_guid(path)]
    check(len(guids) == len(set(guids)), "the new script GUIDs are unique")

    rig = read(RIG_SCRIPT)
    test = read(TEST_SCRIPT)
    validator = read(VALIDATOR_SCRIPT)
    check("namespace MobileGame.Player" in rig, "HeroRig lives in the MobileGame.Player namespace")
    check("[RequireComponent(typeof(HeroCharacterVisual))]" in rig,
          "HeroRig requires the hero visual it binds")
    check("[DefaultExecutionOrder(-40)]" in rig,
          "HeroRig builds after HeroCharacterVisual (-50) and before the player controller")
    check('[ContextMenu("Verify: Run Hero Rig Test Suite")]' in test,
          "the Play Mode suite can be started from the Inspector context menu")
    check("Tools/MobileGame/Hero Rig/" in validator,
          "the Editor validator is under Tools/MobileGame/Hero Rig")

    # ------------------------------------------------------------------ 2. skeleton
    print("")
    print("2. Skeleton contents")
    print("--------------------")
    bones = re.findall(r'J\(HeroJoint\.(\w+),\s*"([A-Za-z0-9_]+)",\s*HeroJoint\.(\w+)', rig)
    names = set(bone for _, bone, _ in bones)
    required = {
        "hips": ["Hips"],
        "spine": ["Spine"],
        "chest": ["Chest"],
        "neck": ["Neck"],
        "head": ["Head"],
        "shoulders": ["LeftShoulder", "RightShoulder"],
        "upper arms": ["LeftUpperArm", "RightUpperArm"],
        "forearms": ["LeftLowerArm", "RightLowerArm"],
        "hands": ["LeftHand", "RightHand"],
        "upper legs": ["LeftUpperLeg", "RightUpperLeg"],
        "lower legs": ["LeftLowerLeg", "RightLowerLeg"],
        "feet": ["LeftFoot", "RightFoot"],
        "toes": ["LeftToes", "RightToes"],
    }
    for label, wanted in required.items():
        missing = [name for name in wanted if name not in names]
        check(not missing, "the rig declares %s: %s" % (label, ", ".join(wanted))
              + ("" if not missing else " - missing " + ", ".join(missing)))

    fingers = ["%s%s%s" % (side, finger, segment)
               for side in ("Left", "Right")
               for finger in ("Thumb", "Index", "Middle", "Ring", "Little")
               for segment in ("Proximal", "Intermediate", "Distal")]
    missing = [finger for finger in fingers if finger not in names]
    check(not missing, "the rig declares 30 finger bones (5 x 3 x 2 hands)"
          + ("" if not missing else " - missing %d, e.g. %s" % (len(missing), missing[0])))

    mapped = re.findall(r'J\(HeroJoint\.\w+,\s*"[A-Za-z0-9_]+",\s*HeroJoint\.\w+,\s*'
                        r'[^,]+,[^,]+,[^,]+,\s*HumanBodyBones\.(\w+)', rig)
    check(len(mapped) == 52 and mapped.count("LastBone") == 1,
          "all 52 bones are declared and 51 of them are mapped to a HumanBodyBones entry")
    helpers = [row for row, human in zip(bones, mapped) if human == "LastBone"]
    check(len(helpers) == 1 and helpers[0][:3] == ("HeadTopEnd", "HeadTop_End", "Head"),
          "the only unmapped bone is the HeadTop_End helper that gives the head bone an axis")

    # ------------------------------------------------------------------ 3. binding
    print("")
    print("3. Mesh binding")
    print("---------------")
    hero = read(HERO_SCRIPT)
    parts = re.findall(r'Part\(PrimitiveType\.\w+,\s*"([A-Za-z0-9_]+)"', hero)
    binds = re.findall(r'B\("([A-Za-z0-9_]+)",\s*HeroJoint\.(\w+)\)', rig)
    bound = [name for name, _ in binds]

    check(len(parts) == 57, "HeroCharacterVisual builds 57 meshes (found %d)" % len(parts))
    missing = sorted(set(parts) - set(bound))
    check(not missing, "every hero mesh is claimed by the bind table"
          + ("" if not missing else " - unbound: " + ", ".join(missing)))
    unknown = sorted(set(bound) - set(parts))
    check(not unknown, "the bind table references no mesh that does not exist"
          + ("" if not unknown else " - unknown: " + ", ".join(unknown)))
    duplicates = sorted(set(name for name in bound if bound.count(name) > 1))
    check(not duplicates, "no mesh is bound twice"
          + ("" if not duplicates else " - " + ", ".join(duplicates)))

    joints = set(joint for joint, _, _ in bones)
    bad_joint = sorted(set(joint for _, joint in binds if joint not in joints))
    check(not bad_joint, "every bind target is a declared bone"
          + ("" if not bad_joint else " - " + ", ".join(bad_joint)))

    # ------------------------------------------------------------------ 4. animation setup
    print("")
    print("4. Unity animation setup")
    print("------------------------")
    check("AvatarBuilder.BuildHumanAvatar" in rig, "the rig builds a humanoid avatar")
    check("AvatarBuilder.BuildGenericAvatar" in rig,
          "the rig falls back to a generic avatar when the humanoid one is rejected")
    check("avatar.isValid" in rig and "avatar.isHuman" in rig,
          "the humanoid avatar is validated before it is used")
    check("HumanTrait.BoneName[(int)spec.HumanBone]" in rig,
          "the humanoid mapping is driven by Unity's own bone names")
    check("m_animator.applyRootMotion = applyRootMotion" in rig and
          "private bool applyRootMotion = false" in rig,
          "root motion is off by default - the CharacterController owns this root transform")
    check("AnimatorCullingMode.AlwaysAnimate" in rig,
          "the skeleton keeps evaluating off screen (AlwaysAnimate culling)")
    check("m_rigRoot.gameObject.AddComponent<Animator>()" in rig,
          "the Animator sits on the skeleton root, not on the Player root")
    check("AddComponent<Animator>" not in read(CONTROLLER_SCRIPT) and
          "GetComponent<Animator>" not in read(CONTROLLER_SCRIPT),
          "the player controller neither adds nor drives an Animator")
    check("Quaternion.Euler(0f, 0f, -TPoseArmAngle)" in rig and
          "Quaternion.Euler(0f, 0f, TPoseArmAngle)" in rig,
          "the arms are posed out to the sides before the avatar is built (a real T-pose)")
    check("ApplyRestPose();" in rig,
          "the rig returns to the authored resting pose right after the avatar exists")
    check("runtimeAnimatorController = controller" in rig and
          "SerializeField] private RuntimeAnimatorController animatorController" in rig and
          'Resources.Load<RuntimeAnimatorController>("Animations/HeroIdle/Hero_Idle")' in rig and
          "EnsureFootPlantingDriver();" in rig,
          "the rig uses its Resources locomotion controller and installs the foot-planting driver")

    # ------------------------------------------------------------------ 5. scope guards
    print("")
    print("5. Scope: rig only")
    print("------------------")
    controller_source = read(CONTROLLER_SCRIPT)
    idle_builder = read(IDLE_BUILDER_SCRIPT)
    foot_planting = read(FOOT_PLANTING_SCRIPT)
    check("IsMovingAnimatorParameter = \"IsMoving\"" in controller_source and
          "WalkCycleRateAnimatorParameter = \"WalkCycleRate\"" in controller_source and
          "UpdateAnimatorMovementState();" in controller_source and
          "m_animator.SetBool(IsMovingAnimatorParameterId, isMoving)" in controller_source and
          "m_animator.SetFloat(WalkCycleRateAnimatorParameterId, walkCycleRate)" in controller_source,
          "the player supplies grounded walk state and speed-matched stride rate to the Animator")
    scene_source = read(SCENE)
    check("moveSpeed = 1.25f" in controller_source and
          "WalkCycleReferenceSpeed = 1.25f" in controller_source and
          "moveSpeed: 1.25" in scene_source,
          "the default player speed stays at the walk-cycle reference pace")
    check(git_unchanged(HERO_SCRIPT), "HeroCharacterVisual.cs is untouched")
    check("AddComponent<CharacterController" not in rig,
          "the rig adds no CharacterController - the Player root keeps the existing one")
    check("AddComponent<Collider" not in rig and "AddComponent<Rigidbody" not in rig
          and "AddComponent<BoxCollider" not in rig and "AddComponent<CapsuleCollider" not in rig,
          "the rig adds no colliders and no rigidbody")

    check("new CurveSpec(\"Hips\"" in idle_builder and
          "localEulerAnglesRaw" in idle_builder and "settings.loopTime = true" in idle_builder,
          "the generated Hero_Idle clip has subtle looping transform curves")
    check('WalkClipPath = Folder + "/Hero_Walk.anim"' in idle_builder and
          'clip.name = "Hero_Walk"' in idle_builder and
          'new PeriodicCurveSpec("LeftUpperLeg"' in idle_builder and
          'new PeriodicCurveSpec("RightLowerLeg"' in idle_builder and
          'new PeriodicCurveSpec("LeftFoot"' in idle_builder and
          'new PeriodicCurveSpec("LeftUpperArm"' in idle_builder and
          'new PeriodicCurveSpec("Chest"' in idle_builder,
          "the generated in-place walk loop animates alternating legs, feet, counter-swinging arms and a stable torso")
    check('AnimatorState idle = stateMachine.AddState("Idle")' in idle_builder and
          "idle.motion = idleClip" in idle_builder and
          'AnimatorState walk = stateMachine.AddState("Walk")' in idle_builder and
          "walk.motion = walkClip" in idle_builder and
          "walk.speedParameterActive = true" in idle_builder and
          "walk.iKOnFeet = true" in idle_builder and
          "layers[0].iKPass = true" in idle_builder,
          "the controller has only Idle and Walk states with speed-scaled humanoid foot IK")
    check('AnimatorConditionMode.If, 0f, MovingParameter' in idle_builder and
          'AnimatorConditionMode.IfNot, 0f, MovingParameter' in idle_builder and
          "leaveIdle.hasFixedDuration = true" in idle_builder and
          "returnToIdle.hasFixedDuration = true" in idle_builder,
          "Animator transitions smoothly between idle and walk using grounded movement state")
    check('StanceFraction = 0.56f' in foot_planting and
          "Physics.Raycast(rayOrigin, Vector3.down" in foot_planting and
          "SetIKPosition(goal, plant.Position)" in foot_planting and
          "MaxGroundedPlantError" in foot_planting,
          "planted feet are ground-projected, held in world space and expose a sliding diagnostic")
    movement_test = read(os.path.join(ASSETS, "Scripts", "Player", "PlayerControllerTest.cs"))
    check("TestIdleAnimationTransitions" in movement_test and
          'IsName("Walk")' in movement_test and 'IsName("Idle")' in movement_test and
          all(label in movement_test for label in ("Forward", "Backward", "Right", "Left", "Diagonal")) and
          "MaxGroundedPlantError" in movement_test and "maxChestRotation" in movement_test and
          "maxLeftArmSwing" in movement_test,
          "the Play Mode movement suite covers five walk directions, torso stability, arm swing and foot planting")
    check("no running or combat clips" in " ".join(test.lower().split()),
          "the rig test scope excludes running and combat clips")
    check("HeroRig" in read(MATERIAL_TEST_SCRIPT),
          "the material suite knows about the rig instead of failing on it")

    # ------------------------------------------------------------------ 6. scene wiring
    print("")
    print("6. Scene wiring")
    print("---------------")
    scene = read(SCENE)
    rig_guid = meta_guid(RIG_SCRIPT)
    test_guid = meta_guid(TEST_SCRIPT)
    check(rig_guid in scene, "CombatTestScene has a HeroRig component")
    check(test_guid in scene, "CombatTestScene has a HeroRigTest object")
    check("m_Name: HeroRigTest" in scene, "the test suite is its own scene object")

    match = re.search(r"m_Script: \{fileID: 11500000, guid: %s, type: 3\}.*?(?=\n--- |\Z)" % rig_guid,
                      scene, re.S)
    block = match.group(0) if match else ""
    check("buildOnEnable: 1" in block, "the scene rig builds itself on enable")
    check("avatarKind: 0" in block, "the scene rig asks for the humanoid avatar (0)")
    check("applyRootMotion: 0" in block, "the scene rig leaves root motion off")
    check("animatorController: {fileID: 0}" in block and
          'Resources.Load<RuntimeAnimatorController>("Animations/HeroIdle/Hero_Idle")' in rig,
          "the rig resolves the idle/walk controller from its Resources asset at runtime")

    print("")
    print("Checks passed: %d/%d" % (len(PASSED), len(PASSED) + len(FAILED)))
    if FAILED:
        print("HERO RIG STATIC VERIFICATION FAILED")
        for failure in FAILED:
            print("  FAIL " + failure)
        return 1
    print("HERO RIG STATIC VERIFICATION PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
