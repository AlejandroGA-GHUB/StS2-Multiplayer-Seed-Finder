using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// Minimal RGBA8 PNG encoder. Hand-rolled rather than pulled from a package: the project has
/// no image dependency, this is the only thing we need one for, and the format's baseline is
/// small enough to be obviously correct.
/// </summary>
public static class Png
{
    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rgba.Length, width * height * 4);

        // Each scanline is prefixed with a filter byte; 0 means "no filtering".
        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (1 + width * 4);
            raw[dst] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, dst + 1, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace

        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream to, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        to.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        to.Write(typeBytes);
        to.Write(data);

        // The CRC covers the type and the data, but not the length.
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> hash = stackalloc byte[4];
        crc.GetCurrentHash(hash);
        hash.Reverse();   // Crc32 emits little-endian; PNG wants big-endian.
        to.Write(hash);
    }
}
