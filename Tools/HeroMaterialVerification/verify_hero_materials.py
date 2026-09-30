#!/usr/bin/env python3
"""Static verification of the hero material pass.

Checks the on-disk state of the material pass without opening Unity:

  1. The seven materials exist, use URP Lit, and carry the same three textures + keyword set.
  2. Every material's smoothness workflow is authored correctly (_Smoothness = 1, mask alpha source)
     and the metallic policy holds: one metal role, six dielectrics.
  3. Each map is present, tileable in size, the right colour space (sRGB albedo, linear normal and
     mask) and actually carries the channel content the shader expects - the mask alpha is read and
     its smoothness range is asserted per role, and the normal map is checked for its alpha == red
     DXT5nm-safe encoding.
  4. ``HeroCharacterVisual.cs`` uses the seven roles, has no leftover belt material, and adds no
     colliders or animation components to the hero.
  5. ``CombatTestScene.unity`` references all seven materials on its hero and contains the
     HeroMaterialTest object.

Exit code 0 means every check passed. This mirrors the Play Mode suite
(``Assets/Scripts/Player/HeroMaterialTest.cs``) and the Editor validator
(``Assets/Scripts/Player/Editor/HeroMaterialValidator.cs``).

Run:  python3 Tools/HeroMaterialVerification/verify_hero_materials.py
"""

import os
import re
import sys
import zlib

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAT_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Materials", "Hero")
TEX_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Textures", "Hero")
HERO_SCRIPT = os.path.join(REPO_ROOT, "Assets", "Scripts", "Player", "HeroCharacterVisual.cs")
MATERIALS_SCRIPT = os.path.join(REPO_ROOT, "Assets", "Scripts", "Player", "HeroMaterials.cs")
SCENE = os.path.join(REPO_ROOT, "Assets", "Scenes", "CombatTestScene.unity")

ROLES = ["Skin", "Hair", "Suit", "Accent", "Boots", "Gloves", "Metal"]
ROLE_KIND = {
    "Skin": "skin",
    "Hair": "hair",
    "Suit": "primary costume",
    "Accent": "secondary costume",
    "Boots": "boots",
    "Gloves": "gloves",
    "Metal": "metallic details",
}
# Expected smoothness (in the mask alpha) by role, and the metallic policy.
EXPECTED_SMOOTHNESS = {
    "Skin": (0.15, 0.65),
    "Hair": (0.10, 0.70),
    "Suit": (0.05, 0.55),
    "Accent": (0.15, 0.70),
    "Boots": (0.05, 0.70),
    "Gloves": (0.10, 0.75),
    "Metal": (0.10, 0.70),
}
URP_LIT_GUID = "933532a4fcc9baf4fa0491de14d08ed7"
REQUIRED_KEYWORDS = {"_NORMALMAP", "_METALLICGLOSSMAP", "_OCCLUSIONMAP"}

PASSED = []
FAILED = []


def check(condition, description):
    (PASSED if condition else FAILED).append(description)
    print("  %s %s" % ("PASS" if condition else "FAIL", description))
    return condition


# ---------------------------------------------------------------------------
# PNG reading (no dependencies)
# ---------------------------------------------------------------------------
def read_png(path):
    with open(path, "rb") as handle:
        data = handle.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG: " + path)

    position = 8
    width = height = depth = color_type = None
    idat = b""
    while position < len(data):
        length = int.from_bytes(data[position:position + 4], "big")
        chunk_type = data[position + 4:position + 8]
        chunk = data[position + 8:position + 8 + length]
        if chunk_type == b"IHDR":
            width = int.from_bytes(chunk[0:4], "big")
            height = int.from_bytes(chunk[4:8], "big")
            depth = chunk[8]
            color_type = chunk[9]
        elif chunk_type == b"IDAT":
            idat += chunk
        position += 12 + length

    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color_type]
    raw = zlib.decompress(idat)
    stride = width * channels
    rows = []
    previous = bytearray(stride)
    offset = 0
    for _ in range(height):
        filter_type = raw[offset]
        offset += 1
        line = bytearray(raw[offset:offset + stride])
        offset += stride
        if filter_type == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif filter_type == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 0xFF
        elif filter_type == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif filter_type == 4:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                up = previous[i]
                up_left = previous[i - channels] if i >= channels else 0
                estimate = left + up - up_left
                pa, pb, pc = abs(estimate - left), abs(estimate - up), abs(estimate - up_left)
                predictor = left if (pa <= pb and pa <= pc) else (up if pb <= pc else up_left)
                line[i] = (line[i] + predictor) & 0xFF
        rows.append(line)
        previous = line

    return dict(width=width, height=height, depth=depth, channels=channels, rows=rows)


def sample_rows(png, step=7):
    """Yields (r, g, b, a) tuples sampled on a grid - cheap statistics without numpy."""
    stride = png["channels"]
    for y in range(0, png["height"], step):
        row = png["rows"][y]
        for x in range(0, png["width"], step):
            base = x * stride
            if stride == 3:
                yield row[base], row[base + 1], row[base + 2], 255
            elif stride == 4:
                yield row[base], row[base + 1], row[base + 2], row[base + 3]
            elif stride == 2:
                yield row[base], row[base], row[base], row[base + 1]
            else:
                value = row[base]
                yield value, value, value, 255


# ---------------------------------------------------------------------------
# Material parsing
# ---------------------------------------------------------------------------
def parse_material(path):
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()

    floats = {}
    for match in re.finditer(r"^\s+- (_\w+): (-?[0-9.]+)\s*$", text, re.MULTILINE):
        floats[match.group(1)] = float(match.group(2))

    colors = {}
    for match in re.finditer(r"^\s+- (_\w+): \{r: ([0-9.]+), g: ([0-9.]+), b: ([0-9.]+), a: ([0-9.]+)\}",
                             text, re.MULTILINE):
        colors[match.group(1)] = tuple(float(match.group(i)) for i in range(2, 6))

    textures = {}
    for match in re.finditer(r"^\s+- (_\w+):\n\s+m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})", text,
                             re.MULTILINE):
        textures[match.group(1)] = match.group(2)

    valid_block = re.search(r"m_ValidKeywords:\n((?:\s+- \S+\n)+)", text)
    valid_keywords = set(valid_block.group(1).split()) - {"-"} if valid_block else set()
    shader = re.search(r"m_Shader: \{fileID: 4800000, guid: ([0-9a-f]{32})", text)
    return dict(name=os.path.basename(path), floats=floats, colors=colors, textures=textures,
                keywords=valid_keywords, shader_guid=shader.group(1) if shader else None)


def read_meta_guid(path):
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("guid:"):
                return line.split(":", 1)[1].strip()
    return None


def read_texture_meta(path):
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()

    def find(pattern, default=None):
        match = re.search(pattern, text)
        return match.group(1) if match else default

    return dict(
        guid=find(r"^guid: (\w+)$"),
        sRGB=find(r"sRGBTexture: (\d)") == "1",
        texture_type=find(r"textureType: (-?\d+)"),
        wrap_u=find(r"wrapU: (\d)"),
        mipmaps=find(r"enableMipMap: (\d)") == "1",
        max_size=int(find(r"maxTextureSize: (\d+)", "0")),
    )


# ---------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------
def check_materials():
    print("\n[1] Materials")
    materials = {}
    for role in ROLES:
        path = os.path.join(MAT_DIR, "M_Hero_%s.mat" % role)
        if not check(os.path.isfile(path), "M_Hero_%s.mat exists (%s)" % (role, ROLE_KIND[role])):
            continue
        materials[role] = parse_material(path)
        check(os.path.isfile(path + ".meta"), "M_Hero_%s.mat.meta exists" % role)

    for role, material in materials.items():
        check(material["shader_guid"] == URP_LIT_GUID,
              "%s uses URP Lit" % material["name"])
        check(material["keywords"] == REQUIRED_KEYWORDS,
              "%s enables %s" % (material["name"], ", ".join(sorted(REQUIRED_KEYWORDS))))

    base_maps = {material["textures"].get("_BaseMap") for material in materials.values()}
    check(len(base_maps) == len(materials), "every role has its own base colour map")

    check(os.path.isfile(os.path.join(MAT_DIR, "M_Hero_Belt.mat")) is False,
          "the pre-material-pass M_Hero_Belt.mat is retired (superseded by M_Hero_Metal.mat)")
    return materials


def check_metallic_policy(materials):
    print("\n[2] Metallic policy and smoothness workflow")
    metallic = {role: material["floats"].get("_Metallic", 1.0) for role, material in materials.items()}
    metallic_roles = [role for role, value in metallic.items() if value > 0.5]
    check(metallic_roles == ["Metal"],
          "exactly one metallic role (%s) - no excessive metallic surfaces" % ", ".join(metallic_roles or ["none"]))
    for role, value in metallic.items():
        if role == "Metal":
            check(value >= 0.5, "Metal uses metallic=%.2f as its scalar fallback" % value)
        else:
            check(value <= 0.2, "%s is a dielectric (_Metallic=%s)" % (role, value))

    for role, material in materials.items():
        check(abs(material["floats"].get("_Smoothness", 0.0) - 1.0) < 1e-4,
              "%s keeps _Smoothness = 1 so the mask alpha is authoritative" % role)
        check(material["floats"].get("_SmoothnessTextureChannel", 1.0) == 0.0,
              "%s takes smoothness from the metallic alpha channel" % role)
        tint = material["colors"].get("_BaseColor", (0, 0, 0, 1))
        luma = 0.2126 * tint[0] + 0.7152 * tint[1] + 0.0722 * tint[2]
        check(0.005 < luma <= 1.0, "%s base colour tint is neither pure black nor bleached (%.2f)" % (role, luma))


def check_textures(materials):
    print("\n[3] Textures and channel content")
    metas = {}
    for role in ROLES:
        for suffix in ("BaseColor", "Normal", "Mask"):
            path = os.path.join(TEX_DIR, "T_Hero_%s_%s.png" % (role, suffix))
            check(os.path.isfile(path), "T_Hero_%s_%s.png exists" % (role, suffix))
            if not os.path.isfile(path):
                continue
            meta = read_texture_meta(path + ".meta")
            metas[(role, suffix)] = meta
            png = read_png(path)
            check(png["width"] == png["height"] and (png["width"] & (png["width"] - 1)) == 0,
                  "%s/%s is square and power of two (%dx%d)" % (role, suffix, png["width"], png["height"]))
            check(meta["wrap_u"] == "0", "%s/%s uses wrap mode Repeat" % (role, suffix))
            check(meta["mipmaps"], "%s/%s has mipmaps" % (role, suffix))
            check(meta["sRGB"] == (suffix == "BaseColor"),
                  "%s/%s colour space is %s" % (role, suffix, "sRGB" if suffix == "BaseColor" else "linear"))
            check(meta["texture_type"] == ("1" if suffix == "Normal" else "0"),
                  "%s/%s texture type is %s" % (role, suffix, "NormalMap" if suffix == "Normal" else "Default"))

            if suffix == "Normal":
                check(png["channels"] == 4, "%s/Normal carries an alpha channel (DXT5nm-safe)" % role)
                samples = list(sample_rows(png, 23))
                ok = all(abs(r - a) <= 1 for r, g, b, a in samples)
                check(ok, "%s/Normal alpha mirrors red, so RGB and AG decoding match" % role)
            if suffix == "Mask":
                samples = list(sample_rows(png, 11))
                metallics = [r / 255.0 for r, g, b, a in samples]
                smoothness = [a / 255.0 for r, g, b, a in samples]
                occlusion = [g / 255.0 for r, g, b, a in samples]
                blues = [b / 255.0 for r, g, b, a in samples]
                alpha_std = max(smoothness) - min(smoothness)
                low, high = EXPECTED_SMOOTHNESS[role]

                if role == "Metal":
                    check(min(metallics) > 0.5, "Metal mask R is metallic everywhere (%.2f-%.2f)" %
                          (min(metallics), max(metallics)))
                else:
                    check(max(metallics) < 0.02, "%s mask R is dielectric everywhere" % role)
                check(low <= min(smoothness) and max(smoothness) <= high,
                      "%s mask A smoothness stays in [%.2f, %.2f] (%.2f-%.2f)" %
                      (role, low, high, min(smoothness), max(smoothness)))
                check(alpha_std > 0.03, "%s roughness varies per pixel (range %.3f)" % (role, alpha_std))
                check(min(occlusion) > 0.4, "%s mask G occlusion is present (%.2f-%.2f)" %
                      (role, min(occlusion), max(occlusion)))
                check(max(blues) < 0.02, "%s mask B is unused (0)" % role)

    # The albedo of each role must not be a flat fill, and not black.
    print("\n[4] Albedo content")
    for role in ROLES:
        png = read_png(os.path.join(TEX_DIR, "T_Hero_%s_BaseColor.png" % role))
        samples = list(sample_rows(png, 11))
        lumas = [(0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 for r, g, b, a in samples]
        spread = max(lumas) - min(lumas)
        check(spread > 0.02, "%s albedo varies per pixel (luma range %.3f, not a flat colour)" % (role, spread))
        check(max(lumas) > 0.02, "%s albedo is not black (max luma %.3f)" % (role, max(lumas)))


def check_code_and_scene(materials):
    print("\n[5] Code and scene wiring")
    with open(HERO_SCRIPT, "r", encoding="utf-8") as handle:
        hero = handle.read()
    with open(MATERIALS_SCRIPT, "r", encoding="utf-8") as handle:
        library = handle.read()

    check("beltMaterial" not in hero, "HeroCharacterVisual no longer references a belt material")
    for role in ROLES:
        check("HeroMaterialRole.%s" % role in hero or role == "Metal" and "Metal" in hero,
              "HeroCharacterVisual uses the %s role" % role)
    check("beltMaterial" not in library and "M_Hero_Belt" not in library,
          "HeroMaterials has no belt role left over")
    check("RecalculateTangents" in hero,
          "the visual generates tangents for the built-in primitives (normal maps sample correctly)")
    check("CreatePrimitive" in hero and "AddComponent<" not in hero.split("CreateVisualHierarchy")[1],
          "the visual only builds primitives - no components, colliders or animation are added")
    check("RoleCount = 7" in library, "HeroMaterials declares seven roles")

    with open(SCENE, "r", encoding="utf-8") as handle:
        scene = handle.read()

    for role in ROLES:
        path = os.path.join(MAT_DIR, "M_Hero_%s.mat.meta" % role)
        guid = read_meta_guid(path)
        check(guid is not None and ("guid: %s, type: 2}" % guid) in scene,
              "CombatTestScene references M_Hero_%s" % role)

    check("HeroMaterialTest" in scene, "CombatTestScene contains the HeroMaterialTest object")

    # Every role's textures are actually referenced by its material.
    for role, material in materials.items():
        referenced = set(material["textures"].values())
        for suffix, property_name in (("BaseColor", "_BaseMap"), ("Normal", "_BumpMap"), ("Mask", "_MetallicGlossMap")):
            path = os.path.join(TEX_DIR, "T_Hero_%s_%s.png.meta" % (role, suffix))
            guid = read_meta_guid(path)
            check(guid in referenced,
                  "M_Hero_%s references its %s map through the texture GUID" % (role, suffix))


def main():
    print("Hero material verification (static)")
    print("Materials: %s" % os.path.relpath(MAT_DIR, REPO_ROOT))
    print("Textures:  %s" % os.path.relpath(TEX_DIR, REPO_ROOT))

    materials = check_materials()
    check_metallic_policy(materials)
    check_textures(materials)
    check_code_and_scene(materials)

    total = len(PASSED) + len(FAILED)
    print("\nChecks passed: %d/%d" % (len(PASSED), total))
    if FAILED:
        print("\nFailed checks:")
        for description in FAILED:
            print("  - " + description)
        print("HERO MATERIAL VERIFICATION FAILED")
        return 1

    print("HERO MATERIAL VERIFICATION PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
