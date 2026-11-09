using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;

namespace Sts2.SeedFinder.Core.Neow;

/// <summary>Which chain a Neow relic's RELIC payload follows.</summary>
public enum RelicPayloadShape
{
    /// <summary>No modelled relic payload.</summary>
    None,

    /// <summary>Small and Large Capsule: one or two pulls from the front of the player's bag.</summary>
    Capsule,

    /// <summary>Neow's Bones: two of Neow's own relics, shuffled, plus a curse.</summary>
    Bones,
}

/// <summary>
/// The relics (and, for Neow's Bones, the curse) three Neow options hand a player when taken.
///
/// <code>
///   SmallCapsule.AfterObtained / LargeCapsule.AfterObtained        -> Capsule
///     for each of 1 / 2:
///       RelicFactory.RollRarity(PlayerRng.Rewards)     one NextFloat:
///                                                      &lt; 0.5 Common, &lt; 0.83 Uncommon, else Rare
///       player.RelicGrabBag.PullFromFront(rarity)      the FRONT of that player's own deque,
///                                                      as the UpFront bag shuffles left it
///     An empty deque walks Common -> Uncommon -> Rare; none is empty at Neow.
///
///   NeowsBones.AfterObtained                                        -> Bones
///     list = Neow.AllPossibleOptions' relics: the ten curses, the fourteen positives, then
///            LavaRock, NeowsTalisman, NutritiousOyster, Pomander, SmallCapsule,
///            StoneHumidifier; filtered by IsAllowedAtNeow(owner), minus Neow's Bones itself
///     PlayerRng.Rewards.Shuffle(list)                  descending Fisher-Yates, count - 1 draws
///     the first two, as rewards that cannot be skipped
///     then one curse: RunState.Rng.Niche.NextItem over the curse pool's generatable cards,
///     ordered by id
/// </code>
///
/// Two properties decide what is safe to promise:
///
/// <list type="bullet">
/// <item><b>The capsules and the Bones relics are per player and exact.</b> Each reads only
/// its own player's bag and Rewards stream, both of which nothing else has touched at Neow,
/// so what a partner picks cannot move them.</item>
/// <item><b>The Bones curse is NOT per player.</b> Niche is a RUN stream shared by the whole
/// lobby, and Kaleidoscope, New Leaf, another Neow's Bones, and the War Paint or Whetstone a
/// capsule can hand out all draw from it on pickup. Who resolves Neow first is not knowable,
/// so the curse is only promised when no such draw can happen anywhere at the table: see
/// <see cref="CurseIsCertain"/>.</item>
/// </list>
///
/// Lasting Candy is the one relic in these bags with an <c>IsAllowed</c> gate that can fail at
/// Neow: it is refused while any Ironclad in the lobby is on an account with no finished runs.
/// That state is not modelled, so a brand-new Ironclad account can see a capsule skip it.
/// </summary>
public static class NeowRelicPayload
{
    /// <summary>Which chain this relic's payload follows.</summary>
    public static RelicPayloadShape ShapeOf(string relicSlug) => relicSlug switch
    {
        "small_capsule" or "large_capsule" => RelicPayloadShape.Capsule,
        "neows_bones" => RelicPayloadShape.Bones,
        _ => RelicPayloadShape.None,
    };

    /// <summary>How many relics this Neow option hands over, or 0 when none is modelled.</summary>
    public static int RelicCount(string relicSlug) => relicSlug switch
    {
        "small_capsule" => 1,
        "large_capsule" => 2,
        "neows_bones" => 2,
        _ => 0,
    };

    /// <summary>Whether this option's relic payload can be predicted at all.</summary>
    public static bool IsPredictable(string relicSlug) => RelicCount(relicSlug) > 0;

    /// <summary>Neow options whose relic payload this tool can name, in offer order.</summary>
    public static readonly string[] Predictable = ["large_capsule", "neows_bones", "small_capsule"];

    /// <summary>
    /// <c>RelicFactory.RollRarity</c>'s thresholds. Plain constants in the game, with no pity
    /// counter and no ascension gate.
    /// </summary>
    public const float CommonBelow = 0.5f, UncommonBelow = 0.83f;

    /// <summary><c>RelicFactory.RollRarity</c>, one <c>NextFloat</c>.</summary>
    public static string RollRarity(Rng rng)
    {
        float roll = rng.NextFloat();
        return roll < CommonBelow ? "Common" : roll < UncommonBelow ? "Uncommon" : "Rare";
    }

    /// <summary>
    /// <c>RelicFactory.FallbackRelic</c>, handed over when every deque is empty. Unreachable at
    /// Neow, where the smallest deque holds twenty-odd relics; here so the walk stays total.
    /// </summary>
    public static readonly PoolRelic Fallback = new("Circlet", "circlet", "Special");

    // ---- Capsules ---------------------------------------------------------------------------

    /// <summary>
    /// What a Small or Large Capsule taken by this player hands over, in pull order.
    /// </summary>
    /// <param name="priorDraws">
    /// Draws already taken off this player's Rewards stream. Zero for a capsule taken at Neow,
    /// which is the only case a search asks about. Non-zero only when a capsule comes out of
    /// Neow's Bones, after the shuffle and whatever was claimed before it.
    /// </param>
    public static IReadOnlyList<PoolRelic> Capsule(
        ulong runSeed, int slot, IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks, string relicSlug, int priorDraws = 0)
    {
        int count = ShapeOf(relicSlug) == RelicPayloadShape.Capsule ? RelicCount(relicSlug) : 0;
        if (count == 0) return Array.Empty<PoolRelic>();

        var deques = RunGenerator.PlayerRelicDeques(runSeed, characters, unlocks, playerUnlocks, slot);
        var rewards = CardRewardGenerator.RewardsRng(runSeed, slot);
        for (int i = 0; i < priorDraws; i++) rewards.NextInt();

        return Pull(rewards, deques, count);
    }

    /// <summary>
    /// <paramref name="count"/> pulls against deques the caller owns. Each pull removes what it
    /// took, so a Large Capsule's second pull sees the deque its first one left, which is the
    /// ordering mistake two capsules sharing a freshly built bag would make.
    /// </summary>
    internal static IReadOnlyList<PoolRelic> Pull(
        Rng rewards, IReadOnlyDictionary<string, List<PoolRelic>> deques, int count)
    {
        var got = new PoolRelic[count];
        for (int i = 0; i < count; i++)
        {
            string? rarity = RollRarity(rewards);
            List<PoolRelic>? deque = null;
            while (rarity is not null)
            {
                if (deques.TryGetValue(rarity, out var d) && d.Count > 0) { deque = d; break; }
                rarity = rarity switch { "Common" => "Uncommon", "Uncommon" => "Rare", _ => null };
            }

            if (deque is null) { got[i] = Fallback; continue; }
            got[i] = deque[0];
            deque.RemoveAt(0);
        }
        return got;
    }

    // ---- Neow's Bones -----------------------------------------------------------------------

    /// <summary>
    /// The list Neow's Bones shuffles, for this player, in the order the game builds it.
    ///
    /// <c>Neow.AllPossibleOptions</c> is curses, positives, then six coin-flip relics in an order
    /// of their own: alphabetical by type, NOT the order Neow flips them in. Filtered with the
    /// same per-player rule as the offer, so a player without every character unlocked has no
    /// Kaleidoscope here either, and a list one shorter shuffles differently throughout.
    /// </summary>
    public static IReadOnlyList<NeowRelic> BonesList(int slot, NeowContext ctx)
    {
        var list = new List<NeowRelic>(30);
        foreach (var r in NeowRelics.Curses.Concat(NeowRelics.Positives).Concat(BonesCoinFlips))
            if (r.Slug != "neows_bones" && ctx.IsAllowedFor(r, slot)) list.Add(r);
        return list;
    }

    /// <summary>The coin-flip relics as <c>AllPossibleOptions</c> lists them.</summary>
    private static readonly NeowRelic[] BonesCoinFlips =
    [
        NeowRelics.LavaRock, NeowRelics.NeowsTalisman, NeowRelics.NutritiousOyster,
        NeowRelics.Pomander, NeowRelics.SmallCapsule, NeowRelics.StoneHumidifier,
    ];

    /// <summary>The two relics Neow's Bones hands this player, in the order it lists them.</summary>
    public static IReadOnlyList<NeowRelic> BonesRelics(ulong runSeed, int slot, NeowContext ctx)
    {
        var list = BonesList(slot, ctx).ToArray();
        var rewards = CardRewardGenerator.RewardsRng(runSeed, slot);

        // Rng.Shuffle: descending Fisher-Yates, the same swaps as UnstableShuffle.
        for (int n = list.Length - 1; n > 0; n--)
        {
            int k = rewards.NextInt(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
        return list.Length >= 2 ? [list[0], list[1]] : list;
    }

    /// <summary>Draws the Bones shuffle takes off the Rewards stream for this player.</summary>
    public static int BonesShuffleDraws(int slot, NeowContext ctx) =>
        Math.Max(BonesList(slot, ctx).Count - 1, 0);

    /// <summary>
    /// The curses Neow's Bones can add: the curse pool's cards that can be generated by
    /// modifiers, in <c>ModelId</c> order. No epoch gates and no mode filter apply to any of
    /// them, so the list is the same for every lobby. The eight curses left out (Ascender's
    /// Bane, Bad Luck, Curse of the Bell, Enthralled, Folly, Greed, Poor Sleep, Spore Mind)
    /// override <c>CanBeGeneratedByModifiers</c> to false.
    /// </summary>
    public static readonly string[] BonesCursePool =
        ["Clumsy", "Debt", "Decay", "Doubt", "Guilty", "Injury", "Normality", "Regret", "Shame", "Writhe"];

    /// <summary>
    /// The curse Neow's Bones adds, assuming its draw is the first one taken off Niche this run.
    /// Whether that assumption holds is <see cref="CurseIsCertain"/>'s question.
    /// </summary>
    public static string BonesCurse(ulong runSeed)
    {
        var niche = new Rng(runSeed, GameHash.SnakeCase("Niche"));
        return BonesCursePool[niche.NextInt(0, BonesCursePool.Length)];
    }

    /// <summary>Neow options that draw from Niche the moment they are taken.</summary>
    private static readonly HashSet<string> NicheAtNeow =
        new(StringComparer.Ordinal) { "kaleidoscope", "new_leaf", "neows_bones" };

    /// <summary>Capsule pulls that draw from Niche on pickup: both upgrade random cards.</summary>
    private static readonly HashSet<string> NicheOnPickup =
        new(StringComparer.Ordinal) { "war_paint", "whetstone" };

    /// <summary>
    /// Whether dealing this relic out of Neow's Bones rules the curse out of being promised:
    /// it either draws from Niche itself or, as a capsule, might.
    /// </summary>
    public static bool MovesCurse(string relicSlug) =>
        NicheAtNeow.Contains(relicSlug) || ShapeOf(relicSlug) == RelicPayloadShape.Capsule;

    /// <summary>
    /// Whether this player's Bones curse is the first Niche draw of the run whatever anyone at
    /// the table picks, and so whether <see cref="BonesCurse"/> can be promised.
    ///
    /// Two conditions, both strict on purpose. A search that refuses a seed it could have kept
    /// costs a few matches; one that promises a curse the game then does not deal costs trust.
    /// <list type="number">
    /// <item>Neither of the Bones relics draws from Niche before the curse. Kaleidoscope and New
    /// Leaf do so directly. A capsule might, through War Paint or Whetstone, but which it pulls
    /// depends on which of the two rewards the player claims first, so a capsule here is
    /// refused rather than guessed.</item>
    /// <item>Nobody else's Neow offer holds anything that could. Their capsules are exact, so
    /// they are checked for what they would really pull; anything in
    /// <see cref="NicheAtNeow"/> refuses outright.</item>
    /// </list>
    /// </summary>
    public static bool CurseIsCertain(
        ulong runSeed, int slot, NeowContext ctx, IReadOnlyList<NeowRelic> bonesRelics,
        IReadOnlyList<NeowOffer> offers, IReadOnlyList<Character> characters,
        UnlockState unlocks, IReadOnlyList<UnlockState>? playerUnlocks)
    {
        foreach (var r in bonesRelics)
            if (MovesCurse(r.Slug)) return false;

        for (int other = 0; other < offers.Count; other++)
        {
            if (other == slot) continue;
            var o = offers[other];
            foreach (var r in new[] { o.Positive1, o.Positive2, o.Curse })
            {
                if (NicheAtNeow.Contains(r.Slug)) return false;
                if (ShapeOf(r.Slug) == RelicPayloadShape.Capsule
                    && characters.Count > other
                    && Capsule(runSeed, other, characters, unlocks, playerUnlocks, r.Slug)
                        .Any(p => NicheOnPickup.Contains(p.Slug)))
                    return false;
            }
        }
        return true;
    }

    // ---- What a search may ask for -----------------------------------------------------------

    /// <summary>Every relic a capsule could ever hand anyone: Common, Uncommon and Rare.</summary>
    public static IReadOnlyList<PoolRelic> CapsuleAll { get; } =
        RelicPoolData.SharedRelics
            .Concat(Enum.GetValues<Character>().SelectMany(RelicPoolData.RelicsFor))
            .Where(r => r.Rarity is "Common" or "Uncommon" or "Rare")
            .ToList();

    /// <summary>"war_paint" to "War Paint", for a capsule relic; a Neow relic by its own name.</summary>
    public static string Display(string slug) =>
        NeowRelics.Find(slug)?.Name ?? ShopRelics.TitleCase(slug);

    /// <summary>Look a capsule relic up by slug or type name, punctuation-insensitively.</summary>
    public static PoolRelic? FindCapsuleRelic(string nameOrSlug)
    {
        var key = ShopRelics.Key(nameOrSlug);
        foreach (var r in CapsuleAll)
            if (ShopRelics.Key(r.Slug) == key || ShopRelics.Key(r.Name) == key) return r;
        return null;
    }

    /// <summary>
    /// What a capsule could hand the player in <paramref name="slot"/>: their own bag's Common,
    /// Uncommon and Rare relics, which is the shared pool plus their character's, less anything
    /// their epochs still hide.
    /// </summary>
    public static IReadOnlyList<PoolRelic> CapsuleOfferable(
        IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks, int slot)
    {
        var result = new List<PoolRelic>();
        foreach (var rarity in new[] { "Common", "Uncommon", "Rare" })
            if (RunGenerator.PlayerDeques(characters, unlocks, playerUnlocks, rarity)[slot] is { } d)
                result.AddRange(d.Relics);
        return result;
    }

    /// <summary>
    /// Whether this player's capsule hands over every relic in <paramref name="wanted"/>.
    ///
    /// The rarity rolls come first because they are a draw or two each and settle most seeds on
    /// their own: a wanted Rare needs a Rare roll, which is one seed in six. Only a seed that
    /// survives pays for replaying the bag shuffles. Rolling a rarity never empties a deque at
    /// Neow, so the fallback walk cannot rescue a seed this rejects.
    /// </summary>
    public static bool CapsuleGives(
        ulong runSeed, int slot, IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks, string relicSlug, IReadOnlyList<PoolRelic> wanted)
    {
        int count = RelicCount(relicSlug);
        var rewards = CardRewardGenerator.RewardsRng(runSeed, slot);
        Span<int> rolled = stackalloc int[3];
        for (int i = 0; i < count; i++)
            rolled[RollRarity(rewards) switch { "Common" => 0, "Uncommon" => 1, _ => 2 }]++;
        foreach (var w in wanted)
            if (--rolled[w.Rarity switch { "Common" => 0, "Uncommon" => 1, _ => 2 }] < 0) return false;

        var got = Capsule(runSeed, slot, characters, unlocks, playerUnlocks, relicSlug);
        foreach (var w in wanted)
            if (!got.Any(g => g.Slug == w.Slug)) return false;
        return true;
    }

    /// <summary>Resolve a curse name or slug against <see cref="BonesCursePool"/>.</summary>
    public static string? FindCurse(string nameOrSlug)
    {
        var key = ShopRelics.Key(nameOrSlug);
        return BonesCursePool.FirstOrDefault(c => ShopRelics.Key(c) == key);
    }
}
