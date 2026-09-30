#!/usr/bin/env python3
"""Headless renderer that shows the hero wearing the authored PBR materials, in the real arena.

The Unity Editor is not available in this environment, so this tool renders the hero itself:

* The body is read straight out of ``Assets/Scripts/Player/HeroCharacterVisual.cs`` - every
  ``Part(...)`` call (primitive type, name, position, scale, rotation, material role) is parsed, so
  the preview always matches the model the game builds at runtime.
* The materials are the real ones: for each role the ``Assets/Art/Materials/Hero/M_Hero_<Role>.mat``
  file is parsed for its texture GUIDs and its ``_BumpScale`` / ``_OcclusionStrength`` sliders, and
  those GUIDs are resolved to the actual PNGs through the ``.meta`` files. A missing or renamed map
  therefore shows up here, not only in Unity.
* Shading is a compact Cook-Torrance model (GGX specular, Schlick Fresnel, metallic workflow) driven
  by the packed mask (R metallic, G occlusion, A smoothness), with a directional-light shadow map so
  the hero self-shadows and drops a shadow on the arena floor it stands on.
* The camera sits exactly where ``ThirdPersonCamera`` puts it (yaw 180, pitch 15, distance 5, pivot
  offset 1.6 m) in ``CombatTestScene``, and the floor colour, sky gradient, ambient colours and the
  direction of the scene's directional light all come from that scene and its materials.
* The three lighting setups are the same three the Play Mode suite asserts on: bright key, dim warm
  key and cool key. The printed luminance/contrast table is the headless mirror of those checks.

Output: ``preview/hero_materials_<setup>.png`` plus ``preview/hero_material_preview.png``.

Run:  python3 Tools/HeroMaterialVerification/render_hero_preview.py
"""

import math
import os
import re
import sys

try:
    import numpy as np
    from PIL import Image
except ImportError as exc:  # pragma: no cover - dependency guard
    sys.stderr.write(
        "This tool needs numpy and Pillow:\n"
        "  python3 -m pip install --user -r Tools/HeroMaterialVerification/requirements.txt\n"
        "(%s)\n" % exc
    )
    sys.exit(2)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT_DIR = os.path.join(REPO_ROOT, "Tools", "HeroMaterialVerification", "preview")
HERO_SCRIPT = os.path.join(REPO_ROOT, "Assets", "Scripts", "Player", "HeroCharacterVisual.cs")
MAT_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Materials", "Hero")
TEX_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Textures", "Hero")

WIDTH, HEIGHT = 540, 405
SHADOW_SIZE = 512
FOV = 60.0
PLAYER_POSITION = np.array([0.0, 1.0, 8.0])
VISUAL_OFFSET = -1.0
PIVOT_HEIGHT = 1.6
DISTANCE = 5.0
YAW, PITCH = 180.0, 15.0

# Assets/Scenes/CombatTestScene.unity: light rotation (Euler 50, -30, 0) and RenderSettings ambient.
LIGHT_QUATERNION = (0.4082179, -0.2345697, 0.1093816, 0.8754261)
# Unity's default procedural skybox, approximated in linear space.
SKY_ZENITH = np.array([0.030, 0.075, 0.240])
SKY_HORIZON = np.array([0.330, 0.400, 0.560])
SKY_GROUND = np.array([0.055, 0.052, 0.045])
# Ambient irradiance. The scene uses RenderSettings.ambientMode = Skybox, so the ambient probe is the
# procedural skybox SH rather than the (unused) flat/trilight colours. These are that sky's SH
# irradiance, approximated in linear space.
# Assets/Art/Materials/M_Greybox_Ground.mat
FLOOR_COLOR = np.array([0.3185468, 0.3185468, 0.3185468])


# ---------------------------------------------------------------------------
# Math helpers
# ---------------------------------------------------------------------------
def normalize(vector):
    length = np.linalg.norm(vector)
    return vector / length if length > 1e-9 else vector


def rotation_matrix(x_deg, y_deg, z_deg):
    """Unity's Quaternion.Euler: rotation = Ry * Rx * Rz."""
    x, y, z = math.radians(x_deg), math.radians(y_deg), math.radians(z_deg)
    rx = np.array([[1, 0, 0], [0, math.cos(x), -math.sin(x)], [0, math.sin(x), math.cos(x)]])
    ry = np.array([[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]])
    rz = np.array([[math.cos(z), -math.sin(z), 0], [math.sin(z), math.cos(z), 0], [0, 0, 1]])
    return ry @ rx @ rz


def quaternion_to_matrix(x, y, z, w):
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def srgb_to_linear(c):
    c = np.asarray(c, dtype=np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(np.asarray(c, dtype=np.float64), 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * (c ** (1.0 / 2.4)) - 0.055)


AMBIENT_SKY = np.array([0.115, 0.145, 0.215])
AMBIENT_EQUATOR = np.array([0.140, 0.155, 0.190])
AMBIENT_GROUND = np.array([0.045, 0.040, 0.033])


# ---------------------------------------------------------------------------
# Primitive meshes with Unity's per-face UV layout
# ---------------------------------------------------------------------------
def make_cube():
    faces = [
        (np.array([0, 0, 1.0]), np.array([1.0, 0, 0]), np.array([0, 1.0, 0])),     # +Z
        (np.array([0, 0, -1.0]), np.array([-1.0, 0, 0]), np.array([0, 1.0, 0])),   # -Z
        (np.array([1.0, 0, 0]), np.array([0, 0, -1.0]), np.array([0, 1.0, 0])),    # +X
        (np.array([-1.0, 0, 0]), np.array([0, 0, 1.0]), np.array([0, 1.0, 0])),    # -X
        (np.array([0, 1.0, 0]), np.array([1.0, 0, 0]), np.array([0, 0, -1.0])),    # +Y
        (np.array([0, -1.0, 0]), np.array([1.0, 0, 0]), np.array([0, 0, 1.0])),    # -Y
    ]
    vertices, normals, uvs, triangles = [], [], [], []
    for normal, u_axis, v_axis in faces:
        base = len(vertices)
        for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            vertices.append(0.5 * (u_axis * su + v_axis * sv + normal))
            normals.append(normal)
            uvs.append([(su + 1) * 0.5, (sv + 1) * 0.5])
        triangles += [(base, base + 1, base + 2), (base, base + 2, base + 3)]
    return (np.array(vertices), np.array(normals), np.array(uvs), np.array(triangles, dtype=np.int32))


def make_sphere(segments=22, rings=14):
    vertices, normals, uvs, triangles = [], [], [], []
    for ring in range(rings + 1):
        v = ring / rings
        phi = math.pi * v
        for segment in range(segments + 1):
            u = segment / segments
            theta = 2.0 * math.pi * u
            normal = np.array([math.sin(phi) * math.cos(theta), math.cos(phi), math.sin(phi) * math.sin(theta)])
            vertices.append(normal * 0.5)
            normals.append(normal)
            uvs.append([u, 1.0 - v])
    stride = segments + 1
    for ring in range(rings):
        for segment in range(segments):
            a = ring * stride + segment
            b = a + stride
            triangles += [(a, b, a + 1), (a + 1, b, b + 1)]
    return (np.array(vertices), np.array(normals), np.array(uvs), np.array(triangles, dtype=np.int32))


def make_capsule(segments=20, cap_rings=8):
    """Unity capsule: radius 0.5, cylinder between y = -0.5 and +0.5, hemispherical caps."""
    cap_span = 0.25
    rows = cap_rings * 2 + 1
    vertices, normals, uvs, triangles = [], [], [], []
    for index in range(rows + 1):
        v = index / rows
        if v < cap_span:
            t = v / cap_span
            phi = (1.0 - t) * math.pi * 0.5
            normal_y = -math.sin(phi)
            radial = math.cos(phi)
            y = -0.5 - 0.5 * math.sin(phi)
        elif v <= 1.0 - cap_span:
            t = (v - cap_span) / (1.0 - 2.0 * cap_span)
            normal_y = 0.0
            radial = 1.0
            y = -0.5 + t
        else:
            t = (v - (1.0 - cap_span)) / cap_span
            phi = t * math.pi * 0.5
            normal_y = math.sin(phi)
            radial = math.cos(phi)
            y = 0.5 + 0.5 * math.sin(phi)

        for segment in range(segments + 1):
            u = segment / segments
            theta = 2.0 * math.pi * u
            direction = np.array([math.cos(theta) * radial, normal_y, math.sin(theta) * radial])
            point = np.array([math.cos(theta) * radial * 0.5, y, math.sin(theta) * radial * 0.5])
            vertices.append(point)
            normals.append(normalize(direction) if v < cap_span or v > 1.0 - cap_span else direction)
            uvs.append([u, v])
    stride = segments + 1
    for ring in range(rows):
        for segment in range(segments):
            a = ring * stride + segment
            b = a + stride
            triangles += [(a, b, a + 1), (a + 1, b, b + 1)]
    return (np.array(vertices), np.array(normals), np.array(uvs), np.array(triangles, dtype=np.int32))


def make_cylinder(segments=20):
    """Unity cylinder: height 2 (-1..1), radius 0.5, flat caps."""
    vertices, normals, uvs, triangles = [], [], [], []
    for v, y in ((0.0, -1.0), (1.0, 1.0)):
        for segment in range(segments + 1):
            u = segment / segments
            theta = 2.0 * math.pi * u
            normal = np.array([math.cos(theta), 0.0, math.sin(theta)])
            vertices.append(np.array([normal[0] * 0.5, y, normal[2] * 0.5]))
            normals.append(normal)
            uvs.append([u, v])
    stride = segments + 1
    for segment in range(segments):
        a, b = segment, stride + segment
        triangles += [(a, b, a + 1), (a + 1, b, b + 1)]

    for y, normal_y in ((1.0, 1.0), (-1.0, -1.0)):
        base = len(vertices)
        vertices.append(np.array([0.0, y, 0.0]))
        normals.append(np.array([0.0, normal_y, 0.0]))
        uvs.append([0.5, 0.5])
        for segment in range(segments + 1):
            theta = 2.0 * math.pi * segment / segments
            vertices.append(np.array([0.5 * math.cos(theta), y, 0.5 * math.sin(theta)]))
            normals.append(np.array([0.0, normal_y, 0.0]))
            uvs.append([0.5 + 0.5 * math.cos(theta), 0.5 + 0.5 * math.sin(theta)])
        for segment in range(segments):
            a, b = base, base + 1 + segment
            triangles.append((a, b, b + 1) if normal_y > 0 else (a, b + 1, b))
    return (np.array(vertices), np.array(normals), np.array(uvs), np.array(triangles, dtype=np.int32))


MESH_BUILDERS = {
    "Cube": make_cube,
    "Sphere": make_sphere,
    "Capsule": make_capsule,
    "Cylinder": make_cylinder,
}


# ---------------------------------------------------------------------------
# Parsing the hero model and the authored materials
# ---------------------------------------------------------------------------
PART_RE = re.compile(
    r'Part\(PrimitiveType\.(\w+),\s*"([^"]+)",\s*new Vector3\(([^)]*)\),\s*new Vector3\(([^)]*)\),\s*'
    r'(?:Quaternion\.Euler\(([^)]*)\)|Quaternion\.identity),\s*HeroMaterialRole\.(\w+)\);')


def parse_floats(text):
    return [float(part.strip().rstrip('f')) for part in text.split(",") if part.strip()]


def parse_hero_parts():
    with open(HERO_SCRIPT, "r", encoding="utf-8") as handle:
        source = handle.read()

    parts = []
    for match in PART_RE.finditer(source):
        primitive, name, position, scale, euler, role = match.groups()
        parts.append(dict(
            primitive=primitive,
            name=name,
            position=np.array(parse_floats(position)),
            scale=np.array(parse_floats(scale)),
            euler=parse_floats(euler) if euler else [0.0, 0.0, 0.0],
            role=role,
        ))

    if not parts:
        raise RuntimeError("no Part(...) calls parsed from " + HERO_SCRIPT)
    return parts


def load_texture(path, srgb):
    image = np.asarray(Image.open(path).convert("RGBA"), dtype=np.float64) / 255.0
    rgb = srgb_to_linear(image[..., :3]) if srgb else image[..., :3]
    return dict(rgb=rgb, alpha=image[..., 3], height=image.shape[0], width=image.shape[1])


def load_material(role):
    """Reads the authored .mat and resolves its texture GUIDs through the .meta files."""
    path = os.path.join(MAT_DIR, "M_Hero_%s.mat" % role)
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()

    floats = {}
    for match in re.finditer(r"- (_\w+): ([0-9.]+)\s*$", text, re.MULTILINE):
        floats[match.group(1)] = float(match.group(2))

    guids = {}
    for match in re.finditer(r"- (_\w+):\s*\n\s*m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})", text):
        guids[match.group(1)] = match.group(2)

    guid_to_texture = {}
    for filename in sorted(os.listdir(TEX_DIR)):
        if not filename.endswith(".png"):
            continue
        with open(os.path.join(TEX_DIR, filename + ".meta"), "r", encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("guid:"):
                    guid_to_texture[line.split(":", 1)[1].strip()] = filename

    def texture(property_name):
        guid = guids.get(property_name)
        if guid is None or guid not in guid_to_texture:
            return None
        return os.path.join(TEX_DIR, guid_to_texture[guid])

    if texture("_BaseMap") is None or texture("_BumpMap") is None or texture("_MetallicGlossMap") is None:
        raise RuntimeError("M_Hero_%s is missing one of its maps (run author_hero_materials.py)" % role)

    return dict(
        role=role,
        albedo=load_texture(texture("_BaseMap"), True),
        albedo_file=os.path.basename(texture("_BaseMap")),
        normal=load_texture(texture("_BumpMap"), False),
        mask=load_texture(texture("_MetallicGlossMap"), False),
        bump_scale=floats.get("_BumpScale", 1.0),
        occlusion_strength=floats.get("_OcclusionStrength", 1.0),
    )


def sample(texture, uv):
    """Bilinear wrap sampling; uv in 0..1 with v pointing up."""
    width, height = texture["width"], texture["height"]
    x = (uv[:, 0] % 1.0) * width - 0.5
    y = (1.0 - (uv[:, 1] % 1.0)) * height - 0.5
    x0 = np.floor(x).astype(np.int64)
    y0 = np.floor(y).astype(np.int64)
    fx = (x - x0)[:, None]
    fy = (y - y0)[:, None]
    x0 %= width
    y0 %= height
    x1 = (x0 + 1) % width
    y1 = (y0 + 1) % height

    def blend(array):
        a = array[y0, x0]
        b = array[y0, x1]
        c = array[y1, x0]
        d = array[y1, x1]
        if array.ndim == 2:
            # Scalar channel (alpha): (n,) weights, not (n, 1), to keep the result 1-D.
            u, v = fx[:, 0], fy[:, 0]
            return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v

        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy

    return blend(texture["rgb"]), blend(texture["alpha"])


def build_scene(parts, materials):
    """Transforms every part into world space and bakes an analytic tangent frame."""
    root_rotation = rotation_matrix(0.0, YAW, 0.0)
    root_origin = PLAYER_POSITION + root_rotation @ np.array([0.0, VISUAL_OFFSET, 0.0])

    triangles = []
    for part in parts:
        local_vertices, local_normals, uvs, local_triangles = MESH_BUILDERS[part["primitive"]]()
        rotation = rotation_matrix(*part["euler"])

        world_vertices = (local_vertices * part["scale"]) @ rotation.T + part["position"]
        world_vertices = world_vertices @ root_rotation.T + root_origin
        world_normals = (local_normals @ rotation.T) @ root_rotation.T

        tangents = np.zeros_like(world_normals)
        tangents[:, 0] = 1.0
        bitangents = np.cross(world_normals, tangents)
        lengths = np.linalg.norm(bitangents, axis=1)
        lengths[lengths < 1e-6] = 1.0
        bitangents = bitangents / lengths[:, None]
        tangents = np.cross(bitangents, world_normals)

        material = materials[part["role"]]
        for triangle in local_triangles:
            triangles.append(dict(
                vertices=world_vertices[triangle],
                normals=world_normals[triangle],
                uvs=uvs[triangle],
                tangents=tangents[triangle],
                bitangents=bitangents[triangle],
                material=material,
                name=part["name"],
            ))
    return triangles


class Camera:
    """Unity camera convention: local +Z is forward, view = [R^T | -R^T p]."""

    def __init__(self, position, rotation, fov_degrees, width, height, near=0.05, far=400.0):
        self.position = np.asarray(position, dtype=np.float64)
        self.rotation = np.asarray(rotation, dtype=np.float64)
        self.width = width
        self.height = height
        self.near = near
        self.far = far
        self.aspect = width / float(height)
        self.view = np.eye(4)
        self.view[:3, :3] = self.rotation.T
        self.view[:3, 3] = -self.rotation.T @ self.position
        focal = 1.0 / math.tan(math.radians(fov_degrees) * 0.5)
        self.projection = np.array([
            [focal / self.aspect, 0, 0, 0],
            [0, focal, 0, 0],
            [0, 0, (far + near) / (near - far), (2.0 * far * near) / (near - far)],
            [0, 0, 1, 0],
        ])

    def view_projection(self):
        return self.projection @ self.view


class FrameBuffer:
    def __init__(self, width, height):
        self.width = width
        self.height = height
        self.color = np.zeros((height, width, 3))
        self.depth = np.full((height, width), np.inf)
        self.mask = np.zeros((height, width), dtype=bool)


def rasterize(framebuffer, camera, triangles, shade):
    """Z-buffered rasteriser; depth is view-space z (linear), which the shadow map reuses."""
    view_projection = camera.view_projection()
    drawn = 0

    for triangle in triangles:
        clip = (view_projection @ np.hstack([triangle["vertices"], np.ones((3, 1))]).T).T
        if np.any(clip[:, 3] <= 1e-5):
            continue

        ndc = clip[:, :3] / clip[:, 3:4]
        screen = np.stack([
            (ndc[:, 0] * 0.5 + 0.5) * framebuffer.width,
            (1.0 - (ndc[:, 1] * 0.5 + 0.5)) * framebuffer.height,
        ], axis=1)

        min_x = max(int(math.floor(screen[:, 0].min())) - 1, 0)
        max_x = min(int(math.ceil(screen[:, 0].max())) + 1, framebuffer.width - 1)
        min_y = max(int(math.floor(screen[:, 1].min())) - 1, 0)
        max_y = min(int(math.ceil(screen[:, 1].max())) + 1, framebuffer.height - 1)
        if min_x > max_x or min_y > max_y:
            continue

        xs = np.arange(min_x, max_x + 1) + 0.5
        ys = np.arange(min_y, max_y + 1) + 0.5
        grid_x, grid_y = np.meshgrid(xs, ys)

        area = ((screen[1, 0] - screen[0, 0]) * (screen[2, 1] - screen[0, 1]) -
                (screen[1, 1] - screen[0, 1]) * (screen[2, 0] - screen[0, 0]))
        if abs(area) < 1e-9:
            continue

        w0 = ((screen[1, 0] - grid_x) * (screen[2, 1] - grid_y) -
              (screen[1, 1] - grid_y) * (screen[2, 0] - grid_x)) / area
        w1 = ((screen[2, 0] - grid_x) * (screen[0, 1] - grid_y) -
              (screen[2, 1] - grid_y) * (screen[0, 0] - grid_x)) / area
        w2 = 1.0 - w0 - w1
        inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        if not inside.any():
            continue

        # Perspective-correct interpolation: attributes are weighted by (barycentric / w).
        inverse_w = 1.0 / clip[:, 3]
        perspective = w0 * inverse_w[0] + w1 * inverse_w[1] + w2 * inverse_w[2]
        depth = np.where(inside, 1.0 / perspective, np.inf)

        window = framebuffer.depth[min_y:max_y + 1, min_x:max_x + 1]
        closer = inside & (depth < window)
        if not closer.any():
            continue

        weights = np.stack([w0 * inverse_w[0], w1 * inverse_w[1], w2 * inverse_w[2]], axis=-1) / perspective[..., None]
        rows, columns = np.nonzero(closer)
        pixel_weights = weights[rows, columns]
        indices = (min_y + rows, min_x + columns)

        position = pixel_weights @ triangle["vertices"]
        normal = pixel_weights @ triangle["normals"]
        uv = pixel_weights @ triangle["uvs"]
        tangent = pixel_weights @ triangle["tangents"]
        bitangent = pixel_weights @ triangle["bitangents"]

        framebuffer.color[indices] = shade(position, normal, uv, tangent, bitangent, triangle["material"])
        framebuffer.depth[indices] = depth[rows, columns]
        framebuffer.mask[indices] = True
        drawn += closer.sum()

    return int(drawn)


# ---------------------------------------------------------------------------
# Shading
# ---------------------------------------------------------------------------
def sky_color(directions):
    up = np.clip(directions[..., 1], -1.0, 1.0)
    blend = np.clip(up, 0.0, 1.0) ** 0.55
    return np.where(up[..., None] > 0,
                    SKY_HORIZON * (1.0 - blend[..., None]) + SKY_ZENITH * blend[..., None],
                    SKY_GROUND)


def make_hero_shader(camera_position, light_direction, light_color, light_intensity, shadow_map,
                     ambient_scale=1.0):
    """GGX metallic-workflow shading from the packed mask, with shadow mapping."""

    def shade(position, normal, uv, tangent, bitangent, material):
        normal = normal / np.maximum(np.linalg.norm(normal, axis=1, keepdims=True), 1e-9)
        tangent = tangent / np.maximum(np.linalg.norm(tangent, axis=1, keepdims=True), 1e-9)
        bitangent = bitangent / np.maximum(np.linalg.norm(bitangent, axis=1, keepdims=True), 1e-9)

        albedo_rgb, _ = sample(material["albedo"], uv)
        normal_rgb, _ = sample(material["normal"], uv)
        mask_rgb, mask_alpha = sample(material["mask"], uv)

        texel = normal_rgb * 2.0 - 1.0
        texel[:, 2] = np.maximum(texel[:, 2], 1e-3)
        texel[:, 0] *= material["bump_scale"]
        texel[:, 1] *= material["bump_scale"]
        texel = texel / np.maximum(np.linalg.norm(texel, axis=1, keepdims=True), 1e-9)
        world_normal = tangent * texel[:, 0:1] + bitangent * texel[:, 1:2] + normal * texel[:, 2:3]
        world_normal = world_normal / np.maximum(np.linalg.norm(world_normal, axis=1, keepdims=True), 1e-9)
        flipped = np.sum(world_normal * normal, axis=1) < 0.0
        world_normal[flipped] *= -1.0

        metallic = mask_rgb[:, 0]
        occlusion = 1.0 - material["occlusion_strength"] * (1.0 - mask_rgb[:, 1])
        roughness = np.clip(1.0 - mask_alpha, 0.045, 1.0)
        alpha = roughness * roughness

        view = normalize(camera_position - position)
        half = normalize(view + light_direction)

        n_dot_l = np.clip(np.sum(world_normal * light_direction, axis=1), 0.0, 1.0)
        n_dot_v = np.clip(np.sum(world_normal * view, axis=1), 1e-4, 1.0)
        n_dot_h = np.clip(np.sum(world_normal * half, axis=1), 0.0, 1.0)
        v_dot_h = np.clip(np.sum(view * half, axis=1), 0.0, 1.0)

        f0 = 0.04 * (1.0 - metallic[:, None]) + albedo_rgb * metallic[:, None]
        fresnel = f0 + (1.0 - f0) * np.power(1.0 - v_dot_h[:, None], 5.0)

        alpha2 = (alpha * alpha)[:, None]
        denominator = (n_dot_h[:, None] ** 2) * (alpha2 - 1.0) + 1.0
        distribution = alpha2 / (math.pi * denominator * denominator)
        k = (((roughness + 1.0) ** 2) / 8.0)[:, None]
        visibility = (n_dot_l[:, None] / (n_dot_l[:, None] * (1.0 - k) + k)) * \
                     (n_dot_v[:, None] / (n_dot_v[:, None] * (1.0 - k) + k))
        specular = distribution * visibility * fresnel / (4.0 * n_dot_l[:, None] * n_dot_v[:, None] + 1e-4)

        shadow = shadow_factor(position, world_normal, light_direction, shadow_map)
        # Unity's directional light intensity already carries the 1/pi of the Lambert BRDF.
        direct = light_color[None, :] * (math.pi * light_intensity * shadow * n_dot_l)[:, None]
        diffuse = albedo_rgb * (1.0 - metallic[:, None]) / math.pi
        color = direct * (diffuse + specular)

        up = np.clip(world_normal[:, 1], -1.0, 1.0)
        irradiance = (AMBIENT_SKY[None, :] * np.clip(up, 0, 1)[:, None] +
                      AMBIENT_EQUATOR[None, :] * (1.0 - np.abs(up))[:, None] +
                      AMBIENT_GROUND[None, :] * np.clip(-up, 0, 1)[:, None]) * ambient_scale

        reflect = 2.0 * np.sum(world_normal * view, axis=1)[:, None] * world_normal - view
        reflection = sky_color(reflect.reshape(-1, 1, 3))[:, 0, :]
        environment = reflection * smoothstep_soft(mask_alpha)[:, None] + irradiance
        color += (albedo_rgb * (1.0 - metallic[:, None]) * irradiance +
                  environment * (f0 * 0.85 + 0.015))

        return color * occlusion[:, None]

    return shade


def smoothstep_soft(smoothness):
    return np.clip(smoothness * smoothness * (3.0 - 2.0 * smoothness), 0.0, 1.0)


def shadow_factor(position, normal, light_direction, shadow_map):
    lifted = position + normal * 0.015
    clip = (shadow_map["view_projection"] @ np.hstack([lifted, np.ones((len(lifted), 1))]).T).T
    depth = clip[:, 3]
    ndc = clip[:, :3] / np.maximum(depth[:, None], 1e-6)
    u = np.clip(((ndc[:, 0] * 0.5 + 0.5) * SHADOW_SIZE).astype(np.int64), 0, SHADOW_SIZE - 1)
    v = np.clip(((1.0 - (ndc[:, 1] * 0.5 + 0.5)) * SHADOW_SIZE).astype(np.int64), 0, SHADOW_SIZE - 1)
    valid = (clip[:, 3] > 0) & (np.abs(ndc[:, 0]) <= 1.0) & (np.abs(ndc[:, 1]) <= 1.0)
    bias = 0.02 + 0.05 * (1.0 - np.clip(np.sum(normal * light_direction, axis=1), 0.0, 1.0))
    occluded = (depth - bias) > shadow_map["depth"][v, u]
    return np.where(valid & occluded, 0.22, 1.0)


def make_floor_shader(light_direction, light_color, light_intensity, shadow_map):
    albedo = srgb_to_linear(FLOOR_COLOR)

    def shade(position, normal, uv, tangent, bitangent, material):
        tile = 1.0 + 0.05 * ((np.floor(uv[:, 0]) + np.floor(uv[:, 1])) % 2.0)
        base = albedo[None, :] * tile[:, None]

        lifted = position + np.array([0.0, 0.01, 0.0])
        clip = (shadow_map["view_projection"] @ np.hstack([lifted, np.ones((len(lifted), 1))]).T).T
        depth = clip[:, 3]
        ndc = clip[:, :3] / np.maximum(depth[:, None], 1e-6)
        u = np.clip(((ndc[:, 0] * 0.5 + 0.5) * SHADOW_SIZE).astype(np.int64), 0, SHADOW_SIZE - 1)
        v = np.clip(((1.0 - (ndc[:, 1] * 0.5 + 0.5)) * SHADOW_SIZE).astype(np.int64), 0, SHADOW_SIZE - 1)
        occluded = ((depth - 0.02) > shadow_map["depth"][v, u]) & (depth > 0)
        shadow = np.where(occluded, 0.32, 1.0)

        n_dot_l = np.clip(normal[:, 1] * (-light_direction[1]), 0.0, 1.0)
        direct = light_color[None, :] * (light_intensity * n_dot_l * shadow)[:, None]
        color = base * (direct * math.pi + AMBIENT_SKY[None, :] * 1.6) / math.pi
        fade = np.clip(1.0 - np.linalg.norm(position[:, [0, 2]], axis=1) / 40.0, 0.35, 1.0)
        return color * fade[:, None]

    return shade


def render_shadow_map(triangles, light_direction):
    """Orthographic depth pass from the light, reused by the hero and the floor."""
    center = np.array([0.0, PLAYER_POSITION[1], PLAYER_POSITION[2]])
    radius = 2.0
    for triangle in triangles:
        radius = max(radius, float(np.max(np.abs(triangle["vertices"] - center).max(axis=1))))

    radius = radius * 1.5 + 0.5
    up_hint = np.array([0.0, 1.0, 0.0])
    if abs(float(np.dot(up_hint, light_direction))) > 0.99:
        up_hint = np.array([0.0, 0.0, 1.0])

    right = normalize(np.cross(up_hint, light_direction))
    camera_up = np.cross(light_direction, right)
    rotation = np.stack([right, camera_up, light_direction], axis=1)
    eye = center - light_direction * (radius * 3.0)

    fov = 2.0 * math.degrees(math.atan(radius / (radius * 3.0)))
    camera = Camera(eye, rotation, fov, SHADOW_SIZE, SHADOW_SIZE, near=0.01, far=radius * 8.0)
    framebuffer = FrameBuffer(SHADOW_SIZE, SHADOW_SIZE)
    rasterize(framebuffer, camera, triangles, lambda p, n, u, t, b, m: np.zeros((len(p), 3)))
    return dict(depth=framebuffer.depth, view_projection=camera.view_projection())


def build_arena_floor(size=40.0):
    vertices = np.array([[-size, 0.0, -size], [size, 0.0, -size], [size, 0.0, size], [-size, 0.0, size]])
    normal = np.array([0.0, 1.0, 0.0])
    half = size * 0.5
    return [
        dict(vertices=vertices[[0, 1, 2]], normals=np.tile(normal, (3, 1)),
             uvs=np.array([[0.0, 0.0], [size / 2, 0.0], [size / 2, size / 2]]) - half,
             material=None, name="floor"),
        dict(vertices=vertices[[0, 2, 3]], normals=np.tile(normal, (3, 1)),
             uvs=np.array([[0.0, 0.0], [size / 2, size / 2], [0.0, size / 2]]) - half,
             material=None, name="floor"),
    ]


LIGHTING_SETUPS = [
    ("bright_key", np.array([1.0, 1.0, 1.0]), 1.0),
    ("dim_warm_key", np.array([1.0, 0.78, 0.55]), 0.35),
    ("cool_key", np.array([0.6, 0.72, 1.0]), 1.2),
]

# Camera views: the gameplay framing used by ThirdPersonCamera, plus portraits that show the costume
# detail the arena camera cannot (it follows the hero from behind).
VIEWS = [
    # name,           yaw,   pitch, distance, pivot height
    ("arena",         180.0, 15.0, 5.0, 1.6),
    ("front",           0.0,  4.0, 2.4, 1.35),
    ("three_quarter",  35.0,  6.0, 2.6, 1.35),
]
PRIMARY_VIEW = "front"


def camera_for(view):
    name, yaw, pitch, distance, pivot_height = view
    rotation = rotation_matrix(pitch, yaw, 0.0)
    pivot = PLAYER_POSITION + np.array([0.0, pivot_height, 0.0])
    position = pivot - (rotation @ np.array([0.0, 0.0, 1.0])) * distance
    return name, Camera(position, rotation, FOV, WIDTH, HEIGHT)


def render_view(camera, triangles, floor, light_direction, light_color, light_intensity, shadow_map):
    framebuffer = FrameBuffer(WIDTH, HEIGHT)

    xs = (np.arange(WIDTH) + 0.5) / WIDTH * 2.0 - 1.0
    ys = 1.0 - (np.arange(HEIGHT) + 0.5) / HEIGHT * 2.0
    grid_x, grid_y = np.meshgrid(xs * camera.aspect, ys)
    focal = 1.0 / math.tan(math.radians(FOV) * 0.5)
    directions = (camera.rotation @ np.stack(
        [grid_x / focal, grid_y / focal, np.ones_like(grid_x)], axis=-1).reshape(-1, 3).T).T
    framebuffer.color = sky_color(directions.reshape(HEIGHT, WIDTH, 3))

    rasterize(framebuffer, camera, floor,
              make_floor_shader(light_direction, light_color, light_intensity, shadow_map))
    pixels = rasterize(framebuffer, camera, triangles,
                       make_hero_shader(camera.position, light_direction, light_color, light_intensity, shadow_map))
    return framebuffer, pixels


def main():
    parts = parse_hero_parts()
    roles = sorted({part["role"] for part in parts})
    materials = {role: load_material(role) for role in roles}

    print("Parsed %d hero parts using %d materials: %s" % (len(parts), len(roles), ", ".join(roles)))
    for role in roles:
        material = materials[role]
        print("  %-7s albedo=%-30s bump=%.2f occlusion=%.2f" % (
            role, material["albedo_file"], material["bump_scale"], material["occlusion_strength"]))

    triangles = build_scene(parts, materials)
    light_direction = normalize(quaternion_to_matrix(*LIGHT_QUATERNION) @ np.array([0.0, 0.0, 1.0]))
    shadow_map = render_shadow_map(triangles, light_direction)
    floor = build_arena_floor()
    cameras = [camera_for(view) for view in VIEWS]

    os.makedirs(OUT_DIR, exist_ok=True)
    rows = []
    failures = 0

    print("\n%-14s %-16s %-11s %-10s %-8s %s" % (
        "LIGHT SETUP", "VIEW", "HERO LUMA", "CONTRAST", "PIXELS", "RESULT"))

    for name, color, intensity in LIGHTING_SETUPS:
        row = []
        for view_name, camera in cameras:
            framebuffer, pixels = render_view(camera, triangles, floor,
                                              light_direction, color, intensity, shadow_map)
            image = linear_to_srgb(framebuffer.color)
            row.append(image)

            Image.fromarray((np.clip(image, 0, 1) * 255).astype(np.uint8), "RGB").save(
                os.path.join(OUT_DIR, "hero_%s_%s.png" % (name, view_name)))

            if view_name != PRIMARY_VIEW:
                continue

            hero = framebuffer.mask
            luma = (0.2126 * image[..., 0] + 0.7152 * image[..., 1] + 0.0722 * image[..., 2])[hero]
            mean = float(luma.mean()) if luma.size else 0.0
            contrast = float(luma.max() - luma.min()) if luma.size else 0.0
            ok = luma.size > 500 and 0.02 < mean < 0.98 and contrast > 0.05
            failures += 0 if ok else 1
            print("%-14s %-16s %-11.3f %-10.3f %-8d %s" % (
                name, view_name, mean, contrast, pixels, "PASS" if ok else "FAIL"))

        rows.append(row)

    sheet = np.concatenate([np.concatenate(row, axis=1) for row in rows], axis=0)
    sheet_path = os.path.join(OUT_DIR, "hero_material_preview.png")
    Image.fromarray((np.clip(sheet, 0, 1) * 255).astype(np.uint8), "RGB").save(sheet_path)

    print("\nWrote %d previews plus %s" % (
        len(VIEWS) * len(LIGHTING_SETUPS), os.path.relpath(sheet_path, REPO_ROOT)))
    print("HERO READABILITY: %s" % ("PASSED" if failures == 0 else "FAILED"))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
