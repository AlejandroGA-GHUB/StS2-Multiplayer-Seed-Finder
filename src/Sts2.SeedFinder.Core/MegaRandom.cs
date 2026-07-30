using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sts2.SeedFinder.Core;

/// <summary>
/// Xoshiro256** seeded via four Splitmix64 draws — the PRNG Slay the Spire 2 uses
/// (as MegaCrit.Sts2.Core.Random.MegaRandom). It replaced System.Random in game version
/// 0.107.1, so anything describing StS2 RNG before that version is obsolete.
///
/// Must stay bit-identical to the game. Verified by Sts2.SeedFinder.Oracle.
///
/// PROVENANCE / LICENSING
/// ----------------------
/// The xoshiro256** algorithm was written in 2018 by David Blackman and Sebastiano Vigna
/// (vigna@acm.org) and dedicated to the public domain under CC0:
/// http://creativecommons.org/publicdomain/zero/1.0/
///
/// The C# shape of this implementation (state layout, NextInner bounding, splitmix64
/// seeding) follows the Redzen library, which StS2 also derives from:
/// https://github.com/colgreen/Redzen/blob/main/Redzen/Random/Xoshiro256StarStarRandom.cs
///
///     Redzen code library.
///     Copyright 2015-2023 Colin D. Green (colin.green1@gmail.com)
///
///     This software is issued under the MIT License.
///
///     Permission is hereby granted, free of charge, to any person obtaining a copy
///     of this software and associated documentation files (the "Software"), to deal
///     in the Software without restriction, including without limitation the rights
///     to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
///     copies of the Software, and to permit persons to whom the Software is
///     furnished to do so, subject to the following conditions:
///
///     The above copyright notice and this permission notice shall be included in
///     all copies or substantial portions of the Software.
///
///     THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
///     IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
///     FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
///     AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
///     LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
///     OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
///     THE SOFTWARE.
/// </summary>
public sealed class MegaRandom
{
    private const double IncrDouble = 1.1102230246251565E-16;
    private const float IncrFloat = 5.9604645E-08f;

    private ulong _s0, _s1, _s2, _s3;

    public MegaRandom(ulong seed) => Reinitialise(seed);

    /// <summary>Splitmix64 PRNG, used only to expand the seed into xoshiro state.</summary>
    public static ulong Splitmix64(ref ulong x)
    {
        ulong num = (x += 11400714819323198485uL);
        num = (num ^ (num >> 30)) * 13787848793156543929uL;
        num = (num ^ (num >> 27)) * 10723151780598845931uL;
        return num ^ (num >> 31);
    }

    public void Reinitialise(ulong seed)
    {
        _s0 = Splitmix64(ref seed);
        _s1 = Splitmix64(ref seed);
        _s2 = Splitmix64(ref seed);
        _s3 = Splitmix64(ref seed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextULongInner()
    {
        ulong s = _s0, s2 = _s1, s3 = _s2, s4 = _s3;
        ulong result = BitOperations.RotateLeft(s2 * 5, 7) * 9;
        ulong num = s2 << 17;
        s3 ^= s;
        s4 ^= s2;
        s2 ^= s3;
        s ^= s4;
        s3 ^= num;
        s4 = BitOperations.RotateLeft(s4, 45);
        _s0 = s; _s1 = s2; _s2 = s3; _s3 = s4;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble() => (NextULongInner() >> 11) * IncrDouble;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat() => (NextULongInner() >> 40) * IncrFloat;

    public ulong NextULong() => NextULongInner();

    public uint NextUInt() => (uint)NextULongInner();

    public int NextInt() => (int)(NextULongInner() >> 33);

    public bool NextBool() => (NextULongInner() & 0x8000000000000000uL) != 0;

    public int Next(int maxValue)
    {
        if (maxValue < 1)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxValue must be > 0");
        return NextInner(maxValue);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxValue must be > minValue");
        long num = (long)maxValue - minValue;
        if (num <= int.MaxValue)
            return NextInner((int)num) + minValue;
        return (int)(NextInner(num) + minValue);
    }

    // The game derives bounded integers from the double, NOT from rejection sampling.
    // Reproducing this exactly matters — a "better" implementation would desync from the game.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextInner(int maxValue) => (int)(NextDouble() * maxValue);

    private long NextInner(long maxValue) => (long)(NextDouble() * maxValue);
}
