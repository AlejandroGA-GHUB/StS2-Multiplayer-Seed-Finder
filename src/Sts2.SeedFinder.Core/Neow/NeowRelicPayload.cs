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
///     list.UnstableShuffle(PlayerRng.NonCombatGeneration)   one draw
///     the first two, as rewards that cannot be skipped, each drawing what it draws when claimed
///     then one curse: PlayerRng.NonCombatGeneration.NextItem over the curse pool's
///     generatable cards, ordered by id
/// </code>
///
/// Everything here is per player and exact: each piece reads only its own player's bag and
/// streams, which nothing else has touched at Neow, so what a partner picks cannot move it.
/// Before v0.113 the Bones shuffle ran on Rewards and the curse came off the RUN-level Niche
/// stream, which the whole table shared; both moved to the player's own NonCombatGeneration.
/// The one thing left open is claim order, see <see cref="BonesCurse"/>.
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
    /// The player's NonCombatGeneration stream, added in v0.113 and seeded like every other
    /// <c>PlayerRngSet</c> generator. Neow's Bones, Kaleidoscope, Phial Holster, War Paint and
    /// Whetstone draw from it on pickup.
    /// </summary>
    public static Rng NonCombatRng(ulong runSeed, int playerSlotIndex) =>
        new(unchecked(runSeed + (ulong)playerSlotIndex), GameHash.SnakeCase("NonCombatGeneration"));

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
        NonCombatRng(runSeed, slot).UnstableShuffle<NeowRelic>(list);
        return list.Length >= 2 ? [list[0], list[1]] : list;
    }

    /// <summary>
    /// Everything taking Neow's Bones costs this player's Rewards stream before their first
    /// fight, or null when the seed does not settle it.
    ///
    /// Whatever the two relics it deals draw when they are claimed; the shuffle itself is on
    /// NonCombatGeneration and costs Rewards nothing. Both relics are always handed over, so both
    /// always draw, and for every relic but one the total is the same in either claim order. The
    /// exception is Scroll Boxes for a Defect: each bundle rolls a 1-in-100 all-Claw check that
    /// skips the bundle's three draws, so what it costs depends on where in the stream it starts.
    /// Dealt alongside a relic that draws nothing, that is still one answer. Dealt alongside one
    /// that draws, the two claim orders can disagree, and only then is this null.
    /// </summary>
    public static int? BonesRewardDraws(ulong runSeed, int slot, NeowContext ctx, Character character)
    {
        int others = 0;
        bool defectBoxes = false;
        foreach (var r in BonesRelics(runSeed, slot, ctx))
        {
            if (character == Character.Defect && r.Slug == "scroll_boxes") { defectBoxes = true; continue; }
            others += Math.Max(CardRewardGenerator.NeowRewardDrawCost(r.Slug, character), 0);
        }
        if (!defectBoxes) return others;

        int boxesFirst = DefectScrollBoxes(Skip(CardRewardGenerator.RewardsRng(runSeed, slot), 0));
        if (others == 0) return boxesFirst;
        int boxesSecond = DefectScrollBoxes(Skip(CardRewardGenerator.RewardsRng(runSeed, slot), others));
        return boxesFirst == boxesSecond ? others + boxesFirst : null;
    }

    private static Rng Skip(Rng rng, int draws)
    {
        for (int i = 0; i < draws; i++) rng.NextInt();
        return rng;
    }

    /// <summary>
    /// Scroll Boxes for a Defect, drawn off <paramref name="rng"/> where it stands: per bundle,
    /// the Claw check, then three picks unless the check fired. Returns the draws it took.
    /// </summary>
    private static int DefectScrollBoxes(Rng rng)
    {
        int draws = 0;
        for (int bundle = 0; bundle < NeowCardPayload.Bundles; bundle++)
        {
            draws++;
            if (rng.NextInt(0, 100) < 1) continue;
            for (int i = 0; i < NeowCardPayload.BundleSize; i++) rng.NextInt();
            draws += NeowCardPayload.BundleSize;
        }
        return draws;
    }

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
    /// What claiming this Neow relic draws off NonCombatGeneration, other than through a capsule.
    /// Kaleidoscope runs one <c>StableShuffle</c> per reward (two), and Phial Holster rolls two
    /// potions at two draws each (rarity, then pick). Fishing Rod uses the stream too, but only
    /// after combats, and New Leaf moved to Transformations in v0.113.
    /// </summary>
    public static int NonCombatDrawsOnClaim(string relicSlug) => relicSlug switch
    {
        "kaleidoscope" => 2,
        "phial_holster" => 4,
        _ => 0,
    };

    /// <summary>
    /// Capsule pulls that draw from NonCombatGeneration on pickup: each upgrades random cards
    /// through one <c>StableShuffle</c>, so one draw whatever the deck holds.
    /// </summary>
    private static readonly HashSet<string> NonCombatOnPickup =
        new(StringComparer.Ordinal) { "war_paint", "whetstone" };

    /// <summary>
    /// The curse Neow's Bones adds to this player's deck, or null when claim order decides it.
    ///
    /// It is the next NonCombatGeneration draw after the shuffle and after whatever the two dealt
    /// relics draw from that stream when claimed. Every relic's own cost is fixed, so this is one
    /// answer, with a single exception: a capsule pulls from the player's bag off Rewards, and
    /// whether it lands on War Paint or Whetstone (one draw each) depends on how far into Rewards
    /// the other dealt relic has already gone. Both orders are played out, and only when they
    /// disagree is the curse left open.
    /// </summary>
    public static string? BonesCurse(
        ulong runSeed, int slot, NeowContext ctx, IReadOnlyList<NeowRelic> bonesRelics,
        IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks)
    {
        int? before = NonCombatDrawsBeforeCurse(runSeed, slot, bonesRelics, characters, unlocks, playerUnlocks);
        if (before is not { } draws) return null;
        var rng = Skip(NonCombatRng(runSeed, slot), draws);
        return BonesCursePool[rng.NextInt(0, BonesCursePool.Length)];
    }

    /// <summary>
    /// NonCombatGeneration draws ahead of the Bones curse: one for the shuffle, plus the two
    /// relics' claims, played out in both claim orders. Null when the orders disagree.
    /// </summary>
    private static int? NonCombatDrawsBeforeCurse(
        ulong runSeed, int slot, IReadOnlyList<NeowRelic> bonesRelics,
        IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks)
    {
        int fixedCost = 1;
        bool capsule = false;
        foreach (var r in bonesRelics)
        {
            fixedCost += NonCombatDrawsOnClaim(r.Slug);
            capsule |= ShapeOf(r.Slug) == RelicPayloadShape.Capsule;
        }
        if (!capsule) return fixedCost;

        int? first = null;
        for (int order = 0; order < Math.Min(bonesRelics.Count, 2); order++)
        {
            var deques = RunGenerator.PlayerRelicDeques(runSeed, characters, unlocks, playerUnlocks, slot);
            var rewards = CardRewardGenerator.RewardsRng(runSeed, slot);
            int pickups = 0;
            for (int i = 0; i < bonesRelics.Count; i++)
            {
                string slug = bonesRelics[(i + order) % bonesRelics.Count].Slug;
                if (ShapeOf(slug) == RelicPayloadShape.Capsule)
                    pickups += Pull(rewards, deques, RelicCount(slug)).Count(p => NonCombatOnPickup.Contains(p.Slug));
                else if (characters.Count > slot && characters[slot] == Character.Defect && slug == "scroll_boxes")
                    DefectScrollBoxes(rewards);
                else
                    Skip(rewards, Math.Max(CardRewardGenerator.NeowRewardDrawCost(slug, characters.Count > slot ? characters[slot] : Character.Ironclad), 0));
            }
            if (first is null) first = pickups;
            else if (first != pickups) return null;
        }
        return fixedCost + first;
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
