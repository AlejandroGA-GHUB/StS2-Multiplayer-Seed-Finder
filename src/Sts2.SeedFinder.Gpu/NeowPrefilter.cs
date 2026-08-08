using System.Runtime.InteropServices;
using ILGPU;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Everything the Neow pre-filter kernel needs, in one blittable struct.
///
/// Kernel arguments are copied per launch, so this is grouped rather than passed loose to keep
/// the launch signature stable as criteria grow. Flags are ints, not bools, because bool's
/// blittable layout is a backend detail not worth depending on.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NeowPrefilterParams
{
    /// <summary>GameHash.Deterministic("NEOW"), computed host-side where strings exist.</summary>
    public ulong NeowHash;

    /// <summary>Count of curse relics that survived availability filtering, i.e. the draw's bound.</summary>
    public int CandidateCount;

    /// <summary>Lobby size. Slots are 0-based and each rolls its own offer.</summary>
    public int PlayerCount;

    /// <summary>
    /// How many criteria <see cref="NeowPrefilterView.Criteria"/> holds. Several relics can be
    /// wanted at once, each with its own slot rule, so this is a count rather than one relic —
    /// the same shape <c>RunFilter</c> uses for its Ancient criteria.
    /// </summary>
    public int CriterionCount;

    /// <summary>
    /// Size of the positive pool BEFORE the curse's counterpart removals and before the coin
    /// flips are appended. Availability filtering is already applied, because it depends on the
    /// lobby rather than on the seed and so is a host constant.
    /// </summary>
    public int BasePositiveCount;

    /// <summary>
    /// Index of Large Capsule among the curse candidates, or -1. It is the one curse that
    /// SKIPS a coin flip, which shifts every later draw, so the kernel has to recognise it.
    /// </summary>
    public int LargeCapsuleIndex;

    /// <summary>1 when this stage runs at all. A card-only search leaves it off.</summary>
    public int Active;
}

/// <summary>
/// The Neow stage's device arrays: the criteria, and the per-curse counterpart table.
///
/// Flat ints rather than struct arrays because a kernel reads these through an
/// <c>ArrayView</c>, and a flat buffer keeps the indexing arithmetic obvious at the one place
/// it happens.
/// </summary>
public readonly struct NeowPrefilterView
{
    /// <summary>Ints per criterion. See the Fld constants for what each one holds.</summary>
    public const int Stride = 5;

    /// <summary>Which branch the wanted relic arrives on, one of the Kind constants.</summary>
    public const int FldKind = 0;

    /// <summary>
    /// Curse: the relic's index among the candidates. Base positive: its index in the filtered
    /// base pool. Coin flip: which of the three pairs, 0 to 2.
    /// </summary>
    public const int FldA = 1;

    /// <summary>Coin flip only: 0 for the pair's first relic, 1 for its second. Unused otherwise.</summary>
    public const int FldB = 2;

    public const int FldRequiredMask = 3, FldAny = 4;

    public const int KindCurse = 0, KindBasePositive = 1, KindCoinFlip = 2;

    public readonly ArrayView<int> Criteria;

    /// <summary>
    /// One bitmask per curse candidate: which positions of the base positive pool that curse
    /// removes, because taking it would duplicate or undo them. Zero for most curses.
    /// </summary>
    public readonly ArrayView<int> RemovedMask;

    public NeowPrefilterView(ArrayView<int> criteria, ArrayView<int> removedMask)
    {
        Criteria = criteria;
        RemovedMask = removedMask;
    }
}

/// <summary>
/// The GPU form of Neow's offer: does this seed give the wanted relic to the required slots?
///
/// BOTH BRANCHES are modelled, by two different routes.
///
/// A CURSE relic is the event's FIRST draw, so one <c>NextInt</c> settles it. Neow's curse and
/// positive pools are disjoint, so for a curse relic "anywhere in the offer" and "curse branch
/// only" ask the same question, and both take that answer.
///
/// A POSITIVE relic needs the offer, which the CPU builds as a list: remove the curse's
/// counterparts, append one winner per coin flip, filter, shuffle, take two. A kernel does not
/// want a mutable variable-length list per thread, so this does what <c>RunFilter</c> does with
/// the relic bags and never materialises the shuffle at all. It works out where the wanted relic
/// STARTS in the list, then follows that one position through the same swaps:
///
/// <code>
/// for n = count-1 down to 1:  k = NextInt(n+1);  if (pos == k) pos = n; else if (pos == n) pos = k;
/// </code>
///
/// which is exactly the reverse Fisher-Yates the game runs, seen from one element. At the end
/// the question is whether that position is 0 or 1, the two the player is shown. No local array,
/// no compaction, and the same draw count as the real thing.
///
/// Three things make the starting position cheap to find. Availability filtering depends on the
/// lobby and not on the seed, so the base pool is a host constant. The counterpart removals are
/// a five-row table keyed by the curse that rolled, passed in as
/// <see cref="NeowPrefilterView.RemovedMask"/>. And the coin flips append in a fixed order, so a
/// flip relic's position is the base length plus how many flips were appended ahead of it.
/// </summary>
public static class NeowPrefilter
{
    /// <summary>
    /// One slot's test for one criterion. Each criterion re-derives the stream from the slot
    /// seed rather than sharing a walk, which costs a few draws and keeps the criteria
    /// independent of the order they are stored in.
    /// </summary>
    private static bool SlotMatches(ulong runSeed, int slot, NeowPrefilterParams p, NeowPrefilterView v, int at)
    {
        int kind = v.Criteria[at + NeowPrefilterView.FldKind];
        int a = v.Criteria[at + NeowPrefilterView.FldA];
        int b = v.Criteria[at + NeowPrefilterView.FldB];

        // EventModel's seed for a non-shared event: the player's lobby slot is folded in, which
        // is why every player rolls a different offer from the same run seed.
        ulong streamSeed = unchecked((ulong)((long)runSeed + slot) + p.NeowHash);
        var rng = new GpuRandom(streamSeed);

        int curse = rng.NextInt(0, p.CandidateCount);
        if (kind == NeowPrefilterView.KindCurse) return curse == a;

        int removed = v.RemovedMask[curse];
        int baseLength = p.BasePositiveCount - PopCount(removed);

        // Where the wanted relic sits before the shuffle, or -1 if it is not in the list at all.
        int pos = -1;

        if (kind == NeowPrefilterView.KindBasePositive)
        {
            // The curse took it out of the pool outright, so no shuffle can put it on offer.
            if (((removed >> a) & 1) != 0) return false;

            // Removals close the gaps, so the index drops by however many went before it.
            pos = a - PopCount(removed & ((1 << a) - 1));
        }

        int appended = 0;
        for (int pair = 0; pair < 3; pair++)
        {
            // Large Capsule skips the first flip entirely. This is the draw-count shift that
            // moves everything after it, so it is a `continue` and not a burned draw.
            if (pair == 0 && curse == p.LargeCapsuleIndex) continue;

            // Rng.NextBool is Next(2) == 0, and true takes the pair's FIRST relic.
            int winner = rng.NextBool() ? 0 : 1;
            if (kind == NeowPrefilterView.KindCoinFlip && pair == a && winner == b)
                pos = baseLength + appended;
            appended++;
        }

        // A coin-flip relic whose coin landed on its partner. Nothing left to test: the
        // remaining draws belong to this slot's stream alone, so stopping here costs nothing.
        if (pos < 0) return false;

        // ListExtensions.UnstableShuffle, followed for one element. See the class remarks.
        int count = baseLength + appended;
        for (int n = count - 1; n >= 1; n--)
        {
            int k = rng.NextInt(n + 1);
            if (pos == k) pos = n;
            else if (pos == n) pos = k;
        }

        // Positive1 and Positive2 are the first two the shuffle leaves.
        return pos < 2;
    }

    /// <summary>
    /// Bits set, over the at-most-fourteen the base pool can have. Written out rather than
    /// calling <c>BitOperations</c>, which is not something every ILGPU backend lowers.
    /// </summary>
    private static int PopCount(int bits)
    {
        int n = 0;
        while (bits != 0) { bits &= bits - 1; n++; }
        return n;
    }

    private static bool SeedMatches(ulong index, NeowPrefilterParams p, NeowPrefilterView v) =>
        MatchesRunSeed(GpuSeedString.RunSeed(index), p, v);

    /// <summary>
    /// The test against an already-derived run seed, so a combined kernel can hash a seed once
    /// and hand it to every stage rather than each stage re-deriving it.
    ///
    /// Every criterion has to hold, so this returns on the first failure. The host stores them
    /// cheapest-first, which matters now that a criterion can be a curse (one draw) or a
    /// positive (a flip pass and a shuffle walk).
    /// </summary>
    public static bool MatchesRunSeed(ulong runSeed, NeowPrefilterParams p, NeowPrefilterView v)
    {
        for (int c = 0; c < p.CriterionCount; c++)
        {
            int at = c * NeowPrefilterView.Stride;
            int requiredMask = v.Criteria[at + NeowPrefilterView.FldRequiredMask];
            int any = v.Criteria[at + NeowPrefilterView.FldAny];

            if (any != 0)
            {
                bool hit = false;
                for (int slot = 0; slot < p.PlayerCount && !hit; slot++)
                    hit = SlotMatches(runSeed, slot, p, v, at);
                if (!hit) return false;
                continue;
            }

            for (int slot = 0; slot < p.PlayerCount; slot++)
            {
                if ((requiredMask & (1 << slot)) == 0) continue;
                if (!SlotMatches(runSeed, slot, p, v, at)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The predicate over a seed INDEX rather than a run seed, for a caller holding an index.
    /// The fused kernel does not use this: it hashes once and hands the run seed to every stage.
    /// </summary>
    public static bool Matches(ulong index, NeowPrefilterParams p, NeowPrefilterView v) =>
        SeedMatches(index, p, v);
}
