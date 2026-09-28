using System.Runtime.CompilerServices;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// XXH64, specialised to the one input shape a seed search ever hashes: exactly twelve bytes,
/// seed 0. This is the kernel-side equivalent of <see cref="Core.GameHash.Deterministic"/>,
/// which reaches the same algorithm through <c>System.IO.Hashing.XxHash64</c>.
///
/// Written out rather than called into because <c>XxHash64</c> is a managed type with a
/// <c>Span</c> API, and neither survives compilation to a kernel. Specialising to twelve bytes
/// removes the length dispatch entirely: a twelve-byte input takes the short-input path, which
/// is one eight-byte block, one four-byte tail, no remaining single bytes, and no accumulator
/// lanes at all.
///
/// XXH64 is by Yann Collet, released under the BSD 2-Clause licence, and is a published
/// algorithm rather than anything belonging to the game.
/// </summary>
public static class GpuHash
{
    private const ulong Prime1 = 0x9E3779B185EBCA87UL;
    private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Prime3 = 0x165667B19E3779F9UL;
    private const ulong Prime4 = 0x85EBCA77C2B2AE63UL;
    private const ulong Prime5 = 0x27D4EB2F165667C5UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));

    /// <summary>
    /// Hash twelve bytes, supplied as the first eight (<paramref name="lo"/>) and the last four
    /// (<paramref name="hi"/>), each little-endian. Returns what the game calls the run seed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Xxh64Seed(ulong lo, uint hi) => Finish(Prefix(lo), hi);

    /// <summary>
    /// The state after the first eight bytes, which is everything that depends on
    /// <paramref name="lo"/> alone.
    ///
    /// Split out because consecutive seed strings share their first eight characters for about
    /// 1.3 million seeds at a time (34^4), so a scan can compute this once per run of seeds and
    /// pay only <see cref="Finish"/> per seed. See <see cref="SeedFilter.Kernel"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Prefix(ulong lo)
    {
        // Inputs under 32 bytes skip the four-lane accumulator and start from Prime5.
        ulong h = Prime5 + GpuSeedString.Length;

        // one full eight-byte block
        ulong k = Rotl(lo * Prime2, 31) * Prime1;
        h ^= k;
        return Rotl(h, 27) * Prime1 + Prime4;
    }

    /// <summary>The rest of the hash: the last four bytes and the final avalanche.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Finish(ulong h, uint hi)
    {
        // four-byte tail
        h ^= hi * Prime1;
        h = Rotl(h, 23) * Prime2 + Prime3;

        // no single-byte tail: twelve is 8 + 4 exactly

        h ^= h >> 33; h *= Prime2;
        h ^= h >> 29; h *= Prime3;
        h ^= h >> 32;
        return h;
    }
}
