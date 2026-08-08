using System.Runtime.CompilerServices;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// The kernel-side <see cref="Core.MegaRandom"/> and <see cref="Core.Rng"/>, fused into one
/// mutable struct so a draw costs four registers and no indirection.
///
/// <c>Core.Rng</c> and <c>Core.MegaRandom</c> are both <c>sealed class</c>, which is right for
/// the reference implementation and impossible in a kernel: every construction would be a heap
/// allocation, and there is no heap. The behaviour is otherwise identical, and
/// <see cref="GpuVerify"/> asserts that draw for draw against the classes.
///
/// The draw counter <c>Rng</c> keeps is deliberately absent. The game serializes it and it
/// never affects a value, so carrying it here would cost a register per stream to reproduce
/// something no search can observe.
///
/// PROVENANCE / LICENSING
/// ----------------------
/// Same lineage as <c>Core/MegaRandom.cs</c>, and the notice below travels with the code
/// wherever it is re-expressed.
///
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
public struct GpuRandom
{
    /// <summary>2^-53. The game spells this constant out; it is what makes NextDouble exact.</summary>
    private const double IncrDouble = 1.1102230246251565E-16;

    private const float IncrFloat = 5.9604645E-08f;

    private ulong _s0, _s1, _s2, _s3;

    /// <summary>Splitmix64, used only to expand a seed into xoshiro state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Splitmix64(ref ulong x)
    {
        ulong n = (x += 11400714819323198485uL);
        n = (n ^ (n >> 30)) * 13787848793156543929uL;
        n = (n ^ (n >> 27)) * 10723151780598845931uL;
        return n ^ (n >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GpuRandom(ulong seed)
    {
        _s0 = Splitmix64(ref seed);
        _s1 = Splitmix64(ref seed);
        _s2 = Splitmix64(ref seed);
        _s3 = Splitmix64(ref seed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextULongInner()
    {
        ulong s = _s0, s2 = _s1, s3 = _s2, s4 = _s3;
        ulong result = Rotl(s2 * 5, 7) * 9;
        ulong num = s2 << 17;
        s3 ^= s;
        s4 ^= s2;
        s2 ^= s3;
        s ^= s4;
        s3 ^= num;
        s4 = Rotl(s4, 45);
        _s0 = s; _s1 = s2; _s2 = s3; _s3 = s4;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextULong() => NextULongInner();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble() => (NextULongInner() >> 11) * IncrDouble;

    /// <summary>
    /// The game derives bounded integers from the double rather than by rejection sampling,
    /// so this multiply-and-truncate has to be reproduced exactly. A "better" bounded draw
    /// would desync from the game on some fraction of seeds and the search would silently
    /// miss them. See <c>Core.MegaRandom.NextInner</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextInner(int maxValue) => (int)(NextDouble() * maxValue);

    /// <summary><c>Rng.NextInt(max)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int maxExclusive) => NextInner(maxExclusive);

    /// <summary>
    /// <c>Rng.NextInt(min, max)</c>. The argument check the class does is dropped: kernels
    /// cannot throw, and every call site here is generated by our own code rather than a user.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int minInclusive, int maxExclusive) =>
        NextInner(maxExclusive - minInclusive) + minInclusive;

    /// <summary>
    /// <c>Rng.NextFloat()</c>, which goes through <c>NextDouble</c> and NOT
    /// <c>MegaRandom.NextFloat</c>. Both consume one draw but derive their value from
    /// different bits (&gt;&gt; 11 against &gt;&gt; 40), so the distinction is load-bearing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat() => (float)NextDouble();

    /// <summary>
    /// <c>Rng.NextBool()</c>, which is <c>Next(2) == 0</c>. Note this is not
    /// <c>MegaRandom.NextBool</c>, which tests the high bit instead and gives different answers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NextBool() => NextInner(2) == 0;

    /// <summary><c>Rng.NextItem</c>'s index, without needing the list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextItemIndex(int count) => NextInner(count);

    /// <summary>Advance without using the values, for skipping over draws we do not model.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Burn(int draws)
    {
        for (int i = 0; i < draws; i++) NextULongInner();
    }

    /// <summary>
    /// The game's named-generator constructor, <c>new Rng(seed, name)</c>, which derives a
    /// decorrelated stream as <c>seed + hash(name)</c>. Kernels have no strings, so the name
    /// is hashed host-side once and passed in as the constant it always was.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuRandom Named(ulong seed, ulong nameHash) => new(unchecked(seed + nameHash));
}
