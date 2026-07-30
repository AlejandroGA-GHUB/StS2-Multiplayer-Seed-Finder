namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// Decoder for S3TC / DXT block compression. A few relic icons ship only in this form rather
/// than BPTC, because Godot imports a separate variant per GPU family.
///
/// DXT1 (BC1) packs 4x4 pixels into 8 bytes: two RGB565 endpoints and 2-bit per-texel indices.
/// DXT5 (BC3) adds an 8-byte alpha block in front: two 8-bit endpoints and 3-bit indices.
/// </summary>
public static class S3tcDecoder
{
    public static byte[] DecodeDxt1(byte[] blocks, int width, int height) =>
        Decode(blocks, width, height, blockBytes: 8, hasAlphaBlock: false);

    public static byte[] DecodeDxt5(byte[] blocks, int width, int height) =>
        Decode(blocks, width, height, blockBytes: 16, hasAlphaBlock: true);

    private static byte[] Decode(byte[] blocks, int width, int height, int blockBytes, bool hasAlphaBlock)
    {
        var rgba = new byte[width * height * 4];
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;

        Span<byte> alpha = stackalloc byte[16];
        Span<int> colors = stackalloc int[4];

        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int at = (by * blocksX + bx) * blockBytes;
            if (at + blockBytes > blocks.Length) return rgba;
            var block = blocks.AsSpan(at, blockBytes);

            if (hasAlphaBlock) DecodeAlphaBlock(block[..8], alpha);
            else alpha.Fill(255);

            var color = hasAlphaBlock ? block[8..] : block;
            int c0 = color[0] | (color[1] << 8);
            int c1 = color[2] | (color[3] << 8);
            colors[0] = Rgb565(c0);
            colors[1] = Rgb565(c1);

            // DXT1 uses the endpoint ordering to signal whether the block has 1-bit alpha.
            if (c0 > c1 || hasAlphaBlock)
            {
                colors[2] = Lerp(colors[0], colors[1], 1, 3);
                colors[3] = Lerp(colors[0], colors[1], 2, 3);
            }
            else
            {
                colors[2] = Lerp(colors[0], colors[1], 1, 2);
                colors[3] = 0;   // transparent black
            }

            uint indices = (uint)(color[4] | (color[5] << 8) | (color[6] << 16) | (color[7] << 24));

            for (int py = 0; py < 4; py++)
            {
                int y = by * 4 + py;
                if (y >= height) break;
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    if (x >= width) break;

                    int t = py * 4 + px;
                    int sel = (int)((indices >> (t * 2)) & 3);
                    int c = colors[sel];

                    int o = (y * width + x) * 4;
                    rgba[o] = (byte)(c >> 16);
                    rgba[o + 1] = (byte)(c >> 8);
                    rgba[o + 2] = (byte)c;
                    rgba[o + 3] = (!hasAlphaBlock && sel == 3 && c0 <= c1) ? (byte)0 : alpha[t];
                }
            }
        }
        return rgba;
    }

    private static void DecodeAlphaBlock(ReadOnlySpan<byte> block, Span<byte> outAlpha)
    {
        Span<byte> a = stackalloc byte[8];
        a[0] = block[0];
        a[1] = block[1];

        if (a[0] > a[1])
            for (int i = 1; i < 7; i++) a[i + 1] = (byte)(((7 - i) * a[0] + i * a[1]) / 7);
        else
        {
            for (int i = 1; i < 5; i++) a[i + 1] = (byte)(((5 - i) * a[0] + i * a[1]) / 5);
            a[6] = 0;
            a[7] = 255;
        }

        // 16 three-bit indices packed into the remaining six bytes.
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (i * 8);
        for (int t = 0; t < 16; t++) outAlpha[t] = a[(int)((bits >> (t * 3)) & 7)];
    }

    private static int Rgb565(int c)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return (r << 16) | (g << 8) | b;
    }

    private static int Lerp(int x, int y, int num, int den)
    {
        int r = (((x >> 16) & 255) * (den - num) + ((y >> 16) & 255) * num) / den;
        int g = (((x >> 8) & 255) * (den - num) + ((y >> 8) & 255) * num) / den;
        int b = ((x & 255) * (den - num) + (y & 255) * num) / den;
        return (r << 16) | (g << 8) | b;
    }
}
