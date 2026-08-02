using ILGPU;

namespace Sts2.SeedFinder.Gpu;

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
/// over a pool. Each stage is skipped entirely when its <c>Active</c> flag is off, so a
/// relic-only search never touches the card code and a card-only search never touches Neow's.
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
        NeowPrefilterParams neow,
        CardFilterParams cards,
        CardPoolView pools,
        CardCriteriaView criteria,
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

            if (neow.Active != 0 && !NeowPrefilter.MatchesRunSeed(runSeed, neow)) continue;
            if (cards.Active != 0 && !CardFilter.Matches(runSeed, cards, pools, criteria)) continue;

            // Sparse by construction, so an atomic append costs far less than writing a flag per
            // seed and scanning it back on the host.
            int pos = Atomic.Add(ref counter[0], 1);
            if (pos < hits.Length) hits[pos] = index;
        }
    }
}
