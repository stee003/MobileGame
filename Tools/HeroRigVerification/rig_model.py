#!/usr/bin/env python3
"""Shared model of the hero rig, read straight out of the C# sources.

``HeroCharacterVisual.cs`` and ``HeroRig.cs`` are the single source of truth: this module parses
the hero's primitive parts, the bone table, the bind table, the interior-extension table and the
split table, then reproduces - statement by statement - what ``HeroRig.Create()`` does at build
time:

  1. collect the hero meshes by name,
  2. apply the interior-only extensions (``FitTable``),
  3. split parts that span more than one bone (``SplitTable``),
  4. create the bone hierarchy from ``JointTable`` (local position = rest position - parent rest
     position),
  5. rigidly bind every mesh to its bone, keeping the authored world transform.

On top of that model it provides forward kinematics and exact-ish volume separation tests, so the
deformation checks measure the same geometry Unity will move.

Nothing here talks to Unity: it exists because no Unity Editor is available in the environment the
rig was produced in. The authoritative runtime check is the Play Mode suite
``Assets/Scripts/Player/HeroRigTest.cs``.
"""

import math
import os
import re

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
HERO_SCRIPT = os.path.join(REPO_ROOT, "Assets", "Scripts", "Player", "HeroCharacterVisual.cs")
RIG_SCRIPT = os.path.join(REPO_ROOT, "Assets", "Scripts", "Player", "HeroRig.cs")

# Unity's built-in primitives are 1 unit across (cube/sphere/cylinder) and 2 units tall (capsule,
# cylinder), centred on the transform origin.
UNITY_CUBE_SIZE = 1.0
UNITY_SPHERE_SIZE = 1.0
UNITY_CYLINDER_HEIGHT = 2.0
UNITY_CAPSULE_HEIGHT = 2.0


# ---------------------------------------------------------------------------
# Vector / quaternion helpers (Unity conventions, left handed, Y up)
# ---------------------------------------------------------------------------
def v(x, y, z):
    return (float(x), float(y), float(z))


def vadd(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def vsub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def vscale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def vdot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def vcross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def vlength(a):
    return math.sqrt(vdot(a, a))


def vnorm(a):
    length = vlength(a)
    return vscale(a, 1.0 / length) if length > 1e-12 else (0.0, 0.0, 0.0)


def q_identity():
    return (0.0, 0.0, 0.0, 1.0)


def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def q_conj(a):
    return (-a[0], -a[1], -a[2], a[3])


def q_rotate(q, vec):
    """Rotates a vector by a quaternion (Unity's Quaternion * Vector3)."""
    x, y, z, w = q
    vx, vy, vz = vec
    # t = 2 * cross(q.xyz, v); v' = v + q.w * t + cross(q.xyz, t)
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def q_from_axis_angle(axis, degrees):
    axis = vnorm(axis)
    half = math.radians(degrees) * 0.5
    s = math.sin(half)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(half))


def q_euler(x, y, z):
    """Unity's Quaternion.Euler: the rotation is Ry * Rx * Rz (Z applied first)."""
    hx, hy, hz = math.radians(x) * 0.5, math.radians(y) * 0.5, math.radians(z) * 0.5
    qx = (math.sin(hx), 0.0, 0.0, math.cos(hx))
    qy = (0.0, math.sin(hy), 0.0, math.cos(hy))
    qz = (0.0, 0.0, math.sin(hz), math.cos(hz))
    return q_mul(q_mul(qy, qx), qz)


def q_to_euler(q):
    """Unity's Quaternion.eulerAngles (ZXY order), in degrees, 0..360."""
    x, y, z, w = q
    sinp = 2.0 * (w * x - y * z)
    sinp = max(-1.0, min(1.0, sinp))
    ex = math.degrees(math.asin(sinp))
    ex_den = 1.0 - sinp * sinp
    if ex_den > 1e-9:
        ey = math.degrees(math.atan2(2.0 * (x * z + w * y), 1.0 - 2.0 * (x * x + y * y)))
        ez = math.degrees(math.atan2(2.0 * (x * y + w * z), 1.0 - 2.0 * (x * x + z * z)))
    else:
        ey = math.degrees(math.atan2(-2.0 * (y * z - w * x), 2.0 * (w * w + x * x) - 1.0))
        ez = 0.0
    return (ex % 360.0, ey % 360.0, ez % 360.0)


# ---------------------------------------------------------------------------
# Parsing the C# sources
# ---------------------------------------------------------------------------
def _floats(text):
    return [float(part.rstrip("f")) for part in text.split(",")]


def parse_hero_parts():
    """Returns the hero's authored parts, in source order.

    Each entry is a dict with name, primitive, position, scale, rotation (quaternion) and role.
    """
    with open(HERO_SCRIPT, "r", encoding="utf-8") as handle:
        source = handle.read()

    pattern = re.compile(
        r'Part\(PrimitiveType\.(\w+),\s*"([A-Za-z0-9_]+)",\s*'
        r'new Vector3\(([^)]*)\),\s*new Vector3\(([^)]*)\),\s*'
        r'(Quaternion\.[A-Za-z0-9_.() ,\-]+?),\s*HeroMaterialRole\.(\w+)\)',
        re.S)

    parts = []
    for primitive, name, position, scale, rotation, role in pattern.findall(source):
        rotation = rotation.strip()
        if rotation.startswith("Quaternion.Euler"):
            angles = _floats(rotation[rotation.index("(") + 1:rotation.rindex(")")])
            quat = q_euler(*angles)
        else:
            quat = q_identity()
        parts.append({
            "name": name,
            "primitive": primitive,
            "position": tuple(_floats(position)),
            "scale": tuple(_floats(scale)),
            "rotation": quat,
            "role": role,
        })

    if not parts:
        raise RuntimeError("no Part(...) calls found in " + HERO_SCRIPT)
    return parts


def parse_joint_table():
    """Returns the bone table of HeroRig.cs, in rig order."""
    with open(RIG_SCRIPT, "r", encoding="utf-8") as handle:
        source = handle.read()

    pattern = re.compile(
        r'J\(HeroJoint\.(\w+),\s*"([A-Za-z0-9_]+)",\s*HeroJoint\.(\w+),\s*'
        r'([^,]+),\s*([^,]+),\s*([^,]+),\s*HumanBodyBones\.(\w+),\s*'
        r'([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)')

    joints = []
    for match in pattern.findall(source):
        (joint, bone_name, parent, x, y, z, human,
         x_min, x_max, y_min, y_max, z_min, z_max) = match
        joints.append({
            "joint": joint,
            "bone": bone_name,
            "parent": parent,
            "position": (float(x.rstrip("f")), float(y.rstrip("f")), float(z.rstrip("f"))),
            "human": human,
            "limit_min": (float(x_min.rstrip("f")), float(y_min.rstrip("f")), float(z_min.rstrip("f"))),
            "limit_max": (float(x_max.rstrip("f")), float(y_max.rstrip("f")), float(z_max.rstrip("f"))),
        })

    if not joints:
        raise RuntimeError("no J(...) rows found in " + RIG_SCRIPT)
    return joints


def parse_bind_table():
    with open(RIG_SCRIPT, "r", encoding="utf-8") as handle:
        source = handle.read()
    pattern = re.compile(r'B\("([A-Za-z0-9_]+)",\s*HeroJoint\.(\w+)\)')
    return [(name, joint) for name, joint in pattern.findall(source)]


def parse_fit_table():
    with open(RIG_SCRIPT, "r", encoding="utf-8") as handle:
        source = handle.read()
    pattern = re.compile(
        r'F\("([A-Za-z0-9_]+)",\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)')
    fits = []
    for match in pattern.findall(source):
        name = match[0]
        values = [float(value.rstrip("f")) for value in match[1:]]
        fits.append({"name": name, "position": tuple(values[0:3]), "scale": tuple(values[3:6])})
    return fits


def parse_split_table():
    with open(RIG_SCRIPT, "r", encoding="utf-8") as handle:
        source = handle.read()
    pattern = re.compile(
        r'S\("([A-Za-z0-9_]+)",\s*"([A-Za-z0-9_]+)",\s*HeroJoint\.(\w+),\s*'
        r'"([A-Za-z0-9_]+)",\s*HeroJoint\.(\w+),\s*([^)]+)\)')
    splits = []
    for name, upper, upper_joint, lower, lower_joint, fraction in pattern.findall(source):
        splits.append({"name": name, "upper": upper, "upper_joint": upper_joint,
                       "lower": lower, "lower_joint": lower_joint,
                       "fraction": float(fraction.rstrip("f"))})
    return splits


# ---------------------------------------------------------------------------
# The rig model - mirrors HeroRig.Create()
# ---------------------------------------------------------------------------
class Part(object):
    """A hero mesh after HeroRig finished with it."""

    def __init__(self, name, primitive, position, scale, rotation, role):
        self.name = name
        self.primitive = primitive
        self.position = tuple(position)     # rest position in rig (HeroVisual) space
        self.scale = tuple(scale)
        self.rotation = rotation            # rest rotation
        self.role = role
        self.joint = None                   # bone it is bound to
        self.local_position = None          # relative to the bone, filled in by bind()
        self.local_rotation = None

    def copy(self, name):
        clone = Part(name, self.primitive, self.position, self.scale, self.rotation, self.role)
        return clone

    # -- geometry ---------------------------------------------------------
    def bounds(self):
        """Axis-aligned bounds in rig space (rest pose) - used for coarse reports."""
        half = self.half_extents()
        # Conservative: rotate the eight corners.
        corners = []
        for sx in (-1.0, 1.0):
            for sy in (-1.0, 1.0):
                for sz in (-1.0, 1.0):
                    offset = q_rotate(self.rotation, (half[0] * sx, half[1] * sy, half[2] * sz))
                    corners.append(vadd(self.position, offset))
        mins = [min(c[i] for c in corners) for i in range(3)]
        maxs = [max(c[i] for c in corners) for i in range(3)]
        return tuple(mins), tuple(maxs)

    def half_extents(self):
        sx, sy, sz = self.scale
        if self.primitive == "Cube":
            return (0.5 * sx, 0.5 * sy, 0.5 * sz)
        if self.primitive == "Sphere":
            return (0.5 * sx, 0.5 * sy, 0.5 * sz)
        if self.primitive == "Cylinder":
            return (0.5 * sx, 0.5 * sy, 0.5 * sz)
        if self.primitive == "Capsule":
            radius = 0.5 * max(sx, sz)
            return (radius, 0.5 * sy, radius)
        raise ValueError("unsupported primitive " + self.primitive)

    def extreme_points(self, position=None, rotation=None):
        """Points on the surface of this part - the corners for a box, rim samples for a cylinder.

        Bounding-box corners of a cylinder or a capsule are not part of the shape, so tests that
        reason about the silhouette have to use these instead.
        """
        position = self.position if position is None else position
        rotation = self.rotation if rotation is None else rotation
        sx, sy, sz = self.scale
        points = []
        if self.primitive == "Cube":
            for ax in (-1.0, 1.0):
                for ay in (-1.0, 1.0):
                    for az in (-1.0, 1.0):
                        points.append(vadd(position, q_rotate(rotation, (0.5 * sx * ax, 0.5 * sy * ay, 0.5 * sz * az))))
            return points
        if self.primitive == "Sphere":
            for axis in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)):
                points.append(vadd(position, q_rotate(rotation, (0.5 * sx * axis[0],
                                                                0.5 * sy * axis[1],
                                                                0.5 * sz * axis[2]))))
            return points
        if self.primitive == "Cylinder":
            radius = 0.5 * max(sx, sz)
            half = UNITY_CYLINDER_HEIGHT * sy * 0.5
            for sign in (-1.0, 1.0):
                for k in range(8):
                    angle = math.pi * k / 4.0
                    points.append(vadd(position, q_rotate(rotation, (radius * math.cos(angle),
                                                                    half * sign,
                                                                    radius * math.sin(angle)))))
            return points
        if self.primitive == "Capsule":
            radius = 0.5 * max(sx, sz)
            half = UNITY_CAPSULE_HEIGHT * sy * 0.5
            reach = max(0.0, half - radius)
            for sign in (-1.0, 1.0):
                cap = vadd(position, q_rotate(rotation, (0.0, reach * sign, 0.0)))
                for axis in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)):
                    points.append((cap[0] + radius * axis[0], cap[1] + radius * axis[1],
                                   cap[2] + radius * axis[2]))
            return points
        raise ValueError("unsupported primitive " + self.primitive)

    def contains(self, point, position=None, rotation=None):
        """Exact inside/outside test for this part's real primitive shape."""
        position = self.position if position is None else position
        rotation = self.rotation if rotation is None else rotation
        sx, sy, sz = self.scale
        delta = vsub(point, position)
        inverse = q_conj(rotation)
        local = q_rotate(inverse, delta)
        if self.primitive == "Cube":
            return (abs(local[0]) <= 0.5 * sx + 1e-9 and abs(local[1]) <= 0.5 * sy + 1e-9 and
                    abs(local[2]) <= 0.5 * sz + 1e-9)
        if self.primitive == "Sphere":
            return ((local[0] / (0.5 * sx)) ** 2 + (local[1] / (0.5 * sy)) ** 2 +
                    (local[2] / (0.5 * sz)) ** 2) <= 1.0 + 1e-9
        if self.primitive == "Cylinder":
            radius = 0.5 * max(sx, sz)
            half = UNITY_CYLINDER_HEIGHT * sy * 0.5
            return (abs(local[1]) <= half + 1e-9 and
                    local[0] ** 2 + local[2] ** 2 <= radius ** 2 + 1e-12)
        if self.primitive == "Capsule":
            radius = 0.5 * max(sx, sz)
            reach = max(0.0, UNITY_CAPSULE_HEIGHT * sy * 0.5 - radius)
            axial = max(-reach, min(reach, local[1]))
            radial = math.sqrt(local[0] ** 2 + (local[1] - axial) ** 2 + local[2] ** 2)
            return radial <= radius + 1e-9
        raise ValueError("unsupported primitive " + self.primitive)

    def lowest_y(self, position=None, rotation=None):
        """Exact lowest world height of this part."""
        position = self.position if position is None else position
        rotation = self.rotation if rotation is None else rotation
        sx, sy, sz = self.scale
        down = (0.0, -1.0, 0.0)
        if self.primitive in ("Cube",):
            axes = (q_rotate(rotation, (1.0, 0.0, 0.0)),
                    q_rotate(rotation, (0.0, 1.0, 0.0)),
                    q_rotate(rotation, (0.0, 0.0, 1.0)))
            half = (0.5 * sx, 0.5 * sy, 0.5 * sz)
            return position[1] - sum(abs(vdot(axes[i], down)) * half[i] for i in range(3))
        if self.primitive == "Sphere":
            return position[1] - 0.5 * min(sx, sy, sz)
        if self.primitive == "Cylinder":
            axis = q_rotate(rotation, (0.0, 1.0, 0.0))
            radius = 0.5 * max(sx, sz)
            half = UNITY_CYLINDER_HEIGHT * sy * 0.5
            along = abs(vdot(axis, down))
            radial = math.sqrt(max(0.0, 1.0 - along * along))
            return position[1] - half * along - radius * radial
        if self.primitive == "Capsule":
            axis = q_rotate(rotation, (0.0, 1.0, 0.0))
            radius = 0.5 * max(sx, sz)
            reach = max(0.0, UNITY_CAPSULE_HEIGHT * sy * 0.5 - radius)
            a = vsub(position, vscale(axis, reach))
            b = vadd(position, vscale(axis, reach))
            return min(a[1], b[1]) - radius
        raise ValueError("unsupported primitive " + self.primitive)

    def volume(self):
        """Returns the collision volume used by the separation tests, in rig space.

        Capsules are exact (segment + radius). Boxes are exact oriented boxes. Spheres and the
        neck cylinder are approximated by the inscribed sphere / a capsule with round caps, which
        is conservative for the "does the seam stay closed" question this model answers.
        """
        return moved_volume(self, self.position, self.rotation)


def moved_volume(part, position, rotation):
    sx, sy, sz = part.scale
    if part.primitive == "Cube":
        return ("box", position, (0.5 * sx, 0.5 * sy, 0.5 * sz), rotation)
    if part.primitive == "Sphere":
        return ("sphere", position, 0.5 * min(sx, sy, sz))
    if part.primitive == "Cylinder":
        radius = 0.5 * max(sx, sz)
        height = UNITY_CYLINDER_HEIGHT * sy
        axis = q_rotate(rotation, (0.0, 1.0, 0.0))
        half = height * 0.5
        if half <= radius:
            return ("sphere", position, radius)
        reach = half - radius
        return ("capsule", vsub(position, vscale(axis, reach)), vadd(position, vscale(axis, reach)), radius)
    if part.primitive == "Capsule":
        radius = 0.5 * max(sx, sz)
        height = UNITY_CAPSULE_HEIGHT * sy
        axis = q_rotate(rotation, (0.0, 1.0, 0.0))
        half = height * 0.5
        if half <= radius:
            return ("sphere", position, radius)
        reach = half - radius
        return ("capsule", vsub(position, vscale(axis, reach)), vadd(position, vscale(axis, reach)), radius)
    raise ValueError("unsupported primitive " + part.primitive)


class Rig(object):
    """The built rig: bones, bound parts and forward kinematics."""

    def __init__(self, apply_fits=True, apply_splits=True):
        self.authored = parse_hero_parts()
        self.joints = parse_joint_table()
        self.bind_table = parse_bind_table()
        self.fit_table = parse_fit_table()
        self.split_table = parse_split_table()
        self.problems = []

        self.parts = [Part(p["name"], p["primitive"], p["position"], p["scale"], p["rotation"], p["role"])
                      for p in self.authored]
        self.by_name = {part.name: part for part in self.parts}
        self.split_count = 0

        if apply_fits:
            self.apply_fits()
        if apply_splits:
            self.apply_splits()

        self.joint_by_name = {j["bone"]: j for j in self.joints}
        self.joint_by_enum = {j["joint"]: j for j in self.joints}
        self.children = {j["joint"]: [] for j in self.joints}
        for joint in self.joints:
            if joint["parent"] != "None":
                self.children.setdefault(joint["parent"], []).append(joint["joint"])

        self.local_positions = {}
        for joint in self.joints:
            parent_position = (0.0, 0.0, 0.0)
            if joint["parent"] != "None":
                parent_position = self.joint_by_enum[joint["parent"]]["position"]
            self.local_positions[joint["joint"]] = vsub(joint["position"], parent_position)

        self.unbound = []
        self.bind()

    # -- build steps ------------------------------------------------------
    def apply_fits(self):
        for fit in self.fit_table:
            part = self.by_name.get(fit["name"])
            if part is None:
                self.problems.append("fit target '%s' is not a hero part" % fit["name"])
                continue
            part.position = vadd(part.position, fit["position"])
            part.scale = vadd(part.scale, fit["scale"])

    def apply_splits(self):
        for split in self.split_table:
            original = self.by_name.get(split["name"])
            if original is None:
                self.problems.append("split target '%s' is not a hero part" % split["name"])
                continue
            height = original.scale[1]
            if height <= 0.0:
                self.problems.append("split target '%s' has no height" % split["name"])
                continue
            upper_height = height * min(1.0, max(0.0, split["fraction"]))
            lower_height = height - upper_height
            up = q_rotate(original.rotation, (0.0, 1.0, 0.0))
            centre = original.position

            upper = original
            upper.name = split["upper"]
            upper.joint_hint = split["upper_joint"]
            upper.scale = (original.scale[0], upper_height, original.scale[2])
            upper.position = vadd(centre, vscale(up, height * 0.5 - upper_height * 0.5))

            lower = Part(split["lower"], original.primitive,
                         vsub(centre, vscale(up, height * 0.5 - lower_height * 0.5)),
                         (original.scale[0], lower_height, original.scale[2]),
                         original.rotation, original.role)
            lower.joint_hint = split["lower_joint"]

            del self.by_name[split["name"]]
            self.parts.append(lower)
            self.by_name[upper.name] = upper
            self.by_name[lower.name] = lower
            self.split_count += 1

    def bind(self):
        bound = set()
        for name, joint in self.bind_table:
            part = self.by_name.get(name)
            if part is None:
                self.problems.append("bind target '%s' is not a hero part" % name)
                continue
            if joint not in self.joint_by_enum:
                self.problems.append("bind target '%s' wants the unknown bone %s" % (name, joint))
                continue
            part.joint = joint
            bound.add(name)
        for part in self.parts:
            if part.name not in bound:
                self.unbound.append(part.name)

        # Rigid bind: the part keeps its authored transform, expressed in bone space.
        world_positions, world_rotations = self.forward_kinematics({})
        for part in self.parts:
            if part.joint is None:
                continue
            bone_rotation = world_rotations[part.joint]
            bone_position = world_positions[part.joint]
            inverse = q_conj(bone_rotation)
            part.local_rotation = q_mul(inverse, part.rotation)
            part.local_position = q_rotate(inverse, vsub(part.position, bone_position))

    # -- forward kinematics ----------------------------------------------
    def forward_kinematics(self, local_rotations):
        """Returns (positions, rotations) per joint in rig space for the given local rotations."""
        positions = {}
        rotations = {}
        for joint in self.joints:
            name = joint["joint"]
            rotation = local_rotations.get(name, q_identity())
            if joint["parent"] == "None":
                positions[name] = self.local_positions[name]
                rotations[name] = rotation
            else:
                parent = joint["parent"]
                parent_rotation = rotations[parent]
                positions[name] = vadd(positions[parent], q_rotate(parent_rotation, self.local_positions[name]))
                rotations[name] = q_mul(parent_rotation, rotation)
        return positions, rotations

    def part_transforms(self, local_rotations):
        """Returns {part name: (position, rotation)} for a pose, using the rigid bind."""
        positions, rotations = self.forward_kinematics(local_rotations)
        transforms = {}
        for part in self.parts:
            if part.joint is None:
                transforms[part.name] = (part.position, part.rotation)
                continue
            bone_rotation = rotations[part.joint]
            bone_position = positions[part.joint]
            transforms[part.name] = (
                vadd(bone_position, q_rotate(bone_rotation, part.local_position)),
                q_mul(bone_rotation, part.local_rotation))
        return transforms

    def rebind(self, overrides):
        """Re-binds parts to different bones and recomputes the rigid bind.

        Used by the simulator to quantify what the shipped binding fixes: passing
        ``{"Cape": "Chest"}`` for a rig built with ``apply_splits=False`` reproduces the naive
        "one rigid cape sheet on the chest" rig, and ``{"Abdomen": None}`` reproduces a part that
        no bone claims.
        """
        for name, joint in overrides.items():
            if name not in self.by_name:
                self.problems.append("rebind target '%s' is not a hero part" % name)
                continue
            self.by_name[name].joint = joint

        self.unbound = [part.name for part in self.parts if part.joint is None]
        positions, rotations = self.forward_kinematics({})
        for part in self.parts:
            if part.joint is None:
                part.local_position = None
                part.local_rotation = None
                continue
            bone_rotation = rotations[part.joint]
            bone_position = positions[part.joint]
            inverse = q_conj(bone_rotation)
            part.local_rotation = q_mul(inverse, part.rotation)
            part.local_position = q_rotate(inverse, vsub(part.position, bone_position))

    def descendants(self, joint):
        """Every joint at or below the given joint."""
        found = []
        stack = [joint]
        while stack:
            current = stack.pop()
            found.append(current)
            stack.extend(self.children.get(current, []))
        return set(found)

    def parts_of(self, joints):
        return [part for part in self.parts if part.joint in joints]

    def joint_path(self, joint):
        path = []
        current = joint
        while current != "None":
            path.append(current)
            current = self.joint_by_enum[current]["parent"]
        return list(reversed(path))


def split_part(rig, name, upper_name, upper_joint, lower_name, lower_joint, fraction):
    """Cuts one part into two pieces along its own local Y and binds them to different bones.

    This is the rig change the simulator *rejected*: the cape sheet spans Hips, Spine and Chest, and
    cutting it in two lets the chest bend the seam open. ``simulate_hero_rig.py`` keeps the function
    so the decision stays a measurement instead of an opinion. The shipped rig does not split
    anything - see the Cape row of HeroRig.BindTable.
    """
    original = rig.by_name[name]
    height = original.scale[1]
    upper_height = height * fraction
    lower_height = height - upper_height
    up = q_rotate(original.rotation, (0.0, 1.0, 0.0))
    centre = original.position

    upper = original
    upper.name = upper_name
    upper.scale = (original.scale[0], upper_height, original.scale[2])
    upper.position = vadd(centre, vscale(up, height * 0.5 - upper_height * 0.5))
    upper.joint = upper_joint

    lower = Part(lower_name, original.primitive,
                 vsub(centre, vscale(up, height * 0.5 - lower_height * 0.5)),
                 (original.scale[0], lower_height, original.scale[2]),
                 original.rotation, original.role)
    lower.joint = lower_joint

    del rig.by_name[name]
    rig.parts.append(lower)
    rig.by_name[upper.name] = upper
    rig.by_name[lower.name] = lower
    rig.rebind({upper_name: upper_joint, lower_name: lower_joint})


# ---------------------------------------------------------------------------
# Volume separation (negative = overlapping, positive = a gap)
# ---------------------------------------------------------------------------
def _box_axes(rotation):
    return (q_rotate(rotation, (1.0, 0.0, 0.0)),
            q_rotate(rotation, (0.0, 1.0, 0.0)),
            q_rotate(rotation, (0.0, 0.0, 1.0)))


def _box_support(box, axis):
    _, centre, half, rotation = box
    axes = _box_axes(rotation)
    total = 0.0
    for i in range(3):
        total += abs(vdot(axes[i], axis)) * half[i]
    return total


def distance_point_box(point, box):
    _, centre, half, rotation = box
    axes = _box_axes(rotation)
    delta = vsub(point, centre)
    local = (vdot(delta, axes[0]), vdot(delta, axes[1]), vdot(delta, axes[2]))
    clamped = tuple(max(-half[i], min(half[i], local[i])) for i in range(3))
    offset = (local[0] - clamped[0], local[1] - clamped[1], local[2] - clamped[2])
    return math.sqrt(offset[0] ** 2 + offset[1] ** 2 + offset[2] ** 2)


def _closest_point_on_segment(point, a, b):
    ab = vsub(b, a)
    length_squared = vdot(ab, ab)
    if length_squared < 1e-12:
        return a
    t = max(0.0, min(1.0, vdot(vsub(point, a), ab) / length_squared))
    return vadd(a, vscale(ab, t))


def distance_point_capsule(point, capsule):
    _, a, b, radius = capsule
    return vlength(vsub(point, _closest_point_on_segment(point, a, b))) - radius


def distance_point_volume(point, volume):
    kind = volume[0]
    if kind == "box":
        return distance_point_box(point, volume)
    if kind == "sphere":
        return vlength(vsub(point, volume[1])) - volume[2]
    if kind == "capsule":
        return distance_point_capsule(point, volume)
    raise ValueError("unknown volume " + kind)


def distance_segment_box(a, b, box):
    """Closest distance between a segment and an oriented box.

    Alternating projection alone stalls when the segment is deep inside the box (the closest point
    on the box to an interior point is on its surface, so the iteration walks away from zero), which
    would report a phantom gap at a joint that is in fact closed. The segment is therefore sampled
    first: an interior sample means the answer is zero, and the best outside sample seeds the
    projection.
    """
    samples = 17
    best_point = a
    best_distance = float("inf")
    for i in range(samples + 1):
        t = float(i) / samples
        point = vadd(a, vscale(vsub(b, a), t))
        distance = distance_point_box(point, box)
        if distance <= 1e-9:
            return 0.0
        if distance < best_distance:
            best_distance = distance
            best_point = point

    pa = _closest_point_on_segment(best_point, a, b)
    pb = best_point
    best = best_distance
    for _ in range(24):
        # Closest point on the box to pb, then closest point on the segment to that.
        _, centre, half, rotation = box
        axes = _box_axes(rotation)
        delta = vsub(pb, centre)
        local = (vdot(delta, axes[0]), vdot(delta, axes[1]), vdot(delta, axes[2]))
        clamped = tuple(max(-half[i], min(half[i], local[i])) for i in range(3))
        pa = vadd(centre, vadd(vscale(axes[0], clamped[0]),
                               vadd(vscale(axes[1], clamped[1]), vscale(axes[2], clamped[2]))))
        pb = _closest_point_on_segment(pa, a, b)
        best = min(best, vlength(vsub(pa, pb)))
        if best < 1e-9:
            break
    return best


def separation(volume_a, volume_b):
    """Gap between two volumes: > 0 means they no longer touch, < 0 means they interpenetrate."""
    ka, kb = volume_a[0], volume_b[0]

    if ka == "box" and kb == "box":
        return _separation_box_box(volume_a, volume_b)

    if ka == "capsule" and kb == "capsule":
        _, a0, a1, ra = volume_a
        _, b0, b1, rb = volume_b
        return _segment_segment_distance(a0, a1, b0, b1) - ra - rb

    if ka == "capsule" and kb == "box":
        _, a0, a1, ra = volume_a
        return distance_segment_box(a0, a1, volume_b) - ra

    if kb == "capsule" and ka == "box":
        _, b0, b1, rb = volume_b
        return distance_segment_box(b0, b1, volume_a) - rb

    # Sphere against anything, or sphere against sphere.
    if ka == "sphere":
        return distance_point_volume(volume_a[1], volume_b) - volume_a[2]
    if kb == "sphere":
        return distance_point_volume(volume_b[1], volume_a) - volume_b[2]

    raise ValueError("unsupported volume pair %s/%s" % (ka, kb))


def _segment_segment_distance(p1, q1, p2, q2):
    d1 = vsub(q1, p1)
    d2 = vsub(q2, p2)
    r = vsub(p1, p2)
    a = vdot(d1, d1)
    e = vdot(d2, d2)
    f = vdot(d2, r)
    if a <= 1e-12 and e <= 1e-12:
        return vlength(r)
    if a <= 1e-12:
        s = 0.0
        t = max(0.0, min(1.0, f / e))
    else:
        c = vdot(d1, r)
        if e <= 1e-12:
            t = 0.0
            s = max(0.0, min(1.0, -c / a))
        else:
            b = vdot(d1, d2)
            denom = a * e - b * b
            if denom > 1e-12:
                s = max(0.0, min(1.0, (b * f - c * e) / denom))
            else:
                s = 0.0
            t = (b * s + f) / e
            if t < 0.0:
                t = 0.0
                s = max(0.0, min(1.0, -c / a))
            elif t > 1.0:
                t = 1.0
                s = max(0.0, min(1.0, (b - c) / a))
    c1 = vadd(p1, vscale(d1, s))
    c2 = vadd(p2, vscale(d2, t))
    return vlength(vsub(c1, c2))


def _separation_box_box(box_a, box_b):
    """Signed separation of two oriented boxes using the separating axis theorem."""
    ca, ha, ra = box_a[1], box_a[2], box_a[3]
    cb, hb, rb = box_b[1], box_b[2], box_b[3]
    axes_a = _box_axes(ra)
    axes_b = _box_axes(rb)
    translation = vsub(cb, ca)

    candidates = list(axes_a) + list(axes_b)
    for i in range(3):
        for j in range(3):
            candidates.append(vcross(axes_a[i], axes_b[j]))

    # SAT: a positive gap on any axis means the boxes are apart. The widest gap is the (lower bound
    # of the) separation; when every axis is negative the largest value is the shallowest
    # penetration, i.e. the distance the two boxes would have to travel to come apart.
    widest = float("-inf")
    for axis in candidates:
        if vdot(axis, axis) < 1e-12:
            continue
        axis = vnorm(axis)
        reach_a = _box_support(box_a, axis)
        reach_b = _box_support(box_b, axis)
        distance = abs(vdot(translation, axis)) - reach_a - reach_b
        widest = max(widest, distance)
    return widest
