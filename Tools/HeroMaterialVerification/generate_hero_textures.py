#!/usr/bin/env python3
"""Deterministic generator for the hero PBR texture set (Assets/Art/Textures/Hero).

The hero is built at runtime out of Unity primitives (see
Assets/Scripts/Player/HeroCharacterVisual.cs). Unity's built-in Cube/Sphere/Capsule/Cylinder
meshes give every face a full 0..1 UV square, so every texture in this set is authored as a
*tileable* square: the same map is used by chest, cape, shoulder, boot and glove faces and it has
to stay seamless wherever it lands.

For each of the seven hero materials the generator writes three maps:

  T_Hero_<Name>_BaseColor.png  sRGB albedo - colour plus baked micro variation, never flat fill
  T_Hero_<Name>_Normal.png     linear tangent-space normal map (surface micro detail)
  T_Hero_<Name>_Mask.png       linear packed mask: R = metallic, G = occlusion,
                               A = smoothness, B = 0 (URP Lit channel-packed layout)

URP Lit multiplies the metallic-gloss alpha by the material's `_Smoothness` slider, so every hero
material keeps `_Smoothness = 1` and the authored value lives in the mask alpha.

Everything is procedural, seeded and reproducible: re-running the script rewrites byte-identical
PNGs. Missing numpy/Pillow is reported with an actionable message instead of a traceback.

Run:  python3 Tools/HeroMaterialVerification/generate_hero_textures.py
"""

import hashlib
import os
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
OUT_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Textures", "Hero")

TAU = 2.0 * np.pi


# ---------------------------------------------------------------------------
# Seeded, tileable noise primitives
# ---------------------------------------------------------------------------
def make_rng(name):
    """Stable per-material RNG (Python's hash() is salted, so use md5)."""
    digest = hashlib.md5(("MobileGame.HeroTextures." + name).encode("utf-8")).digest()
    return np.random.default_rng(int.from_bytes(digest[:8], "little"))


def _interp_axis(length, cells):
    """Wrap-aware smoothstep interpolation indices/weights for one axis."""
    t = np.arange(length, dtype=np.float64) * (cells / float(length))
    i0 = np.floor(t).astype(np.int64)
    frac = t - i0
    frac = frac * frac * (3.0 - 2.0 * frac)
    return (i0 % cells), ((i0 + 1) % cells), frac


def noise(size, freq_u, freq_v, rng):
    """Tileable smooth value noise on a (freq_v x freq_u) random grid."""
    grid = rng.random((freq_v, freq_u))
    iy0, iy1, wy = _interp_axis(size, freq_v)
    ix0, ix1, wx = _interp_axis(size, freq_u)
    a = grid[np.ix_(iy0, ix0)]
    b = grid[np.ix_(iy0, ix1)]
    c = grid[np.ix_(iy1, ix0)]
    d = grid[np.ix_(iy1, ix1)]
    wx = wx[None, :]
    wy = wy[:, None]
    return (a * (1.0 - wx) + b * wx) * (1.0 - wy) + (c * (1.0 - wx) + d * wx) * wy


def fbm(size, freq, octaves, rng, persistence=0.5, stretch=1):
    """Fractal value noise. `freq * 2**(octaves-1) * stretch` must divide `size`."""
    total = np.zeros((size, size), dtype=np.float64)
    amp = 1.0
    norm = 0.0
    fu, fv = freq * stretch, freq
    for _ in range(octaves):
        total += amp * noise(size, fu, fv, rng)
        norm += amp
        amp *= persistence
        fu *= 2
        fv *= 2
    return total / norm


def ridged(size, freq, octaves, rng, stretch=1):
    """Ridged noise for creases / wrinkles / scratches (1 = crease line)."""
    return 1.0 - np.abs(fbm(size, freq, octaves, rng, stretch=stretch) * 2.0 - 1.0)


def cellular(size, cells, rng, jitter=1.0):
    """Tileable Worley F1 distance in cell units (~0 at a cell centre, ~1 at the border)."""
    pts = (rng.random((cells, cells, 2)) - 0.5) * jitter + 0.5
    idx = np.arange(size, dtype=np.int64) * cells // size
    yy, xx = np.meshgrid(idx, idx, indexing="ij")
    uy = (yy + 0.5) / cells
    ux = (xx + 0.5) / cells
    best = np.full((size, size), 1e9)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            p = pts[(yy + dy) % cells, (xx + dx) % cells]
            d = np.hypot(ux - (p[..., 0] + dx), uy - (p[..., 1] + dy))
            np.minimum(best, d, out=best)
    return best * cells


def warp_field(field, warp_x, warp_y):
    """Periodic domain warp (indices wrap, so the result stays tileable)."""
    size = field.shape[0]
    y, x = np.meshgrid(np.arange(size), np.arange(size), indexing="ij")
    xs = (x + np.rint(warp_x).astype(np.int64)) % size
    ys = (y + np.rint(warp_y).astype(np.int64)) % size
    return field[ys, xs]


def box_blur_periodic(field, radius):
    """Separable box blur with wrap-around (used to derive occlusion from height)."""
    if radius < 1:
        return field.copy()
    out = field
    for axis in (0, 1):
        cum = np.cumsum(np.concatenate([out, out, out], axis=axis), axis=axis)
        n = out.shape[axis]
        i = np.arange(n)
        lo = np.take(cum, n + i - radius - 1, axis=axis)
        hi = np.take(cum, n + i + radius, axis=axis)
        out = (hi - lo) / float(2 * radius + 1)
    return out


def normal_from_height(height, strength):
    """Tangent-space normal map from a height field.

    Tangents: +X = +U, +Y = +V, +Z = out. PNG row 0 is v = 1, so dH/dv = -dH/drow.
    """
    dh_du = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    dh_drow = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5
    dh_dv = -dh_drow
    nx = -dh_du * strength
    ny = -dh_dv * strength
    nz = np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.stack([nx / length, ny / length, nz / length], axis=-1)


def ao_from_height(height, radius, strength, bias=0.0):
    """Concave-only ambient occlusion from a height field."""
    blurred = box_blur_periodic(height, radius)
    cavity = np.clip(blurred - height, 0.0, None)
    return np.clip(1.0 - strength * cavity - bias, 0.0, 1.0)


def smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / max(edge1 - edge0, 1e-9), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def resize(field, size):
    """Wrap-safe area resize of a 1-channel float field (padding keeps the tiling seamless)."""
    if field.shape[0] == size and field.shape[1] == size:
        return field
    pad = field.shape[0] // 8
    padded = np.pad(field, pad, mode="wrap")
    img = Image.fromarray(np.clip(padded * 255.0, 0, 255).astype(np.uint8), "L")
    scaled = padded.shape[0] + (size * 2 * pad) // field.shape[0] - padded.shape[0]
    scaled = size + 2 * max(1, (pad * size) // field.shape[0])
    img = img.resize((scaled, scaled), Image.LANCZOS)
    out = np.asarray(img, dtype=np.float64) / 255.0
    crop = (scaled - size) // 2
    return out[crop:crop + size, crop:crop + size]


def uv_grid(size):
    u = (np.arange(size, dtype=np.float64) + 0.5) / size
    return np.meshgrid(u, u, indexing="xy")


def tile_v_gradient(size):
    """Texture-space v: 0 at the bottom of the UV square, 1 at the top (PNG row 0 = v 1)."""
    v = 1.0 - (np.arange(size, dtype=np.float64) + 0.5) / size
    return np.repeat(v[:, None], size, axis=1)


def edge_fade(u, v, width):
    """0 inside the UV square, 1 on its border (used for stitching / trim shading)."""
    d = np.minimum(np.minimum(u, 1.0 - u), np.minimum(v, 1.0 - v))
    return 1.0 - np.clip(d / width, 0.0, 1.0)


def tint(color, scale):
    """sRGB colour modulated by a scalar or a per-pixel field, clamped to valid sRGB."""
    rgb = np.asarray(color, dtype=np.float64)
    if np.isscalar(scale):
        return np.clip(rgb * scale, 0.0, 1.0)
    return np.clip(scale[..., None] * rgb[None, None, :], 0.0, 1.0)


def weave_pattern(size, threads, over_under=True):
    """Plain-woven cloth: warp/weft threads alternating over and under each other."""
    u = np.arange(size, dtype=np.float64)[None, :] * threads / size
    v = np.arange(size, dtype=np.float64)[:, None] * threads / size
    warp = np.cos(TAU * u) * 0.5 + 0.5
    weft = np.cos(TAU * v) * 0.5 + 0.5
    warp = np.broadcast_to(warp, (size, size))
    weft = np.broadcast_to(weft, (size, size))
    if not over_under:
        return np.maximum(warp, weft)
    over = ((np.floor(u) + np.floor(v)) % 2) == 0
    return np.where(over, warp, weft)


# ---------------------------------------------------------------------------
# PNG helpers
# ---------------------------------------------------------------------------
def save_rgb(path, rgb):
    Image.fromarray(np.clip(rgb * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGB").save(path, optimize=True)


def save_rgba(path, rgba):
    Image.fromarray(np.clip(rgba * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGBA").save(path, optimize=True)


def save_normal(path, normal_xyz):
    """Normal maps carry alpha = red channel.

    Unity's Lit shaders decode a normal map either as plain RGB or, on platforms that compress to
    DXT5nm / use the AG layout, as (x = alpha, y = green, z = reconstructed). Writing A = R makes
    both decode paths produce the identical vector, so the detail is correct on every target
    (Android Vulkan/GLES, Metal, desktop D3D) without depending on an importer swizzle.
    """
    encoded = encode_normal(normal_xyz)
    rgba = np.concatenate([encoded, encoded[..., 0:1]], axis=-1)
    save_rgba(path, rgba)


def encode_normal(normal_xyz):
    return normal_xyz * 0.5 + 0.5


# ---------------------------------------------------------------------------
# Material pattern builders
# ---------------------------------------------------------------------------
def build_skin(rng, sizes):
    """Skin: warm mid tone, fine pores and creases, matte micro roughness - never plastic."""
    n = sizes["normal"]
    pores = 1.0 - cellular(n, n // 8, rng)
    pores_fine = 1.0 - cellular(n, n // 4, rng)
    pore_field = np.clip(pores, 0, 1) ** 1.6 * 0.75 + np.clip(pores_fine, 0, 1) ** 2.0 * 0.25
    creases = smoothstep(0.86, 0.99, ridged(n, 8, 5, rng))
    wrinkle = smoothstep(0.80, 1.0, ridged(n, 16, 4, rng)) * 0.35

    height = -pore_field * 0.55 - creases * 0.35 - wrinkle * 0.25 + fbm(n, 16, 5, rng) * 0.06
    normal = normal_from_height(height, strength=2.2)
    ao = ao_from_height(height, radius=max(1, n // 128), strength=1.1, bias=0.02)

    a = sizes["albedo"]
    tone = fbm(a, 4, 5, rng)
    blotch = fbm(a, 16, 4, rng)
    warmth = smoothstep(0.55, 0.95, fbm(a, 8, 4, rng))
    creases_a = smoothstep(0.80, 1.0, ridged(a, 8, 5, rng))

    base = np.array([0.876, 0.700, 0.588])                 # sRGB light-medium warm skin
    albedo = tint(base, 0.94 + 0.10 * tone - 0.10 * creases_a)
    albedo[..., 0] = np.clip(albedo[..., 0] + 0.050 * warmth, 0, 1)      # cheeks/ears warm up
    albedo[..., 1] = np.clip(albedo[..., 1] - 0.035 * warmth + 0.020 * blotch, 0, 1)
    albedo[..., 2] = np.clip(albedo[..., 2] - 0.030 * warmth - 0.010 * creases_a, 0, 1)

    pore_a = resize(pore_field, a)
    smooth_patch = 0.42 + 0.10 * (fbm(a, 4, 3, rng) - 0.5) * 2.0
    smooth_map = np.clip(smooth_patch - 0.14 * pore_a - 0.12 * creases_a, 0.18, 0.62)

    mask = np.zeros((a, a, 4))
    mask[..., 0] = 0.0                                      # dielectric
    mask[..., 1] = 0.90 + 0.10 * resize(ao, a)              # occlusion in the creases only
    mask[..., 3] = smooth_map
    return dict(albedo=(a, albedo), normal=(n, encode_normal(normal)), mask=(a, mask))


def build_hair(rng, sizes):
    """Hair: deep blue-black strands with a streaky sheen - dark, but never a black hole."""
    strands_n = max(8, sizes["normal"] // 6)
    strands_a = max(8, sizes["albedo"] // 6)

    n = sizes["normal"]
    u_n, v_n = uv_grid(n)
    flow = (fbm(n, 8, 3, rng) * 0.10 + fbm(n, 16, 4, rng) * 0.06 + fbm(n, 64, 3, rng) * 0.02)
    strand_shape = np.clip(np.sin(TAU * strands_n * u_n + flow * TAU * 2.5), -1.0, 1.0) * 0.5 + 0.5
    along = fbm(n, 64, 4, rng)
    tip = smoothstep(0.15, 0.95, tile_v_gradient(n)) * 0.25

    height = strand_shape ** 1.4 * 0.85 + along * 0.15 + tip * 0.10
    normal = normal_from_height(height, strength=2.6)
    ao = ao_from_height(height, radius=max(1, n // 96), strength=1.3, bias=0.03)

    a = sizes["albedo"]
    u_a, _ = uv_grid(a)
    flow_a = fbm(a, 8, 3, rng) * 0.10 + fbm(a, 16, 4, rng) * 0.06 + fbm(a, 32, 3, rng) * 0.02
    strand_a = np.clip(np.sin(TAU * strands_a * u_a + flow_a * TAU * 2.0), -1.0, 1.0) * 0.5 + 0.5
    along_a = fbm(a, 32, 4, rng)
    v_a = tile_v_gradient(a)

    shades = 0.86 + 0.42 * strand_a + 0.18 * (along_a - 0.5) + 0.20 * v_a
    base = np.array([0.216, 0.235, 0.310])                 # sRGB blue-black (deliberately not #000)
    albedo = tint(base, shades / shades.mean())

    smooth_map = np.clip(0.34 + 0.22 * strand_a + 0.10 * (fbm(a, 8, 3, rng) - 0.5), 0.14, 0.68)

    mask = np.zeros((a, a, 4))
    mask[..., 0] = 0.0
    mask[..., 1] = 0.86 + 0.14 * resize(ao, a)
    mask[..., 3] = smooth_map
    return dict(albedo=(a, albedo), normal=(n, encode_normal(normal)), mask=(a, mask))


def build_suit(rng, sizes):
    """Primary costume: deep midnight-teal weave, panel stitching and soft folds."""
    a, n = sizes["albedo"], sizes["normal"]
    threads = a // 8                    # 64 threads across the face: readable, not aliasing mush


    weave_n = weave_pattern(n, threads)
    fuzz_n = fbm(n, 64, 4, rng)
    folds = fbm(n, 6, 5, rng)
    folds = warp_field(folds,
                       (fbm(n, 4, 3, rng) - 0.5) * 26.0,
                       (fbm(n, 4, 3, rng) - 0.5) * 26.0)
    fold_mask = smoothstep(0.35, 0.85, folds)

    u_n, v_n = uv_grid(n)
    seam = smoothstep(0.45, 1.0, edge_fade(u_n, v_n, 0.02))

    height = weave_n * 0.30 + fuzz_n * 0.10 - fold_mask * 0.42 - seam * 0.55
    normal = normal_from_height(height, strength=1.9)
    ao = ao_from_height(height, radius=max(1, n // 64), strength=1.6, bias=0.02)

    weave_a = weave_pattern(a, threads)
    fuzz_a = fbm(a, 32, 4, rng)
    folds_a = resize(fold_mask, a)
    seam_a = resize(seam, a)

    base = np.array([0.212, 0.353, 0.494])                 # sRGB deep teal-blue (reads in shadow)
    shade = 0.92 + 0.30 * weave_a + 0.12 * (fuzz_a - 0.5) - 0.32 * folds_a - 0.38 * seam_a
    albedo = tint(base, shade)
    albedo = np.clip(albedo * (1.0 + 0.10 * (weave_a - 0.5)[..., None]), 0, 1)   # thread sheen

    smooth_map = np.clip(0.20 + 0.16 * weave_a + 0.08 * (fuzz_a - 0.5)
                         - 0.10 * folds_a - 0.06 * seam_a, 0.08, 0.50)

    mask = np.zeros((a, a, 4))
    mask[..., 0] = 0.0
    mask[..., 1] = 0.80 + 0.20 * resize(ao, a)
    mask[..., 3] = smooth_map
    return dict(albedo=(a, albedo), normal=(n, encode_normal(normal)), mask=(a, mask))


def build_accent(rng, sizes):
    """Secondary costume: electric-cyan ripstop trim, matte with a light sheen."""
    size = sizes["albedo"]
    u, v = uv_grid(size)

    ripstop = weave_pattern(size, 64, over_under=False)
    grid = np.maximum(np.cos(TAU * 8 * u) * 0.5 + 0.5, np.cos(TAU * 8 * v) * 0.5 + 0.5)
    fuzz = fbm(size, 32, 4, rng)
    trim = smoothstep(0.45, 1.0, edge_fade(u, v, 0.025))

    height = ripstop * 0.26 + grid * 0.30 + fuzz * 0.12 - trim * 0.30
    normal = normal_from_height(height, strength=1.7)
    ao = ao_from_height(height, radius=max(1, size // 48), strength=1.2, bias=0.02)

    base = np.array([0.129, 0.663, 0.808])                 # sRGB electric cyan (vivid, not blown out)
    shade = 0.92 + 0.14 * grid + 0.10 * (ripstop - 0.5) + 0.08 * (fuzz - 0.5) - 0.16 * trim
    albedo = tint(base, shade)
    albedo[..., 2] = np.clip(albedo[..., 2] * (1.0 + 0.06 * (fuzz - 0.5)), 0, 1)
    albedo[..., 0] = np.clip(albedo[..., 0] * (1.0 + 0.10 * (grid - 0.5)), 0, 1)

    smooth_map = np.clip(0.40 + 0.10 * grid + 0.08 * (fuzz - 0.5) - 0.06 * trim, 0.22, 0.62)
    mask = np.zeros((size, size, 4))
    mask[..., 0] = 0.0
    mask[..., 1] = 0.84 + 0.16 * ao
    mask[..., 3] = smooth_map
    return dict(albedo=(size, albedo), normal=(size, encode_normal(normal)), mask=(size, mask))


def build_boots(rng, sizes):
    """Boots: warm bone leather, pebbled grain, stitching, scuffs and a worn sole gradient."""
    size = sizes["normal"]
    u, v = uv_grid(size)

    pebble = np.clip(1.0 - cellular(size, size // 12, rng, jitter=1.05), 0, 1) ** 1.25
    grain = np.clip(1.0 - cellular(size, size // 28, rng), 0, 1) ** 1.6
    leather = pebble * 0.6 + grain * 0.4

    scuffs = smoothstep(0.90, 1.0, ridged(size, 24, 4, rng))
    scuff_smear = np.clip(box_blur_periodic(scuffs, max(1, size // 128)), 0, 1)
    stitch_line = 1.0 - np.clip(np.abs(tile_v_gradient(size) - 0.86) * 220.0, 0, 1)
    stitches = stitch_line * (np.cos(TAU * 28 * u) * 0.5 + 0.5)
    crease = (1.0 - np.clip(np.abs(tile_v_gradient(size) - 0.20) * 26.0, 0, 1)) * 0.7

    height = leather * 0.55 - crease * 0.45 - stitches * 0.18 - scuff_smear * 0.10
    normal = normal_from_height(height, strength=2.0)
    ao = ao_from_height(height, radius=max(1, size // 96), strength=1.5, bias=0.02)

    base = np.array([0.902, 0.886, 0.835])                 # sRGB bone / off-white leather
    sole = smoothstep(0.16, 0.0, tile_v_gradient(size))    # ground dirt creeps up from the sole
    wear = smoothstep(0.30, 0.95, fbm(size, 8, 4, rng))
    shade = (1.00 + 0.10 * leather + 0.05 * (fbm(size, 16, 4, rng) - 0.5)
             - 0.22 * sole - 0.08 * wear - 0.14 * crease - 0.10 * scuff_smear)
    albedo = tint(base, shade)
    albedo[..., 2] = np.clip(albedo[..., 2] - 0.07 * sole, 0, 1)     # dirt drifts warm
    albedo[..., 1] = np.clip(albedo[..., 1] - 0.03 * sole, 0, 1)

    smooth_map = np.clip(0.30 + 0.16 * leather + 0.12 * wear - 0.10 * sole
                         + 0.14 * scuff_smear, 0.12, 0.66)
    mask = np.zeros((size, size, 4))
    mask[..., 0] = 0.0
    mask[..., 1] = 0.78 + 0.22 * ao
    mask[..., 3] = smooth_map
    return dict(albedo=(size, albedo), normal=(size, encode_normal(normal)), mask=(size, mask))


def build_gloves(rng, sizes):
    """Gloves: cool light-gray grip leather - finer grain, quilting, finger seams, knuckles.

    Deliberately a different material family than the boots (hue, grain scale, seam layout and
    roughness curve all differ) so the two light materials never read as one shared white.
    """
    size = sizes["normal"]
    u, v = uv_grid(size)

    fine = np.clip(1.0 - cellular(size, size // 34, rng, jitter=0.9), 0, 1) ** 1.5
    quilt = np.maximum(np.cos(TAU * 12 * (u + v)) * 0.5 + 0.5,
                       np.cos(TAU * 12 * (u - v)) * 0.5 + 0.5)

    seam = np.zeros((size, size))
    for centre in (0.25, 0.5, 0.75):
        seam = np.maximum(seam, 1.0 - np.clip(np.abs(u - centre) * 120.0, 0, 1))

    knuckle = np.zeros((size, size))
    for centre_u, centre_v in ((0.2, 0.78), (0.5, 0.80), (0.8, 0.78), (0.35, 0.62), (0.65, 0.62)):
        knuckle += np.exp(-(((u - centre_u) ** 2 * 2.4 + (v - centre_v) ** 2) * 260.0))
    knuckle = np.clip(knuckle, 0, 1)

    height = fine * 0.42 + quilt * 0.30 + knuckle * 0.26 - seam * 0.50
    normal = normal_from_height(height, strength=2.1)
    ao = ao_from_height(height, radius=max(1, size // 96), strength=1.4, bias=0.02)

    base = np.array([0.836, 0.855, 0.878])                 # sRGB cool light gray
    grime = smoothstep(0.45, 0.95, fbm(size, 8, 4, rng))
    shade = 0.99 + 0.12 * fine + 0.06 * (quilt - 0.5) - 0.28 * seam - 0.08 * grime + 0.05 * knuckle
    albedo = tint(base, shade)
    albedo[..., 0] = np.clip(albedo[..., 0] - 0.03 * grime, 0, 1)
    albedo[..., 2] = np.clip(albedo[..., 2] + 0.02 * (1.0 - grime), 0, 1)

    smooth_map = np.clip(0.34 + 0.18 * fine + 0.14 * knuckle + 0.10 * quilt - 0.12 * seam, 0.18, 0.70)
    mask = np.zeros((size, size, 4))
    mask[..., 0] = 0.0
    mask[..., 1] = 0.80 + 0.20 * ao
    mask[..., 3] = smooth_map
    return dict(albedo=(size, albedo), normal=(size, encode_normal(normal)), mask=(size, mask))


def build_metal(rng, sizes):
    """Metallic details: brushed brass with worn highlights and mild tarnish."""
    size = sizes["normal"]
    u, _ = uv_grid(size)

    brush = fbm(size, 4, 4, rng, stretch=16)                     # stretched along u: brush lines
    micro = np.sin(TAU * 32 * u + brush * TAU * 2.0) * 0.5 + 0.5
    tarnish = smoothstep(0.42, 0.92, fbm(size, 6, 4, rng))
    dents = smoothstep(0.88, 1.0, ridged(size, 20, 3, rng))

    height = micro * 0.30 + (brush - 0.5) * 0.35 - dents * 0.25
    normal = normal_from_height(height, strength=1.5)
    ao = ao_from_height(height, radius=max(1, size // 64), strength=1.0, bias=0.02)

    base = np.array([0.867, 0.686, 0.325])                       # sRGB brass / gold
    tarnish_tint = np.array([0.588, 0.502, 0.286])
    blend = (tarnish * 0.75)[..., None]
    albedo = np.clip(tint(base, 0.94 + 0.12 * (brush - 0.5) - 0.10 * dents) * (1.0 - blend)
                     + tint(tarnish_tint, 0.90 + 0.10 * (brush - 0.5)) * blend, 0, 1)

    metallic = 0.92 - 0.16 * tarnish                # tarnish is metal, just less reflective
    smooth_map = np.clip(0.34 + 0.18 * (brush - 0.5) + 0.14 * micro - 0.12 * tarnish
                         + 0.10 * dents, 0.16, 0.66)

    mask = np.zeros((size, size, 4))
    mask[..., 0] = metallic
    mask[..., 1] = 0.86 + 0.14 * ao
    mask[..., 3] = smooth_map
    return dict(albedo=(size, albedo), normal=(size, encode_normal(normal)), mask=(size, mask))


# ---------------------------------------------------------------------------
# Material table
# ---------------------------------------------------------------------------
MATERIALS = [
    ("Skin", 256, 512, 256, build_skin),
    ("Hair", 256, 512, 256, build_hair),
    ("Suit", 512, 512, 256, build_suit),
    ("Accent", 256, 256, 256, build_accent),
    ("Boots", 256, 256, 256, build_boots),
    ("Gloves", 256, 256, 256, build_gloves),
    ("Metal", 256, 256, 256, build_metal),
]


def main():
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)

    print("Generating hero PBR textures into %s" % os.path.relpath(OUT_DIR, REPO_ROOT))
    print("%-7s %-24s %-13s %-22s %-12s %s" % (
        "MATERIAL", "FILE", "PIXELS", "MEAN ALBEDO (sRGB)", "SMOOTHNESS", "METALLIC"))

    total_bytes = 0
    for name, a_size, n_size, m_size, builder in MATERIALS:
        rng = make_rng(name)
        result = builder(rng, dict(albedo=a_size, normal=n_size, mask=m_size))

        for kind, suffix, saver in (("albedo", "BaseColor", save_rgb),
                                    ("normal", "Normal", save_normal),
                                    ("mask", "Mask", save_rgba)):
            size, data = result[kind]
            path = os.path.join(OUT_DIR, "T_Hero_%s_%s.png" % (name, suffix))
            saver(path, data)
            total_bytes += os.path.getsize(path)

        albedo = result["albedo"][1]
        mask = result["mask"][1]
        print("%-7s %-24s %-13s %-22s %-12s %s" % (
            name,
            "T_Hero_%s_*" % name,
            "%d/%d/%d" % (result["albedo"][0], result["normal"][0], result["mask"][0]),
            "%.3f %.3f %.3f" % tuple(albedo.reshape(-1, 3).mean(axis=0)),
            "%.2f-%.2f" % (mask[..., 3].min(), mask[..., 3].max()),
            "%.2f-%.2f" % (mask[..., 0].min(), mask[..., 0].max()),
        ))

    print("\nWrote 21 PNGs, %.2f MB total." % (total_bytes / (1024.0 * 1024.0)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
