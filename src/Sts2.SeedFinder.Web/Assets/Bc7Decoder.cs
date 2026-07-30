namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// Decoder for BPTC / BC7 block-compressed textures — how the game stores 315 of its 323
/// relic icons.
///
/// Each 4x4 pixel block is 16 bytes. The low bits select one of eight modes, and the mode
/// decides everything else: how many subsets the block is partitioned into, how many bits the
/// endpoints get, whether alpha is stored separately, and how wide the interpolation indices
/// are. Decoding is exact — for given input there is exactly one correct output — so a wrong
/// table shows up immediately as a visibly scrambled icon rather than as subtle drift.
///
/// Written from the BC7 format specification (Khronos/D3D), not ported from an implementation.
/// </summary>
public static class Bc7Decoder
{
    public static bool IsSupported => true;

    // Per-mode parameters, indexed by mode 0-7.
    private static readonly int[] Subsets      = { 3, 2, 3, 2, 1, 1, 1, 2 };
    private static readonly int[] PartitionBits= { 4, 6, 6, 6, 0, 0, 0, 6 };
    private static readonly int[] RotationBits = { 0, 0, 0, 0, 2, 2, 0, 0 };
    private static readonly int[] IndexSelBits = { 0, 0, 0, 0, 1, 0, 0, 0 };
    private static readonly int[] ColorBits    = { 4, 6, 5, 7, 5, 7, 7, 5 };
    private static readonly int[] AlphaBits    = { 0, 0, 0, 0, 6, 8, 7, 5 };
    private static readonly int[] EndpointPBits= { 1, 0, 0, 1, 0, 0, 1, 1 };
    private static readonly int[] SharedPBits  = { 0, 1, 0, 0, 0, 0, 0, 0 };
    private static readonly int[] IndexBits    = { 3, 3, 2, 2, 2, 2, 4, 2 };
    private static readonly int[] IndexBits2   = { 0, 0, 0, 0, 3, 2, 0, 0 };

    // Which subset each of the 16 texels belongs to, for 2- and 3-subset partitionings.
    private static readonly byte[][] Partition2 = BuildPartition2();
    private static readonly byte[][] Partition3 = BuildPartition3();

    // The texel that carries the implicit leading-zero index bit, per partition.
    private static readonly byte[] AnchorTable2 =
    {
        15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
        15, 2, 8, 2, 2, 8, 8,15, 2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15, 2, 8, 2, 2, 2,15,15, 6,
         6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
    };
    private static readonly byte[] AnchorTable3A =
    {
         3, 3,15,15, 8, 3,15,15, 8, 8, 6, 6, 6, 5, 3, 3,
         3, 3, 8,15, 3, 3, 6,10, 5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
         3,15, 5, 5, 5, 8, 5,10, 5,10, 8,13,15,12,3,3,
    };
    private static readonly byte[] AnchorTable3B =
    {
        15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
        15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
        15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
    };

    // Interpolation weights for 2-, 3- and 4-bit indices.
    private static readonly byte[] Weights2 = { 0, 21, 43, 64 };
    private static readonly byte[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    private static readonly byte[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    public static byte[] Decode(byte[] blocks, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        var pixels = new byte[16 * 4];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int offset = (by * blocksX + bx) * 16;
                if (offset + 16 > blocks.Length) return rgba;

                DecodeBlock(blocks.AsSpan(offset, 16), pixels);

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width) break;
                        Buffer.BlockCopy(pixels, (py * 4 + px) * 4, rgba, (y * width + x) * 4, 4);
                    }
                }
            }
        }
        return rgba;
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, byte[] outPixels)
    {
        var bits = new BitReader(block);

        int mode = 0;
        while (mode < 8 && bits.Read(1) == 0) mode++;
        if (mode == 8)
        {
            // Reserved: an all-zero low byte has no valid mode. The spec says return black.
            Array.Clear(outPixels);
            return;
        }

        int subsets = Subsets[mode];
        int partition = PartitionBits[mode] > 0 ? bits.Read(PartitionBits[mode]) : 0;
        int rotation = RotationBits[mode] > 0 ? bits.Read(RotationBits[mode]) : 0;
        int indexSel = IndexSelBits[mode] > 0 ? bits.Read(IndexSelBits[mode]) : 0;

        int cb = ColorBits[mode], ab = AlphaBits[mode];
        int endpointCount = subsets * 2;

        // Endpoints are stored channel-major: every R, then every G, then B, then A.
        var r = new int[6]; var g = new int[6]; var b = new int[6]; var a = new int[6];
        for (int i = 0; i < endpointCount; i++) r[i] = bits.Read(cb);
        for (int i = 0; i < endpointCount; i++) g[i] = bits.Read(cb);
        for (int i = 0; i < endpointCount; i++) b[i] = bits.Read(cb);
        for (int i = 0; i < endpointCount; i++) a[i] = ab > 0 ? bits.Read(ab) : 255;

        // P-bits extend each endpoint's precision by one bit, either per endpoint or shared
        // across the pair.
        if (EndpointPBits[mode] == 1)
        {
            for (int i = 0; i < endpointCount; i++)
            {
                int p = bits.Read(1);
                r[i] = (r[i] << 1) | p;
                g[i] = (g[i] << 1) | p;
                b[i] = (b[i] << 1) | p;
                if (ab > 0) a[i] = (a[i] << 1) | p;
            }
            cb++; if (ab > 0) ab++;
        }
        else if (SharedPBits[mode] == 1)
        {
            for (int s = 0; s < subsets; s++)
            {
                int p = bits.Read(1);
                for (int e = 0; e < 2; e++)
                {
                    int i = s * 2 + e;
                    r[i] = (r[i] << 1) | p;
                    g[i] = (g[i] << 1) | p;
                    b[i] = (b[i] << 1) | p;
                    if (ab > 0) a[i] = (a[i] << 1) | p;
                }
            }
            cb++; if (ab > 0) ab++;
        }

        // Scale endpoints up to 8 bits by replicating the high bits into the low ones.
        for (int i = 0; i < endpointCount; i++)
        {
            r[i] = Unquantize(r[i], cb);
            g[i] = Unquantize(g[i], cb);
            b[i] = Unquantize(b[i], cb);
            a[i] = AlphaBits[mode] > 0 ? Unquantize(a[i], ab) : 255;
        }

        var partitionMap = subsets switch
        {
            2 => Partition2[partition],
            3 => Partition3[partition],
            _ => null,
        };

        // Index bits: the anchor texel of each subset stores one bit fewer, because its high
        // bit is known to be zero. That asymmetry is why indices cannot be read as a flat run.
        int ib = IndexBits[mode], ib2 = IndexBits2[mode];
        var idx = new int[16];
        for (int i = 0; i < 16; i++)
            idx[i] = bits.Read(IsAnchor(i, subsets, partition, partitionMap) ? ib - 1 : ib);

        var idx2 = new int[16];
        if (ib2 > 0)
            for (int i = 0; i < 16; i++)
                idx2[i] = bits.Read(i == 0 ? ib2 - 1 : ib2);

        var colorWeights = WeightsFor(ib);
        var alphaWeights = ib2 > 0 ? WeightsFor(ib2) : colorWeights;

        for (int i = 0; i < 16; i++)
        {
            int s = partitionMap?[i] ?? 0;
            int e0 = s * 2, e1 = e0 + 1;

            // Mode 4 can swap which index set drives colour vs alpha.
            int ci = idx[i], ai = ib2 > 0 ? idx2[i] : idx[i];
            var cw = colorWeights; var aw = alphaWeights;
            if (indexSel == 1) { (ci, ai) = (ai, ci); (cw, aw) = (aw, cw); }

            byte cr = Interpolate(r[e0], r[e1], cw[ci]);
            byte cg = Interpolate(g[e0], g[e1], cw[ci]);
            byte cbv = Interpolate(b[e0], b[e1], cw[ci]);
            byte ca = AlphaBits[mode] > 0 ? Interpolate(a[e0], a[e1], aw[ai]) : (byte)255;

            // Modes 4 and 5 may rotate a colour channel into the alpha slot.
            switch (rotation)
            {
                case 1: (cr, ca) = (ca, cr); break;
                case 2: (cg, ca) = (ca, cg); break;
                case 3: (cbv, ca) = (ca, cbv); break;
            }

            int o = i * 4;
            outPixels[o] = cr; outPixels[o + 1] = cg; outPixels[o + 2] = cbv; outPixels[o + 3] = ca;
        }
    }

    private static byte[] WeightsFor(int bits) => bits switch
    {
        2 => Weights2,
        3 => Weights3,
        _ => Weights4,
    };

    private static bool IsAnchor(int texel, int subsets, int partition, byte[]? map)
    {
        if (texel == 0) return true;                       // subset 0's anchor is always texel 0
        if (subsets == 1) return false;
        if (subsets == 2) return texel == AnchorTable2[partition];
        return texel == AnchorTable3A[partition] || texel == AnchorTable3B[partition];
    }

    private static int Unquantize(int value, int bits)
    {
        if (bits >= 8) return value;
        int shifted = value << (8 - bits);
        return shifted | (shifted >> bits);
    }

    private static byte Interpolate(int e0, int e1, int weight) =>
        (byte)((e0 * (64 - weight) + e1 * weight + 32) >> 6);

    /// <summary>
    /// Reads little-endian bits, least significant first, across the block's 16 bytes.
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit = 0;

        public int Read(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++, _bit++)
            {
                int byteIndex = _bit >> 3;
                if (byteIndex >= _data.Length) return result;
                int b = (_data[byteIndex] >> (_bit & 7)) & 1;
                result |= b << i;
            }
            return result;
        }
    }

    // The partition tables are given in the spec as 64 patterns of 16 two- or three-valued
    // entries. They are packed here two bits per texel to keep them readable and compact.
    private static byte[][] BuildPartition2() => Unpack(new uint[]
    {
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC800, 0xFFF0, 0xFFFC, 0xF000,
        0xFFF0, 0xCCCC, 0xEEEE, 0xEEE0, 0xECC8, 0xC800, 0xFFF0, 0xFFFC,
        0x8888, 0xCCCC, 0xF000, 0xFF00, 0xFFF0, 0xF000, 0xFF00, 0xCCCC,
        0xC800, 0xEEE0, 0xFFC0, 0xF0F0, 0xF000, 0xC800, 0xFF00, 0xF0F0,
        0x8888, 0xCCCC, 0xFF00, 0xF0F0, 0xF00F, 0xCC00, 0xAAAA, 0xF0F0,
        0xEE00, 0xFF00, 0xCCCC, 0xAAAA, 0xF0F0, 0xAAAA, 0xCCCC, 0xF0F0,
        0xC0C0, 0xF3F3, 0xAAAA, 0xCCCC, 0xA5A5, 0x5A5A, 0xF00F, 0xFF00,
        0xAAAA, 0x0F0F, 0x9669, 0x6996, 0xC33C, 0x3CC3, 0xA55A, 0x5AA5,
    }, 2);

    private static byte[][] BuildPartition3() => Unpack(new uint[]
    {
        0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
        0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090, 0x94949494, 0xA4A4A4A4, 0xA9A5A5A9, 0x2A0A4245,
        0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
        0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414, 0x50A4A450, 0x6A5A0200,
        0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424, 0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50,
        0x500AA550, 0xAAAA4444, 0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
        0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580, 0xAA141414, 0x96960000,
        0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000, 0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
    }, 3);

    /// <summary>Expands packed 2-bits-per-texel patterns into one subset index per texel.</summary>
    private static byte[][] Unpack(uint[] packed, int subsets)
    {
        var result = new byte[packed.Length][];
        for (int p = 0; p < packed.Length; p++)
        {
            var map = new byte[16];
            for (int t = 0; t < 16; t++)
            {
                int shift = subsets == 2 ? t : t * 2;
                map[t] = subsets == 2
                    ? (byte)((packed[p] >> shift) & 1)
                    : (byte)((packed[p] >> shift) & 3);
            }
            result[p] = map;
        }
        return result;
    }
}
