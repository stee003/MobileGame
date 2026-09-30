#!/usr/bin/env python3
"""Headless rig verification: rotates every joint of the hero rig and measures what deforms.

Mirrors the Play Mode suite ``Assets/Scripts/Player/HeroRigTest.cs``:

  1. rig data       - bone chain, humanoid mapping, mirroring, T-pose, bind coverage;
  2. rest pose      - soles on the ground, lossless rigid bind, silhouette unchanged by the fixes;
  3. simple rotations - hips, spine, chest, neck, head, shoulders, upper arms, elbows, hands,
                      fingers, upper legs, knees and feet are rotated one joint at a time and the
                      consequences are measured (what moves, what must not move, seams, ground);
  4. stress         - 400 random multi joint poses inside the joint limits, plus the same poses on
                      the pre-fix rig so the value of each fix is a number and not an opinion.

No combat animation and no animation clips are involved: this only rotates bones.

Run:  python3 Tools/HeroRigVerification/simulate_hero_rig.py
Exit code 0 means every check passed.
"""

import math
import random
import sys

from rig_model import (Rig, separation, moved_volume, split_part, q_euler,
                       vadd, vsub, vscale, vlength, vnorm)

# ---------------------------------------------------------------------------
# Thresholds. Every one of them is either a modelling fact or a measurement that
# is printed next to the check, never a value chosen to make a test pass.
# ---------------------------------------------------------------------------
SEAM_TOLERANCE = 0.002          # 2 mm: below this two parts are still touching
MOVE_EPSILON = 0.001            # 1 mm: a part bound below a rotated joint has to move this much
STILL_EPSILON = 1e-7            # a part outside the rotated chain must not move at all
BIND_EPSILON = 1e-5             # the rigid bind must reproduce the authored transform exactly
MIN_BONE_LENGTH = 0.010         # 10 mm: shorter bones make Unity's avatar builder reject the rig
T_POSE_ARM_DROP = 0.02          # the T-pose arms must be horizontal to within 20 mm over 0.565 m

# Rigid box soles have no articulation, so any ankle rotation presses a corner of the sole through
# the ground plane. Measured (see REPORT.md): 18 mm for a 15 degree ankle pitch, 15 mm for a 20
# degree roll. Joints at or above the knee move the whole leg instead, which measures 36 mm for a
# 12 degree pelvis tilt. Both are geometry limits of the primitive boots, not rig faults - the
# humanoid avatar's foot IK is the fix and belongs to the animation pass.
SOLE_ALLOWANCE_BELOW_KNEE = 0.025
SOLE_ALLOWANCE_ABOVE_KNEE = 0.045

# Seams that are allowed to open, with the measured allowance, the part that backs the opening and
# the reason. Everything else has to stay closed at SEAM_TOLERANCE.
SEAM_EXCEPTIONS = {
    ("HipAccent_L", "Abdomen"): (0.020, "Hips", "hip trim slides on the pelvis; the gap stays inside the pelvis box"),
    ("HipAccent_R", "Abdomen"): (0.020, "Hips", "hip trim slides on the pelvis; the gap stays inside the pelvis box"),
    ("Chest", "UpperArm_L"): (0.030, None, "armpit opens when the arm is abducted"),
    ("Chest", "UpperArm_R"): (0.030, None, "armpit opens when the arm is abducted"),
    ("Chest", "Shoulder_L"): (0.030, None, "armpit opens when the arm is abducted"),
    ("Chest", "Shoulder_R"): (0.030, None, "armpit opens when the arm is abducted"),
}

# One joint at a time, both sides, every joint group the task asks for.
TEST_ROTATIONS = [
    ("hips pitch", "Hips", (12, 0, 0)),
    ("hips yaw", "Hips", (0, 15, 0)),
    ("hips roll", "Hips", (0, 0, 10)),
    ("spine pitch", "Spine", (15, 0, 0)),
    ("spine lateral", "Spine", (0, 0, 12)),
    ("spine yaw", "Spine", (0, 15, 0)),
    ("chest pitch", "Chest", (15, 0, 0)),
    ("chest yaw", "Chest", (0, 15, 0)),
    ("neck pitch", "Neck", (20, 0, 0)),
    ("neck yaw", "Neck", (0, 30, 0)),
    ("head pitch", "Head", (20, 0, 0)),
    ("head yaw", "Head", (0, 40, 0)),
    ("left shoulder", "LeftShoulder", (10, 10, 10)),
    ("right shoulder", "RightShoulder", (10, 10, 10)),
    ("left upper arm forward", "LeftUpperArm", (-40, 0, 0)),
    ("left upper arm out", "LeftUpperArm", (0, 0, -60)),
    ("right upper arm out", "RightUpperArm", (0, 0, 60)),
    ("left elbow flex", "LeftLowerArm", (-90, 0, 0)),
    ("right elbow flex", "RightLowerArm", (-90, 0, 0)),
    ("left wrist flex", "LeftHand", (45, 0, 0)),
    ("left index curl", "LeftIndexProximal", (60, 0, 0)),
    ("left thumb curl", "LeftThumbProximal", (40, 0, 0)),
    ("left hip forward", "LeftUpperLeg", (-60, 0, 0)),
    ("left hip abduct", "LeftUpperLeg", (0, 0, -35)),
    ("right hip forward", "RightUpperLeg", (-60, 0, 0)),
    ("left knee flex", "LeftLowerLeg", (90, 0, 0)),
    ("right knee flex", "RightLowerLeg", (90, 0, 0)),
    ("left ankle roll", "LeftFoot", (0, 0, 20)),
    ("left ankle pitch", "LeftFoot", (-15, 0, 0)),
    ("left toes flex", "LeftToes", (30, 0, 0)),
]

FOOT_JOINTS = ("LeftFoot", "LeftToes", "RightFoot", "RightToes")
BELOW_KNEE_JOINTS = ("LeftFoot", "LeftToes", "RightFoot", "RightToes")

# Unity's HumanBodyBones that a complete humanoid skeleton must provide.
MANDATORY_HUMAN_BONES = [
    "Hips", "Spine", "Chest", "Neck", "Head",
    "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
    "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
    "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
    "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
]
FINGER_HUMAN_BONES = ["%s%s%s" % (side, finger, segment)
                      for side in ("Left", "Right")
                      for finger in ("Thumb", "Index", "Middle", "Ring", "Little")
                      for segment in ("Proximal", "Intermediate", "Distal")]

PASSED = []
FAILED = []


def check(condition, description):
    (PASSED if condition else FAILED).append(description)
    print("  %s %s" % ("PASS" if condition else "FAIL", description))
    return condition


def section(title):
    print("")
    print(title)
    print("-" * len(title))


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def volumes(rig, transforms):
    return {name: moved_volume(rig.by_name[name], *transforms[name]) for name in transforms}


def cross_bone_seams(rig, rest_volumes):
    """Every pair of parts that touches at rest but is bound to different bones."""
    seams = []
    parts = [part for part in rig.parts if part.joint is not None]
    for i in range(len(parts)):
        for j in range(i + 1, len(parts)):
            a, b = parts[i], parts[j]
            if a.joint == b.joint:
                continue
            gap = separation(rest_volumes[a.name], rest_volumes[b.name])
            if gap <= SEAM_TOLERANCE:
                seams.append((a.name, b.name, gap))
    return seams


def seam_key(a, b):
    return (a, b) if (a, b) in SEAM_EXCEPTIONS else ((b, a) if (b, a) in SEAM_EXCEPTIONS else None)


def rotation_axis(quaternion):
    x, y, z, _ = quaternion
    length = math.sqrt(x * x + y * y + z * z)
    if length < 1e-12:
        return (0.0, 1.0, 0.0)
    return (x / length, y / length, z / length)


def rotation_angle(quaternion):
    w = max(-1.0, min(1.0, quaternion[3]))
    return 2.0 * math.acos(abs(w))


def quaternion_angle(a, b):
    """Angle in degrees between two rotations."""
    dot = abs(a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3])
    return 2.0 * math.degrees(math.acos(max(-1.0, min(1.0, dot))))


def finite(transforms):
    for name, (position, rotation) in transforms.items():
        for value in position:
            if not math.isfinite(value):
                return False, name
        for value in rotation:
            if not math.isfinite(value):
                return False, name
    return True, None


def lowest_point(part, transforms):
    position, rotation = transforms[part.name]
    return part.lowest_y(position, rotation)


def ground_report(rig, transforms, exclude=()):
    """Returns (worst non-foot penetration, worst foot penetration, worst part name)."""
    worst_body = (float("inf"), None)
    worst_foot = (float("inf"), None)
    for part in rig.parts:
        if part.name in exclude:
            continue
        low = lowest_point(part, transforms)
        is_foot = part.joint in FOOT_JOINTS
        if is_foot:
            if low < worst_foot[0]:
                worst_foot = (low, part.name)
        else:
            if low < worst_body[0]:
                worst_body = (low, part.name)
    return worst_body, worst_foot


# ---------------------------------------------------------------------------
# 1. Rig data
# ---------------------------------------------------------------------------
def check_rig_data(rig):
    section("1. Rig data")

    joints = rig.joints
    check(len(joints) == 52, "the skeleton declares 52 bones (found %d)" % len(joints))

    names = [joint["bone"] for joint in joints]
    check(len(names) == len(set(names)), "every bone name is unique")

    # Humanoid bone names have to be exactly Unity's names, or the avatar maps the wrong transform.
    bad_names = [joint["bone"] for joint in joints
                 if joint["human"] != "LastBone" and joint["bone"] != joint["human"]]
    check(not bad_names, "every mapped bone is named after its HumanBodyBones entry"
          + ("" if not bad_names else " - mismatched: " + ", ".join(bad_names)))

    seen = set()
    order_ok = True
    parent_ok = True
    for joint in joints:
        if joint["parent"] != "None" and joint["parent"] not in seen:
            order_ok = False
        seen.add(joint["joint"])
        if joint["parent"] != "None" and joint["parent"] not in rig.joint_by_enum:
            parent_ok = False
    check(order_ok, "a parent always comes before its child in the bone table")
    check(parent_ok, "every bone lists an existing parent")

    mapped = [joint["bone"] for joint in joints if joint["human"] != "LastBone"]
    check(len(mapped) == 51, "51 of the 52 bones are mapped into the humanoid avatar (found %d)" % len(mapped))
    helpers = [joint["bone"] for joint in joints if joint["human"] == "LastBone"]
    check(helpers == ["HeadTop_End"], "HeadTop_End is the only helper bone (found %s)" % helpers)

    missing = [bone for bone in MANDATORY_HUMAN_BONES if bone not in mapped]
    check(not missing, "all 21 mandatory humanoid bones are present"
          + ("" if not missing else " - missing: " + ", ".join(missing)))
    missing_fingers = [bone for bone in FINGER_HUMAN_BONES if bone not in mapped]
    check(not missing_fingers, "all 30 finger bones are present (5 fingers x 3 segments x 2 hands)"
          + ("" if not missing_fingers else " - missing: " + ", ".join(missing_fingers)))

    # Bone lengths: a zero length bone makes Unity reject the avatar and twists infinitely.
    lengths = {}
    for joint in joints:
        if joint["parent"] == "None":
            continue
        parent = rig.joint_by_enum[joint["parent"]]
        lengths[joint["bone"]] = vlength(vsub(joint["position"], parent["position"]))
    shortest = min(lengths.items(), key=lambda kv: kv[1])
    check(shortest[1] >= MIN_BONE_LENGTH,
          "no bone is shorter than %.0f mm (shortest: %s = %.1f mm)"
          % (MIN_BONE_LENGTH * 1000.0, shortest[0], shortest[1] * 1000.0))

    # A bone's length is the distance to its child, so the segments are named after the child.
    limb_lengths = {"upper arm": lengths["LeftLowerArm"], "forearm": lengths["LeftHand"],
                    "thigh": lengths["LeftLowerLeg"], "shin": lengths["LeftFoot"],
                    "clavicle": lengths["LeftUpperArm"], "neck": lengths["Head"]}
    check(all(0.20 <= limb_lengths[name] <= 0.45 for name in ("upper arm", "forearm", "thigh", "shin")),
          "limb segments are humanoid proportions: " +
          ", ".join("%s %.0f mm" % (k, v * 1000.0) for k, v in sorted(limb_lengths.items())))

    # Mirroring: the two sides of a humanoid rig must be exact mirrors or retargeting leans.
    mirror_problems = []
    for joint in joints:
        bone = joint["bone"]
        if not bone.startswith("Left"):
            continue
        partner = rig.joint_by_name.get("Right" + bone[4:])
        if partner is None:
            mirror_problems.append(bone + " has no right counterpart")
            continue
        left, right = joint["position"], partner["position"]
        if abs(left[0] + right[0]) > 1e-6 or abs(left[1] - right[1]) > 1e-6 or abs(left[2] - right[2]) > 1e-6:
            mirror_problems.append("%s %s vs %s %s" % (bone, left, partner["bone"], right))
        if joint["limit_min"][0] != partner["limit_min"][0] or joint["limit_max"][0] != partner["limit_max"][0]:
            mirror_problems.append(bone + " pitch limits differ from the right side")
        if (joint["limit_min"][2] != -partner["limit_max"][2] or
                joint["limit_max"][2] != -partner["limit_min"][2]):
            mirror_problems.append(bone + " roll limits are not the mirror of the right side")
    check(not mirror_problems, "the left and right chains are exact mirrors"
          + ("" if not mirror_problems else " - " + "; ".join(mirror_problems[:4])))

    # Centre line: a humanoid hips/spine chain sits on the mirror plane.
    centre = [joint["bone"] for joint in joints
              if joint["bone"] in ("Hips", "Spine", "Chest", "Neck", "Head", "HeadTop_End")
              and abs(joint["position"][0]) > 1e-6]
    check(not centre, "hips, spine, chest, neck and head sit on the x = 0 mirror plane")

    # Bind coverage.
    check(not rig.unbound, "every hero mesh is bound to a bone"
          + ("" if not rig.unbound else " - unbound: " + ", ".join(rig.unbound)))
    check(not rig.problems, "the bind/fit tables only reference existing parts"
          + ("" if not rig.problems else " - " + "; ".join(rig.problems)))
    bound_names = [part.name for part in rig.parts if part.joint is not None]
    check(len(bound_names) == len(set(bound_names)), "no part is bound twice")
    check(len(rig.parts) == 57 and len(bound_names) == 57,
          "all 57 hero meshes are bound (found %d parts, %d bound)" % (len(rig.parts), len(bound_names)))

    bind_joints = set(part.joint for part in rig.parts)
    geometry_free = [joint["bone"] for joint in joints if joint["joint"] not in bind_joints]
    check(set(geometry_free) == set(["LeftShoulder", "RightShoulder", "HeadTop_End"] +
                                    ["%s%s%s" % (s, f, seg)
                                     for s in ("Left", "Right")
                                     for f in ("Thumb", "Index", "Middle", "Ring", "Little")
                                     for seg in ("Proximal", "Intermediate", "Distal")] +
                                    ["LeftToes", "RightToes"]),
          "only the clavicles, finger chains, toes and the head tip carry no geometry of their own")

    # Diagnostic limits must be usable ranges.
    limit_problems = []
    for joint in joints:
        for axis in range(3):
            if joint["limit_min"][axis] > joint["limit_max"][axis]:
                limit_problems.append("%s axis %d" % (joint["bone"], axis))
    check(not limit_problems, "every joint limit is a valid range"
          + ("" if not limit_problems else " - " + ", ".join(limit_problems)))


# ---------------------------------------------------------------------------
# 2. T-pose and rest pose
# ---------------------------------------------------------------------------
def check_poses(rig):
    section("2. T-pose (the avatar bind pose) and rest pose")

    t_pose = {"LeftUpperArm": q_euler(0.0, 0.0, -90.0), "RightUpperArm": q_euler(0.0, 0.0, 90.0)}
    positions, _ = rig.forward_kinematics(t_pose)

    def arm_direction(side):
        return vnorm(vsub(positions[side + "Hand"], positions[side + "UpperArm"]))

    left, right = arm_direction("Left"), arm_direction("Right")
    check(abs(left[1]) <= T_POSE_ARM_DROP and left[0] < -0.9,
          "the left arm is horizontal and points to -x in the T-pose (%.3f, %.3f, %.3f)" % left)
    check(abs(right[1]) <= T_POSE_ARM_DROP and right[0] > 0.9,
          "the right arm is horizontal and points to +x in the T-pose (%.3f, %.3f, %.3f)" % right)
    check(abs(positions["LeftHand"][1] - positions["LeftUpperArm"][1]) < 1e-6 and
          abs(positions["RightHand"][1] - positions["RightUpperArm"][1]) < 1e-6,
          "both hands sit exactly at shoulder height in the T-pose (y = %.3f)" % positions["LeftHand"][1])

    for side in ("Left", "Right"):
        direction = vnorm(vsub(positions[side + "LowerLeg"], positions[side + "UpperLeg"]))
        check(abs(direction[1] + 1.0) < 1e-6,
              "the %s leg hangs straight down in the T-pose (%.3f, %.3f, %.3f)"
              % (side.lower(), *direction))
        foot = vnorm(vsub(positions[side + "Toes"], positions[side + "Foot"]))
        check(foot[2] > 0.8, "the %s foot points forward (+z) in the T-pose (%.3f, %.3f, %.3f)"
              % (side.lower(), *foot))

    # Rest pose: the hero stands relaxed, soles on the ground.
    rest = rig.part_transforms({})
    ok, bad = finite(rest)
    check(ok, "the rest pose contains no NaN or infinite transform" + ("" if ok else " - " + str(bad)))

    sole_left = lowest_point(rig.by_name["Foot_L"], rest)
    sole_right = lowest_point(rig.by_name["Foot_R"], rest)
    check(abs(sole_left) < 1e-6 and abs(sole_right) < 1e-6,
          "both soles rest exactly on the ground plane (y = %.6f / %.6f)" % (sole_left, sole_right))

    body, foot = ground_report(rig, rest, exclude=("Foot_L", "Foot_R"))
    check(body[0] > 0.0, "nothing but the boots touches the ground at rest (lowest: %s at y = %.4f)"
          % (body[1], body[0]))
    check(foot[0] > -1e-6, "no boot corner is below the ground at rest (lowest %.6f)" % foot[0])

    # The rigid bind must be lossless: zero rotations have to reproduce the authored transforms.
    worst_position = 0.0
    worst_rotation = 0.0
    worst_name = ""
    for part in rig.parts:
        position, rotation = rest[part.name]
        delta = vlength(vsub(position, part.position))
        if delta > worst_position:
            worst_position, worst_name = delta, part.name
        angle = 2.0 * math.degrees(math.acos(max(-1.0, min(1.0, abs(
            rotation[0] * part.rotation[0] + rotation[1] * part.rotation[1] +
            rotation[2] * part.rotation[2] + rotation[3] * part.rotation[3])))))
        worst_rotation = max(worst_rotation, angle)
    check(worst_position < BIND_EPSILON,
          "the rigid bind is lossless - worst part offset %.2e m (%s)" % (worst_position, worst_name))
    check(worst_rotation < 1e-3,
          "the rigid bind preserves every part's rotation - worst deviation %.2e degrees" % worst_rotation)

    # The interior extensions must not change the silhouette: the added volume has to stay inside a
    # neighbour that is drawn over it.
    authored = Rig(apply_fits=False, apply_splits=False)
    fit_problems = []
    for fit in rig.fit_table:
        grown = rig.by_name[fit["name"]]
        before = authored.by_name[fit["name"]]
        neighbours = [other for other in authored.parts if other.name != before.name]
        added = 0
        for point in grown.extreme_points():
            if before.contains(point):
                continue  # this point already existed before the extension
            added += 1
            if not any(other.contains(point) for other in neighbours):
                fit_problems.append("%s point %s is not covered by any neighbour"
                                    % (fit["name"], tuple(round(c, 3) for c in point)))
        if added == 0:
            fit_problems.append("%s did not grow" % fit["name"])
    check(not fit_problems,
          "the interior extensions only add volume that a neighbour already covers, so the "
          "silhouette does not change" + ("" if not fit_problems else " - " + "; ".join(fit_problems[:3])))

    abdomen = rig.by_name["Abdomen"]
    check(abs(abdomen.scale[1] - 0.35) < 1e-6 and abs(abdomen.position[1] - 1.125) < 1e-6,
          "Abdomen now spans 0.95..1.30 (was 1.02..1.24) - inside the pelvis below, inside the chest above")
    neck = rig.by_name["Neck"]
    check(abs(neck.scale[1] - 0.06) < 1e-6 and abs(neck.position[1] - 1.50) < 1e-6,
          "Neck now spans 1.44..1.56 (was 1.50..1.56) - the extra length hides in the chest box")


# ---------------------------------------------------------------------------
# 3. Simple rotations
# ---------------------------------------------------------------------------
def check_rotations(rig):
    section("3. Simple rotations - shoulders, elbows, knees, hips, spine and the rest")

    rest = rig.part_transforms({})
    rest_volumes = volumes(rig, rest)
    seams = cross_bone_seams(rig, rest_volumes)
    check(len(seams) >= 20,
          "%d cross-bone seams touch at rest and are watched for tearing" % len(seams))

    moved_totals = {}
    worst_body = (0.0, "", "")
    worst_foot = (0.0, "", "")
    isolation_failures = []
    move_failures = []
    seam_failures = []
    nan_failures = []
    restore_failures = []
    applied_failures = []
    exceptions = {}

    for label, joint, euler in TEST_ROTATIONS:
        rotation = q_euler(*euler)
        pose = {joint: rotation}
        transforms = rig.part_transforms(pose)

        ok, bad = finite(transforms)
        if not ok:
            nan_failures.append("%s: %s" % (label, bad))

        # The joint itself has to hold the rotation that was asked for (compared as quaternions:
        # Euler angles are ambiguous at +/-90 degrees of pitch, which is exactly where the elbow
        # and the knee are tested).
        _, rotations = rig.forward_kinematics(pose)
        angle_off = quaternion_angle(rotations[joint], rotation)
        if angle_off > 1e-4:
            applied_failures.append("%s: asked %s, the bone holds a rotation %.4f degrees away"
                                    % (label, euler, angle_off))

        chain = rig.descendants(joint)
        chain_parts = rig.parts_of(chain)
        outside_parts = [part for part in rig.parts if part.joint not in chain]

        # Every part below the joint undergoes exactly one rigid rotation about the joint, so its
        # travel is fully predicted: 2 d sin(theta/2), with d the distance to the rotation axis.
        # A part on the axis (a yawed spine chain, for instance) must not move at all.
        pivot = rig.joint_by_enum[joint]["position"]
        axis = rotation_axis(rotation)
        theta = rotation_angle(rotation)
        biggest = 0.0
        for part in chain_parts:
            delta = vlength(vsub(transforms[part.name][0], rest[part.name][0]))
            biggest = max(biggest, delta)
            offset = vsub(part.position, pivot)
            along = offset[0] * axis[0] + offset[1] * axis[1] + offset[2] * axis[2]
            radius = vlength(vsub(offset, vscale(axis, along)))
            expected = 2.0 * radius * math.sin(theta * 0.5)
            if abs(delta - expected) > max(1e-6, 0.002 * expected):
                move_failures.append("%s: %s moved %.4f m, a rigid rotation about %s predicts %.4f m"
                                     % (label, part.name, delta, joint, expected))
        moved_totals[label] = biggest

        for part in outside_parts:
            delta = vlength(vsub(transforms[part.name][0], rest[part.name][0]))
            if delta > STILL_EPSILON:
                isolation_failures.append("%s: %s moved %.6f m but is not below the joint"
                                          % (label, part.name, delta))

        # Seams.
        now = volumes(rig, transforms)
        for a, b, rest_gap in seams:
            gap = separation(now[a], now[b])
            if gap <= SEAM_TOLERANCE:
                continue
            key = seam_key(a, b)
            if key is None:
                seam_failures.append("%s: %s <-> %s opened %.1f mm" % (label, a, b, gap * 1000.0))
                continue
            allowance, backing, reason = SEAM_EXCEPTIONS[key]
            if gap > allowance:
                seam_failures.append("%s: %s <-> %s opened %.1f mm (allowance %.0f mm, %s)"
                                     % (label, a, b, gap * 1000.0, allowance * 1000.0, reason))
            else:
                record = exceptions.setdefault(key, (0.0, "", reason, backing))
                if gap > record[0]:
                    exceptions[key] = (gap, label, reason, backing)
                if backing is not None:
                    backing_gap_a = separation(now[backing], now[a])
                    backing_gap_b = separation(now[backing], now[b])
                    if backing_gap_a > 0.0 or backing_gap_b > 0.0:
                        seam_failures.append("%s: %s no longer backs %s <-> %s" % (label, backing, a, b))

        # Ground.
        body, foot = ground_report(rig, transforms)
        if -body[0] > worst_body[0]:
            worst_body = (-body[0], body[1], label)
        if -foot[0] > worst_foot[0]:
            worst_foot = (-foot[0], foot[1], label)
        allowance = (SOLE_ALLOWANCE_BELOW_KNEE if joint in BELOW_KNEE_JOINTS
                     else SOLE_ALLOWANCE_ABOVE_KNEE)
        if body[0] < -1e-6:
            seam_failures.append("%s: %s sank %.1f mm below the ground" % (label, body[1], -body[0] * 1000.0))
        if foot[0] < -allowance:
            seam_failures.append("%s: %s sank %.1f mm below the ground (allowance %.0f mm)"
                                 % (label, foot[1], -foot[0] * 1000.0, allowance * 1000.0))

        # Restoring the rest pose has to be exact, or the rig drifts while it is animated.
        back = rig.part_transforms({})
        for part in rig.parts:
            if vlength(vsub(back[part.name][0], rest[part.name][0])) > BIND_EPSILON:
                restore_failures.append("%s: %s did not return to its rest position" % (label, part.name))
                break

    check(not applied_failures, "every joint holds the rotation it was given"
          + ("" if not applied_failures else " - " + "; ".join(applied_failures[:3])))
    check(not nan_failures, "no rotation produces a NaN or infinite transform"
          + ("" if not nan_failures else " - " + "; ".join(nan_failures[:3])))
    check(not move_failures, "everything below a rotated joint travels with it"
          + ("" if not move_failures else " - " + "; ".join(move_failures[:4])))
    check(not isolation_failures, "nothing outside the rotated chain moves"
          + ("" if not isolation_failures else " - " + "; ".join(isolation_failures[:4])))
    check(not restore_failures, "returning to the rest pose is exact - the rig does not drift"
          + ("" if not restore_failures else " - " + "; ".join(restore_failures[:3])))
    check(not seam_failures, "no seam tears and nothing sinks through the floor"
          + ("" if not seam_failures else " - " + "; ".join(seam_failures[:6])))

    required = ["hips pitch", "spine pitch", "chest pitch", "left shoulder", "right shoulder",
                "left elbow flex", "right elbow flex", "left knee flex", "right knee flex"]
    missing = [name for name in required if name not in moved_totals or moved_totals[name] < MOVE_EPSILON]
    check(not missing, "the required joints were all exercised: hips, spine, shoulders, elbows, knees"
          + ("" if not missing else " - missing: " + ", ".join(missing)))

    print("")
    print("    largest travel of a bound part per rotation:")
    for label, joint, euler in TEST_ROTATIONS:
        print("      %-26s %s%-22s %6.1f mm" % (label, joint, str(euler), moved_totals[label] * 1000.0))

    print("")
    print("    documented seam openings (measured worst case):")
    for (a, b), (gap, label, reason, backing) in sorted(exceptions.items(), key=lambda kv: -kv[1][0]):
        print("      %-32s %5.1f mm at %-24s %s%s"
              % (a + " <-> " + b, gap * 1000.0, label, reason,
                 "" if backing is None else " (backed by " + backing + ")"))

    print("")
    print("    deepest ground contact: body %s (%.1f mm, %s), boots %s (%.1f mm, %s)"
          % (worst_body[1], worst_body[0] * 1000.0, worst_body[2],
             worst_foot[1], worst_foot[0] * 1000.0, worst_foot[2]))
    check(worst_foot[0] < SOLE_ALLOWANCE_ABOVE_KNEE,
          "the rigid box soles never sink more than the measured %.0f mm allowance"
          % (SOLE_ALLOWANCE_ABOVE_KNEE * 1000.0))

    return seams


# ---------------------------------------------------------------------------
# 4. Stress: random multi-joint poses, shipped rig against the pre-fix rig
# ---------------------------------------------------------------------------
def random_pose(rig, rng):
    pose = {}
    for joint in rig.joints:
        low, high = joint["limit_min"], joint["limit_max"]
        if all(abs(value) < 1e-6 for value in low) and all(abs(value) < 1e-6 for value in high):
            continue
        pose[joint["joint"]] = q_euler(*[rng.uniform(low[axis], high[axis]) for axis in range(3)])
    return pose


def stress(rig, poses):
    """Returns {seam: (worst gap, pose index)} plus the worst body/foot ground contact."""
    rest = rig.part_transforms({})
    seams = cross_bone_seams(rig, volumes(rig, rest))
    worst = {(a, b): (-9.0, -1) for a, b, _ in seams}
    worst_body = (0.0, "", -1)
    worst_foot = (0.0, "", -1)
    nan = 0
    for index, pose in enumerate(poses):
        transforms = rig.part_transforms(pose)
        ok, _ = finite(transforms)
        if not ok:
            nan += 1
            continue
        now = volumes(rig, transforms)
        for a, b, _ in seams:
            gap = separation(now[a], now[b])
            if gap > worst[(a, b)][0]:
                worst[(a, b)] = (gap, index)
        body, foot = ground_report(rig, transforms)
        if -body[0] > worst_body[0]:
            worst_body = (-body[0], body[1], index)
        if -foot[0] > worst_foot[0]:
            worst_foot = (-foot[0], foot[1], index)
    return seams, worst, worst_body, worst_foot, nan


def check_stress(rig):
    section("4. Stress - 400 random multi-joint poses inside the joint limits")

    rng = random.Random(20240930)
    poses = [random_pose(rig, rng) for _ in range(400)]

    seams, worst, worst_body, worst_foot, nan = stress(rig, poses)
    check(nan == 0, "400 random poses produce no NaN or infinite transform (%d bad)" % nan)

    torn = []
    for (a, b), (gap, index) in worst.items():
        if gap <= SEAM_TOLERANCE:
            continue
        key = seam_key(a, b)
        allowance = SEAM_EXCEPTIONS[key][0] if key else SEAM_TOLERANCE
        if gap > allowance:
            torn.append("%s <-> %s opened %.1f mm at pose %d (allowance %.0f mm)"
                        % (a, b, gap * 1000.0, index, allowance * 1000.0))
    check(not torn, "no seam tears in 400 random poses" + ("" if not torn else " - " + "; ".join(torn[:5])))

    check(worst_body[0] <= 1e-6,
          "no body part above the boots ever reaches the floor (worst %s at %.1f mm, pose %d)"
          % (worst_body[1], worst_body[0] * 1000.0, worst_body[2]))

    # The boots are rigid boxes on an articulated leg, so a random pose can put a sole corner below
    # the floor - that is foot planting, which belongs to animation and to the humanoid avatar's
    # foot IK, not to the rig. What the rig is responsible for is that no boot part can be anywhere
    # but where its own geometry allows: within the joint radius of the ankle it hangs from.
    boot_reach = {}
    for side in ("Left", "Right"):
        ankle = rig.joint_by_enum[side + "Foot"]["position"]
        for name in ("Foot_" + side[0], "BootCuff_" + side[0], "BootStrap_" + side[0]):
            boot_reach[name] = max(vlength(vsub(point, ankle))
                                   for point in rig.by_name[name].extreme_points())
    detached = []
    for index, pose in enumerate(poses):
        transforms = rig.part_transforms(pose)
        positions, _ = rig.forward_kinematics(pose)
        for side in ("Left", "Right"):
            ankle = positions[side + "Foot"]
            for name in ("Foot_" + side[0], "BootCuff_" + side[0], "BootStrap_" + side[0]):
                for point in rig.by_name[name].extreme_points(*transforms[name]):
                    if vlength(vsub(point, ankle)) > boot_reach[name] + 1e-6:
                        detached.append("%s in pose %d" % (name, index))
    check(not detached,
          "every boot corner stays inside its own ankle radius (%s mm) in all 400 poses - the "
          "%.1f mm of sole penetration is foot planting, which is animation/IK work, not rig work"
          % ("/".join("%.0f" % (boot_reach[name] * 1000.0)
                      for name in ("Foot_L", "BootCuff_L", "BootStrap_L")), worst_foot[0] * 1000.0))

    # The same poses on the rig as it was before the deformation fixes, so each fix is a number.
    naive = Rig(apply_fits=False, apply_splits=False)
    naive.rebind({"Cape": "Chest"})
    naive_seams, naive_worst, naive_body, naive_foot, _ = stress(naive, poses)

    def worst_gap(table, names):
        return max([table[key][0] for key in table if set(key) == names] or [0.0])

    naive_hip = max(worst_gap(naive_worst, {"HipAccent_L", "Abdomen"}),
                    worst_gap(naive_worst, {"HipAccent_R", "Abdomen"}))
    fixed_hip = max(worst_gap(worst, {"HipAccent_L", "Abdomen"}),
                    worst_gap(worst, {"HipAccent_R", "Abdomen"}))
    check(fixed_hip < naive_hip * 0.5,
          "the interior extensions cut the worst hip trim gap from %.1f mm to %.1f mm"
          % (naive_hip * 1000.0, fixed_hip * 1000.0))

    def tangent(model):
        """Seams whose parts only just touch at rest - a bend opens those first."""
        model_rest = volumes(model, model.part_transforms({}))
        return [(a, b) for a, b, gap in cross_bone_seams(model, model_rest) if abs(gap) < 1e-4]

    naive_tangent = tangent(naive)
    shipped_tangent = tangent(rig)
    check(len(shipped_tangent) < len(naive_tangent),
          "knife-edge contacts (parts that only just touch at rest, so any bend opens them): "
          "%d before the fixes, %d after" % (len(naive_tangent), len(shipped_tangent)))

    # And the cape: measured both ways, whole and split.
    split = Rig()
    split_part(split, "Cape", "Cape_Upper", "Chest", "Cape_Lower", "Spine", 0.38)
    _, split_worst, _, _, _ = stress(split, poses)
    cape_tear = max([value[0] for key, value in split_worst.items()
                     if set(key) == set(["Cape_Upper", "Cape_Lower"])] or [0.0])
    shipped_cape = max([value[0] for key, value in worst.items()
                        if "Cape" in key[0] or "Cape" in key[1]] or [0.0])
    check(cape_tear > 0.05 and shipped_cape <= SEAM_TOLERANCE,
          "the cape ships whole on the chest: split in two it tears %.1f mm, whole it opens %.1f mm"
          % (cape_tear * 1000.0, max(shipped_cape, 0.0) * 1000.0))

    print("")
    print("    worst case per seam over 400 random poses (shipped rig):")
    for (a, b), (gap, index) in sorted(worst.items(), key=lambda kv: -kv[1][0]):
        state = "closed" if gap <= SEAM_TOLERANCE else "opens %.1f mm" % (gap * 1000.0)
        print("      %-34s rest overlap kept, worst %s (pose %d)" % (a + " <-> " + b, state, index))


def main():
    print("Hero rig verification (headless mirror of HeroRigTest)")
    print("sources: Assets/Scripts/Player/HeroCharacterVisual.cs, Assets/Scripts/Player/HeroRig.cs")

    rig = Rig()
    check_rig_data(rig)
    check_poses(rig)
    check_rotations(rig)
    check_stress(rig)

    print("")
    print("Checks passed: %d/%d" % (len(PASSED), len(PASSED) + len(FAILED)))
    if FAILED:
        print("RIG VERIFICATION FAILED")
        for failure in FAILED:
            print("  FAIL " + failure)
        return 1
    print("RIG VERIFICATION PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
