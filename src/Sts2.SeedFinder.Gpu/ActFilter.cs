using System.Runtime.InteropServices;
using ILGPU;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Gpu;

/// <summary>Which act candidates a search can still accept, flattened.</summary>
public readonly struct ActFilterView
{
    /// <summary>How many candidates each act index has. Act 1 has two; acts 2 and 3 have one.</summary>
    public readonly ArrayView<int> CandidateCount;

    /// <summary>Where each act's candidates start in <see cref="Accept"/>.</summary>
    public readonly ArrayView<int> Offset;

    /// <summary>1 when a candidate could still satisfy the search, 0 when it rules the seed out.</summary>
    public readonly ArrayView<byte> Accept;

    public ActFilterView(ArrayView<int> candidateCount, ArrayView<int> offset, ArrayView<byte> accept)
    {
        CandidateCount = candidateCount;
        Offset = offset;
        Accept = accept;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct ActFilterParams
{
    /// <summary>GameHash.Deterministic("act_selection"), the stream this rolls on.</summary>
    public ulong ActNameHash;

    /// <summary>Number of acts, which is <c>ActData.ByIndex.Length</c>.</summary>
    public int ActCount;

    /// <summary>1 when this stage runs at all.</summary>
    public int Active;
}

/// <summary>
/// The GPU form of <c>SeedSearcher</c>'s Act 1 and <c>MapsCouldSatisfy</c> filters.
///
/// This is the cheapest useful thing in the whole run chain and the only part of it that
/// stands alone. Act selection is three draws on its own <c>act_selection</c> stream, and in
/// MULTIPLAYER it has no branching: the singleplayer "first undiscovered act is taken without
/// consuming a draw" path is unreachable, so every act is simply a bounded draw over its
/// candidates. Nothing about bags, shuffles or cross-act state is involved.
///
/// The acceptance test is precomputed host-side into a byte per candidate, so the kernel never
/// has to know what a boss or an event is. Whether a candidate could satisfy the search is a
/// property of the criteria, which are fixed for the whole scan; only which candidate got drawn
/// varies per seed. That collapses three different criterion kinds into one array lookup.
/// </summary>
public static class ActFilter
{
    /// <summary>
    /// Roll each act and reject the seed if any drawn candidate is one the criteria exclude.
    ///
    /// Returning early is safe: this stream is used for nothing else, so abandoning it mid-way
    /// cannot desynchronise a later stage.
    /// </summary>
    public static bool MatchesRunSeed(ulong runSeed, ActFilterParams p, ActFilterView acts)
    {
        var rng = GpuRandom.Named(runSeed, p.ActNameHash);
        for (int i = 0; i < p.ActCount; i++)
        {
            int pick = rng.NextInt(0, acts.CandidateCount[i]);
            if (acts.Accept[acts.Offset[i] + pick] == 0) return false;
        }
        return true;
    }
}

/// <summary>
/// Builds the acceptance table from search criteria, mirroring <c>SeedSearcher</c>'s own
/// pre-filters so the two cannot disagree about which seeds are worth generating.
/// </summary>
public static class ActFilterFactory
{
    /// <summary>
    /// Work out, per act candidate, whether it could still satisfy the search.
    ///
    /// Three rules, each lifted from the CPU path:
    /// the Act 1 map criterion names a candidate outright; a required boss must be one the
    /// candidate can produce; a required event must be in the candidate's pool or in the shared
    /// pool. Excluded bosses are deliberately absent: a boss the map cannot produce satisfies an
    /// exclusion trivially, so filtering on it would throw away valid seeds.
    ///
    /// Returns false when no criterion constrains the acts, in which case the stage is skipped
    /// rather than run as a no-op.
    /// </summary>
    public static bool TryBuild(
        SearchCriteria criteria,
        out int[] candidateCount,
        out int[] offset,
        out byte[] accept)
    {
        var byIndex = ActData.ByIndex;
        candidateCount = new int[byIndex.Length];
        offset = new int[byIndex.Length];

        int total = 0;
        for (int i = 0; i < byIndex.Length; i++)
        {
            candidateCount[i] = byIndex[i].Length;
            offset[i] = total;
            total += byIndex[i].Length;
        }

        accept = new byte[total];
        bool constrains = false;

        for (int i = 0; i < byIndex.Length; i++)
        {
            int act = i + 1;   // criteria are 1-based, as everywhere the user sees them
            for (int j = 0; j < byIndex[i].Length; j++)
            {
                var candidate = byIndex[i][j];
                bool ok = true;

                if (i == 0 && criteria.Act1 is not null
                    && !candidate.Name.Equals(criteria.Act1, StringComparison.OrdinalIgnoreCase))
                    ok = false;

                foreach (var want in criteria.Bosses)
                    if (!want.Exclude && want.Act == act && !candidate.Bosses.Any(b => b.Name == want.Boss))
                        ok = false;

                foreach (var want in criteria.Events)
                    if (want.Act == act && !candidate.Events.Contains(want.Event)
                        && !ActData.SharedEvents.Contains(want.Event))
                        ok = false;

                accept[offset[i] + j] = ok ? (byte)1 : (byte)0;
                if (!ok) constrains = true;
            }
        }

        // Only worth a stage if it can actually reject something. With one candidate per act on
        // acts 2 and 3, a criterion there either rules every seed out or none.
        return constrains;
    }
}
