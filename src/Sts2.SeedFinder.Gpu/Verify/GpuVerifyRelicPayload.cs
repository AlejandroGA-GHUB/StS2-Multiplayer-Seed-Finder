using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Holds the relic payload stage to <c>NeowRelicPayload</c>, seed by seed and in both directions.
///
/// The reference replays what the CPU does: builds the player's deques from the bag shuffles,
/// pulls, and shuffles the Bones list. The kernel does none of that, following one index through
/// the swaps instead, so the two agreeing is real evidence rather than one implementation
/// checked against itself.
///
/// The curse is exact unless a capsule was dealt: the kernel then tries every offset the capsule
/// could add rather than resolving its pulls, so it may accept a seed Core rejects. That one
/// direction is tolerated, and counted, on exactly those seeds.
///
/// The criteria the caller passes must be ones the stage answers EXACTLY: one relic for "any
/// player", or pinned slots. Two relics for "any player" is deliberately looser on the kernel
/// side and would be reported here as a disagreement.
/// </summary>
public static class GpuVerifyRelicPayload
{
    public static void ProbeKernel(
        Index1D i, ArrayView<ulong> indices, RelicPayloadParams p, RelicPayloadView v, ArrayView<int> matched)
    {
        matched[i] = RelicPayloadFilter.Matches(GpuSeedString.RunSeed(indices[i]), p, v) ? 1 : 0;
    }

    public static GpuCheck Run(GpuEngine engine, SearchCriteria criteria, ulong start, int samples, string label)
    {
        string name = $"relic payload {label}";
        if (!RelicPayloadFilterFactory.TryBuild(criteria, out var p, out var probes))
            return new GpuCheck(name, false, "the stage declined to build for these criteria");

        var indices = new ulong[samples];
        for (int i = 0; i < samples; i++) indices[i] = start + (ulong)i;

        var acc = engine.Accelerator;
        using var dProbes = acc.Allocate1D(probes);
        using var dIndices = acc.Allocate1D(indices);
        using var dMatched = acc.Allocate1D<int>(samples);

        var kernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<ulong>, RelicPayloadParams, RelicPayloadView, ArrayView<int>>(ProbeKernel);
        kernel(samples, dIndices.View, p, new RelicPayloadView(dProbes.View), dMatched.View);
        acc.Synchronize();
        var got = dMatched.GetAsArray1D();

        int matches = 0, disagreements = 0, loose = 0;
        string first = "";
        for (int i = 0; i < samples; i++)
        {
            ulong runSeed = SeedCodec.RunSeed(SeedCodec.FromIndex(indices[i], GpuSeedString.Length));
            bool want = Satisfies(criteria, runSeed);
            if (want) matches++;
            if (want == (got[i] != 0)) continue;
            if (!want && CapsuleMovesCurse(criteria, runSeed)) { loose++; continue; }

            disagreements++;
            if (first.Length == 0)
                first = $"index {indices[i]}: kernel {(got[i] != 0 ? "matched" : "rejected")}, "
                        + $"Core {(want ? "matched" : "rejected")}";
        }

        if (matches == 0 && disagreements == 0)
            return new GpuCheck(name, false, $"{samples:N0} seeds and no matches, so the check proved nothing");
        return disagreements == 0
            ? new GpuCheck(name, true, $"{samples:N0} seeds, {matches:N0} matches, every verdict agrees"
                                       + (loose > 0 ? $" ({loose:N0} capsule-dealt over-accepts)" : ""))
            : new GpuCheck(name, false, $"{disagreements:N0} of {samples:N0} disagree; first: {first}");
    }

    /// <summary>Whether a curse is asked for and a capsule was dealt alongside it.</summary>
    private static bool CapsuleMovesCurse(SearchCriteria c, ulong runSeed) =>
        c.NeowCriteria.Any(w => w.PayloadCurse is not null)
        && Enumerable.Range(0, c.PlayerCount).Any(slot =>
            NeowRelicPayload.BonesRelics(runSeed, slot, c.Context)
                .Any(r => NeowRelicPayload.ShapeOf(r.Slug) == RelicPayloadShape.Capsule));

    private static bool Satisfies(SearchCriteria c, ulong runSeed)
    {
        foreach (var want in c.NeowCriteria)
        {
            if (want.Relics.Count == 0 && want.PayloadCurse is null) continue;

            bool SlotHas(int slot)
            {
                if (want.PayloadCurse is { } curse)
                {
                    var bones = NeowRelicPayload.BonesRelics(runSeed, slot, c.Context);
                    if (NeowRelicPayload.BonesCurse(runSeed, slot, c.Context, bones, c.Characters,
                                                    c.Unlocks, c.PlayerUnlocks) != NeowRelicPayload.FindCurse(curse))
                        return false;
                }
                var got = NeowRelicPayload.ShapeOf(want.Relic.Slug) == RelicPayloadShape.Capsule
                    ? NeowRelicPayload.Capsule(runSeed, slot, c.Characters, c.Unlocks, c.PlayerUnlocks, want.Relic.Slug)
                        .Select(r => r.Slug).ToList()
                    : NeowRelicPayload.BonesRelics(runSeed, slot, c.Context).Select(r => r.Slug).ToList();
                return want.Relics.All(got.Contains);
            }

            bool ok = want.Requirement == SlotRequirement.Any
                ? Enumerable.Range(0, c.PlayerCount).Any(SlotHas)
                : want.ResolveSlots(c.PlayerCount).All(SlotHas);
            if (!ok) return false;
        }
        return true;
    }
}
