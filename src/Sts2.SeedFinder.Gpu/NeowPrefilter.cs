using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Everything the Neow pre-filter kernel needs, in one blittable struct.
///
/// Kernel arguments are copied per launch, so this is grouped rather than passed loose to keep
/// the launch signature stable as criteria grow. <c>Any</c> is an int, not a bool, because
/// bool's blittable layout is a backend detail not worth depending on.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NeowPrefilterParams
{
    /// <summary>GameHash.Deterministic("NEOW"), computed host-side where strings exist.</summary>
    public ulong NeowHash;

    /// <summary>Count of curse relics that survived availability filtering, i.e. the draw's bound.</summary>
    public int CandidateCount;

    /// <summary>Index of the wanted relic within those candidates.</summary>
    public int WantIndex;

    /// <summary>Lobby size. Slots are 0-based and each rolls its own offer.</summary>
    public int PlayerCount;

    /// <summary>Bit per slot that must satisfy the criterion, for the "exactly these" case.</summary>
    public int RequiredMask;

    /// <summary>1 when any single slot matching is enough, 0 when every required slot must.</summary>
    public int Any;

    /// <summary>1 when this stage runs at all. A card-only search leaves it off.</summary>
    public int Active;
}

/// <summary>
/// The GPU form of <c>SeedSearcher</c>'s curse fast path: does this seed give the wanted Neow
/// curse relic to the required player slots?
///
/// Only the curse branch is modelled, and deliberately so. The curse is the event's FIRST draw,
/// so a seed is settled in one draw against roughly twenty plus a shuffle for the whole offer,
/// and Neow's curse and positive pools are disjoint, so for any curse relic "anywhere in the
/// offer" and "curse branch only" ask the same question. That is the same reasoning the CPU
/// path uses, and the same reason this is worth doing at all.
/// </summary>
public static class NeowPrefilter
{
    /// <summary>
    /// One slot's test: build the player's Neow stream and take its first draw.
    /// Mirrors <c>NeowGenerator.RngSeed</c> followed by <c>rng.NextInt(0, candidates.Count)</c>.
    /// </summary>
    private static bool SlotMatches(ulong runSeed, int slot, NeowPrefilterParams p)
    {
        // EventModel's seed for a non-shared event: the player's lobby slot is folded in, which
        // is why every player rolls a different offer from the same run seed.
        ulong streamSeed = unchecked((ulong)((long)runSeed + slot) + p.NeowHash);
        var rng = new GpuRandom(streamSeed);
        return rng.NextInt(0, p.CandidateCount) == p.WantIndex;
    }

    private static bool SeedMatches(ulong index, NeowPrefilterParams p) =>
        MatchesRunSeed(GpuSeedString.RunSeed(index), p);

    /// <summary>
    /// The test against an already-derived run seed, so a combined kernel can hash a seed once
    /// and hand it to every stage rather than each stage re-deriving it.
    /// </summary>
    public static bool MatchesRunSeed(ulong runSeed, NeowPrefilterParams p)
    {
        if (p.Any != 0)
        {
            for (int slot = 0; slot < p.PlayerCount; slot++)
                if (SlotMatches(runSeed, slot, p)) return true;
            return false;
        }

        for (int slot = 0; slot < p.PlayerCount; slot++)
        {
            if ((p.RequiredMask & (1 << slot)) == 0) continue;
            if (!SlotMatches(runSeed, slot, p)) return false;
        }
        return true;
    }

    /// <summary>
    /// One thread walks <paramref name="perThread"/> consecutive seeds.
    ///
    /// Batching rather than one seed per thread because at these rates the launch is dominated
    /// by index arithmetic and scheduling; a short serial run per thread amortises both and
    /// keeps consecutive lanes hashing consecutive indices, which the divides pipeline well.
    /// </summary>
    public static void Kernel(
        Index1D idx,
        ulong start,
        int perThread,
        long total,
        NeowPrefilterParams p,
        ArrayView<ulong> hits,
        ArrayView<int> counter)
    {
        long first = (long)idx.X * perThread;
        for (int n = 0; n < perThread; n++)
        {
            long offset = first + n;
            if (offset >= total) return;

            ulong index = start + (ulong)offset;
            if (!SeedMatches(index, p)) continue;

            // Sparse by construction (roughly 1 in 9 per required slot), so an atomic append
            // costs far less than writing a flag per seed and scanning it back on the host.
            int pos = Atomic.Add(ref counter[0], 1);
            if (pos < hits.Length) hits[pos] = index;
        }
    }

    /// <summary>The same predicate on the host, so results can be re-checked without the GPU.</summary>
    public static bool MatchesOnHost(ulong index, NeowPrefilterParams p) => SeedMatches(index, p);
}
