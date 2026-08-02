using System.Diagnostics;
using Sts2.SeedFinder.Core.Neow;
using Sts2.SeedFinder.Gpu;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Proves the GPU kernels agree with <c>Core</c>, and says how fast they are.
///
/// This is the gate the GPU search path sits behind. The Oracle cannot reach these kernels:
/// they are a second implementation of the same arithmetic, compiled by a different compiler
/// for a different instruction set, so <c>sts2.dll</c> has nothing to say about them. What
/// stands in for it is <c>Core</c> itself, which the Oracle already vouches for.
///
/// A machine with no GPU is not a failure. The kernels still run under ILGPU's CPU accelerator,
/// which is how they can be developed and checked on a laptop that has nothing to accelerate
/// with, and a real search on such a machine simply uses the existing CPU searcher.
/// </summary>
public static class GpuDoctor
{
    public static int Run(bool verbose, bool bench)
    {
        Console.WriteLine("GPU kernels");
        Console.WriteLine("===========");
        Console.WriteLine();

        bool ok = true;

        // Host-side first. If the primitives disagree with Core in managed code there is no
        // point asking a driver about them, and the failure is far easier to read here.
        Console.WriteLine("Primitives (no device needed)");
        foreach (var check in GpuVerify.Primitives())
        {
            Report(check);
            ok &= check.Passed;
        }
        Console.WriteLine();

        // allowCpuAccelerator: this command is the one place we WANT the CPU backend, so the
        // kernels are still exercised on a machine with no usable GPU.
        using var engine = GpuEngine.TryCreate(out var status, allowCpuAccelerator: true);
        if (engine is null)
        {
            Console.WriteLine($"  no device: {status.Detail}");
            Console.WriteLine();
            Console.WriteLine(ok
                ? "Primitives match Core. Searches will use the CPU path on this machine."
                : "Primitives do NOT match Core. The GPU path must stay off until this is fixed.");
            return ok ? 0 : 2;
        }

        Console.WriteLine($"Device: {status.Backend} / {status.DeviceName}");
        if (verbose) Console.WriteLine($"        {status.Detail}");
        Console.WriteLine();

        var onDevice = GpuVerify.OnDevice(engine);
        Report(onDevice);
        ok &= onDevice.Passed;

        // Every combination that changes the kernel's control flow: lobby size, "any slot"
        // against "these slots", and a relic at a different index in the candidate list.
        foreach (var (label, p, span) in PredicateCases())
        {
            var check = GpuVerify.Predicate(engine, p, start: 4_000_000_000, count: span);
            Report(check with { Name = $"predicate {label}" });
            ok &= check.Passed;
        }

        // Dense on purpose: "any of four players" matches about a third of all seeds, which is
        // what makes a tile overrun its hit buffer and exercise the retry.
        {
            var ctx = new NeowContext { PlayerCount = 4 };
            var candidates = NeowGenerator.CurseCandidates(ctx);
            if (NeowPrefilterFactory.TryBuild(ctx, candidates[0], anySlot: true,
                    requiredSlots: Array.Empty<int>(), out var dense))
            {
                var check = GpuVerify.Overflow(engine, dense, start: 7_000_000_000, count: 4_000_000);
                Report(check);
                ok &= check.Passed;
            }
        }

        // Cards, against the generator rather than against our reading of it. Two characters so
        // the per-slot pool addressing is exercised, and both fights because fight 2 continues
        // fight 1's stream rather than starting a new one.
        foreach (int ascension in new[] { 0, 10 })
        {
            foreach (int fight in new[] { 1, 2 })
            {
                var check = GpuVerifyCards.Run(
                    engine,
                    new[] { Sts2.SeedFinder.Core.Acts.Character.Ironclad, Sts2.SeedFinder.Core.Acts.Character.Silent },
                    ascension, unlocks: null, start: 9_000_000_000, samples: 20_000, fight: fight);
                Report(check with { Name = $"{check.Name} A{ascension}" });
                ok &= check.Passed;
            }
        }

        Console.WriteLine();

        if (bench) Benchmark(engine);

        Console.WriteLine(ok
            ? "GPU kernels agree with Core."
            : "GPU kernels DISAGREE with Core. The GPU path must stay off until this is fixed.");
        return ok ? 0 : 2;
    }

    /// <summary>
    /// The cases worth crossing. Spans are small enough that the CPU side of the comparison
    /// stays quick, since it is the slow half by two orders of magnitude.
    /// </summary>
    private static IEnumerable<(string Label, NeowPrefilterParams Params, long Span)> PredicateCases()
    {
        foreach (int players in new[] { 2, 3, 4 })
        {
            var ctx = new NeowContext { PlayerCount = players };
            var candidates = NeowGenerator.CurseCandidates(ctx);

            // First and last candidate: a wrong candidate count shows up as an index that is
            // only ever reachable at one end of the list.
            foreach (int which in new[] { 0, candidates.Count - 1 })
            {
                var relic = candidates[which];

                if (NeowPrefilterFactory.TryBuild(ctx, relic, anySlot: false,
                        requiredSlots: Enumerable.Range(0, players).ToArray(), out var all))
                    yield return ($"{players}p all/{relic.Slug}", all, 2_000_000);

                if (NeowPrefilterFactory.TryBuild(ctx, relic, anySlot: true,
                        requiredSlots: Array.Empty<int>(), out var any))
                    yield return ($"{players}p any/{relic.Slug}", any, 1_000_000);
            }

            // A single named slot, which is the case the mask exists for.
            if (players > 1 && NeowPrefilterFactory.TryBuild(ctx, candidates[0], anySlot: false,
                    requiredSlots: new[] { players - 1 }, out var one))
                yield return ($"{players}p slot{players - 1}/{candidates[0].Slug}", one, 1_000_000);
        }
    }

    private static void Benchmark(GpuEngine engine)
    {
        Console.WriteLine("Throughput");

        var ctx = new NeowContext { PlayerCount = 2 };
        var candidates = NeowGenerator.CurseCandidates(ctx);
        if (!NeowPrefilterFactory.TryBuild(ctx, candidates[0], anySlot: false,
                requiredSlots: new[] { 0, 1 }, out var p))
        {
            Console.WriteLine("  (could not build parameters)");
            return;
        }

        using var search = new GpuNeowSearch(engine);

        // Warm-up launch: the first one pays for ILGPU's JIT, which would otherwise be
        // reported as the device being slow.
        _ = search.Scan(p, 1_000_000_000, 4 * 1024 * 1024).Count();

        foreach (long count in new[] { 64L << 20, 512L << 20 })
        {
            var sw = Stopwatch.StartNew();
            int hits = search.Scan(p, 1_000_000_000, count).Count();
            sw.Stop();
            Console.WriteLine($"  {count,12:N0} seeds  {sw.Elapsed.TotalSeconds,7:F3}s  " +
                              $"{count / sw.Elapsed.TotalSeconds / 1e6,9:N1} M seeds/s  {hits:N0} hits");
        }
        Console.WriteLine();
    }

    private static void Report(GpuCheck check)
    {
        Console.WriteLine($"  [{(check.Passed ? "ok" : "FAIL")}] {check.Name,-22} {check.Detail}");
    }
}
