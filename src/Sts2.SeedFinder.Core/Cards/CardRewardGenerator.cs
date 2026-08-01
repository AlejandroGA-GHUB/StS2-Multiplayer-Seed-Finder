using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Cards;

/// <summary>One card offered by a reward, with the rarity that was rolled to reach it.</summary>
public sealed record RewardCard(string TypeName, CardRarity Rarity, bool Upgraded)
{
    /// <summary>The game's own id form, e.g. <c>strike_ironclad</c>.</summary>
    public string Slug => GameHash.SnakeCase(TypeName);
}

/// <summary>
/// Everything the first combat of a run drops for one player.
/// </summary>
/// <param name="Cards">The three card choices, in offered order.</param>
/// <param name="HasPotion">
/// Whether the potion roll landed. Which potion is not modelled, only that it costs two
/// draws, but the roll itself is deterministic so this flag is reliable.
/// </param>
public sealed record FirstFightReward(IReadOnlyList<RewardCard> Cards, bool HasPotion);

/// <summary>
/// The rewards of a run of consecutive normal fights, fight 1 first.
///
/// Only the first fight is guaranteed by the map. Everything after it assumes the party keeps
/// walking into Monster rooms, which is why <see cref="CardRewardGenerator.Hallway"/> takes the
/// count rather than deciding it.
/// </summary>
public sealed record HallwayRewards(IReadOnlyList<FirstFightReward> Fights)
{
    /// <summary>The reward for the <paramref name="fight"/>'th fight, 1-based, or null.</summary>
    public FirstFightReward? Fight(int fight) =>
        fight >= 1 && fight <= Fights.Count ? Fights[fight - 1] : null;
}

/// <summary>
/// Reproduces the card reward the FIRST combat of a run offers a player.
///
/// Row 1 of every act map is forced to Monster with <c>CanBeModified = false</c>
/// (<c>StandardActMap.AssignPointTypes</c>), so the first room after the Ancient is always a
/// normal fight, and it is the first thing in the run to touch <c>PlayerRng.Rewards</c>.
///
/// Draw order on that stream, and it is the whole trick — the potion roll happens while the
/// reward LIST is being built, before anything is populated:
/// <code>
///   RewardsSet.GenerateRewardsFor:  potion odds roll        NextFloat   (1)
///   RewardsSet.GenerateWithoutOffering populates in insertion order:
///     GoldReward.Populate:          gold amount             NextInt     (1)
///     PotionReward.Populate:        rarity + pick           2 draws     (only if the roll hit)
///     CardReward.Populate -> CardFactory.CreateForReward, three times:
///                                   rarity roll             NextFloat
///                                   card pick               NextItem
///                                   upgrade roll            NextFloat
/// </code>
///
/// THE ONE CAVEAT: this assumes the player's Neow pick consumed no draws from this stream.
/// Most do not, but five relics obtainable at Neow do — see <see cref="NeowRewardDrawCost"/>.
/// Pass the cost through <c>priorDraws</c> when you know what was taken.
///
/// Two consequences of the pity counter starting at -0.05 are worth knowing:
/// the first fight can never offer a Rare (the rare threshold stays negative across all
/// three draws), and no card is ever upgraded in Act 1 (the upgrade odds scale with the act
/// index, which is 0).
/// </summary>
public static class CardRewardGenerator
{
    /// <summary>AscensionLevel.Scarcity is the 7th enum entry, and HasLevel is >=.</summary>
    public const int Scarcity = 7;

    // CardRarityOdds. Only the rare and uncommon base odds are ever queried by a roll;
    // regularCommonOdds exists in the game but no code path reads it.
    private const float RegularUncommonOdds = 0.37f;
    private const float BaseRarityOffset = -0.05f;
    private const float MaxRarityOffset = 0.4f;

    private static float RegularRareOdds(int ascension) => ascension >= Scarcity ? 0.0149f : 0.03f;
    private static float RarityGrowth(int ascension) => ascension >= Scarcity ? 0.005f : 0.01f;

    /// <summary>PotionRewardOdds starts here and a Monster room adds no bonus.</summary>
    private const float BasePotionRewardOdds = 0.4f;

    /// <summary>
    /// The player's Rewards stream. <c>Player.cs:330</c> seeds the whole PlayerRngSet with
    /// <c>hash(seed) + slotIndex</c>, and each generator in the set is then named-derived.
    /// </summary>
    public static Rng RewardsRng(ulong runSeed, int playerSlotIndex) =>
        new(unchecked(runSeed + (ulong)playerSlotIndex), GameHash.SnakeCase("Rewards"));

    /// <summary>
    /// Draws a Neow option takes off the Rewards stream before the first fight ever rolls.
    /// Zero for all but these five: everything else Neow can hand out either uses a different
    /// generator or none at all.
    /// </summary>
    /// <remarks>
    /// Scroll Boxes is listed at its floor. It draws 6 for the two bundles, plus one extra
    /// per bundle for Defect only (a 1-in-100 all-Claw check), so a Defect player's cost is 8.
    /// Neow's Bones shuffles the other Neow relics, which is <c>count - 1</c> draws.
    /// </remarks>
    public static int NeowRewardDrawCost(string relicSlug, Character character) => relicSlug switch
    {
        "arcane_scroll" => 1,          // 1 uniform-rare card, no upgrade roll
        "hefty_tablet" => 3,           // 3 of the same
        "massive_scroll" => 9,         // 3 cards with rarity and upgrade rolls
        "scroll_boxes" => character == Character.Defect ? 8 : 6,
        "neows_bones" => -1,           // shuffle length depends on the Neow pool; see NeowGenerator
        _ => 0,
    };

    /// <summary>
    /// The reward the first fight offers this player.
    /// </summary>
    /// <param name="priorDraws">
    /// Draws already taken off the Rewards stream, i.e. the Neow pick's cost. Zero is right
    /// for every Neow option except the handful in <see cref="NeowRewardDrawCost"/>.
    /// </param>
    public static FirstFightReward FirstFight(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        int ascension = 0,
        UnlockState? unlocks = null,
        int priorDraws = 0) =>
        Hallway(runSeed, playerSlotIndex, character, 1, ascension, unlocks, priorDraws).Fights[0];

    /// <summary>
    /// The rewards of the first <paramref name="fights"/> consecutive normal fights.
    ///
    /// Fight 1 is guaranteed by the map. Every fight after it is a PRECONDITION: it assumes the
    /// party walks from one Monster room into another, taking no shop, elite, event or rest in
    /// between. That is a normal opening but not the only one, so the caller has to mean it.
    ///
    /// Player CHOICES are free — generation happens before the offer, so taking or skipping a
    /// card, and taking or skipping a potion, move nothing. What carries between fights is only
    /// what was rolled:
    /// <list type="bullet">
    /// <item>the stream position, 11 draws per fight plus 2 more when a potion landed;</item>
    /// <item><c>CardRarityOdds.CurrentValue</c>, the rare pity offset, which grows by
    /// <c>RarityGrowth</c> per card drawn and resets to the floor whenever a Rare lands;</item>
    /// <item><c>PotionRewardOdds.CurrentValue</c>, which moves 0.1 down on a hit and 0.1 up on a
    /// miss, so fight 2's potion chance is never the base 0.4.</item>
    /// </list>
    ///
    /// The card blacklist does NOT carry: it is scoped to one reward, so fight 2 can re-offer a
    /// card fight 1 showed.
    /// </summary>
    public static HallwayRewards Hallway(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        int fights = 2,
        int ascension = 0,
        UnlockState? unlocks = null,
        int priorDraws = 0)
    {
        var rng = RewardsRng(runSeed, playerSlotIndex);
        Burn(rng, priorDraws);

        var pool = PoolFor(character, unlocks);
        var results = new List<FirstFightReward>(fights);

        // Both pity counters start at their run-start values and persist across fights.
        float rarityOffset = BaseRarityOffset;
        float potionOdds = BasePotionRewardOdds;

        for (int fight = 0; fight < fights; fight++)
        {
            // PotionRewardOdds.Roll, at reward-LIST build time and so before anything is
            // populated. A Monster room adds no elite bonus, so the threshold is the counter
            // itself. Note the counter moves whichever way the roll goes.
            bool hasPotion = rng.NextFloat() < potionOdds;
            potionOdds += hasPotion ? -0.1f : 0.1f;

            Burn(rng, 1);                          // GoldReward.Populate: NextInt(min, max + 1)
            if (hasPotion) Burn(rng, 2);           // PotionReward.Populate: rarity roll, then pick

            var cards = new List<RewardCard>(3);
            var taken = new List<string>(3);

            for (int i = 0; i < 3; i++)
            {
                // CardFactory.CreateForReward blacklists the cards already offered, so each draw
                // sees a slightly shorter pool than the last. That used to be expressed by
                // materialising the shorter list, twice per draw — once for the blacklist and
                // once for the rarity — which is nine times a fight over a ninety-card pool.
                // Counting and indexing in place asks the same questions of the same entries in
                // the same order, and allocates nothing.
                var rarity = RollRarity(rng, ref rarityOffset, pool, taken, ascension);

                int count = CountAvailable(pool, taken, rarity);
                var picked = NthAvailable(pool, taken, rarity, rng.NextInt(0, count));

                // The upgrade draw is taken outside the IsUpgradable check, so it always happens.
                // In Act 1 the odds are 0 (they scale with the act index), so it never lands.
                float roll = rng.NextFloat();
                bool upgraded = roll <= 0f;

                cards.Add(new RewardCard(picked.TypeName, picked.Rarity, upgraded));
                taken.Add(picked.TypeName);
            }

            results.Add(new FirstFightReward(cards, hasPotion));
        }
        return new HallwayRewards(results);
    }

    /// <summary>
    /// Whether a fight this far into a run can offer a Rare at all.
    ///
    /// Fight 1 never can: the pity offset starts at -0.05 and grows by at most 0.01 a draw, so
    /// the rare threshold is still ≤ 0 on all three draws and no roll can fall under it. By
    /// fight 2 it has grown past zero, so a Rare becomes possible — which is why the pickers
    /// must stop hiding rares the moment a criterion targets anything but the first fight.
    /// </summary>
    public static bool CanOfferRare(int fight) => fight >= 2;

    /// <summary>
    /// How deep into a hallway the tool will predict.
    ///
    /// The cap is a product decision, not a limit in the maths: <see cref="Hallway"/> walks as
    /// far as it is asked to. What each extra fight costs is ASSUMPTION. Fight 1 is free, since
    /// row 1 of the map is forced to Monster. Every fight after it assumes the party walked
    /// straight into the next Monster room, with no shop, elite, event or rest breaking the
    /// chain, and that gets less likely with each one.
    ///
    /// Three is where that stops paying: a three-room opening hallway is still an ordinary route
    /// and the UI states the assumption plainly, where a prediction quietly conditional on a
    /// four-room one would be worse than no prediction at all.
    /// </summary>
    public const int MaxPredictableFight = 3;

    /// <summary>
    /// Advance the stream without caring about the value. Every draw method on Rng consumes
    /// exactly one step of the underlying generator, so which one we call does not matter —
    /// only how many times.
    /// </summary>
    private static void Burn(Rng rng, int draws)
    {
        for (int i = 0; i < draws; i++) rng.NextFloat();
    }

    /// <summary>
    /// CardRarityOdds.Roll for a regular encounter, including the pity offset it maintains:
    /// a rare resets it to the floor, anything else nudges it up toward the cap.
    /// </summary>
    private static CardRarity RollRarity(
        Rng rng, ref float offset, CardEntry[] pool, List<string> taken, int ascension)
    {
        float roll = rng.NextFloat();
        float rareThreshold = RegularRareOdds(ascension) + offset;

        var rarity = roll < rareThreshold ? CardRarity.Rare
                   : roll < RegularUncommonOdds + rareThreshold ? CardRarity.Uncommon
                   : CardRarity.Common;

        offset = rarity == CardRarity.Rare
            ? BaseRarityOffset
            : Math.Min(offset + RarityGrowth(ascension), MaxRarityOffset);

        return NextAllowedRarity(rarity, pool, taken);
    }

    /// <summary>
    /// CardFactory.GetNextAllowedRarity — walk up the rarity ladder (with wrapping) until one
    /// the pool can actually satisfy is found. A full pool never needs this, but a pool drained
    /// by the blacklist could.
    /// </summary>
    private static CardRarity NextAllowedRarity(CardRarity rarity, CardEntry[] pool, List<string> taken)
    {
        // The "already tried" set is a bitmask rather than a List, since this runs once per draw
        // and the ladder has at most four rungs.
        int seen = 1 << (int)rarity;
        while (rarity != CardRarity.None && CountAvailable(pool, taken, rarity) == 0)
        {
            rarity = NextHighest(rarity);
            int bit = 1 << (int)rarity;
            if ((seen & bit) != 0) return CardRarity.None;
            seen |= bit;
        }
        return rarity;
    }

    /// <summary>How many cards of one rarity the pool still offers, ignoring those already drawn
    /// into this reward.</summary>
    private static int CountAvailable(CardEntry[] pool, List<string> taken, CardRarity rarity)
    {
        int n = 0;
        for (int i = 0; i < pool.Length; i++)
            if (pool[i].Rarity == rarity && !Blacklisted(taken, pool[i].TypeName)) n++;
        return n;
    }

    /// <summary>
    /// The <paramref name="index"/>'th still-available card of a rarity, in pool order. Pool order
    /// is what the game indexes into, so this has to walk rather than sort or hash.
    /// </summary>
    private static CardEntry NthAvailable(CardEntry[] pool, List<string> taken, CardRarity rarity, int index)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Rarity != rarity || Blacklisted(taken, pool[i].TypeName)) continue;
            if (index-- == 0) return pool[i];
        }
        throw new InvalidOperationException($"no card of rarity {rarity} left at index {index}.");
    }

    /// <summary>Linear because the blacklist holds at most two entries when it is consulted.</summary>
    private static bool Blacklisted(List<string> taken, string typeName)
    {
        for (int i = 0; i < taken.Count; i++)
            if (taken[i] == typeName) return true;
        return false;
    }

    /// <summary>CardRarityExtensions.GetNextHighestRarityWithWrapping — Rare wraps to Common.</summary>
    private static CardRarity NextHighest(CardRarity r) => r switch
    {
        CardRarity.Basic => CardRarity.Common,
        CardRarity.Common => CardRarity.Uncommon,
        CardRarity.Uncommon => CardRarity.Rare,
        CardRarity.Rare => CardRarity.Common,
        _ => CardRarity.None,
    };

    /// <summary>
    /// One cached pool per character per thread, paired with the unlock state it was built for.
    ///
    /// The pool depends only on the character and the unlocks, so it is the same for every seed
    /// in a search, but it was rebuilt on every call: a LINQ filter and a ToList over ninety
    /// cards, twice per seed in a two-player lobby. Thread-static keeps it uncontended under
    /// Parallel.For and bounded by worker count; the state is stored WITH the pool so the pair
    /// can never be read half-updated.
    /// </summary>
    private sealed record PoolCache(UnlockState? Unlocks, CardEntry[] Pool);

    [ThreadStatic] private static PoolCache?[]? _pools;

    /// <summary>
    /// The character's pool as a reward sees it. Callers must treat the result as read-only:
    /// it is shared with every other caller on this thread.
    /// </summary>
    public static CardEntry[] PoolFor(Character character, UnlockState? unlocks = null)
    {
        var cache = _pools ??= new PoolCache?[Enum.GetValues<Character>().Length];
        int slot = (int)character;

        if (cache[slot] is { } hit && ReferenceEquals(hit.Unlocks, unlocks)) return hit.Pool;

        var built = BuildPool(character, unlocks);
        cache[slot] = new PoolCache(unlocks, built);
        return built;
    }

    /// <summary>
    /// The character's pool as a reward sees it: epoch-gated cards removed, then filtered for
    /// run mode. Multiplayer drops singleplayer-only cards; the game currently ships none, but
    /// the filter is what the code does and a patch could add some.
    /// </summary>
    private static CardEntry[] BuildPool(Character character, UnlockState? unlocks)
    {
        var pool = CardPoolData.For(character).AsEnumerable();

        if (unlocks is not null &&
            CardPoolData.EpochGates.TryGetValue(character.ToString(), out var gates))
        {
            var locked = new HashSet<string>();
            foreach (var (epoch, cards) in gates)
                if (!unlocks.IsEpochRevealed(epoch))
                    foreach (var c in cards) locked.Add(c);

            if (locked.Count > 0) pool = pool.Where(c => !locked.Contains(c.TypeName));
        }

        return pool.Where(c => c.Mode != CardMode.SingleplayerOnly).ToArray();
    }
}


