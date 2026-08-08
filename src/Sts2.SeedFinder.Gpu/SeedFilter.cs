using System.Runtime.InteropServices;
using ILGPU;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Every stage's scalars, as one kernel argument.
///
/// Grouped rather than passed side by side because a kernel signature is a fixed budget: ILGPU
/// binds one generic delegate per arity, and each new stage would otherwise cost two more slots
/// and a new signature everywhere the kernel is loaded or launched. One struct per role means a
/// stage is added by adding a field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SeedFilterParams
{
    public ActFilterParams Acts;
    public NeowPrefilterParams Neow;
    public CardFilterParams Cards;
    public RunFilterParams Run;
}

/// <summary>Every stage's device arrays, as one kernel argument. See <see cref="SeedFilterParams"/>.</summary>
public readonly struct SeedFilterViews
{
    public readonly ActFilterView Acts;
    public readonly NeowPrefilterView Neow;
    public readonly CardPoolView Pools;
    public readonly CardCriteriaView Cards;
    public readonly RunFilterView Run;

    public SeedFilterViews(
        ActFilterView acts,
        NeowPrefilterView neow,
        CardPoolView pools,
        CardCriteriaView cards,
        RunFilterView run)
    {
        Acts = acts;
        Neow = neow;
        Pools = pools;
        Cards = cards;
        Run = run;
    }
}

/// <summary>
/// The one kernel a search launches: every stage that has been ported, in cost order, over one
/// pass of the seed space.
///
/// One pass rather than one kernel per stage, because the intermediate would dominate. A loose
/// Neow criterion passes about one seed in nine, so chaining two launches over a billion seeds
/// would mean writing roughly a hundred million indices to device memory purely so the next
/// launch could read them back. Fused, a thread hashes the seed once, keeps the run seed in a
/// register, and only reaches the expensive stage on the seeds that earned it.
///
/// Stages are ordered cheapest-first for the same reason <c>SeedSearcher</c> orders its criteria
/// that way: Neow settles a seed in one draw per player, cards cost a dozen or so plus a walk
/// over a pool, and run generation is several hundred sequential draws. Each stage is skipped
/// entirely when its <c>Active</c> flag is off, so a relic-only search never touches the card
/// code and a card-only search never touches Neow's.
/// </summary>
public static class SeedFilter
{
    /// <summary>
    /// One thread walks <paramref name="perThread"/> consecutive seeds.
    ///
    /// Batching amortises the index arithmetic and the launch scheduling, and keeps neighbouring
    /// lanes hashing neighbouring indices.
    /// </summary>
    public static void Kernel(
        Index1D idx,
        ulong start,
        int perThread,
        long total,
        SeedFilterParams p,
        SeedFilterViews v,
        ArrayView<ulong> hits,
        ArrayView<int> counter)
    {
        long first = (long)idx.X * perThread;
        for (int n = 0; n < perThread; n++)
        {
            long offset = first + n;
            if (offset >= total) return;

            ulong index = start + (ulong)offset;
            ulong runSeed = GpuSeedString.RunSeed(index);

            // Acts first: three draws and an array lookup, and it gates the criteria that would
            // otherwise need full run generation.
            if (p.Acts.Active != 0 && !ActFilter.MatchesRunSeed(runSeed, p.Acts, v.Acts)) continue;
            if (p.Neow.Active != 0 && !NeowPrefilter.MatchesRunSeed(runSeed, p.Neow, v.Neow)) continue;
            if (p.Cards.Active != 0 && !CardFilter.Matches(runSeed, p.Cards, v.Pools, v.Cards)) continue;

            // Last, and by a wide margin the most expensive: several hundred sequential draws
            // that no amount of cleverness can skip, since the Act 3 boss is at the far end of
            // one stream. Every cheaper stage above exists to keep seeds away from it.
            if (p.Run.Active != 0 && !RunFilter.Matches(runSeed, p.Run, v.Run)) continue;

            // Sparse by construction, so an atomic append costs far less than writing a flag per
            // seed and scanning it back on the host.
            int pos = Atomic.Add(ref counter[0], 1);
            if (pos < hits.Length) hits[pos] = index;
        }
    }
}
