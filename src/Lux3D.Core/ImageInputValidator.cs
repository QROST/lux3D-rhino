using System.Buffers.Binary;
namespace Lux3D.Core;

public static class ImageInputValidator
{
    public static string Validate(byte[] bytes)
    {
        if (bytes.Length is < 24 or > 20_000_000) throw new ArgumentException("图片大小不支持。");
        int width = 0, height = 0;
        string extension;
        if (bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            extension = ".png";
            if (!bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new ArgumentException("PNG 头无效。");
            width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)); height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
        }
        else if (bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            extension = ".jpg";
            int p = 2;
            while (p + 9 < bytes.Length && p < 1024 * 1024)
            {
                if (bytes[p++] != 0xff) break;
                while (p < bytes.Length && bytes[p] == 0xff) p++;
                if (p + 2 >= bytes.Length) break;
                byte marker = bytes[p++];
                if (marker is 0xd9 or 0xda) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p));
                if (length < 2 || p + length > bytes.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2)
                { height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p + 3)); width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p + 5)); break; }
                p += length;
            }
        }
        else throw new ArgumentException("仅支持 PNG/JPEG 图片。");
        if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 32_000_000)
            throw new ArgumentException("图片尺寸无效，单边上限 8192，像素总数上限 3200 万。");
        return extension;
    }
}
