namespace Sts2.SeedFinder.Core.Neow;

/// <summary>
/// Reproduces Neow.GenerateInitialOptions (StS2 v0.109.0) — the complete offer, not just
/// the curse slot.
///
/// The draw order is load-bearing and subtle. In particular the Lava Rock / Small Capsule
/// coin flip is SKIPPED when the curse rolled is Large Capsule, which shifts every later
/// draw by one. Getting that wrong silently corrupts both positive options.
///
/// Sequence:
///   1. filter curses by availability, then one NextItem draw    -> the curse
///   2. copy the positive pool, remove the curse's counterpart(s)
///   3. coin flip Lava Rock / Small Capsule   (ONLY if curse is not Large Capsule)
///   4. coin flip Nutritious Oyster / Stone Humidifier
///   5. coin flip Neow's Talisman / Pomander
///   6. filter positives by availability
///   7. shuffle and take 2
/// </summary>
public static class NeowGenerator
{
    /// <summary>Slugify("Neow") — the model entry hashed into the event's RNG seed.</summary>
    public const string NeowModelEntry = "NEOW";

    private static readonly ulong NeowIdHash = GameHash.Deterministic(NeowModelEntry);

    /// <summary>
    /// EventModel's RNG seed for a non-shared event (Neow does not override IsShared),
    /// so the player's lobby slot is folded in and every player rolls their own offer.
    /// </summary>
    public static ulong RngSeed(ulong runSeed, int playerSlotIndex) =>
        unchecked((ulong)((long)runSeed + playerSlotIndex) + NeowIdHash);

    /// <summary>The curse relics actually presented to the RNG, after availability filtering.</summary>
    public static IReadOnlyList<NeowRelic> CurseCandidates(NeowContext ctx) =>
        NeowRelics.Curses.Where(ctx.IsAllowed).ToArray();

    /// <summary>
    /// The curse relic alone. Cheaper than the full offer because it is the first draw —
    /// used by the search hot path to reject a seed without building the positive pool.
    /// </summary>
    public static NeowRelic PredictCurse(ulong runSeed, int slot, NeowContext ctx)
    {
        var candidates = CurseCandidates(ctx);
        var rng = new Rng(RngSeed(runSeed, slot));
        return candidates[rng.NextInt(0, candidates.Count)];
    }

    /// <summary>The complete three-option offer for one player slot.</summary>
    public static NeowOffer PredictOffer(ulong runSeed, int slot, NeowContext ctx)
    {
        var rng = new Rng(RngSeed(runSeed, slot));

        // 1. The curse — the event's first draw.
        var curseCandidates = CurseCandidates(ctx);
        var curse = curseCandidates[rng.NextInt(0, curseCandidates.Count)];

        // 2. Positive pool, minus the option that would duplicate/undo the curse.
        //    The game does these removals before any availability filtering.
        var positives = NeowRelics.Positives.ToList();
        if (NeowRelics.Counterparts.TryGetValue(curse.Slug, out var counterparts))
            foreach (var slug in counterparts) Remove(positives, slug);

        // 3. Skipped entirely for Large Capsule — this is the draw-count shift.
        if (curse.Slug != "large_capsule")
            positives.Add(rng.NextBool() ? NeowRelics.LavaRock : NeowRelics.SmallCapsule);

        // 4-5. Unconditional coin flips.
        positives.Add(rng.NextBool() ? NeowRelics.NutritiousOyster : NeowRelics.StoneHumidifier);
        positives.Add(rng.NextBool() ? NeowRelics.NeowsTalisman : NeowRelics.Pomander);

        // 6. Availability filtering happens only now, after the flips.
        positives.RemoveAll(r => !ctx.IsAllowed(r));

        // 7. Shuffle (consumes count-1 draws) and take the first two.
        Shuffle(positives, rng);
        return new NeowOffer(curse, positives[0], positives[1]);
    }

    /// <summary>Every player's offer, in lobby slot order.</summary>
    public static NeowOffer[] PredictAllOffers(ulong runSeed, NeowContext ctx)
    {
        var offers = new NeowOffer[ctx.PlayerCount];
        for (int slot = 0; slot < ctx.PlayerCount; slot++)
            offers[slot] = PredictOffer(runSeed, slot, ctx);
        return offers;
    }

    private static void Remove(List<NeowRelic> list, string slug) => list.RemoveAll(r => r.Slug == slug);

    /// <summary>
    /// ListExtensions.UnstableShuffle — a reverse Fisher-Yates whose exact draw count and
    /// swap order must match the game.
    /// </summary>
    private static void Shuffle<T>(List<T> list, Rng rng)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }
}
