using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Holds the run kernel to <c>RunGenerator</c>, seed by seed.
///
/// This is the harness the run stage needs most. Every other stage reads a stream that is a few
/// draws long and answers a question about its own output; this one walks several hundred
/// sequential draws, and a single misplaced draw anywhere in that walk produces a run that is
/// entirely plausible and entirely wrong from that point on. Nothing about the output would look
/// suspicious, so it is compared against the reference on every seed rather than sampled.
///
/// Two things it is deliberately strict about. It compares the VERDICT on every seed in the
/// range, both directions, so a kernel that wrongly rejects fails here — that being the failure
/// the CPU re-check downstream cannot catch. And it is run with criteria dense enough that the
/// range produces matches to disagree about: a check where nothing matches on either side passes
/// while proving almost nothing.
/// </summary>
public static class GpuVerifyRun
{
    /// <summary>
    /// Compare the kernel's verdict with full run generation on a contiguous range of seeds.
    ///
    /// <paramref name="label"/> names the shape being tested, since a run check is only as good
    /// as the criteria it carries and the caller varies them deliberately.
    /// </summary>
    public static GpuCheck Run(
        GpuEngine engine, SearchCriteria criteria, ulong start, int samples, string label)
    {
        using var stage = GpuRunStage.TryCreate(engine, criteria);
        if (stage is null)
            return new GpuCheck($"run {label}", false, "the run stage declined to build for these criteria");

        var indices = new ulong[samples];
        for (int i = 0; i < samples; i++) indices[i] = start + (ulong)i;

        var acc = engine.Accelerator;
        using var dIndices = acc.Allocate1D(indices);
        using var dMatched = acc.Allocate1D<int>(samples);

        var kernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<ulong>, RunFilterParams, RunFilterView, ArrayView<int>>(
            RunFilter.ProbeKernel);

        kernel(samples, dIndices.View, stage.RunParams, stage.View, dMatched.View);
        acc.Synchronize();

        var got = dMatched.GetAsArray1D();

        int matches = 0, disagreements = 0;
        string firstDetail = "";
        for (int i = 0; i < samples; i++)
        {
            ulong runSeed = SeedCodec.RunSeed(SeedCodec.FromIndex(indices[i], GpuSeedString.Length));
            bool want = Satisfies(criteria, runSeed);

            if (want) matches++;
            if (want == (got[i] != 0)) continue;

            disagreements++;
            if (firstDetail.Length == 0)
                firstDetail = $"index {indices[i]}: kernel "
                    + $"{(got[i] != 0 ? "matched" : "rejected")}, generator {(want ? "matched" : "rejected")}";
        }

        // A range where nothing matched on either side agrees trivially and proves nothing about
        // the several hundred draws in between, so it is reported as a failure of the CHECK.
        if (disagreements == 0 && matches == 0)
            return new GpuCheck($"run {label}", false,
                $"{samples:N0} seeds and no matches on either side, so the check proved nothing");

        return disagreements == 0
            ? new GpuCheck($"run {label}", true,
                $"{samples:N0} seeds, {matches:N0} matches, every verdict agrees")
            : new GpuCheck($"run {label}", false,
                $"{disagreements:N0} of {samples:N0} disagree; first: {firstDetail}");
    }

    /// <summary>
    /// The reference answer: generate the run and apply exactly the criteria the kernel models.
    ///
    /// Ancients are matched on IDENTITY alone, which is what the stage claims to decide. An
    /// Ancient criterion naming a relic runs a fresh chain per player over variable-length branch
    /// sets, so the CPU keeps that half; holding the kernel to the stricter test here would fail
    /// it for being correctly loose.
    /// </summary>
    private static bool Satisfies(SearchCriteria criteria, ulong runSeed)
    {
        // playerUnlocks matters as much as the run's own state: it sizes each player's bag, and
        // the bags are shuffled before act generation. Leaving it off here would compare a
        // fully-unlocked reference against a kernel that was correctly honouring a mixed lobby,
        // and report the kernel as broken for being right.
        var run = RunGenerator.GenerateRun(
            runSeed, criteria.Unlocks, isMultiplayer: true, criteria.Characters,
            acts: null, criteria.Ascension, withShopRelics: criteria.NeedsShopRelics,
            playerUnlocks: criteria.PlayerUnlocks);

        foreach (var want in criteria.ShopRelicsWanted)
        {
            bool ok = false;
            for (int slot = 0; slot < criteria.PlayerCount && !ok; slot++)
            {
                if (want.Slot >= 0 && want.Slot != slot) continue;
                ok = run.ShopRelic(slot, want.Visit) is { } got
                     && string.Equals(got.Slug, want.Relic, StringComparison.OrdinalIgnoreCase);
            }
            if (!ok) return false;
        }

        foreach (var want in criteria.Bosses)
        {
            bool present = run.Acts[want.Act - 1].Bosses.Any(b => b.Name == want.Boss);
            if (present == want.Exclude) return false;
        }

        foreach (var want in criteria.Events)
            if (!run.Acts[want.Act - 1].Events.Take(want.WithinFirst).Contains(want.Event))
                return false;

        foreach (var want in criteria.Ancients)
        {
            bool present = false;
            foreach (var act in run.Acts)
                if (AncientOffers.TryParse(act.Ancient, out var got) && got == want.Ancient)
                {
                    present = true;
                    break;
                }
            if (!present) return false;
        }
        return true;
    }
}
