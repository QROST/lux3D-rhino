# Original GLB verification fixtures

These assets are authored by this repository's deterministic generator. They
contain no downloaded models, third-party images, private inputs, credentials,
or provider-generated content. The source and generated assets use the
repository's Apache-2.0 license.

From the repository root, using Python 3 and its standard library only:

```bash
python3 scripts/create-fixtures.py
python3 scripts/create-fixtures.py --check
```

The second command verifies byte-for-byte reproducibility and the manifest
without writing. PNGs use fixed uncompressed DEFLATE blocks, no timestamps, and
no platform-dependent compressor output. Generation also checks GLB lengths,
alignment, accessor counts, transformed bounds, and PNG CRCs/decoded dimensions.

## Geometry and node transforms

Both fixtures share one closed asymmetric triangular wedge, one mesh, one
TRIANGLES primitive, eight triangles, 18 accessor vertices, and 24 indices.
There are six distinct positions; face normals and UV seams intentionally
duplicate vertices. Native importers can consolidate or split vertex storage,
so 18 is the glTF accessor count rather than a guarantee of Rhino's vertex count.
All faces use outward counterclockwise winding. POSITION, NORMAL, TANGENT,
TEXCOORD_0 and uint16 indices are explicitly supplied.

glTF is right-handed, Y-up, and measured in meters. Before node transforms:

| Property | Expected value |
| --- | --- |
| Local bounds minimum | `(0, 0, 0)` |
| Local bounds maximum | `(2, 3, 1)` |
| Local dimensions | `(2, 3, 1)` |
| Parent translation | `(10, 20, 30)` |
| Child scale, before rotation | `(2, 1, 0.5)` |
| Child rotation | `+90 degrees` about Y |
| Child translation, after rotation | `(1, 2, 3)` |
| Combined world mapping | `(x,y,z) -> (11 + 0.5z, 22 + y, 33 - 2x)` |
| World bounds minimum | `(11, 22, 29)` |
| World bounds maximum | `(11.5, 25, 33)` |
| World dimensions | `(0.5, 3, 4)` |
| World Y height | `3 m` |

The parent has a translation and a child. The child stores a column-major
matrix with the stated scale, rotation, and translation. No extension, skin,
animation, morph target, external URI, or sparse accessor is used.

If the native importer maps glTF `(x,y,z)` to Rhino `(x,-z,y)`, world bounds
in a meter document should become `(11,-33,22)` to `(11.5,-29,25)`. In a
millimeter document that correctly converts meters, multiply those coordinates
by 1000. These are conditional mathematical expectations: measure the actual
Rhino importer behavior before adding any coordinate or unit conversion.
Placement then works from the verified imported bounds; do not apply the same
conversion a second time. A target height of `H` should scale the three-meter
height by `H / 3` when both quantities use meters.

## PBR fixture

`asymmetric-pbr.glb` has exactly one explicit material and three embedded
4x4 RGBA PNGs. Texture UV set is zero, filtering is nearest, wrapping is
clamp-to-edge, alpha mode is OPAQUE, and doubleSided is false.

Each image contains four 2x2 quadrants. The order below is image row-major:
upper left, upper right, lower left, lower right. glTF UV origins and renderer
image orientation must be taken into account when comparing rendered faces.

| Channel | PNG values / interpretation |
| --- | --- |
| Base color, sRGB | RGB `(230,50,35)`, `(35,190,70)`, `(40,85,225)`, `(245,200,40)`; alpha always 255; factor `(1,1,1,1)` |
| Roughness, linear | Green values `64,192,64,192`; roughnessFactor `0.8`; effective values `0.2007843137,0.6023529412,0.2007843137,0.6023529412` |
| Metallic, linear | Blue values `32,32,224,224`; metallicFactor `0.75`; effective values `0.0941176471,0.0941176471,0.6588235294,0.6588235294` |
| Normal, linear tangent space | RGB `(160,128,251)`, `(96,128,251)`, `(128,160,251)`, `(128,96,251)`; normal scale `0.6` |

Roughness and metallic share one PNG using the standard glTF channel packing;
its red channel is 255 and unused. The normal PNG contains non-flat normals,
so missing normal-map support is detectable. PNGs intentionally contain no
color-profile or gamma chunks; glTF determines each channel's color space.
Rhino may split the packed image into separate texture resources, so its
texture-resource count need not equal three.

`asymmetric-geometry.glb` has no explicit materials, textures, or images. Rhino
may assign its default material. It isolates coordinate and geometry behavior.

## Exact artifacts

Machine-readable counts, bounds, byte lengths and digests are in
[`manifest.json`](manifest.json).

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `asymmetric-pbr.glb` | 3492 | `54b0beb3e3248667cd24aa3afc97373b94cb22ac4101fb2663ffc414f1fc8382` |
| `asymmetric-geometry.glb` | 2192 | `97d19134ec64335bd18bf634092d7af21d4e6de3554a85683f294c97dc7ba131` |

These deterministic fixtures support geometry, PBR channel mapping, placement,
and save/reopen checks. They do not prove compatibility with real Lux3D output,
visual fidelity in Rhino, Undo/Redo, or material survival after document/resource
migration. Record those separately after exercising the actual host.
