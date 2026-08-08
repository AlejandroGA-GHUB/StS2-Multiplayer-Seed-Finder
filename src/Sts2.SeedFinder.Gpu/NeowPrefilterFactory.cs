using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Turns the Core-side description of a Neow search into the flat buffers the kernel takes.
///
/// One place, deliberately: the CLI, the web app and the verifier all need this mapping, and a
/// second copy of "which index is this relic in the filtered pool" is a silent wrong-answer
/// waiting to happen. Both pools depend on the lobby — player count and unlock state filter
/// them — so an index is a property of the search, not of the relic.
/// </summary>
public static class NeowPrefilterFactory
{
    /// <summary>Slugify("Neow"), hashed once here because kernels have no strings.</summary>
    private static readonly ulong NeowIdHash = GameHash.Deterministic(NeowGenerator.NeowModelEntry);

    /// <summary>
    /// Build kernel parameters and the device buffers for a search's Neow requirements, or
    /// return false when none of them can be accelerated.
    ///
    /// Both branches are expressible now. A criterion is dropped only when it cannot be located
    /// at all, and dropping one is safe in one direction only: it hands MORE seeds forward,
    /// which the narrowing contract allows. A criterion is never weakened, only omitted.
    ///
    /// Independent per-criterion testing is exact rather than approximate for curse relics,
    /// because a player is offered exactly ONE of them and so two different ones can never be
    /// satisfied by the same slot. Positives are looser — a player gets two — and testing them
    /// separately can accept a seed that satisfies two criteria with one relic each on the same
    /// player. That is the CPU's reading of the same criteria too (see <c>NeowPlan</c>), so the
    /// two agree, which is what <c>--gpu-verify</c> holds them to.
    /// </summary>
    public static bool TryBuild(
        SearchCriteria criteria,
        out NeowPrefilterParams parameters,
        out int[] criteriaBuffer,
        out int[] removedMaskBuffer)
    {
        parameters = default;
        criteriaBuffer = Array.Empty<int>();
        removedMaskBuffer = Array.Empty<int>();

        var ctx = criteria.Context;
        var curseCandidates = NeowGenerator.CurseCandidates(ctx);

        // Availability is a property of the lobby, not of the seed, so the base pool is settled
        // here once. The game filters AFTER the flips rather than before, but filtering and the
        // counterpart removals are both removals and both order-preserving, so doing this first
        // reaches the same list.
        var basePositives = NeowRelics.Positives.Where(ctx.IsAllowed).ToList();

        // The kernel assumes every coin-flip relic reaches the pool, because none of the six
        // declares an availability rule. Checked rather than trusted: if a future patch gates
        // one, the flips would no longer append unconditionally and the positions this computes
        // would silently drift.
        if (NeowRelics.CoinFlips.Any(r => !ctx.IsAllowed(r))) return false;

        var packed = new List<int>();
        foreach (var want in criteria.NeowCriteria)
        {
            if (!TryLocate(want.Relic, curseCandidates, basePositives, out int kind, out int a, out int b))
                continue;

            // CurseOnly on a positive relic, or PositiveOnly on a curse, is a contradiction
            // rather than a filter. Validate rejects both, so this only guards a caller that
            // skipped it.
            if (want.Where == OfferSlot.CurseOnly && kind != NeowPrefilterView.KindCurse) continue;
            if (want.Where == OfferSlot.PositiveOnly && kind == NeowPrefilterView.KindCurse) continue;

            bool anySlot = want.Requirement == SlotRequirement.Any;
            int mask = 0;
            foreach (var slot in want.ResolveSlots(ctx.PlayerCount))
                if (slot >= 0 && slot < ctx.PlayerCount) mask |= 1 << slot;

            // "Exactly these slots" with nothing left after clamping asks nothing, and a zero
            // mask would silently pass every seed. Leaving it out says the same thing without
            // pretending the stage filtered anything.
            if (!anySlot && mask == 0) continue;

            packed.Add(kind);
            packed.Add(a);
            packed.Add(b);
            packed.Add(anySlot ? 0 : mask);
            packed.Add(anySlot ? 1 : 0);
        }

        if (packed.Count == 0) return false;

        // Cheapest first: a curse is one draw, a positive is a flip pass plus a shuffle walk.
        var ordered = new List<int>(packed.Count);
        foreach (int kind in (int[])[NeowPrefilterView.KindCurse, NeowPrefilterView.KindBasePositive,
                                     NeowPrefilterView.KindCoinFlip])
            for (int at = 0; at < packed.Count; at += NeowPrefilterView.Stride)
                if (packed[at] == kind)
                    ordered.AddRange(packed.GetRange(at, NeowPrefilterView.Stride));

        parameters = new NeowPrefilterParams
        {
            NeowHash = NeowIdHash,
            CandidateCount = curseCandidates.Count,
            PlayerCount = ctx.PlayerCount,
            CriterionCount = ordered.Count / NeowPrefilterView.Stride,
            BasePositiveCount = basePositives.Count,
            LargeCapsuleIndex = IndexOfSlug(curseCandidates, "large_capsule"),
            Active = 1,
        };
        criteriaBuffer = ordered.ToArray();
        removedMaskBuffer = RemovedMasks(curseCandidates, basePositives);
        return true;
    }

    /// <summary>
    /// Which branch a relic arrives on, and where it starts. <paramref name="a"/> is the curse
    /// candidate index, the base pool index, or the coin-flip pair; <paramref name="b"/> is the
    /// side of that pair.
    /// </summary>
    private static bool TryLocate(
        NeowRelic relic,
        IReadOnlyList<NeowRelic> curseCandidates,
        IReadOnlyList<NeowRelic> basePositives,
        out int kind,
        out int a,
        out int b)
    {
        b = 0;

        int curse = IndexOf(curseCandidates, relic);
        if (curse >= 0) { kind = NeowPrefilterView.KindCurse; a = curse; return true; }

        int positive = IndexOf(basePositives, relic);
        if (positive >= 0) { kind = NeowPrefilterView.KindBasePositive; a = positive; return true; }

        // CoinFlips is flat and in flip order, so a pair is a neighbouring even/odd slot, and
        // the even one is the relic Rng.NextBool picks when it returns true.
        var flips = NeowRelics.CoinFlips;
        for (int i = 0; i < flips.Count; i++)
        {
            if (flips[i] != relic && flips[i].Slug != relic.Slug) continue;
            kind = NeowPrefilterView.KindCoinFlip;
            a = i / 2;
            b = i % 2;
            return true;
        }

        // Unavailable in this lobby, so it is in no pool. Validate rejects that outright; the
        // stage simply declines rather than guessing an index.
        kind = -1;
        a = -1;
        return false;
    }

    /// <summary>
    /// For each curse candidate, the base-pool positions it removes. The game does these
    /// removals before the flips and before availability filtering, and they are keyed by slug.
    /// </summary>
    private static int[] RemovedMasks(
        IReadOnlyList<NeowRelic> curseCandidates, IReadOnlyList<NeowRelic> basePositives)
    {
        var masks = new int[curseCandidates.Count];
        for (int c = 0; c < curseCandidates.Count; c++)
        {
            if (!NeowRelics.Counterparts.TryGetValue(curseCandidates[c].Slug, out var counterparts))
                continue;

            int mask = 0;
            for (int i = 0; i < basePositives.Count; i++)
                if (counterparts.Contains(basePositives[i].Slug, StringComparer.Ordinal))
                    mask |= 1 << i;
            masks[c] = mask;
        }
        return masks;
    }

    private static int IndexOf(IReadOnlyList<NeowRelic> pool, NeowRelic relic)
    {
        for (int i = 0; i < pool.Count; i++)
            if (ReferenceEquals(pool[i], relic) || pool[i].Slug == relic.Slug) return i;
        return -1;
    }

    private static int IndexOfSlug(IReadOnlyList<NeowRelic> pool, string slug)
    {
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].Slug == slug) return i;
        return -1;
    }
}
