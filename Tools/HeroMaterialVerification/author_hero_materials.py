#!/usr/bin/env python3
"""Authors the seven hero PBR materials and their texture import settings.

Writes, deterministically:

  Assets/Art/Materials/Hero/M_Hero_<Role>.mat (+ .meta)    URP Lit materials, one per body part
  Assets/Art/Textures/Hero/*.meta                          texture import settings for the PNGs

The seven roles, and why each one exists:

  Skin      - skin      : dielectric, pore/crease normals, matte roughness variation (no plastic)
  Hair      - hair      : dielectric, strand normals, streaky sheen, dark blue-black base
  Suit      - primary   : dielectric cloth weave, fold normals, per-pixel roughness + occlusion
  Accent    - secondary : dielectric ripstop trim with a light sheen (the cyan graphic language)
  Boots     - boots     : dielectric leather grain, stitching, scuffs, dirt gradient
  Gloves    - gloves    : dielectric fine-grain grip leather, quilting, knuckle pads
  Metal     - metallic  : the only metal - brass belt/buckle/emblem ring/visor bolts

All seven use the same keyword set (`_NORMALMAP`, `_METALLICGLOSSMAP`, `_OCCLUSIONMAP`) so they stay in
a single SRP Batcher variant; only the mask contents differ. `_Smoothness` is pinned to 1 on every
material because URP multiplies it into the mask alpha, and the authored roughness lives in that
alpha channel.

Run:  python3 Tools/HeroMaterialVerification/author_hero_materials.py [--check]
"""

import hashlib
import os
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAT_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Materials", "Hero")
TEX_DIR = os.path.join(REPO_ROOT, "Assets", "Art", "Textures", "Hero")

URP_LIT_SHADER = "{fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}"
TEXTURE_REF = "{fileID: 2800000, guid: %s, type: 3}"

# Roles are the single source of truth: HeroCharacterVisual.cs reads the same seven names.
MATERIALS = [
    # role,     texture stem,   bump scale, occlusion strength, metallic note
    ("Skin",   "Skin",   1.00, 0.85),
    ("Hair",   "Hair",   1.15, 0.90),
    ("Suit",   "Suit",   1.00, 0.95),
    ("Accent", "Accent", 0.85, 0.80),
    ("Boots",  "Boots",  1.00, 0.90),
    ("Gloves", "Gloves", 1.00, 0.85),
    ("Metal",  "Metal",  0.70, 0.80),
]

# Existing assets keep their GUIDs so no scene or prefab reference breaks.
FIXED_MATERIAL_GUIDS = {
    "Skin": "8f9a3f76f92e49779e6b0cb223ec0590",
    "Hair": "3f5a3334c7154ee19d43d577f8a90474",
    "Suit": "b7751b8902cc46749d0d7b584f731fb0",
    "Accent": "9f3940ee892842cf915595a683a7bc2c",
    "Boots": "b96c638082914c5c8257e3e1aa2c312c",
}

OBSOLETE_ASSETS = ["M_Hero_Belt.mat", "M_Hero_Belt.mat.meta"]

MATERIAL_TEMPLATE = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Shader: {shader}
  m_Parent: {{fileID: 0}}
  m_ModifiedSerializedProperties: 0
  m_ValidKeywords:{keywords}
  m_InvalidKeywords: []
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 1
  m_DoubleSidedGI: 0
  m_CustomRenderQueue: -1
  stringTagMap:
    RenderType: Opaque
  disabledShaderPasses: []
  m_LockedProperties: 
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
{texenvs}    m_Ints: []
    m_Floats:
{floats}    m_Colors:
    - _BaseColor: {{r: 1, g: 1, b: 1, a: 1}}
    - _Color: {{r: 1, g: 1, b: 1, a: 1}}
    - _EmissionColor: {{r: 0, g: 0, b: 0, a: 1}}
    - _SpecColor: {{r: 0.2, g: 0.2, b: 0.2, a: 1}}
  m_BuildTextureStacks: []
"""

TEXTURE_META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: {srgb}
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMasterTextureLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: {max_size}
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 2
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationDetail: -1
  textureType: {texture_type}
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 3
    buildTarget: DefaultTexturePlatform
    maxTextureSize: {max_size}
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID: 
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    nameFileIdTable: {{}}
  spritePackingTag: 
  pSDRemoveMatte: 0
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""

FOLDER_META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""


def stable_guid(asset_path):
    """Deterministic 32-hex GUID for assets that did not exist before this material pass."""
    digest = hashlib.md5(("MobileGame.HeroAssets/" + asset_path.replace(os.sep, "/")).encode("utf-8")).hexdigest()
    return digest


def material_guid(role, name):
    return FIXED_MATERIAL_GUIDS.get(role) or stable_guid("Assets/Art/Materials/Hero/%s.mat" % name)


def texture_guid(filename):
    return stable_guid("Assets/Art/Textures/Hero/%s" % filename)


def unity_float(value):
    """Format like Unity does in .mat files: 0.0, 1.0, 0.85, 0.005."""
    text = "%.4f" % float(value)
    text = text.rstrip("0")
    if text.endswith("."):
        text += "0"
    return text


def texenv(property_name, guid):
    if guid is None:
        return ("    - %s:\n"
                "        m_Texture: {fileID: 0}\n"
                "        m_Scale: {x: 1, y: 1}\n"
                "        m_Offset: {x: 0, y: 0}\n" % property_name)
    return ("    - %s:\n"
            "        m_Texture: %s\n"
            "        m_Scale: {x: 1, y: 1}\n"
            "        m_Offset: {x: 0, y: 0}\n" % (property_name, TEXTURE_REF % guid))


def build_material(role, stem, bump_scale, occlusion_strength):
    name = "M_Hero_%s" % role
    # URP takes metallic from the mask's red channel when the mask is assigned; this scalar is the
    # fallback (and what the Inspector reports), so it carries the role's actual intent.
    metallic_scalar = 0.9 if role == "Metal" else 0.0
    base_map = "T_Hero_%s_BaseColor.png" % stem
    normal_map = "T_Hero_%s_Normal.png" % stem
    mask_map = "T_Hero_%s_Mask.png" % stem

    for filename in (base_map, normal_map, mask_map):
        if not os.path.isfile(os.path.join(TEX_DIR, filename)):
            sys.stderr.write("missing texture %s - run generate_hero_textures.py first\n" % filename)
            return None

    texenvs = ""
    for property_name in ("_BaseMap", "_BumpMap", "_DetailAlbedoMap", "_DetailMask", "_DetailNormalMap",
                          "_EmissionMap", "_MainTex"):
        guid = texture_guid(base_map) if property_name in ("_BaseMap", "_MainTex") else None
        if property_name == "_BumpMap":
            guid = texture_guid(normal_map)
        texenvs += texenv(property_name, guid)
    texenvs += texenv("_MetallicGlossMap", texture_guid(mask_map))
    texenvs += texenv("_OcclusionMap", texture_guid(mask_map))
    for property_name in ("_ParallaxMap", "_SpecGlossMap", "unity_Lightmaps", "unity_LightmapsInd",
                          "unity_ShadowMasks"):
        texenvs += texenv(property_name, None)

    floats = [
        ("_AddPrecomputedVelocity", 0.0),
        ("_AlphaClip", 0.0),
        ("_AlphaToMask", 0.0),
        ("_Blend", 0.0),
        ("_BlendModePreserveSpecular", 1.0),
        ("_BumpScale", bump_scale),
        ("_ClearCoatMask", 0.0),
        ("_ClearCoatSmoothness", 0.0),
        ("_Cull", 2.0),
        ("_Cutoff", 0.5),
        ("_DetailAlbedoMapScale", 1.0),
        ("_DetailNormalMapScale", 1.0),
        ("_DstBlend", 0.0),
        ("_DstBlendAlpha", 0.0),
        ("_EnvironmentReflections", 1.0),
        ("_GlossMapScale", 1.0),
        ("_Glossiness", 1.0),
        ("_GlossyReflections", 1.0),
        ("_Metallic", metallic_scalar),
        ("_OcclusionStrength", occlusion_strength),
        ("_Parallax", 0.005),
        ("_QueueOffset", 0.0),
        ("_ReceiveShadows", 1.0),
        # Mask alpha is multiplied by this, so 1.0 makes the authored map authoritative.
        ("_Smoothness", 1.0),
        ("_SmoothnessTextureChannel", 0.0),
        ("_SpecularHighlights", 1.0),
        ("_SrcBlend", 1.0),
        ("_SrcBlendAlpha", 1.0),
        ("_Surface", 0.0),
        ("_WorkflowMode", 1.0),
        ("_ZWrite", 1.0),
    ]
    float_block = "".join("    - %s: %s\n" % (key, unity_float(value)) for key, value in floats)

    keywords = "\n    - _NORMALMAP\n    - _METALLICGLOSSMAP\n    - _OCCLUSIONMAP"

    content = MATERIAL_TEMPLATE.format(
        name="M_Hero_%s" % role,
        shader=URP_LIT_SHADER,
        keywords=keywords,
        texenvs=texenvs,
        floats=float_block,
    )
    return name, content


def write(path, content, check_only, actions):
    existing = None
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8") as handle:
            existing = handle.read()
    if existing == content:
        actions.append(("ok", path))
        return False
    actions.append(("write", path))
    if not check_only:
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)
    return True


def main():
    check_only = "--check" in sys.argv[1:]
    if not os.path.isdir(TEX_DIR):
        sys.stderr.write("texture folder missing: %s - run generate_hero_textures.py first\n" % TEX_DIR)
        return 2
    if not os.path.isdir(MAT_DIR):
        os.makedirs(MAT_DIR)

    actions = []
    changed = 0

    # Folder meta so Unity has a stable GUID for the texture folder.
    folder_meta = os.path.join(os.path.dirname(TEX_DIR), "Hero.meta")
    changed += write(folder_meta, FOLDER_META_TEMPLATE.format(guid=stable_guid("Assets/Art/Textures/Hero")),
                     check_only, actions)

    # Texture import settings: colour in sRGB, normal + packed mask linear.
    for stem in [entry[1] for entry in MATERIALS]:
        for suffix, srgb, texture_type, size in (
                ("BaseColor", 1, 0, None),
                ("Normal", 0, 1, None),
                ("Mask", 0, 0, None)):
            filename = "T_Hero_%s_%s.png" % (stem, suffix)
            path = os.path.join(TEX_DIR, filename)
            if not os.path.isfile(path):
                sys.stderr.write("missing texture: %s\n" % filename)
                return 2
            max_size = _png_size(path)[0]
            content = TEXTURE_META_TEMPLATE.format(
                guid=texture_guid(filename),
                srgb=srgb,
                texture_type=texture_type,
                max_size=max_size,
            )
            changed += write(path + ".meta", content, check_only, actions)

    # Materials.
    print("%-8s %-16s %-9s %-9s %-9s" % ("ROLE", "MATERIAL", "METALLIC", "SMOOTH", "AO"))
    for role, stem, bump_scale, occlusion_strength in MATERIALS:
        built = build_material(role, stem, bump_scale, occlusion_strength)
        if built is None:
            return 2
        name, content = built
        path = os.path.join(MAT_DIR, name + ".mat")
        changed += write(path, content, check_only, actions)
        mask_note = "map R" if role == "Metal" else "0 (dielectric)"
        print("%-8s %-16s %-9s %-9s %-9s" % (role, name, mask_note, "map A", "map G"))
        meta = ("fileFormatVersion: 2\nguid: %s\nNativeFormatImporter:\n  externalObjects: {}\n"
                "  mainObjectFileID: 2100000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
                % material_guid(role, name))
        changed += write(path + ".meta", meta, check_only, actions)

    # Retire the pre-material-pass belt material (its role is now covered by M_Hero_Metal).
    for obsolete in OBSOLETE_ASSETS:
        path = os.path.join(MAT_DIR, obsolete)
        if os.path.isfile(path):
            actions.append(("delete", path))
            changed += 1
            if not check_only:
                os.remove(path)

    written = [path for kind, path in actions if kind == "write"]
    deleted = [path for kind, path in actions if kind == "delete"]
    unchanged = [path for kind, path in actions if kind == "ok"]

    print("\n%s: %d written, %d deleted, %d already up to date."
          % ("CHECK" if check_only else "AUTHORED", len(written), len(deleted), len(unchanged)))
    for path in written[:6]:
        print("  wrote    %s" % os.path.relpath(path, REPO_ROOT))
    if len(written) > 6:
        print("  wrote    ... and %d more" % (len(written) - 6))
    for path in deleted:
        print("  removed  %s" % os.path.relpath(path, REPO_ROOT))

    if check_only and (written or deleted):
        print("HERO MATERIALS OUT OF DATE")
        return 1
    if check_only:
        print("HERO MATERIALS UP TO DATE")
    return 0


def _png_size(path):
    with open(path, "rb") as handle:
        header = handle.read(24)
    width = int.from_bytes(header[16:20], "big")
    height = int.from_bytes(header[20:24], "big")
    return width, height


if __name__ == "__main__":
    sys.exit(main())
