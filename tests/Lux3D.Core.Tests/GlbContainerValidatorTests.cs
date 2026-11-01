// Adapted from Tripo-Rhino (Apache-2.0), commit 838fbd8; namespace, errors and supported profile modified.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Lux3D.Core.Tests;

public sealed class GlbContainerValidatorTests
{
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinaryChunkType = 0x004E4942;

    [Fact]
    public void ValidateAcceptsEmbeddedGlbVersion2()
    {
        byte[] pngHeader = CreatePngHeader(width: 16, height: 8);
        byte[] glb = BuildImageGlb(pngHeader, "image/png");

        Lux3D.Core.GlbContainerInfo info =
            Lux3D.Core.GlbContainerValidator.Validate(glb);

        Assert.True(info.JsonChunkLength > 0);
        Assert.Equal((pngHeader.Length + 3) & ~3, info.BinaryChunkLength);
    }

    [Fact]
    public void ValidateAcceptsJsonOnlyContainer()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.1"}}""");

        Lux3D.Core.GlbContainerInfo info =
            Lux3D.Core.GlbContainerValidator.Validate(glb);

        Assert.Null(info.BinaryChunkLength);
    }

    [Fact]
    public void ValidateAcceptsBoundedTexturedTriangleFixture()
    {
        byte[] glb = CreateTexturedTriangleGlb();

        Lux3D.Core.GlbContainerInfo info =
            Lux3D.Core.GlbContainerValidator.Validate(glb);

        Assert.True(info.BinaryChunkLength > 0);
    }

    [Fact]
    public void ValidateRejectsWrongMagic()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        glb[0] ^= 0xFF;

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsUnsupportedContainerVersion()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 1);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsDeclaredLengthMismatch()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(8),
            checked((uint)glb.Length + 4));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsBinaryChunkBeforeJson()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (BinaryChunkType, new byte[4]),
            (JsonChunkType, json));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMultipleJsonChunks()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (JsonChunkType, json),
            (JsonChunkType, json));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMultipleBinaryChunks()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (JsonChunkType, json),
            (BinaryChunkType, new byte[4]),
            (BinaryChunkType, new byte[4]));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsUnknownChunkType()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (JsonChunkType, json),
            (0x12345678, new byte[4]));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsTrailingBytes()
    {
        byte[] valid = BuildGlb("""{"asset":{"version":"2.0"}}""");
        byte[] withTrailing = [.. valid, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(
            withTrailing.AsSpan(8),
            checked((uint)withTrailing.Length));

        AssertInvalid(withTrailing);
    }

    [Fact]
    public void ValidateRejectsUnalignedChunkLength()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), 2);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsChunkLengthBeyondActualContainerWithoutAllocation()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), 0xFFFFFFFC);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"asset":{}}""")]
    [InlineData("""{"asset":{"version":"1.0"}}""")]
    [InlineData("""{"asset":{"version":"2.x"}}""")]
    [InlineData("""{"asset":{"version":2}}""")]
    public void ValidateRejectsMissingOrInvalidAssetVersion(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Theory]
    [InlineData(
        """{"asset":{"version":"2.0"},"buffers":[{"uri":"https://example.test/model.bin"}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"buffers":[{"uri":"data:application/octet-stream;base64,AA=="}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"images":[{"uri":"texture.png"}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"images":[{"uri":null}]}""")]
    public void ValidateRejectsAnyBufferOrImageUri(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsMalformedJson()
    {
        AssertInvalid(BuildGlb("""{"asset":{"version":"2.0"}"""));
    }

    [Fact]
    public void ValidateAcceptsJsonAboveFormerSizeLimit()
    {
        string padding = new(
            ' ',
            4 * 1024 * 1024);
        byte[] glb = BuildGlb(
            """{"asset":{"version":"2.0"},"extras":""" +
            JsonSerializer.Serialize(padding) +
            "}");

        Lux3D.Core.GlbContainerValidator.Validate(glb);
    }

    [Fact]
    public void ValidateAcceptsDeepJsonExtrasWithinActualContainer()
    {
        string extras = new string('[', 256) + "0" + new string(']', 256);
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"},"extras":""" + extras + "}");

        Lux3D.Core.GlbContainerValidator.Validate(glb);
    }

    [Fact]
    public void ValidateAcceptsGeometryArraysAboveFormerCountLimits()
    {
        var primitive = new { attributes = new { POSITION = 0 } };
        var meshes = Enumerable.Range(0, 4097).Select(index => new {
            primitives = Enumerable.Repeat(primitive, index == 0 ? 8193 : 1).ToArray()
        }).ToArray();
        string json = JsonSerializer.Serialize(new {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 36 } },
            bufferViews = Enumerable.Repeat(new { buffer = 0, byteLength = 36 }, 16385).ToArray(),
            accessors = Enumerable.Repeat(new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" }, 16385).ToArray(),
            meshes,
            nodes = Enumerable.Range(0, 4097).Select(index => new { mesh = index }).ToArray(),
            scenes = Enumerable.Repeat(new { nodes = new[] { 0 } }, 129).ToArray(),
            materials = Enumerable.Repeat(new { }, 257).ToArray()
        });

        Lux3D.Core.GlbContainerValidator.Validate(BuildGlb(json, new byte[36]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateDeepNodeChainWithoutRecursion(bool cyclic)
    {
        const int nodeCount = 20000;
        string nodes = string.Join(',', Enumerable.Range(0, nodeCount).Select(index =>
            index == nodeCount - 1
                ? cyclic ? "{\"children\":[0]}" : "{}"
                : "{\"children\":[" + (index + 1).ToString(CultureInfo.InvariantCulture) + "]}"));
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"},"nodes":[""" + nodes + "]}");

        if (cyclic)
            AssertInvalid(glb);
        else
            Lux3D.Core.GlbContainerValidator.Validate(glb);
    }

    [Fact]
    public void ValidateRejectsAccessorCountBeyondActualBuffer()
    {
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":4}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteLength\":4}]," +
            "\"accessors\":[{\"bufferView\":0,\"componentType\":5121,\"count\":" +
            "1500001" +
            ",\"type\":\"SCALAR\"}]}";
        byte[] glb = BuildGlb(json, new byte[4]);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData("""{"asset":{"version":"2.0"},"buffers":[{"byteLength":4}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"bufferViews":[{"buffer":0,"byteLength":4}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}]}""")]
    public void ValidateRejectsMissingEmbeddedResource(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsAccessorByteRangeOverflowAgainstActualBuffer()
    {
        byte[] glb = BuildGlb("""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],
             "bufferViews":[{"buffer":0,"byteLength":4,"byteStride":252}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":2147483647,"type":"MAT4"}]}
            """, new byte[4]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsBufferViewIntegerOverflow()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],"bufferViews":[{"buffer":0,"byteOffset":2147483647,"byteLength":4}]}
            """,
            new byte[4]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateAcceptsImageDimensionsAboveFormerLimit()
    {
        byte[] glb = BuildImageGlb(CreatePngHeader(8192, 8192), "image/png");

        Lux3D.Core.GlbContainerValidator.Validate(glb);
    }

    [Theory]
    [InlineData(0, 8192)]
    [InlineData(8192, 0)]
    [InlineData(-1, 1)]
    public void ValidateRejectsInvalidPngDimensions(int width, int height)
    {
        AssertInvalid(BuildImageGlb(CreatePngHeader(width, height), "image/png"));
    }

    [Fact]
    public void ValidateRejectsTruncatedPngHeaderWithinImageResource()
    {
        byte[] png = CreatePngHeader(8192, 8192);

        AssertInvalid(BuildImageGlb(png[..24], "image/png"));
    }

    [Theory]
    [InlineData(8, 255)]
    [InlineData(24, 3)]
    [InlineData(25, 7)]
    [InlineData(26, 1)]
    public void ValidateRejectsInvalidPngHeaderFields(int offset, byte value)
    {
        byte[] png = CreatePngHeader(8192, 8192);
        png[offset] = value;

        AssertInvalid(BuildImageGlb(png, "image/png"));
    }

    [Fact]
    public void ValidateAcceptsJpegFrameBeyondFormerHeaderScanLimit()
    {
        using MemoryStream image = new();
        image.Write(new byte[] { 0xFF, 0xD8 });
        byte[] metadata = new byte[65533];
        for (int segment = 0; segment < 17; segment++)
        {
            image.Write(new byte[] { 0xFF, 0xE1, 0xFF, 0xFF });
            image.Write(metadata);
        }
        // A complete one-component SOF header, read without decoding image data.
        image.Write(new byte[] { 0xFF, 0xC0, 0, 11, 8, 0x20, 0, 0x20, 0, 1, 1, 0x11, 0 });

        Lux3D.Core.GlbContainerValidator.Validate(BuildImageGlb(image.ToArray(), "image/jpeg"));
    }

    [Fact]
    public void ValidateRejectsJpegSegmentBeyondImageResource()
    {
        byte[] image = [0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0, 0];

        AssertInvalid(BuildImageGlb(image, "image/jpeg"));
    }

    [Fact]
    public void ValidateAcceptsAccessorsAboveFormerElementAndDecodedByteLimits()
    {
        // Shared embedded accessors describe over 6 million elements and over
        // 64 MiB of decoded data without requiring a large test allocation.
        const int count = 1_500_001;
        byte[] binary = new byte[count];
        string accessor = FormattableString.Invariant($$"""
            {"bufferView":0,"componentType":5121,"count":{{count}},"type":"SCALAR"}
            """);
        string json =
            "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":" +
            count.ToString(CultureInfo.InvariantCulture) + "}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":" +
            count.ToString(CultureInfo.InvariantCulture) + "}],\"accessors\":[" +
            string.Join(',', Enumerable.Repeat(accessor, 48)) + "]}";

        Lux3D.Core.GlbContainerValidator.Validate(BuildGlb(json, binary));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("4294967296")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ValidateRejectsUnrepresentableOrInvalidAccessorCount(string count)
    {
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":" +
            count +
            ",\"type\":\"MAT4\"}]}";

        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsSparseAccessorsInNativeImportSubset()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":8}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":1},{"buffer":0,"byteOffset":4,"byteLength":4}],"accessors":[{"componentType":5126,"count":1,"type":"SCALAR","sparse":{"count":1,"indices":{"bufferView":0,"componentType":5121},"values":{"bufferView":1}}}]}
            """,
            new byte[8]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsSparseRangeWhenBaseBufferViewExists()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],"bufferViews":[{"buffer":0,"byteLength":4}],"accessors":[{"bufferView":0,"componentType":5121,"count":1,"type":"SCALAR","sparse":{"count":1,"indices":{"bufferView":4,"componentType":5121},"values":{"bufferView":0}}}]}
            """,
            new byte[4]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMisalignedAccessorData()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":8}],"bufferViews":[{"buffer":0,"byteOffset":1,"byteLength":2}],"accessors":[{"bufferView":0,"componentType":5123,"count":1,"type":"SCALAR"}]}
            """,
            new byte[8]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMisalignedRelativeAccessorOffset()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":16}],"bufferViews":[{"buffer":0,"byteOffset":1,"byteLength":15}],"accessors":[{"bufferView":0,"byteOffset":3,"componentType":5126,"count":1,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
            """,
            new byte[16]);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData(
        """{"asset":{"version":"2.0"},"nodes":[{"children":[0]}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"nodes":[{"children":[1]},{"children":[0]}]}""")]
    public void ValidateRejectsCyclicNodeHierarchy(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateAcceptsImagePixelsAboveFormerAggregateLimit()
    {
        byte[] glb = BuildImageGlb(CreatePngHeader(4096, 4096), "image/png", imageCount: 3);

        Lux3D.Core.GlbContainerValidator.Validate(glb);
    }

    [Fact]
    public void ValidateAcceptsImageCountAboveFormerLimit()
    {
        byte[] glb = BuildImageGlb(CreatePngHeader(1, 1), "image/png", imageCount: 513);

        Lux3D.Core.GlbContainerValidator.Validate(glb);
    }

    [Fact]
    public void ValidateRejectsNonFloatPositionAccessor()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5123,"count":3,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsFloatIndexAccessor()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"componentType":5126,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMismatchedPrimitiveAttributeCount()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"componentType":5126,"count":2,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1}}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsVertexAttributeOffsetNotAlignedToFourBytes()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":6}],"bufferViews":[{"buffer":0,"byteLength":6}],"accessors":[{"componentType":5126,"count":1,"type":"VEC3"},{"bufferView":0,"byteOffset":2,"componentType":5123,"count":1,"type":"VEC2"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1}}]}]}
            """,
            new byte[6]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMismatchedMorphTargetCount()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"componentType":5126,"count":2,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"targets":[{"POSITION":1}]}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsIndexOutsidePositionAccessor()
    {
        byte[] indices = [0, 0, 1, 0, 3, 0];
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":6}],"bufferViews":[{"buffer":0,"byteLength":6}],"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":0,"componentType":5123,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}
            """,
            indices);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsStridedIndexAccessor()
    {
        byte[] indices = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(indices, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(8), 2);
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":10}],"bufferViews":[{"buffer":0,"byteLength":10,"byteStride":4}],"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":0,"componentType":5123,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}
            """,
            indices);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData(5121, 256)]
    [InlineData(5123, 65536)]
    public void ValidateRejectsReservedMaximumIndex(
        int componentType,
        int positionCount)
    {
        byte[] indices;
        if (componentType == 5121)
        {
            indices = [0, 1, byte.MaxValue];
        }
        else
        {
            indices = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(
                indices.AsSpan(2),
                1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                indices.AsSpan(4),
                ushort.MaxValue);
        }

        string byteLength =
            indices.Length.ToString(CultureInfo.InvariantCulture);
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":" + byteLength + "}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteLength\":" +
            byteLength + "}]," +
            "\"accessors\":[" +
            "{\"componentType\":5126,\"count\":" +
            positionCount.ToString(CultureInfo.InvariantCulture) +
            ",\"type\":\"VEC3\"}," +
            "{\"bufferView\":0,\"componentType\":" +
            componentType.ToString(CultureInfo.InvariantCulture) +
            ",\"count\":3,\"type\":\"SCALAR\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}," +
            "\"indices\":1}]}]}";
        byte[] glb = BuildGlb(json, indices);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateAcceptsZeroInitializedAccessorInNativeCountRange()
    {
        string count = int.MaxValue
            .ToString(CultureInfo.InvariantCulture);
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":" +
            count +
            ",\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":0}]}]}";

        Lux3D.Core.GlbContainerValidator.Validate(BuildGlb(json));
    }

    [Theory]
    [InlineData(272630, 283986)]
    [InlineData(1600001, 600000)]
    public void ValidateAcceptsIndexedMeshAboveFormerGeometryLimits(
        int vertexCount, int triangleCount)
    {
        // The first case matches the rejected G1-Turbo result's geometry counts.
        // The larger case exceeds the former vertex, triangle, accessor element
        // and decoded byte quotas, while keeping indices in the embedded buffer.
        byte[] glb = CreateIndexedMeshGlb(vertexCount, triangleCount);

        Lux3D.Core.GlbContainerInfo info = Lux3D.Core.GlbContainerValidator.Validate(glb);

        Assert.True(info.BinaryChunkLength > 0);
    }

    [Theory]
    [InlineData(900000u)]
    [InlineData(uint.MaxValue)]
    public void ValidateRejectsMaliciousIndexInLargeMesh(uint finalIndex)
    {
        byte[] glb = CreateIndexedMeshGlb(900000, 300000, finalIndex);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateAcceptsTriangleStripAboveFormerTriangleLimit()
    {
        const int indexCount = 500003;
        string json = FormattableString.Invariant($$"""
            {"asset":{"version":"2.0"},
             "accessors":[{"componentType":5126,"count":3,"type":"VEC3"},
                          {"componentType":5125,"count":{{indexCount}},"type":"SCALAR"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"mode":5}]}]}
            """);

        Lux3D.Core.GlbContainerValidator.Validate(BuildGlb(json));
    }

    [Fact]
    public void ValidateAcceptsSharedPositionAccessorPrimitiveExpansion()
    {
        const int count = 900003;
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":" +
            count.ToString(CultureInfo.InvariantCulture) + ",\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[" +
            "{\"attributes\":{\"POSITION\":0},\"mode\":0}," +
            "{\"attributes\":{\"POSITION\":0},\"mode\":0}]}]}";

        Lux3D.Core.GlbContainerValidator.Validate(BuildGlb(json));
    }

    [Fact]
    public void ValidateAcceptsRepeatedNodeMeshExpansion()
    {
        const int count = 900003;
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":" +
            count.ToString(CultureInfo.InvariantCulture) + ",\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":0}]}]," +
            "\"nodes\":[{\"mesh\":0},{\"mesh\":0}]}";

        Lux3D.Core.GlbContainerValidator.Validate(BuildGlb(json));
    }

    private static byte[] CreateIndexedMeshGlb(int vertexCount, int triangleCount,
        uint? finalIndex = null)
    {
        int indexCount = checked(triangleCount * 3);
        byte[] indices = new byte[checked(indexCount * sizeof(uint))];
        for (int index = 0; index < indexCount; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(index * sizeof(uint)),
                (uint)(index % vertexCount));
        if (finalIndex is not null)
            BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(indices.Length - sizeof(uint)),
                finalIndex.Value);

        // Missing attribute buffer views are legal zero-initialized accessors. Keep
        // this structural fixture compact while storing and checking real indices.
        string json = FormattableString.Invariant($$"""
            {"asset":{"version":"2.0"},
             "buffers":[{"byteLength":{{indices.Length}}}],
             "bufferViews":[{"buffer":0,"byteLength":{{indices.Length}}}],
             "accessors":[
               {"componentType":5126,"count":{{vertexCount}},"type":"VEC3"},
               {"componentType":5126,"count":{{vertexCount}},"type":"VEC3"},
               {"componentType":5126,"count":{{vertexCount}},"type":"VEC2"},
               {"componentType":5126,"count":{{vertexCount}},"type":"VEC4"},
               {"bufferView":0,"componentType":5125,"count":{{indexCount}},"type":"SCALAR"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2,"TANGENT":3},"indices":4}]}]}
            """);
        return BuildGlb(json, indices);
    }

    private static void AssertInvalid(byte[] data)
    {
        Lux3D.Core.AssetValidationException exception =
            Assert.Throws<Lux3D.Core.AssetValidationException>(
                () => Lux3D.Core.GlbContainerValidator.Validate(data));
        Assert.Equal("glb_invalid", exception.Code);
    }

    internal static byte[] BuildGlb(
        string json,
        byte[]? binary = null)
    {
        byte[] jsonChunk = PadJson(json);
        return binary is null
            ? BuildChunks((JsonChunkType, jsonChunk))
            : BuildChunks(
                (JsonChunkType, jsonChunk),
                (BinaryChunkType, PadBinary(binary)));
    }

    private static byte[] BuildChunks(
        params (uint Type, byte[] Content)[] chunks)
    {
        int length = 12 + chunks.Sum(chunk => 8 + chunk.Content.Length);
        byte[] result = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(8),
            checked((uint)length));

        int offset = 12;
        foreach ((uint type, byte[] content) in chunks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(offset),
                checked((uint)content.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(offset + 4),
                type);
            content.CopyTo(result, offset + 8);
            offset += 8 + content.Length;
        }

        return result;
    }

    private static byte[] PadJson(string json)
    {
        byte[] raw = Encoding.UTF8.GetBytes(json);
        int paddedLength = (raw.Length + 3) & ~3;
        byte[] padded = Enumerable.Repeat((byte)' ', paddedLength).ToArray();
        raw.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] PadBinary(byte[] binary)
    {
        int paddedLength = (binary.Length + 3) & ~3;
        byte[] padded = new byte[paddedLength];
        binary.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] result =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G',
            0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0, 0,
            0, 0, 0, 0,
            8, 6, 0, 0, 0,
            0, 0, 0, 0,
        ];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(20), height);
        return result;
    }

    private static byte[] BuildImageGlb(byte[] encodedImage, string mimeType, int imageCount = 1)
    {
        string image = JsonSerializer.Serialize(new { bufferView = 0, mimeType });
        string json = FormattableString.Invariant($$"""
            {"asset":{"version":"2.0"},
             "buffers":[{"byteLength":{{encodedImage.Length}}}],
             "bufferViews":[{"buffer":0,"byteLength":{{encodedImage.Length}}}],
             "images":[{{string.Join(',', Enumerable.Repeat(image, imageCount))}}],
             "textures":[{"source":0}]}
            """);
        return BuildGlb(json, encodedImage);
    }

    internal static byte[] CreateTexturedTriangleGlb()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
            "AAAAC0lEQVR42mP8/x8AAusB9Y9ZlP8AAAAASUVORK5CYII=");
        const int imageOffset = 44;
        byte[] binary = new byte[imageOffset + png.Length];
        WriteSingle(binary, 12, 1);
        WriteSingle(binary, 28, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(36), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(38), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(40), 2);
        png.CopyTo(binary, imageOffset);
        string byteLength =
            binary.Length.ToString(CultureInfo.InvariantCulture);
        string imageLength =
            png.Length.ToString(CultureInfo.InvariantCulture);
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":" + byteLength + "}]," +
            "\"bufferViews\":[" +
            "{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36,\"target\":34962}," +
            "{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6,\"target\":34963}," +
            "{\"buffer\":0,\"byteOffset\":44,\"byteLength\":" + imageLength + "}]," +
            "\"accessors\":[" +
            "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}," +
            "{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]," +
            "\"images\":[{\"bufferView\":2,\"mimeType\":\"image/png\"}]," +
            "\"textures\":[{\"source\":0}]," +
            "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0,\"roughnessFactor\":1}}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1,\"material\":0}]}]," +
            "\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
        return BuildGlb(json, binary);
    }

    private static void WriteSingle(byte[] destination, int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset),
            BitConverter.SingleToInt32Bits(value));
    }
}
