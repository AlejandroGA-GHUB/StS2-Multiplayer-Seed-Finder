using System.Diagnostics;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;
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

        // Run generation, against RunGenerator. The most demanding of these checks by far: the
        // stage walks several hundred sequential draws, so every case here is really asking
        // whether the stream is still in step at the far end of it.
        foreach (var (label, criteria, samples) in RunCases())
        {
            var check = GpuVerifyRun.Run(engine, criteria, start: 11_000_000_000, samples: samples, label: label);
            Report(check);
            ok &= check.Passed;
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

    /// <summary>
    /// Run-stage cases, each chosen to break the stage somewhere different.
    ///
    /// Act 1 exercises the two-map roll, Act 3 the far end of the stream, an exclusion the
    /// inverted test, an event the shuffle position tracking, an Ancient the shared-pool
    /// distribution, and Ascension 10 the second boss drawn after every act is done. Three and
    /// four players are in there because the party sizes the relic bags, and a bag burn that is
    /// one draw out puts every act after it on the wrong footing.
    ///
    /// Sample counts are modest because the reference side generates a whole run per seed, which
    /// is the slow half by three orders of magnitude.
    /// </summary>
    private static IEnumerable<(string Label, SearchCriteria Criteria, int Samples)> RunCases()
    {
        var duo = new[] { Character.Ironclad, Character.Silent };
        var trio = new[] { Character.Ironclad, Character.Silent, Character.Defect };

        SearchCriteria Build(IReadOnlyList<Character> party, int ascension = 0) => new()
        {
            Context = new NeowContext { PlayerCount = party.Count },
            Characters = party,
            Ascension = ascension,
        };

        yield return ("act1 boss", Build(duo) with
        {
            Bosses = new[] { new BossCriterion(1, "TheKinBoss") },
        }, 8_000);

        yield return ("act3 boss", Build(duo) with
        {
            Bosses = new[] { new BossCriterion(3, "QueenBoss") },
        }, 8_000);

        yield return ("act3 boss excluded", Build(duo) with
        {
            Bosses = new[] { new BossCriterion(3, "QueenBoss", Exclude: true) },
        }, 4_000);

        yield return ("act3 event", Build(duo) with
        {
            Events = new[] { new EventCriterion(3, "RoundTeaParty", WithinFirst: 6) },
        }, 8_000);

        yield return ("act2 ancient", Build(duo) with
        {
            Ancients = new[] { new AncientCriterion(Ancient.Orobas) },
        }, 8_000);

        yield return ("boss and event", Build(trio) with
        {
            Bosses = new[] { new BossCriterion(2, "KaiserCrabBoss") },
            Events = new[] { new EventCriterion(2, "ZenWeaver", WithinFirst: 8) },
        }, 8_000);

        // A10 pins the pair, which is the one case where a criterion has to see both of the
        // final act's bosses and where the extra draw lands after everything else.
        yield return ("a10 second boss", Build(duo, ascension: 10) with
        {
            Bosses = new[] { new BossCriterion(3, "AeonglassBoss") },
        }, 8_000);

        yield return ("4p bag burn", Build(new[]
        {
            Character.Ironclad, Character.Silent, Character.Defect, Character.Ironclad,
        }) with
        {
            Bosses = new[] { new BossCriterion(3, "TestSubjectBoss") },
        }, 4_000);

        // Shop relics settle inside the bag shuffles, so these check the OTHER end of the
        // stream from the act cases: where one relic lands in one shuffle, rather than whether
        // the stream is still in step several hundred draws later. P2 matters separately from
        // P1 because its deque starts a whole bag further in.
        var shop = ShopRelics.All.First().Slug;

        yield return ("shop p1 first visit", Build(duo) with
        {
            ShopRelicsWanted = new[] { new ShopRelicCriterion(0, shop) },
        }, 4_000);

        yield return ("shop p2 second visit", Build(duo) with
        {
            ShopRelicsWanted = new[] { new ShopRelicCriterion(1, shop, Visit: 1) },
        }, 4_000);

        yield return ("shop any player", Build(trio) with
        {
            ShopRelicsWanted = new[] { new ShopRelicCriterion(-1, shop) },
        }, 4_000);

        // Shop and act criteria together, which is the case where the probes have to leave the
        // stream exactly where act generation expects to find it.
        yield return ("shop and boss", Build(duo) with
        {
            ShopRelicsWanted = new[] { new ShopRelicCriterion(0, shop) },
            Bosses = new[] { new BossCriterion(3, "QueenBoss") },
        }, 8_000);
    }

    /// <summary>
    /// A scan rate as a phrase, matching what the web UI shows so the two never disagree about
    /// the same machine. Billions are B rather than the SI G: this is read by players.
    /// </summary>
    private static string Rate(long seeds, double seconds)
    {
        double rate = seeds / seconds;
        return rate >= 1e9 ? $"{rate / 1e9,8:N2} B seeds/s" : $"{rate / 1e6,8:N1} M seeds/s";
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

        using var search = new GpuSeedScan(engine);

        // Warm-up launch: the first one pays for ILGPU's JIT, which would otherwise be
        // reported as the device being slow.
        _ = search.Scan(p, 1_000_000_000, 4 * 1024 * 1024).Count();

        foreach (long count in new[] { 64L << 20, 512L << 20 })
        {
            var sw = Stopwatch.StartNew();
            int hits = search.Scan(p, 1_000_000_000, count).Count();
            sw.Stop();
            Console.WriteLine($"  neow  {count,12:N0} seeds  {sw.Elapsed.TotalSeconds,7:F3}s  " +
                              $"{Rate(count, sw.Elapsed.TotalSeconds)}  {hits:N0} hits");
        }

        // The run stage on its own, with nothing downstream of it. A search reports a rate that
        // also carries the CPU re-check of every candidate the kernel yields, so measuring it
        // here is the only way to tell a slow kernel from a slow consumer.
        var runCriteria = new SearchCriteria
        {
            Context = new NeowContext { PlayerCount = 2 },
            Characters = new[] { Character.Ironclad, Character.Silent },
            Bosses = new[] { new BossCriterion(3, "QueenBoss") },
        };

        // A shop-only search, which stops at the bag shuffles and never generates an act. Worth
        // measuring apart from the act case: it is the same stage doing a completely different
        // amount of work, and it is the shape a shop search actually takes.
        var shopCriteria = runCriteria with
        {
            Bosses = Array.Empty<BossCriterion>(),
            ShopRelicsWanted = new[] { new ShopRelicCriterion(0, ShopRelics.All.First().Slug) },
        };

        foreach (var (label, criteria, counts) in new[]
                 {
                     ("run  ", runCriteria, new[] { 16L << 20, 128L << 20 }),
                     ("shop ", shopCriteria, new[] { 64L << 20, 512L << 20 }),
                 })
        {
            using var stage = GpuRunStage.TryCreate(engine, criteria);
            if (stage is null) continue;

            _ = search.Scan(stage.Params, stage.Views, 1_000_000_000, 4 * 1024 * 1024).Count();

            foreach (long count in counts)
            {
                var sw = Stopwatch.StartNew();
                int hits = search.Scan(stage.Params, stage.Views, 1_000_000_000, count).Count();
                sw.Stop();
                Console.WriteLine($"  {label} {count,12:N0} seeds  {sw.Elapsed.TotalSeconds,7:F3}s  " +
                                  $"{Rate(count, sw.Elapsed.TotalSeconds)}  {hits:N0} hits");
            }
        }

        Console.WriteLine();
    }

    private static void Report(GpuCheck check)
    {
        Console.WriteLine($"  [{(check.Passed ? "ok" : "FAIL")}] {check.Name,-22} {check.Detail}");
    }
}
