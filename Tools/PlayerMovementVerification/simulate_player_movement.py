#!/usr/bin/env python3
"""Headless verification harness for MobileGame.Player.ThirdPersonPlayerController.

The Unity Editor is not available in this environment, so this harness re-implements the
controller's Update() loop statement by statement (see Assets/Scripts/Player/
ThirdPersonPlayerController.cs) together with a simplified model of the Unity
CharacterController semantics used by the capsule (ground probe, slope following, ground snap,
horizontal blocking). The arena geometry is read straight out of
Assets/Scenes/CombatTestScene.unity, so the slopes, platforms and walls under test are the real
ones.

Every scenario below corresponds to one check in the in-Editor suite
(Assets/Scripts/Player/PlayerControllerTest.cs), so the numbers can be compared directly.

Run:  python3 Tools/PlayerMovementVerification/simulate_player_movement.py
"""

import math
import os
import re
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SCENE_PATH = os.path.join(REPO_ROOT, "Assets", "Scenes", "CombatTestScene.unity")

TOLERANCE = 0.02
EPSILON = 1e-9


# ---------------------------------------------------------------------------
# Minimal vector / quaternion helpers (Unity semantics)
# ---------------------------------------------------------------------------

class Vec3(object):
    __slots__ = ("x", "y", "z")

    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x = float(x)
        self.y = float(y)
        self.z = float(z)

    def __add__(self, o):
        return Vec3(self.x + o.x, self.y + o.y, self.z + o.z)

    def __sub__(self, o):
        return Vec3(self.x - o.x, self.y - o.y, self.z - o.z)

    def __mul__(self, s):
        return Vec3(self.x * s, self.y * s, self.z * s)

    __rmul__ = __mul__

    def dot(self, o):
        return self.x * o.x + self.y * o.y + self.z * o.z

    def cross(self, o):
        return Vec3(self.y * o.z - self.z * o.y,
                    self.z * o.x - self.x * o.z,
                    self.x * o.y - self.y * o.x)

    @property
    def magnitude(self):
        return math.sqrt(self.dot(self))

    @property
    def sqr_magnitude(self):
        return self.dot(self)

    def normalized(self):
        m = self.magnitude
        if m < EPSILON:
            return Vec3(0, 0, 0)
        return Vec3(self.x / m, self.y / m, self.z / m)

    def project_on_plane(self, normal):
        return self - normal * self.dot(normal)

    def distance(self, o):
        return (self - o).magnitude

    def __repr__(self):
        return "Vec3(%.4f, %.4f, %.4f)" % (self.x, self.y, self.z)


UP = Vec3(0, 1, 0)
ZERO = Vec3(0, 0, 0)


# ---------------------------------------------------------------------------
# Mirror of Assets/Scripts/Core/PhysicsQueryGuard.cs
# ---------------------------------------------------------------------------

DIRECTION_TOLERANCE = 0.001   # Unity: "Assertion failed on expression: 'IsNormalized(dir, 0.001f)'"


def usable_motion(motion, min_distance=0.0):
    """True when a motion vector may be handed to CharacterController.Move.

    Move normalizes its motion internally; a zero-length vector (or one shorter than the
    controller's minMoveDistance, which it ignores anyway) makes it assert IsNormalized(dir).
    """
    threshold = max(abs(min_distance), DIRECTION_TOLERANCE)
    return motion.sqr_magnitude > threshold * threshold


def usable_direction(direction, tolerance=DIRECTION_TOLERANCE):
    """True when a vector is a legal physics query direction (unit length within the tolerance)."""
    return abs(direction.magnitude - 1.0) <= tolerance


def angle_between(a, b):
    """Unity's Vector3.Angle (degrees, 0..180)."""
    denom = a.magnitude * b.magnitude
    if denom < EPSILON:
        return 0.0
    c = max(-1.0, min(1.0, a.dot(b) / denom))
    return math.degrees(math.acos(c))


def euler_to_matrix(euler_deg):
    """Unity's Matrix4x4 rotation for Euler(x, y, z) in degrees: R = Rz * Ry * Rx."""
    x, y, z = (math.radians(v) for v in euler_deg)
    cx, sx = math.cos(x), math.sin(x)
    cy, sy = math.cos(y), math.sin(y)
    cz, sz = math.cos(z), math.sin(z)

    rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]
    ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]
    rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]

    def mul(a, b):
        return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

    return mul(rz, mul(ry, rx))


class Rotation(object):
    def __init__(self, euler_deg):
        self.matrix = euler_to_matrix(euler_deg)

    def apply(self, v):
        m = self.matrix
        return Vec3(
            m[0][0] * v.x + m[0][1] * v.y + m[0][2] * v.z,
            m[1][0] * v.x + m[1][1] * v.y + m[1][2] * v.z,
            m[2][0] * v.x + m[2][1] * v.y + m[2][2] * v.z,
        )

    def apply_inverse(self, v):
        m = self.matrix
        return Vec3(
            m[0][0] * v.x + m[1][0] * v.y + m[2][0] * v.z,
            m[0][1] * v.x + m[1][1] * v.y + m[2][1] * v.z,
            m[0][2] * v.x + m[1][2] * v.y + m[2][2] * v.z,
        )


# ---------------------------------------------------------------------------
# Arena geometry, parsed from the scene file
# ---------------------------------------------------------------------------

class Box(object):
    """An axis-aligned-in-local-space box collider, i.e. Unity's BoxCollider on a scaled cube."""

    def __init__(self, name, center, euler_deg, scale, layer):
        self.name = name
        self.center = center
        self.rotation = Rotation(euler_deg)
        self.half = Vec3(scale.x / 2.0, scale.y / 2.0, scale.z / 2.0)
        self.layer = layer

    def capsule_overlap(self, center, radius, segment_half_height):
        """Overlap of a vertical capsule (swept sphere segment) with this box.

        The segment runs between the capsule's two sphere centres, exactly like the
        CharacterController's collision volume. Returns (world_normal, depth) or None.
        """
        lower = self.rotation.apply_inverse(
            Vec3(center.x, center.y - segment_half_height, center.z) - self.center)
        upper = self.rotation.apply_inverse(
            Vec3(center.x, center.y + segment_half_height, center.z) - self.center)

        delta = upper - lower
        length_sqr = delta.sqr_magnitude
        if length_sqr < 1e-12:
            closest = lower
        else:
            t = max(0.0, min(1.0, -lower.dot(delta) / length_sqr))
            closest = lower + delta * t

        # Closest point of the box to the segment's closest point.
        box_point = Vec3(
            max(-self.half.x, min(self.half.x, closest.x)),
            max(-self.half.y, min(self.half.y, closest.y)),
            max(-self.half.z, min(self.half.z, closest.z)),
        )

        offset = closest - box_point
        distance = offset.magnitude
        if distance >= radius:
            return None

        if distance > 1e-9:
            local_normal = offset * (1.0 / distance)
            depth = radius - distance
        else:
            # Segment inside the box: push out along the axis of least penetration.
            penetrations = (
                (self.half.x - abs(closest.x), Vec3(math.copysign(1.0, closest.x) if closest.x else 1.0, 0.0, 0.0)),
                (self.half.y - abs(closest.y), Vec3(0.0, math.copysign(1.0, closest.y) if closest.y else 1.0, 0.0)),
                (self.half.z - abs(closest.z), Vec3(0.0, 0.0, math.copysign(1.0, closest.z) if closest.z else 1.0)),
            )
            depth, local_normal = min(penetrations, key=lambda item: item[0])

        return self.rotation.apply(local_normal), depth


def parse_scene(path):
    """Reads every BoxCollider prop out of a Unity scene file.

    Only objects that actually own a BoxCollider are returned: the scene also contains empty
    grouping transforms (Walls, Architecture, HeightVariation) that must not become geometry.
    """
    src = open(path).read()

    names = {}
    components = {}
    for block in re.split(r"\n--- !u!", src):
        m = re.match(r"1 &(\d+)\nGameObject:\n(.*)", block, re.S)
        if not m:
            continue
        nm = re.search(r"m_Name: (.*)", m.group(2))
        layer = re.search(r"m_Layer: (\d+)", m.group(2))
        comps = [c for c in re.findall(r"- component: \{fileID: (\d+)\}", m.group(2))]
        names[m.group(1)] = (nm.group(1).strip() if nm else "", int(layer.group(1)) if layer else 0)
        components[m.group(1)] = comps

    # fileID -> component class id, so BoxColliders (class 65) can be identified.
    colliders = set()
    for m in re.finditer(r"^--- !u!65 &(\d+)\nBoxCollider:", src, re.M):
        colliders.add(m.group(1))

    boxes = []
    for block in re.split(r"\n--- !u!", src):
        m = re.match(r"4 &(\d+)\nTransform:\n(.*)", block, re.S)
        if not m:
            continue
        go = re.search(r"m_GameObject: \{fileID: (\d+)\}", m.group(2))
        if not go or go.group(1) not in names:
            continue
        if not any(c in colliders for c in components.get(go.group(1), [])):
            continue
        name, layer = names[go.group(1)]

        pos = re.search(r"m_LocalPosition: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)\}", m.group(2))
        scale = re.search(r"m_LocalScale: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)\}", m.group(2))
        euler = re.search(r"m_LocalEulerAnglesHint: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)\}", m.group(2))
        rot = re.search(r"m_LocalRotation: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+), w: ([-\d.e]+)\}", m.group(2))
        if not (pos and scale):
            continue

        # Prefer the euler hint; fall back to the quaternion's x/z components (arena has no X/Y pitch).
        if euler:
            angles = tuple(float(v) for v in euler.groups())
        elif rot:
            qx, qy, qz, qw = (float(v) for v in rot.groups())
            pitch = math.degrees(math.atan2(2 * (qw * qx + qy * qz), 1 - 2 * (qx * qx + qy * qy)))
            yaw = math.degrees(math.atan2(2 * (qw * qy + qz * qx), 1 - 2 * (qy * qy + qz * qz)))
            roll = math.degrees(math.asin(max(-1.0, min(1.0, 2 * (qw * qz - qx * qy)))))
            angles = (pitch, yaw, roll)
        else:
            angles = (0.0, 0.0, 0.0)

        boxes.append(Box(
            name,
            Vec3(*(float(v) for v in pos.groups())),
            angles,
            Vec3(*(float(v) for v in scale.groups())),
            layer,
        ))

    return boxes


# ---------------------------------------------------------------------------
# Simplified CharacterController world
# ---------------------------------------------------------------------------

class World(object):
    """Models the CharacterController behaviour the controller relies on.

    Geometry is queried analytically: every arena prop is an oriented box, so the height of its
    top face at a given (x, z) is exact. The CharacterController's skin width is honoured both in
    the blocking test and in the resting height, and its step offset limits how far the capsule
    can climb in a single frame.
    """

    def __init__(self, boxes, ground_layers, max_slope_angle, skin_width=0.08, step_offset=0.3):
        self.boxes = boxes
        self.ground_layers = ground_layers
        self.max_slope_angle = max_slope_angle
        self.skin_width = skin_width
        self.step_offset = step_offset

    # -- surfaces ------------------------------------------------------------
    def top_surface(self, x, z):
        """Highest walkable top face at (x, z) as (height, normal), or None."""
        best = None
        for box in self.boxes:
            if not (self.ground_layers & (1 << box.layer)):
                continue

            local = box.rotation.apply_inverse(Vec3(x, 0.0, z) - box.center)
            if abs(local.x) > box.half.x or abs(local.z) > box.half.z:
                continue

            normal = box.rotation.apply(UP)
            if abs(normal.y) < 1e-6:
                continue  # vertical face, not a floor

            face_center = box.center + box.rotation.apply(Vec3(0.0, box.half.y, 0.0))
            height = face_center.y - (normal.x * (x - face_center.x) + normal.z * (z - face_center.z)) / normal.y

            if best is None or height > best[0]:
                best = (height, normal)
        return best

    def floor_at(self, x, z, y):
        """Highest walkable surface at (x, z) that is at or below height y."""
        surface = self.top_surface(x, z)
        if surface is None or surface[0] > y + 1e-6:
            return None
        return surface

    # -- ground probe (mirrors ProbeGround / Physics.SphereCast) --------------
    def probe_ground(self, position, radius, distance, segment_half_height):
        """Ground probe at the capsule footprint.

        Mirrors the C# sphere cast: the sphere sits at the capsule's lower sphere centre and sweeps
        down by groundCheckDistance, so a surface is reported while the clearance between the sphere
        and that surface is within [0, groundCheckDistance].
        """
        surface = self.top_surface(position.x, position.z)
        if surface is None:
            return None

        height, normal = surface
        if angle_between(normal, UP) > self.max_slope_angle + 0.5:
            return None

        # Perpendicular distance from the lower sphere centre to the surface plane.
        clearance = normal.y * ((position.y - segment_half_height) - height)
        if clearance < -1e-6 or clearance > radius + distance:
            return None

        return max(0.0, clearance - radius), normal

    # -- motion (mirrors CharacterController.Move) ---------------------------
    def move(self, position, motion, radius, height):
        # The CharacterController's collision volume is the capsule shrunk by the skin width; its
        # bottom sits skinWidth above the visual capsule bottom.
        collision_radius = max(0.01, radius - self.skin_width)
        collision_segment_half = height / 2.0 - radius
        collision_half_height = height / 2.0 - self.skin_width

        # The requested motion is the one that decides whether the controller may snap down onto a
        # surface; the per-contact projection below rewrites `motion`, so remember it first.
        descending = motion.y <= 0.0

        target = position + motion

        # 1. Depenetration: push the capsule out of anything it overlaps. This is what lets the
        #    capsule settle exactly on a surface instead of hovering a fraction above it.
        target = self._depenetrate(target, collision_radius, collision_segment_half)

        # 2. Collision response: slide the remaining motion along the contacted surface. Projecting
        #    onto a slope plane is what makes the capsule climb it; a wall stops the player.
        motion = target - position
        for _ in range(4):
            contact = self._contact(position + motion, collision_radius, collision_segment_half)
            if contact is None:
                break
            normal, _depth = contact
            into = motion.dot(normal)
            if into >= -1e-9:
                break
            motion = motion - normal * into
        target = position + motion

        # 3. Step limit: never climb more than the CharacterController's step offset in one frame.
        bottom_here = position.y - collision_half_height
        bottom_there = target.y - collision_half_height
        floor_here = self.floor_at(position.x, position.z, bottom_here + 1e-6)
        floor_there = self.floor_at(target.x, target.z, bottom_there + 1e-6)
        if floor_there is not None and floor_here is not None and \
                floor_there[0] - floor_here[0] > self.step_offset + 1e-6:
            target = Vec3(position.x, target.y, position.z)

        # 4. Vertical: stop at the surface when moving down, snap when following a slope. The capsule
        #    is placed tangent to the surface plane, so on a slope it rides higher than on flat
        #    ground.
        if descending:
            surface = self.top_surface(target.x, target.z)
            if surface is not None:
                height, normal = surface
                clearance = normal.y * ((target.y - collision_segment_half) - height)
                if clearance <= collision_radius + 1e-9:
                    target.y = height + collision_radius / normal.y + collision_segment_half

        return target

    def _depenetrate(self, point, radius, segment_half_height, iterations=4):
        for _ in range(iterations):
            contact = self._contact(point, radius, segment_half_height)
            if contact is None:
                break
            normal, depth = contact
            point = point + normal * (depth + 1e-6)
        return point

    def _contact(self, point, radius, segment_half_height):
        """Deepest capsule/box overlap as (world_normal, depth), or None."""
        best = None
        for box in self.boxes:
            overlap = box.capsule_overlap(point, radius, segment_half_height)
            if overlap is None:
                continue
            if best is None or overlap[1] > best[1]:
                best = overlap
        return best


# ---------------------------------------------------------------------------
# Mirror of ThirdPersonPlayerController.Update()
# ---------------------------------------------------------------------------

class PlayerConfig(object):
    def __init__(self, move_speed=1.25, acceleration=30.0, deceleration=45.0, rotation_speed=720.0,
                 min_speed_to_rotate=0.1, gravity=20.0, max_fall_speed=30.0, ground_stick_force=2.0,
                 ground_check_distance=0.15, max_slope_angle=45.0):
        self.move_speed = move_speed
        self.acceleration = acceleration
        self.deceleration = deceleration
        self.rotation_speed = rotation_speed
        self.min_speed_to_rotate = min_speed_to_rotate
        self.gravity = gravity
        self.max_fall_speed = max_fall_speed
        self.ground_stick_force = ground_stick_force
        self.ground_check_distance = ground_check_distance
        self.max_slope_angle = max_slope_angle


class Player(object):
    """Line-by-line mirror of the C# controller (see the script header for the mapping)."""

    def __init__(self, config, world, position, yaw, radius=0.5, height=2.0, skin_width=0.08,
                 min_move_distance=0.001):
        self.config = config
        self.world = world
        self.position = position
        self.yaw = yaw                      # transform.rotation.eulerAngles.y
        self.radius = radius
        self.height = height
        self.skin_width = skin_width
        self.min_move_distance = min_move_distance   # CharacterController.minMoveDistance

        self.planar_velocity = Vec3(0, 0, 0)
        self.vertical_velocity = 0.0

        # Mirrors ThirdPersonPlayerController.SkippedMotionFrames / the engine calls it makes.
        self.move_calls = 0
        self.skipped_motion_frames = 0

        # Collision capsule geometry (CharacterController shrinks the capsule by the skin width).
        self.probe_radius = max(0.01, radius - skin_width)
        self.segment_half_height = height / 2.0 - radius

        self.is_grounded = False
        self.was_grounded = False
        self.just_landed = False
        self.just_became_airborne = False
        self.ground_normal = UP
        self.ground_distance = 0.0
        self.last_landing_impact_speed = 0.0

    # -- public state (mirrors the C# properties) ----------------------------
    @property
    def speed(self):
        return self.planar_velocity.magnitude

    @property
    def slope_angle(self):
        return angle_between(self.ground_normal, UP)

    @property
    def forward(self):
        """transform.forward for a yaw-only rotation."""
        return Vec3(math.sin(math.radians(self.yaw)), 0.0, math.cos(math.radians(self.yaw)))

    # -- Update() ------------------------------------------------------------
    def update(self, delta_time, move_input, camera_forward, camera_right):
        if delta_time <= 0.0:
            return

        self.just_landed = False
        self.just_became_airborne = False

        # 1. Input -> camera-relative planar direction.
        move_direction = self._resolve_move_direction(move_input, camera_forward, camera_right)

        # 2. Acceleration / deceleration.
        self._update_planar_velocity(move_direction, delta_time)

        # 3. Rotation toward the movement direction.
        self._rotate_towards_movement(delta_time)

        # 4. Ground detection and gravity.
        self.was_grounded = self.is_grounded
        self.is_grounded = self._probe_ground()

        if self.is_grounded:
            if not self.was_grounded:
                self.just_landed = True
                self.last_landing_impact_speed = max(0.0, -self.vertical_velocity)
            # Re-armed from any fall and from every landing. It stays exactly zero only in the
            # state ResetMovementState() leaves behind, where the motion guard in step 5 skips the
            # engine call (a zero-length motion would assert IsNormalized(dir, 0.001f)).
            if self.vertical_velocity < 0.0:
                self.vertical_velocity = -abs(self.config.ground_stick_force)
        else:
            if self.was_grounded:
                self.just_became_airborne = True
            self.vertical_velocity = max(self.vertical_velocity - self.config.gravity * delta_time,
                                         -abs(self.config.max_fall_speed))

        # 5. Apply the motion.
        velocity = self.planar_velocity + UP * self.vertical_velocity
        motion = velocity * delta_time

        # PhysicsQueryGuard.IsUsableMotion: CharacterController.Move normalizes its motion, so a
        # zero-length vector (or one shorter than minMoveDistance, which Move ignores anyway) must
        # never reach the engine - it would assert "IsNormalized(dir, 0.001f)".
        if not usable_motion(motion, self.min_move_distance):
            self.skipped_motion_frames += 1
            return

        self.move_calls += 1
        self.position = self.world.move(self.position, motion, self.radius, self.height)

    # -- internals -----------------------------------------------------------
    def _resolve_move_direction(self, move_input, camera_forward, camera_right):
        if move_input[0] ** 2 + move_input[1] ** 2 < 0.0001:
            return Vec3(0, 0, 0)

        direction = camera_forward * move_input[1] + camera_right * move_input[0]
        if direction.sqr_magnitude > 1.0:
            direction = direction.normalized()
        return direction

    def _update_planar_velocity(self, move_direction, delta_time):
        target_velocity = move_direction * self.config.move_speed
        rate = self.config.acceleration if move_direction.sqr_magnitude > 0.0001 else self.config.deceleration
        rate = abs(rate)

        # Vector3.MoveTowards
        delta = target_velocity - self.planar_velocity
        distance = delta.magnitude
        step = rate * delta_time
        if distance <= step or distance < EPSILON:
            self.planar_velocity = target_velocity
        else:
            self.planar_velocity = self.planar_velocity + delta.normalized() * step

    def _rotate_towards_movement(self, delta_time):
        if self.planar_velocity.sqr_magnitude <= self.config.min_speed_to_rotate ** 2:
            return

        target_yaw = math.degrees(math.atan2(self.planar_velocity.x, self.planar_velocity.z))
        max_step = abs(self.config.rotation_speed) * delta_time
        diff = (target_yaw - self.yaw + 180.0) % 360.0 - 180.0
        if abs(diff) <= max_step:
            self.yaw = target_yaw
        else:
            self.yaw += math.copysign(max_step, diff)

    def _probe_ground(self):
        hit = self.world.probe_ground(self.position, self.probe_radius,
                                      max(0.01, self.config.ground_check_distance),
                                      self.segment_half_height)
        if hit is not None:
            self.ground_distance, self.ground_normal = hit[0], hit[1]
            return True

        # Safety net: the C# falls back to CharacterController.isGrounded. The harness models the
        # arena floor on a masked layer, so the sphere probe is authoritative here.
        self.ground_distance, self.ground_normal = 0.0, UP
        return False

    def reset_movement_state(self):
        self.planar_velocity = Vec3(0, 0, 0)
        self.vertical_velocity = 0.0


# ---------------------------------------------------------------------------
# Scenario runner
# ---------------------------------------------------------------------------

class CameraRig(object):
    """Planar forward/right of the third-person camera for a given yaw (pitch is projected away)."""

    def __init__(self, yaw=180.0):
        self.yaw = yaw

    @property
    def planar_forward(self):
        return Vec3(math.sin(math.radians(self.yaw)), 0.0, math.cos(math.radians(self.yaw)))

    @property
    def planar_right(self):
        return Vec3(math.cos(math.radians(self.yaw)), 0.0, -math.sin(math.radians(self.yaw)))


class Report(object):
    def __init__(self):
        self.passed = 0
        self.total = 0
        self.lines = []

    def check(self, condition, pass_message, fail_message):
        self.total += 1
        if condition:
            self.passed += 1
            self.lines.append("  PASS  " + pass_message)
        else:
            self.lines.append("  FAIL  " + fail_message)
        return condition


def advance(player, camera, move_input, frames, delta_time=1.0 / 60.0):
    for _ in range(frames):
        player.update(delta_time, move_input, camera.planar_forward, camera.planar_right)


def advance_for(player, camera, move_input, seconds, delta_time=1.0 / 60.0):
    advance(player, camera, move_input, int(round(seconds / delta_time)), delta_time)


def input_for_world_direction(world_direction, camera):
    return (world_direction.dot(camera.planar_right), world_direction.dot(camera.planar_forward))


def settle(player, camera, frames=15):
    advance(player, camera, (0.0, 0.0), frames)


def expected_travel_distance(config, duration):
    time_to_cruise = config.move_speed / config.acceleration
    if duration <= time_to_cruise:
        return 0.5 * config.acceleration * duration * duration
    return (0.5 * config.acceleration * time_to_cruise * time_to_cruise +
            config.move_speed * (duration - time_to_cruise))


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

def test_static_configuration(report, config, world):
    ok = config.move_speed > 0 and config.acceleration > 0 and config.deceleration > 0 \
        and config.rotation_speed > 0 and config.gravity > 0
    report.check(ok,
                 "Inspector values valid: speed=%.1f m/s, accel=%.1f m/s^2, decel=%.1f m/s^2, "
                 "rotation=%.0f deg/s, gravity=%.1f m/s^2" % (config.move_speed, config.acceleration,
                                                              config.deceleration, config.rotation_speed,
                                                              config.gravity),
                 "movement speed / acceleration / deceleration / rotation speed / gravity must be > 0")

    has_ground_layer = bool(world.ground_layers & (1 << 17))
    report.check(has_ground_layer,
                 "Ground layers include the arena Ground layer (17)",
                 "ground layers do not include layer 17; the arena floor would not be detected")


def test_grounded_at_spawn(report, player, camera):
    settle(player, camera)
    rest_y = player.position.y
    report.check(player.is_grounded,
                 "Grounded at spawn, resting at y=%.3f" % rest_y,
                 "player reports airborne at spawn (y=%.3f)" % rest_y)

    settle(player, camera, 30)
    stable = abs(player.position.y - rest_y) <= TOLERANCE
    report.check(player.is_grounded and stable,
                 "Stable while idle for 30 frames (y=%.3f)" % player.position.y,
                 "player drifted or left the ground while idle (y=%.3f)" % player.position.y)


def test_idle_motion_integrity(report, player, camera):
    """Regression check for Unity's "Assertion failed on expression: 'IsNormalized(dir, 0.001f)'".

    Every teleport in the in-Editor suite calls ResetMovementState(), which zeroes both velocities.
    Idle on the ground the controller must either hand CharacterController.Move a usable motion
    (the ground stick has to re-arm from zero) or skip the engine call - it must never pass a
    zero-length vector, which Move normalizes to zero before Unity asserts on it.
    """
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera, 12)

    grounded_before = player.is_grounded
    move_calls_before = player.move_calls
    skipped_before = player.skipped_motion_frames

    zero_motion_frames = 0
    for _ in range(60):
        previous_skips = player.skipped_motion_frames
        player.update(1.0 / 60.0, (0.0, 0.0), camera.planar_forward, camera.planar_right)

        velocity = player.planar_velocity + UP * player.vertical_velocity
        if velocity.sqr_magnitude <= 1e-12 and player.skipped_motion_frames == previous_skips:
            zero_motion_frames += 1

    report.check(zero_motion_frames == 0,
                 "Idle motion: 60 idle frames after a movement reset never handed Move a zero-length vector "
                 "(%d engine call(s), %d skipped)" % (player.move_calls - move_calls_before,
                                                      player.skipped_motion_frames - skipped_before),
                 "Idle motion: %d frame(s) handed CharacterController.Move a zero-length vector; Unity asserts "
                 "IsNormalized(dir, 0.001f) on each of them" % zero_motion_frames)

    report.check(player.skipped_motion_frames > skipped_before,
                 "Idle motion: the degenerate zero-length motion (%d frame(s)) is detected and skipped instead of "
                 "being passed to the engine" % (player.skipped_motion_frames - skipped_before),
                 "Idle motion: the zero-length motion was never detected; it reached CharacterController.Move and "
                 "Unity asserted IsNormalized(dir, 0.001f)")


    report.check(grounded_before and player.is_grounded,
                 "Idle motion: grounded before and after the idle window (y=%.3f)" % player.position.y,
                 "Idle motion: player left the ground during the idle window (y=%.3f)" % player.position.y)


def test_directional_movement(report, player, camera, config, label, world_direction):
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    start = player.position
    move_input = input_for_world_direction(world_direction, camera)
    advance_for(player, camera, move_input, 0.6)

    displacement = player.position - start
    planar = Vec3(displacement.x, 0, displacement.z)
    distance = planar.magnitude
    expected = expected_travel_distance(config, 0.6)
    alignment = planar.normalized().dot(world_direction.normalized()) if distance > 0.01 else -1.0

    report.check(distance >= expected * 0.6,
                 "%s: travelled %.2fm in 0.60s (expected ~%.2fm)" % (label, distance, expected),
                 "%s: travelled only %.2fm, expected about %.2fm" % (label, distance, expected))
    report.check(alignment >= 0.95,
                 "%s: displacement aligned with %s (alignment=%.3f)" % (label, world_direction, alignment),
                 "%s: displacement %s does not match %s (alignment=%.3f)" % (label, planar, world_direction, alignment))

    settle(player, camera, 20)
    report.check(player.is_grounded,
                 "%s: still grounded on flat ground after moving" % label,
                 "%s: left the ground while walking on flat ground" % label)


def test_camera_relative(report, player, camera, config):
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    forward_before = camera.planar_forward
    camera.yaw += 90.0
    forward_after = camera.planar_forward

    start = player.position
    advance_for(player, camera, (0.0, 1.0), 0.5)  # "forward" stick input
    displacement = player.position - start
    planar = Vec3(displacement.x, 0, displacement.z)
    distance = planar.magnitude

    report.check(angle_between(forward_before, forward_after) > 45.0,
                 "Camera rotated %.0f deg for the relativity check" % angle_between(forward_before, forward_after),
                 "camera did not rotate enough to test relativity")

    alignment_new = planar.normalized().dot(forward_after) if distance > 0.01 else -1.0
    alignment_old = planar.normalized().dot(forward_before) if distance > 0.01 else -1.0

    report.check(distance >= expected_travel_distance(config, 0.5) * 0.6,
                 "Camera-relative: travelled %.2fm on forward input" % distance,
                 "Camera-relative: travelled only %.2fm" % distance)
    report.check(alignment_new >= 0.95,
                 "Camera-relative: forward input tracks the new camera forward (alignment=%.3f)" % alignment_new,
                 "Camera-relative: forward input moved along %s, expected %s (alignment=%.3f)"
                 % (planar.normalized(), forward_after, alignment_new))
    report.check(alignment_old <= 0.5,
                 "Camera-relative: movement no longer follows the previous heading (alignment=%.3f)" % alignment_old,
                 "Camera-relative: movement still follows the previous camera heading (alignment=%.3f)" % alignment_old)

    camera.yaw = 180.0


def test_acceleration(report, player, camera, config):
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    previous = player.speed
    monotonic = True
    elapsed = 0.0
    time_to_cruise = -1.0
    delta_time = 1.0 / 60.0

    while elapsed < 2.0:
        player.update(delta_time, (0.0, 1.0), camera.planar_forward, camera.planar_right)
        elapsed += delta_time
        if player.speed < previous - 0.001:
            monotonic = False
        previous = player.speed
        if time_to_cruise < 0 and player.speed >= config.move_speed * 0.95:
            time_to_cruise = elapsed

    expected_time = config.move_speed / config.acceleration
    report.check(monotonic,
                 "Acceleration: speed rises monotonically from rest",
                 "Acceleration: speed decreased while input was held")
    report.check(time_to_cruise > 0,
                 "Acceleration: reached cruise speed in %.3fs (theoretical %.3fs)" % (time_to_cruise, expected_time),
                 "Acceleration: never reached cruise speed (%.2f m/s)" % player.speed)
    report.check(time_to_cruise <= expected_time * 2 + 0.1,
                 "Acceleration: ramp time within tolerance (%.3fs vs %.3fs)" % (time_to_cruise, expected_time),
                 "Acceleration: reached cruise in %.3fs, expected about %.3fs" % (time_to_cruise, expected_time))
    report.check(abs(player.speed - config.move_speed) <= config.move_speed * 0.05,
                 "Acceleration: cruise speed %.3f m/s (target %.1f)" % (player.speed, config.move_speed),
                 "Acceleration: cruise speed %.3f m/s, expected %.1f" % (player.speed, config.move_speed))


def test_deceleration(report, player, camera, config):
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    advance_for(player, camera, (0.0, 1.0), 0.6)
    start_speed = player.speed
    start_position = player.position

    previous = start_speed
    monotonic = True
    elapsed = 0.0
    time_to_stop = -1.0
    delta_time = 1.0 / 60.0

    while elapsed < 2.0:
        player.update(delta_time, (0.0, 0.0), camera.planar_forward, camera.planar_right)
        elapsed += delta_time
        if player.speed > previous + 0.001:
            monotonic = False
        previous = player.speed
        if time_to_stop < 0 and player.speed <= 0.001:
            time_to_stop = elapsed

    travelled = Vec3(player.position.x - start_position.x, 0, player.position.z - start_position.z).magnitude
    expected_slide = (start_speed * start_speed) / (2.0 * config.deceleration)

    report.check(monotonic,
                 "Deceleration: speed falls monotonically after input release",
                 "Deceleration: speed increased after input was released")
    report.check(time_to_stop > 0,
                 "Deceleration: %.2f m/s -> 0 in %.3fs (theoretical %.3fs)"
                 % (start_speed, time_to_stop, start_speed / config.deceleration),
                 "Deceleration: player never came to rest (%.3f m/s)" % player.speed)
    report.check(travelled <= expected_slide + 0.5,
                 "Deceleration: slid %.2fm, no overshoot beyond the %.2fm stopping distance" % (travelled, expected_slide),
                 "Deceleration: slid %.2fm, %.2fm beyond the expected stopping distance" % (travelled, expected_slide))
    report.check(player.speed <= 0.001,
                 "Deceleration: comes to a complete rest (%.4f m/s residual)" % player.speed,
                 "Deceleration: residual speed %.4f m/s" % player.speed)


def test_rotation(report, player, camera, config):
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    advance_for(player, camera, (1.0, 1.0), 0.8)  # diagonal
    velocity = player.planar_velocity
    heading_error = angle_between(player.forward, velocity.normalized()) if velocity.magnitude > 0.01 else 999.0

    report.check(velocity.magnitude >= config.move_speed * 0.5 and heading_error <= 15.0,
                 "Rotation: heading error %.1f deg after a diagonal move (%.2f m/s)"
                 % (heading_error, velocity.magnitude),
                 "Rotation: heading error %.1f deg at %.2f m/s (expected <= 15 deg)"
                 % (heading_error, velocity.magnitude))

    # Rate limited, not instant.
    player.position = Vec3(0, 1, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)
    advance(player, camera, (1.0, 0.0), 3)
    early_error = angle_between(player.forward, player.planar_velocity.normalized()) \
        if player.planar_velocity.magnitude > 0.01 else 999.0

    report.check(early_error >= 1.0,
                 "Rotation: turn is rate limited (%.1f deg remaining 3 frames in)" % early_error,
                 "Rotation: heading snapped instantly; rotation speed is not applied")


def test_gravity_falling_landing(report, player, camera, config):
    player.position = Vec3(0, 6, 8)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera, 6)

    report.check(not player.is_grounded,
                 "Gravity: player is airborne 5m above the arena floor",
                 "Gravity: player still reports grounded 5m above the floor")

    fall_height = 5.0
    expected_impact = math.sqrt(2.0 * config.gravity * fall_height)

    landed = False
    lowest = 0.0
    elapsed = 0.0
    delta_time = 1.0 / 60.0
    while elapsed < 3.0 and not landed:
        player.update(delta_time, (0.0, 0.0), camera.planar_forward, camera.planar_right)
        elapsed += delta_time
        if player.just_landed:
            landed = True
        if not player.is_grounded:
            lowest = min(lowest, player.vertical_velocity)

    report.check(lowest < 0.0,
                 "Gravity: vertical velocity reaches %.2f m/s while falling" % lowest,
                 "Gravity: vertical velocity never became negative (min %.2f)" % lowest)
    report.check(landed,
                 "Landing: touched down after %.2fs" % elapsed,
                 "Landing: never touched down within 3.0s (vertical speed %.2f m/s)" % player.vertical_velocity)

    settle(player, camera, 30)
    report.check(player.is_grounded,
                 "Landing: grounded and settled at y=%.3f after the fall" % player.position.y,
                 "Landing: player is airborne after landing (y=%.3f)" % player.position.y)
    report.check(player.vertical_velocity >= -config.ground_stick_force - 0.5,
                 "Landing: vertical velocity reset to the ground stick value (%.2f m/s)" % player.vertical_velocity,
                 "Landing: vertical velocity not reset (%.2f m/s)" % player.vertical_velocity)

    impact_error = abs(player.last_landing_impact_speed - expected_impact)
    report.check(impact_error <= expected_impact * 0.15 + 0.5,
                 "Landing: impact speed %.2f m/s (expected %.2f m/s)" % (player.last_landing_impact_speed, expected_impact),
                 "Landing: impact speed %.2f m/s, expected about %.2f m/s"
                 % (player.last_landing_impact_speed, expected_impact))


def test_slope_traversal(report, player, camera):
    # Ramp_West: ground level at x=-5.5 rising to 1.2m at x=-10.5 (~13.5 degrees).
    player.position = Vec3(-5.0, 1.0, 6.0)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    report.check(player.is_grounded,
                 "Slope: settled on the ramp approach at y=%.3f" % player.position.y,
                 "Slope: player did not settle on the ramp approach")

    start_y = player.position.y
    max_slope = 0.0
    delta_time = 1.0 / 60.0
    move_input = input_for_world_direction(Vec3(-1, 0, 0), camera)  # west, up the ramp
    elapsed = 0.0
    while elapsed < 4.2:
        player.update(delta_time, move_input, camera.planar_forward, camera.planar_right)
        elapsed += delta_time
        if player.is_grounded:
            max_slope = max(max_slope, player.slope_angle)

    climbed = player.position.y - start_y

    report.check(climbed >= 0.8,
                 "Slope: climbed %.2fm walking up the 13.5 deg ramp" % climbed,
                 "Slope: only climbed %.2fm walking up the ramp (expected about 1.2m)" % climbed)
    report.check(player.is_grounded,
                 "Slope: grounded while walking up the ramp (max slope %.1f deg)" % max_slope,
                 "Slope: left the ground while walking up the ramp")
    report.check(max_slope >= 10.0,
                 "Slope: ground normal sampled on the ramp (max %.1f deg)" % max_slope,
                 "Slope: slope angle only reached %.1f deg on the ramp; ground normal not sampled" % max_slope)

    descent_start = player.position.y
    advance_for(player, camera, input_for_world_direction(Vec3(1, 0, 0), camera), 4.2)  # east, down
    descended = descent_start - player.position.y

    report.check(descended >= 0.8,
                 "Slope: descended %.2fm walking back down the ramp" % descended,
                 "Slope: only descended %.2fm walking down the ramp (expected about 1.2m)" % descended)
    report.check(player.is_grounded,
                 "Slope: grounded while walking down the ramp (slope %.1f deg)" % player.slope_angle,
                 "Slope: left the ground while walking down the ramp")


def test_walk_off_ledge(report, player, camera):
    """Walking off Platform_West (top at 1.2m) must leave the ground and land on the arena floor."""
    player.position = Vec3(-13.0, 2.2, 6.0)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    report.check(player.is_grounded and player.position.y > 2.0,
                 "Ledge: standing on Platform_West at y=%.3f" % player.position.y,
                 "Ledge: player did not settle on Platform_West (y=%.3f)" % player.position.y)

    became_airborne = False
    landed = False
    impact = 0.0
    elapsed = 0.0
    delta_time = 1.0 / 60.0
    move_input = input_for_world_direction(Vec3(1, 0, 0), camera)  # east, off the platform edge

    while elapsed < 4.0:
        player.update(delta_time, move_input, camera.planar_forward, camera.planar_right)
        elapsed += delta_time
        if player.just_became_airborne:
            became_airborne = True
        if player.just_landed:
            landed = True
            impact = player.last_landing_impact_speed
            break

    report.check(became_airborne,
                 "Ledge: player left the ground after walking off the platform edge",
                 "Ledge: player never became airborne after walking off the platform edge")
    report.check(landed,
                 "Ledge: landed on the arena floor after %.2fs with %.2f m/s impact" % (elapsed, impact),
                 "Ledge: player never landed after walking off the platform edge")

    # The player keeps sliding east after touchdown, so it may come to rest on the ramp rather than
    # on the flat floor; what matters is that it is grounded and stops moving vertically.
    delta_time = 1.0 / 60.0
    for _ in range(180):
        player.update(delta_time, (0.0, 0.0), camera.planar_forward, camera.planar_right)
        if player.speed <= 0.001:
            break
    rest_y = player.position.y
    settle(player, camera, 20)
    # A small residual descent is expected on a slope: the ground stick force projects onto the
    # ramp plane and slides the capsule gently downhill until it re-snaps.
    report.check(player.is_grounded and abs(player.position.y - rest_y) < 0.12,
                 "Ledge: settled on the surface below the platform at y=%.3f (grounded, <=0.12m slope settle)"
                 % player.position.y,
                 "Ledge: player did not settle after the fall (y=%.3f, grounded=%s)"
                 % (player.position.y, player.is_grounded))


def test_wall_collision(report, player, camera):
    """Walking into the monolith must stop the player instead of passing through it."""
    player.position = Vec3(8.0, 1.0, 10.0)
    player.yaw = 180.0
    player.reset_movement_state()
    settle(player, camera)

    move_input = input_for_world_direction(Vec3(1, 0, 0), camera)  # east, into the monolith
    advance_for(player, camera, move_input, 5.0)

    # Monolith: centre (15, 4, 10), half extent 1.25 -> west face at x = 13.75.
    face = 13.75
    report.check(player.position.x <= face - player.probe_radius + 0.05,
                 "Wall: stopped %.2fm from the monolith face (x=%.2f, capsule radius %.2f)"
                 % (face - player.position.x, player.position.x, player.probe_radius),
                 "Wall: player reached x=%.2f, past the monolith face at %.2f" % (player.position.x, face))
    report.check(player.is_grounded,
                 "Wall: still grounded while pushing into the monolith",
                 "Wall: left the ground while pushing into the monolith")


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    boxes = parse_scene(SCENE_PATH)
    geometry = [b for b in boxes if b.name not in ("", "Player", "PlayerSpawn")]
    print("Loaded %d colliders from %s" % (len(geometry), os.path.relpath(SCENE_PATH, REPO_ROOT)))

    config = PlayerConfig()  # matches the Inspector defaults written into CombatTestScene
    world = World(geometry, ground_layers=(1 << 0) | (1 << 14) | (1 << 17), max_slope_angle=45.0)
    camera = CameraRig(yaw=180.0)
    player = Player(config, world, Vec3(0, 1, 8), 180.0)

    report = Report()

    print("\n=== Static configuration ===")
    test_static_configuration(report, config, world)

    print("\n=== Ground detection ===")
    test_grounded_at_spawn(report, player, camera)

    print("\n=== Idle motion integrity (no zero-length CharacterController.Move) ===")
    test_idle_motion_integrity(report, player, camera)

    print("\n=== Directional movement (camera yaw 180 deg) ===")
    test_directional_movement(report, player, camera, config, "Forward", Vec3(0, 0, -1))
    test_directional_movement(report, player, camera, config, "Backward", Vec3(0, 0, 1))
    test_directional_movement(report, player, camera, config, "Right", Vec3(-1, 0, 0))
    test_directional_movement(report, player, camera, config, "Left", Vec3(1, 0, 0))
    test_directional_movement(report, player, camera, config, "Diagonal",
                              Vec3(1, 0, 1).normalized())

    print("\n=== Camera-relative movement ===")
    test_camera_relative(report, player, camera, config)

    print("\n=== Acceleration / deceleration ===")
    test_acceleration(report, player, camera, config)
    test_deceleration(report, player, camera, config)

    print("\n=== Rotation toward movement direction ===")
    test_rotation(report, player, camera, config)

    print("\n=== Gravity, falling and landing ===")
    test_gravity_falling_landing(report, player, camera, config)

    print("\n=== Slope traversal (Ramp_West) ===")
    test_slope_traversal(report, player, camera)

    print("\n=== Walking off a ledge ===")
    test_walk_off_ledge(report, player, camera)

    print("\n=== Wall collision ===")
    test_wall_collision(report, player, camera)

    print("\n--- Detailed results ---")
    for line in report.lines:
        print(line)

    print("\nChecks passed: %d/%d" % (report.passed, report.total))
    if report.passed == report.total:
        print("VERIFICATION PASSED")
        return 0

    print("VERIFICATION FAILED")
    return 1


if __name__ == "__main__":
    sys.exit(main())
