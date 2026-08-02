using System.Runtime.CompilerServices;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// The kernel-side half of <see cref="Core.SeedCodec"/>: turn a seed-space index into the
/// twelve ASCII bytes the game hashes, without ever materialising a string.
///
/// A seed string is packed into one <c>ulong</c> plus one <c>uint</c> rather than a byte
/// buffer, because that is exactly the shape XXH64 consumes for a twelve-byte input (one
/// eight-byte block, one four-byte tail) and because local arrays inside a kernel spill to
/// global memory on most backends, which for a value this small costs more than the hash.
/// </summary>
public static class GpuSeedString
{
    /// <summary>Seed strings are always this long here; the game accepts others, searches do not.</summary>
    public const int Length = 12;

    /// <summary>Size of <see cref="Core.SeedCodec.Alphabet"/>, the base this counts in.</summary>
    public const int Radix = 34;

    /// <summary>
    /// Digit value to its ASCII byte in the game's alphabet, by arithmetic rather than a table.
    ///
    /// The alphabet is the ASCII run 0-9A-Z with I and O removed (they canonicalize to 1 and 0),
    /// so it is four contiguous stretches with a one-byte step between them. Doing this with
    /// three compares beats uploading a lookup table, which would otherwise have to be threaded
    /// through every kernel signature as an extra <c>ArrayView</c>.
    ///
    /// Derived by hand, so <see cref="GpuVerify.Primitives"/> asserts it against
    /// <c>SeedCodec.Alphabet</c> for all 34 digits rather than trusting the arithmetic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte DigitToAscii(int d) =>
        d < 10 ? (byte)(48 + d)     // '0'..'9'
        : d < 18 ? (byte)(55 + d)   // 'A'..'H'
        : d < 23 ? (byte)(56 + d)   // 'J'..'N'  (I skipped)
        : (byte)(57 + d);           // 'P'..'Z'  (O skipped)

    /// <summary>
    /// Pack the seed string at <paramref name="index"/> into its twelve ASCII bytes,
    /// little-endian within each word so the hash can read them without shuffling.
    ///
    /// Mirrors <c>SeedCodec.FromIndex</c>: least significant digit last, fixed width, so
    /// enumerating indices visits every distinct seed string exactly once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pack(ulong index, out ulong lo, out uint hi)
    {
        lo = 0;
        hi = 0;
        for (int k = Length - 1; k >= 0; k--)
        {
            byte ch = DigitToAscii((int)(index % Radix));
            index /= Radix;
            if (k < 8) lo |= (ulong)ch << (8 * k);
            else hi |= (uint)ch << (8 * (k - 8));
        }
    }

    /// <summary>
    /// The run-level seed for an index, which is what every RNG in the game is derived from.
    ///
    /// Equivalent to <c>SeedCodec.RunSeed(SeedCodec.FromIndex(index))</c>. The "old"-prefixed
    /// legacy path that method carries is unreachable here: a generated seed string only ever
    /// contains alphabet characters, and the alphabet is uppercase.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RunSeed(ulong index)
    {
        Pack(index, out ulong lo, out uint hi);
        return GpuHash.Xxh64Seed(lo, hi);
    }
}
