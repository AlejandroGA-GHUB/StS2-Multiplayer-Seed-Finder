using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;

namespace Sts2.SeedFinder.Gpu;

/// <summary>One named check and what it found.</summary>
public sealed record GpuCheck(string Name, bool Passed, string Detail);

/// <summary>
/// The differential harness for the GPU path, and the reason any of it can be trusted.
///
/// The Oracle proves <c>Core</c> matches the game. It cannot reach these kernels: they are a
/// second implementation of the same arithmetic, compiled by a different compiler for a
/// different instruction set, and the game's assembly has nothing to say about them. So the
/// GPU is held to <c>Core</c> the same way <c>Core</c> is held to <c>sts2.dll</c>.
///
/// The check that matters most is <see cref="Predicate"/>, and specifically that it compares
/// hit SETS rather than sampling reported hits. Verifying that everything the GPU returned is
/// genuinely a match only catches false positives, and false positives are the harmless
/// failure: the CPU re-check throws them out. A kernel that wrongly REJECTS seeds produces a
/// search that is quietly incomplete, looks perfectly healthy, and cannot be noticed by
/// inspecting its output. That is the failure this file exists to catch.
/// </summary>
public static class GpuVerify
{
    /// <summary>
    /// Checks that need no GPU: the kernel-side primitives, run as ordinary managed code,
    /// against the <c>Core</c> types they mirror.
    /// </summary>
    public static IEnumerable<GpuCheck> Primitives(int samples = 200_000)
    {
        yield return Alphabet();
        yield return Packing(samples);
        yield return Hashing(samples);
        yield return Draws(samples);
    }

    private static GpuCheck Alphabet()
    {
        for (int d = 0; d < GpuSeedString.Radix; d++)
        {
            byte got = GpuSeedString.DigitToAscii(d);
            char want = SeedCodec.Alphabet[d];
            if (got != want)
                return new GpuCheck("alphabet", false, $"digit {d}: kernel '{(char)got}' vs codec '{want}'");
        }
        return new GpuCheck("alphabet", true, $"all {GpuSeedString.Radix} digits match SeedCodec.Alphabet");
    }

    private static GpuCheck Packing(int samples)
    {
        var rng = new Random(20260802);
        for (int i = 0; i < samples; i++)
        {
            ulong index = NextIndex(rng);
            GpuSeedString.Pack(index, out ulong lo, out uint hi);

            string want = SeedCodec.FromIndex(index, GpuSeedString.Length);
            for (int k = 0; k < GpuSeedString.Length; k++)
            {
                byte got = k < 8 ? (byte)(lo >> (8 * k)) : (byte)(hi >> (8 * (k - 8)));
                if (got != want[k])
                    return new GpuCheck("packing", false, $"index {index}: byte {k} '{(char)got}' vs '{want[k]}' in \"{want}\"");
            }
        }
        return new GpuCheck("packing", true, $"{samples:N0} indices pack to the same bytes as SeedCodec.FromIndex");
    }

    private static GpuCheck Hashing(int samples)
    {
        var rng = new Random(20260803);
        for (int i = 0; i < samples; i++)
        {
            ulong index = NextIndex(rng);
            ulong got = GpuSeedString.RunSeed(index);
            ulong want = SeedCodec.RunSeed(SeedCodec.FromIndex(index, GpuSeedString.Length));
            if (got != want)
                return new GpuCheck("hashing", false, $"index {index}: XXH64 {got:X16} vs {want:X16}");
        }
        return new GpuCheck("hashing", true, $"{samples:N0} run seeds match GameHash.Deterministic");
    }

    /// <summary>
    /// Every draw shape <c>Rng</c> exposes, in sequence off one stream, so a divergence in
    /// state advance shows up and not just a divergence in the returned value.
    /// </summary>
    private static GpuCheck Draws(int samples)
    {
        var rng = new Random(20260804);
        for (int i = 0; i < samples; i++)
        {
            ulong seed = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            var reference = new Rng(seed);
            var kernel = new GpuRandom(seed);

            for (int step = 0; step < 8; step++)
            {
                int bound = 2 + (step * 7 % 61);

                if (reference.NextInt(0, bound) != kernel.NextInt(0, bound))
                    return new GpuCheck("draws", false, $"seed {seed}: NextInt(0,{bound}) diverged at step {step}");

                if (BitConverter.DoubleToInt64Bits(reference.NextDouble())
                    != BitConverter.DoubleToInt64Bits(kernel.NextDouble()))
                    return new GpuCheck("draws", false, $"seed {seed}: NextDouble diverged at step {step}");

                if (BitConverter.SingleToInt32Bits(reference.NextFloat())
                    != BitConverter.SingleToInt32Bits(kernel.NextFloat()))
                    return new GpuCheck("draws", false, $"seed {seed}: NextFloat diverged at step {step}");

                if (reference.NextBool() != kernel.NextBool())
                    return new GpuCheck("draws", false, $"seed {seed}: NextBool diverged at step {step}");
            }
        }
        return new GpuCheck("draws", true, $"{samples:N0} streams x 8 steps x 4 draw kinds match Core.Rng");
    }

    /// <summary>
    /// The same primitives again, this time compiled and executed on the device, because a
    /// backend's 64-bit multiply or shift is where "runs fine in managed code" stops meaning
    /// anything.
    /// </summary>
    public static GpuCheck OnDevice(GpuEngine engine, int samples = 1 << 16)
    {
        using var seeds = engine.Accelerator.Allocate1D<ulong>(samples);
        using var outRunSeed = engine.Accelerator.Allocate1D<ulong>(samples);
        using var outDraw = engine.Accelerator.Allocate1D<int>(samples);

        var indices = new ulong[samples];
        var rng = new Random(20260805);
        for (int i = 0; i < samples; i++) indices[i] = NextIndex(rng);
        seeds.CopyFromCPU(indices);

        var kernel = engine.Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<ulong>, ArrayView<ulong>, ArrayView<int>>(
            static (i, idx, runSeeds, draws) =>
            {
                ulong runSeed = GpuSeedString.RunSeed(idx[i]);
                runSeeds[i] = runSeed;
                var r = new GpuRandom(runSeed);
                draws[i] = r.NextInt(0, 9);
            });

        kernel(samples, seeds.View, outRunSeed.View, outDraw.View);
        engine.Accelerator.Synchronize();

        var gotRunSeed = outRunSeed.GetAsArray1D();
        var gotDraw = outDraw.GetAsArray1D();

        for (int i = 0; i < samples; i++)
        {
            ulong wantRunSeed = SeedCodec.RunSeed(SeedCodec.FromIndex(indices[i], GpuSeedString.Length));
            if (gotRunSeed[i] != wantRunSeed)
                return new GpuCheck("device primitives", false,
                    $"index {indices[i]}: device run seed {gotRunSeed[i]:X16} vs host {wantRunSeed:X16}");

            int wantDraw = new Rng(wantRunSeed).NextInt(0, 9);
            if (gotDraw[i] != wantDraw)
                return new GpuCheck("device primitives", false,
                    $"index {indices[i]}: device draw {gotDraw[i]} vs host {wantDraw}");
        }

        return new GpuCheck("device primitives", true,
            $"{samples:N0} run seeds and draws match Core on {engine.Status.Backend}");
    }

    /// <summary>
    /// The whole predicate over a contiguous range, compared as a set against the same
    /// predicate evaluated on the CPU. Any index in one and not the other fails, which is what
    /// makes this able to see a false negative.
    /// </summary>
    /// <remarks>
    /// The CPU side is <c>Core</c>'s own <see cref="NeowPlan"/>, not a host copy of the kernel.
    /// A second implementation of the kernel's arithmetic could only prove the kernel matches
    /// itself; holding it to Core is what makes this worth running, since the Oracle already
    /// holds Core to the game.
    /// </remarks>
    public static GpuCheck Predicate(
        GpuEngine engine,
        SearchCriteria criteria,
        ulong start,
        long count,
        int hitCapacity = GpuSeedScan.DefaultHitCapacity,
        long tileSize = GpuSeedScan.DefaultTileSize)
    {
        if (!NeowPrefilterFactory.TryBuild(criteria, out var p, out var packed, out var removed))
            return new GpuCheck("predicate", false, "no Neow criterion could be accelerated");

        using var buffer = engine.Accelerator.Allocate1D(packed);
        using var removedBuffer = engine.Accelerator.Allocate1D(removed);
        using var search = new GpuSeedScan(engine, hitCapacity);
        var view = new NeowPrefilterView(buffer.View, removedBuffer.View);
        var fromGpu = new HashSet<ulong>(search.Scan(p, view, start, count, tileSize: tileSize));

        var plan = NeowPlan.Build(criteria);
        var fromCpu = new HashSet<ulong>();
        for (long i = 0; i < count; i++)
        {
            ulong index = start + (ulong)i;
            if (plan.Matches(SeedCodec.RunSeed(SeedCodec.FromIndex(index, GpuSeedString.Length))))
                fromCpu.Add(index);
        }

        var missed = fromCpu.Except(fromGpu).Take(3).ToArray();
        var spurious = fromGpu.Except(fromCpu).Take(3).ToArray();

        if (missed.Length > 0 || spurious.Length > 0)
        {
            var parts = new List<string>();
            if (missed.Length > 0) parts.Add($"GPU missed {fromCpu.Except(fromGpu).Count()} (e.g. {string.Join(", ", missed)})");
            if (spurious.Length > 0) parts.Add($"GPU invented {fromGpu.Except(fromCpu).Count()} (e.g. {string.Join(", ", spurious)})");
            return new GpuCheck("predicate", false, string.Join("; ", parts));
        }

        return new GpuCheck("predicate", true,
            $"{count:N0} seeds, {fromCpu.Count:N0} matches, identical sets");
    }

    /// <summary>
    /// The overflow retry, forced.
    ///
    /// A tile that produces more hits than the buffer holds is the one place this design can
    /// lose seeds silently, so it is checked rather than reasoned about. The capacity here is
    /// deliberately absurd: a few thousand slots against a predicate that matches roughly a
    /// third of everything, so the halving loop runs many times over and the result still has
    /// to be the complete set.
    /// </summary>
    public static GpuCheck Overflow(GpuEngine engine, SearchCriteria criteria, ulong start, long count)
    {
        var check = Predicate(engine, criteria, start, count, hitCapacity: 4096, tileSize: 1L << 20);
        return check with { Name = "overflow retry" };
    }

    /// <summary>
    /// An index anywhere in the space, not just the low end, so packing is exercised across
    /// all twelve digits rather than leaving the leading ones at '0'.
    /// </summary>
    private static ulong NextIndex(Random rng)
    {
        ulong max = SeedCodec.SpaceSize(GpuSeedString.Length) ?? ulong.MaxValue;
        ulong v = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
        return v % max;
    }
}
