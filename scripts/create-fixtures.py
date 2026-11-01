#!/usr/bin/env python3
"""Rebuild original, deterministic glTF 2.0 fixtures using only Python stdlib."""

import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import zlib


ROOT = Path(__file__).resolve().parents[1]
VERTICES = [(0, 0, 0), (2, 0, 0), (0, 3, 0),
            (0, 0, 1), (2, 0, 1), (0, 3, 1)]
# Counterclockwise when seen from outside: front, back, bottom, slope, left.
FACES = [(0, 2, 1), (3, 4, 5), (0, 1, 4, 3),
         (1, 2, 5, 4), (2, 0, 3, 5)]
CHILD_MATRIX = [0, 0, -2, 0, 0, 1, 0, 0,
                0.5, 0, 0, 0, 1, 2, 3, 1]
WORLD_MIN = [11, 22, 29]
WORLD_MAX = [11.5, 25, 33]


def difference(a, b):
    return tuple(x - y for x, y in zip(a, b))


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def unit(v):
    length = math.sqrt(sum(x * x for x in v))
    return tuple(x / length for x in v)


def png_chunk(name, data):
    return (struct.pack('>I', len(data)) + name + data
            + struct.pack('>I', zlib.crc32(name + data)))


def png_rgba(quadrants):
    """4x4 RGBA PNG, without metadata or platform-dependent compression.

    The zlib payload uses one literal DEFLATE stored block, so a different
    zlib compressor version cannot change fixture hashes.
    """
    rows = [b'\x00' + bytes(c for x in range(4)
                            for c in quadrants[(y // 2) * 2 + x // 2])
            for y in range(4)]
    raw = b''.join(rows)
    compressed = (b'\x78\x01\x01'
                  + struct.pack('<HH', len(raw), 65535 - len(raw)) + raw
                  + struct.pack('>I', zlib.adler32(raw)))
    return (b'\x89PNG\r\n\x1a\n'
            + png_chunk(b'IHDR', struct.pack('>IIBBBBB', 4, 4, 8, 6, 0, 0, 0))
            + png_chunk(b'IDAT', compressed) + png_chunk(b'IEND', b''))


def geometry():
    positions, normals, tangents, uv, indices = [], [], [], [], []
    for face in FACES:
        points = [VERTICES[i] for i in face]
        edge = difference(points[1], points[0])
        normal = unit(cross(edge, difference(points[2], points[0])))
        tangent = (*unit(edge), 1)
        base = len(positions)
        positions.extend(points)
        normals.extend([normal] * len(face))
        tangents.extend([tangent] * len(face))
        uv.extend([(0, 0), (1, 0), (1, 1), (0, 1)] if len(face) == 4
                  else [(0, 0), (1, 0), (0, 1)])
        indices.extend([base, base + 1, base + 2])
        if len(face) == 4:
            indices.extend([base, base + 2, base + 3])
    return positions, normals, tangents, uv, indices


def make_glb(textured):
    binary = bytearray()
    views = []
    accessors = []

    def view(data, target=None):
        binary.extend(b'\x00' * (-len(binary) % 4))
        result = {'buffer': 0, 'byteOffset': len(binary), 'byteLength': len(data)}
        if target is not None:
            result['target'] = target
        views.append(result)
        binary.extend(data)
        return len(views) - 1

    def accessor(values, kind, components, code='f', component_type=5126):
        flat = [x for value in values for x in value] if components > 1 else values
        index = view(struct.pack('<' + code * len(flat), *flat),
                     34963 if kind == 'SCALAR' else 34962)
        record = {'bufferView': index, 'componentType': component_type,
                  'count': len(values), 'type': kind}
        if kind == 'VEC3' and not accessors:
            record['min'] = [min(value[i] for value in values) for i in range(3)]
            record['max'] = [max(value[i] for value in values) for i in range(3)]
        accessors.append(record)
        return len(accessors) - 1

    positions, normals, tangents, uv, indices = geometry()
    attributes = {'POSITION': accessor(positions, 'VEC3', 3),
                  'NORMAL': accessor(normals, 'VEC3', 3),
                  'TANGENT': accessor(tangents, 'VEC4', 4),
                  'TEXCOORD_0': accessor(uv, 'VEC2', 2)}
    primitive = {'attributes': attributes,
                 'indices': accessor(indices, 'SCALAR', 1, 'H', 5123),
                 'mode': 4}
    document = {
        'asset': {'version': '2.0', 'generator': 'Lux3D original fixture generator v1',
                  'copyright': 'Copyright 2026 Lux3D for Rhino contributors. Apache-2.0.'},
        'scene': 0,
        'scenes': [{'name': 'Asymmetric Y-up wedge', 'nodes': [0]}],
        'nodes': [{'name': 'Translated parent', 'translation': [10, 20, 30], 'children': [1]},
                  {'name': 'Rotated scaled child', 'matrix': CHILD_MATRIX, 'mesh': 0}],
        'meshes': [{'name': 'Original closed triangular wedge', 'primitives': [primitive]}],
        'accessors': accessors,
        'bufferViews': views,
    }
    if textured:
        image_specs = [
            ('Base color sRGB', [(230, 50, 35, 255), (35, 190, 70, 255),
                                 (40, 85, 225, 255), (245, 200, 40, 255)]),
            ('Roughness G metallic B linear', [(255, 64, 32, 255), (255, 192, 32, 255),
                                              (255, 64, 224, 255), (255, 192, 224, 255)]),
            ('Tangent space normal linear', [(160, 128, 251, 255), (96, 128, 251, 255),
                                             (128, 160, 251, 255), (128, 96, 251, 255)]),
        ]
        document['images'] = [{'name': name, 'bufferView': view(png_rgba(colors)),
                               'mimeType': 'image/png'} for name, colors in image_specs]
        document['samplers'] = [{'magFilter': 9728, 'minFilter': 9728,
                                 'wrapS': 33071, 'wrapT': 33071}]
        document['textures'] = [{'source': i, 'sampler': 0} for i in range(3)]
        document['materials'] = [{
            'name': 'Fixture PBR four channel',
            'pbrMetallicRoughness': {'baseColorFactor': [1, 1, 1, 1],
                                    'baseColorTexture': {'index': 0, 'texCoord': 0},
                                    'metallicFactor': 0.75, 'roughnessFactor': 0.8,
                                    'metallicRoughnessTexture': {'index': 1, 'texCoord': 0}},
            'normalTexture': {'index': 2, 'texCoord': 0, 'scale': 0.6},
            'alphaMode': 'OPAQUE', 'doubleSided': False,
        }]
        primitive['material'] = 0
    document['buffers'] = [{'byteLength': len(binary)}]
    encoded = json.dumps(document, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    encoded += b' ' * (-len(encoded) % 4)
    binary.extend(b'\x00' * (-len(binary) % 4))
    data = (struct.pack('<III', 0x46546C67, 2, 28 + len(encoded) + len(binary))
            + struct.pack('<II', len(encoded), 0x4E4F534A) + encoded
            + struct.pack('<II', len(binary), 0x004E4942) + binary)
    validate(data, textured)
    return bytes(data)


def validate(data, textured):
    """Independent structural and known-value checks; Rhino remains a separate gate."""
    magic, version, length = struct.unpack_from('<III', data)
    assert (magic, version, length) == (0x46546C67, 2, len(data))
    json_length, kind = struct.unpack_from('<II', data, 12)
    assert kind == 0x4E4F534A and json_length % 4 == 0
    doc = json.loads(data[20:20 + json_length])
    bin_length, kind = struct.unpack_from('<II', data, 20 + json_length)
    binary = data[28 + json_length:]
    assert kind == 0x004E4942 and len(binary) == bin_length and bin_length % 4 == 0
    assert 0 <= bin_length - doc['buffers'][0]['byteLength'] <= 3
    assert doc['accessors'][0]['count'] == 18
    assert doc['accessors'][4]['count'] == 24
    assert not any(name in doc for name in ('skins', 'animations', 'extensionsUsed'))
    for view in doc['bufferViews']:
        assert view['byteOffset'] % 4 == 0
        assert view['byteOffset'] + view['byteLength'] <= len(binary)
    # Transform actual float32 accessor bytes, not just the declared bounds.
    position_view = doc['bufferViews'][doc['accessors'][0]['bufferView']]
    positions = list(struct.iter_unpack('<fff', binary[position_view['byteOffset']:
                     position_view['byteOffset'] + position_view['byteLength']]))
    world = [(11 + 0.5 * z, 22 + y, 33 - 2 * x) for x, y, z in positions]
    assert [min(p[i] for p in world) for i in range(3)] == WORLD_MIN
    assert [max(p[i] for p in world) for i in range(3)] == WORLD_MAX
    assert len(doc.get('images', [])) == (3 if textured else 0)
    for image in doc.get('images', []):
        view = doc['bufferViews'][image['bufferView']]
        png = binary[view['byteOffset']:view['byteOffset'] + view['byteLength']]
        assert png[:8] == b'\x89PNG\r\n\x1a\n'
        offset, inflated = 8, b''
        while offset < len(png):
            size = struct.unpack_from('>I', png, offset)[0]
            name, payload = png[offset + 4:offset + 8], png[offset + 8:offset + 8 + size]
            crc = struct.unpack_from('>I', png, offset + 8 + size)[0]
            assert zlib.crc32(name + payload) == crc
            if name == b'IHDR':
                assert struct.unpack('>IIBBBBB', payload) == (4, 4, 8, 6, 0, 0, 0)
            if name == b'IDAT':
                inflated += zlib.decompress(payload)
            offset += 12 + size
        assert len(inflated) == 68


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Check checked-in bytes without writing')
    args = parser.parse_args()
    directory = ROOT / 'fixtures'
    if not args.check:
        directory.mkdir(parents=True, exist_ok=True)
    manifest = {'schemaVersion': 1, 'units': 'meters', 'upAxis': 'Y',
                'localBounds': {'min': [0, 0, 0], 'max': [2, 3, 1]},
                'worldBounds': {'min': WORLD_MIN, 'max': WORLD_MAX},
                'accessorVertexCount': 18, 'triangleCount': 8, 'meshCount': 1,
                'primitiveCount': 1, 'nodeCount': 2, 'assets': []}
    for filename, textured in [('asymmetric-pbr.glb', True), ('asymmetric-geometry.glb', False)]:
        data = make_glb(textured)
        path = directory / filename
        if args.check:
            assert path.read_bytes() == data, f'{filename} differs: regenerate fixtures'
        else:
            path.write_bytes(data)
        digest = hashlib.sha256(data).hexdigest()
        manifest['assets'].append({'file': filename, 'sha256': digest, 'byteLength': len(data),
                                   'materialCount': int(textured), 'embeddedImageCount': 3 if textured else 0})
        print(f'{digest}  {filename}  ({len(data)} bytes)')
    encoded = (json.dumps(manifest, indent=2) + '\n').encode('utf-8')
    if args.check:
        assert (directory / 'manifest.json').read_bytes() == encoded, 'manifest.json differs'
    else:
        (directory / 'manifest.json').write_bytes(encoded)


if __name__ == '__main__':
    main()
