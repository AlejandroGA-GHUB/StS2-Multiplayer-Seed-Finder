using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Modifiers;

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
/// Most do not, but ten relics obtainable at Neow do — see <see cref="NeowRewardDrawCost"/>.
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
    /// Zero for all but the ten below: everything else Neow can hand out either uses a
    /// different generator or none at all.
    /// </summary>
    /// <remarks>
    /// Not only the scrolls. Anything that builds a card, potion or relic reward in
    /// <c>AfterObtained</c> pays for it here, because every reward factory defaults to this
    /// stream: <c>CardFactory.CreateForReward</c> (and <c>CardRarityOdds</c>, which is bound to
    /// it), <c>PotionFactory</c> via <c>PotionReward.Populate</c>, and <c>RelicFactory.RollRarity</c>.
    /// A non-Uniform card costs 3 (rarity via <c>RollWithBaseOdds</c>, which moves no pity, then
    /// pick, then upgrade), a potion 2, a relic rarity 1.
    ///
    /// Scroll Boxes is listed at its floor. It draws 6 for the two bundles, plus one extra
    /// per bundle for Defect only (a 1-in-100 all-Claw check), so a Defect player's cost is 8.
    /// Neow's Bones shuffles the other Neow relics, which is <c>count - 1</c> draws.
    ///
    /// Lead Paperweight, Lost Coffer and both Capsules were confirmed against finished v0.110.1
    /// and v0.111.0 runs, whose fight 1 matched only at these offsets. Kaleidoscope is from
    /// source alone (v0.111.0): no finished run on a current card pool took it.
    /// </remarks>
    public static int NeowRewardDrawCost(string relicSlug, Character character) => relicSlug switch
    {
        "arcane_scroll" => 1,          // 1 uniform-rare card, no upgrade roll
        "hefty_tablet" => 3,           // 3 of the same
        "massive_scroll" => 9,         // 3 cards with rarity and upgrade rolls
        "lead_paperweight" => 6,       // 2 colourless cards with rarity and upgrade rolls
        "kaleidoscope" => 18,          // 2 rewards x 3 other pools x 1 card with both rolls
        "lost_coffer" => 11,           // a 3-card reward (9), then a potion (2)
        "small_capsule" => 1,          // a relic reward: one rarity roll
        "large_capsule" => 2,          // two relics, one rarity roll each
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
        int priorDraws = 0,
        bool isMultiplayer = true,
        IReadOnlyList<Character>? addedCharacters = null) =>
        Hallway(runSeed, playerSlotIndex, character, 1, ascension, unlocks, priorDraws,
                isMultiplayer, addedCharacters).Fights[0];

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
        int priorDraws = 0,
        bool isMultiplayer = true,
        IReadOnlyList<Character>? addedCharacters = null)
    {
        var rng = RewardsRng(runSeed, playerSlotIndex);
        Burn(rng, priorDraws);

        // Solo and co-op pools differ by the five multiplayer-only cards per character, all of
        // them Uncommon or Rare, so the rarity roll is unaffected but the pick inside it is not.
        // A CharacterCards tickbox unions another character's pool into every reward flagged
        // IsCardReward, and CardReward sets that flag in its own constructor, so these rewards
        // see the longer pool. The rarity roll is unaffected, but the pick inside it is not.
        var pool = PoolFor(character, unlocks, isMultiplayer, addedCharacters);
        var index = PoolIndex.For(pool);
        var results = new List<FirstFightReward>(fights);

        // Hoisted out of the fight loop: three entries is the most a reward can blacklist, and
        // stackallocating inside the loop would repeat the reservation per fight.
        Span<int> taken = stackalloc int[3];

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
            int takenCount = 0;

            for (int i = 0; i < 3; i++)
            {
                // CardFactory.CreateForReward blacklists the cards already offered, so each draw
                // sees a slightly shorter pool than the last. That used to be expressed by
                // materialising the shorter list, twice per draw — once for the blacklist and
                // once for the rarity — which is nine times a fight over a ninety-card pool.
                // Counting and indexing in place asks the same questions of the same entries in
                // the same order, and allocates nothing.
                //
                // The walk is now over one rarity's entries rather than the whole pool, and the
                // blacklist compares interned type ids rather than strings. Both are bookkeeping
                // changes: the same entries are visited in the same pool order and the same
                // draws are taken, so the answer is unchanged. See PoolIndex.
                var rarity = RollRarity(rng, ref rarityOffset, index, pool, taken[..takenCount], ascension);

                int count = CountAvailable(index, pool, taken[..takenCount], rarity);
                int picked = NthAvailable(index, pool, taken[..takenCount], rarity, rng.NextInt(0, count));

                // The upgrade draw is taken outside the IsUpgradable check, so it always happens.
                // In Act 1 the odds are 0 (they scale with the act index), so it never lands.
                float roll = rng.NextFloat();
                bool upgraded = roll <= 0f;

                cards.Add(new RewardCard(pool[picked].TypeName, pool[picked].Rarity, upgraded));
                taken[takenCount++] = picked;
            }

            results.Add(new FirstFightReward(cards, hasPotion));
        }
        return new HallwayRewards(results);
    }

    /// <summary>
    /// The ten three-card rewards the Draft modifier walks a player through at Neow.
    ///
    /// A real reward chain rather than a payload one-liner, which is why it lives here beside
    /// <see cref="Hallway"/> instead of in <c>Modifiers/</c>. Three things make it differ from a
    /// fight reward, and all three come from the options Draft builds:
    ///
    /// <list type="bullet">
    /// <item><b>No pity, in either direction.</b> Draft sets neither <c>ForceRarityOddsChange</c>
    /// nor an Encounter source, so <c>CardFactory.RollForRarity</c> takes
    /// <c>RollWithBaseOdds</c>, which reads the base odds with NO offset and does not write the
    /// counter back. Every one of the thirty cards rolls against the same fixed threshold, so
    /// unlike a fight reward a Rare is possible from the very first card. It also means the
    /// hallway rewards afterwards still start from the run's initial offset, which is what lets
    /// Draft be accounted for as a plain draw count.</item>
    /// <item><b>No upgrade roll</b>, so a card is two draws rather than three.</item>
    /// <item><b>One blacklist per reward of three</b>, not across all ten: each
    /// <c>CardReward.Populate</c> is its own <c>CreateForReward</c> call. So a card can appear in
    /// several rewards, and only within one are the three distinct.</item>
    /// </list>
    ///
    /// Sixty draws in all, which is <see cref="RunModifiers.NeowRewardDraws"/> for Draft. The
    /// player picking one card from each reward consumes nothing: generation happens in
    /// <c>Populate</c>, before the offer, exactly as it does for a fight reward.
    ///
    /// The pool is the character's, widened by CharacterCards the same way a fight reward is,
    /// because <c>CardReward</c>'s constructor adds the <c>IsCardReward</c> flag that hook needs.
    /// </summary>
    public static RewardCard[][] DraftRewards(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        int ascension = 0,
        UnlockState? unlocks = null,
        int priorDraws = 0,
        bool isMultiplayer = true,
        IReadOnlyList<Character>? addedCharacters = null)
    {
        var rng = RewardsRng(runSeed, playerSlotIndex);
        Burn(rng, priorDraws);

        var pool = PoolFor(character, unlocks, isMultiplayer, addedCharacters);
        var index = PoolIndex.For(pool);

        var rewards = new RewardCard[DraftRewardCount][];
        Span<int> taken = stackalloc int[DraftOptionCount];

        for (int reward = 0; reward < DraftRewardCount; reward++)
        {
            var cards = new RewardCard[DraftOptionCount];
            int takenCount = 0;

            for (int i = 0; i < DraftOptionCount; i++)
            {
                var rarity = RollBaseRarity(rng, index, pool, taken[..takenCount], ascension);

                int count = CountAvailable(index, pool, taken[..takenCount], rarity);
                int picked = NthAvailable(index, pool, taken[..takenCount], rarity, rng.NextInt(0, count));

                cards[i] = new RewardCard(pool[picked].TypeName, pool[picked].Rarity, false);
                taken[takenCount++] = picked;
            }

            rewards[reward] = cards;
        }
        return rewards;
    }

    /// <summary>How many three-card rewards Draft offers.</summary>
    public const int DraftRewardCount = 10;

    /// <summary>How many cards each Draft reward offers, of which the player keeps one.</summary>
    public const int DraftOptionCount = 3;

    /// <summary>
    /// <c>CardRarityOdds.RollWithBaseOdds</c>: the base thresholds with no pity offset applied
    /// and none written back. Separate from <see cref="RollRarity"/> rather than a flag on it,
    /// because the two differ in both the threshold and the side effect, and a single method
    /// taking a "use pity" bool is how those get mixed up.
    /// </summary>
    private static CardRarity RollBaseRarity(
        Rng rng, PoolIndex index, CardEntry[] pool, ReadOnlySpan<int> taken, int ascension)
    {
        float roll = rng.NextFloat();
        float rare = RegularRareOdds(ascension);

        var rarity = roll < rare ? CardRarity.Rare
                   : roll < RegularUncommonOdds + rare ? CardRarity.Uncommon
                   : CardRarity.Common;

        return NextAllowedRarity(rarity, index, pool, taken);
    }

    /// <summary>
    /// <c>CardRarityOdds.RollWithBaseOdds</c> over an arbitrary pool, for the one Neow payload
    /// that rolls a rarity. Exposed rather than duplicated so Massive Scroll and Draft cannot
    /// drift from each other or from the fight walk's ladder.
    /// </summary>
    internal static CardRarity RollBaseRarityFor(
        Rng rng, PoolIndex index, CardEntry[] pool, ReadOnlySpan<int> taken, int ascension) =>
        RollBaseRarity(rng, index, pool, taken, ascension);

    /// <summary>
    /// Whether a fight this far into a run can offer a Rare at all, at this ascension.
    ///
    /// Fight 1 never can: the pity offset starts at -0.05 and grows by 0.01 a draw, so the rare
    /// threshold is still ≤ 0 on all three draws and no roll can fall under it. Below A7 it has
    /// grown past zero by fight 2. Scarcity (A7+) halves both the base odds and the growth, and
    /// then the first card whose threshold clears zero is the LAST card of fight 3, so fights 1
    /// and 2 can never offer a Rare there.
    /// </summary>
    public static bool CanOfferRare(int fight, int ascension) => RaresCanLand([fight], ascension);

    /// <summary>
    /// The fights, up to <see cref="MaxPredictableFight"/>, that can offer a Rare at this
    /// ascension. What the web picker greys rares against, sent by the server so the page never
    /// carries its own copy of the pity arithmetic.
    /// </summary>
    public static int[] RareFights(int ascension) =>
        Enumerable.Range(1, MaxPredictableFight).Where(f => CanOfferRare(f, ascension)).ToArray();

    /// <summary>
    /// Whether ONE player's consecutive rewards can hold a Rare in each fight listed, together.
    /// A fight listed twice asks for two Rares in that one reward.
    ///
    /// Not a product of the single-fight answers, because a Rare resets the pity offset to its
    /// floor: after one lands, the threshold is back below zero for the next few cards. So at A0
    /// Rares in fights 2 and 3 are possible only when the first comes early in fight 2 (a Rare
    /// on fight 2's last card leaves fight 3 unable to roll one), and at A7+, where only fight
    /// 3's last card can ever be Rare, any two Rares are impossible.
    ///
    /// Walks the card positions with the offset arithmetic <see cref="RollRarity"/> uses, float
    /// for float, since the answer hinges on thresholds that land exactly on zero. A Rare is
    /// possible on a card when its threshold is above zero, since a roll is never negative.
    /// Rolling a Rare nobody asked for is never taken: it only resets the offset, which can only
    /// make a later Rare harder.
    /// </summary>
    public static bool RaresCanLand(IReadOnlyList<int> rareFights, int ascension)
    {
        var need = new int[MaxPredictableFight + 1];
        foreach (int f in rareFights)
        {
            if (f < 1 || f > MaxPredictableFight) return false;
            if (++need[f] > 3) return false;   // a reward is three cards
        }
        return Walk(0, BaseRarityOffset);

        bool Walk(int card, float offset)
        {
            int fight = card / 3 + 1;
            if (card % 3 == 0 && fight > 1 && need[fight - 1] > 0) return false;   // left one behind
            if (card == MaxPredictableFight * 3) return true;

            if (need[fight] > 0 && RegularRareOdds(ascension) + offset > 0f)
            {
                need[fight]--;
                bool landed = Walk(card + 1, NextRarityOffset(offset, rare: true, ascension));
                need[fight]++;
                if (landed) return true;
            }
            return Walk(card + 1, NextRarityOffset(offset, rare: false, ascension));
        }
    }

    /// <summary>
    /// The pity offset after one card: a Rare resets it to the floor, anything else nudges it up
    /// toward the cap. Shared by <see cref="RollRarity"/> and <see cref="RaresCanLand"/> so the
    /// two can never disagree about the arithmetic.
    /// </summary>
    private static float NextRarityOffset(float offset, bool rare, int ascension) =>
        rare ? BaseRarityOffset : Math.Min(offset + RarityGrowth(ascension), MaxRarityOffset);

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
        Rng rng, ref float offset, PoolIndex index, CardEntry[] pool, ReadOnlySpan<int> taken, int ascension)
    {
        float roll = rng.NextFloat();
        float rareThreshold = RegularRareOdds(ascension) + offset;

        var rarity = roll < rareThreshold ? CardRarity.Rare
                   : roll < RegularUncommonOdds + rareThreshold ? CardRarity.Uncommon
                   : CardRarity.Common;

        offset = NextRarityOffset(offset, rarity == CardRarity.Rare, ascension);

        return NextAllowedRarity(rarity, index, pool, taken);
    }

    /// <summary>
    /// CardFactory.GetNextAllowedRarity — walk up the rarity ladder (with wrapping) until one
    /// the pool can actually satisfy is found. A full pool never needs this, but a pool drained
    /// by the blacklist could.
    /// </summary>
    private static CardRarity NextAllowedRarity(
        CardRarity rarity, PoolIndex index, CardEntry[] pool, ReadOnlySpan<int> taken)
    {
        // The "already tried" set is a bitmask rather than a List, since this runs once per draw
        // and the ladder has at most four rungs.
        int seen = 1 << (int)rarity;
        while (rarity != CardRarity.None && CountAvailable(index, pool, taken, rarity) == 0)
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
    internal static int CountAvailable(
        PoolIndex index, CardEntry[] pool, ReadOnlySpan<int> taken, CardRarity rarity)
    {
        // The pool cannot change during a reward, so the only thing that moves is the blacklist,
        // and it holds at most two entries when this is consulted. Subtracting them from a
        // precomputed total replaces a ninety-entry walk with a loop of two.
        int n = index.CountOf(rarity);
        for (int i = 0; i < taken.Length; i++)
            if (pool[taken[i]].Rarity == rarity) n--;
        return n;
    }

    /// <summary>
    /// The <paramref name="nth"/>'th still-available card of a rarity, in pool order, returned as
    /// its index into <paramref name="pool"/>. Pool order is what the game indexes into, so this
    /// walks rather than sorting or hashing; it just walks one rarity's entries instead of all.
    /// </summary>
    internal static int NthAvailable(
        PoolIndex index, CardEntry[] pool, ReadOnlySpan<int> taken, CardRarity rarity, int nth)
    {
        var group = index.Of(rarity);
        for (int i = 0; i < group.Length; i++)
        {
            int poolIndex = group[i];
            if (Blacklisted(index, taken, poolIndex)) continue;
            if (nth-- == 0) return poolIndex;
        }
        throw new InvalidOperationException($"no card of rarity {rarity} left at index {nth}.");
    }

    /// <summary>
    /// Whether a candidate is one of the cards already offered.
    ///
    /// Compares interned type ids rather than the pool positions themselves, because the game
    /// blacklists by card TYPE. Two pool entries sharing a TypeName would have to exclude each
    /// other, and comparing positions would let the second one through. See
    /// <see cref="PoolIndex.TypeIds"/>.
    /// </summary>
    private static bool Blacklisted(PoolIndex index, ReadOnlySpan<int> taken, int candidate)
    {
        int id = index.TypeIds[candidate];
        for (int i = 0; i < taken.Length; i++)
            if (index.TypeIds[taken[i]] == id) return true;
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

    /// <summary>
    /// How many CharacterCards combinations the pool cache keys on: one bit per character in
    /// <c>CharacterCardsModifier.TickboxOrder</c>.
    /// </summary>
    private const int AddedPoolCombinations = 32;

    /// <summary>
    /// Two pools per character, indexed by <c>(character * 2) + (multiplayer ? 1 : 0)</c>. The
    /// second slot exists only for the verifiers: a search is always multiplayer, but a run save
    /// written by a solo game has five fewer cards per character in its pool, and comparing
    /// against one means building the pool the way that run did.
    /// </summary>
    [ThreadStatic] private static PoolCache?[]? _pools;

    /// <summary>
    /// The character's pool as a reward sees it. Callers must treat the result as read-only:
    /// it is shared with every other caller on this thread.
    /// </summary>
    /// <param name="addedCharacters">
    /// Characters whose pools the CharacterCards modifier appends, in the game's tickbox order.
    /// Pass what <see cref="CharacterCardsModifier.AddedFor"/> returns, which has already
    /// dropped this player's own character: ticking a player's own character adds nothing,
    /// because the hook unions pool INSTANCES and theirs is already there.
    /// </param>
    public static CardEntry[] PoolFor(
        Character character, UnlockState? unlocks = null, bool isMultiplayer = true,
        IReadOnlyList<Character>? addedCharacters = null)
    {
        int added = addedCharacters is { Count: > 0 } a ? CharacterCardsModifier.Mask(a) : 0;

        var cache = _pools ??=
            new PoolCache?[Enum.GetValues<Character>().Length * 2 * AddedPoolCombinations];
        int slot = (((int)character * 2) + (isMultiplayer ? 1 : 0)) * AddedPoolCombinations + added;

        if (cache[slot] is { } hit && ReferenceEquals(hit.Unlocks, unlocks)) return hit.Pool;

        var built = BuildPool(character, unlocks, isMultiplayer);

        // CharacterCards appends whole pools rather than merging them, because the game's
        // GetPossibleCards is CardPools.SelectMany(p => p.GetUnlockedCards(...)): one pool after
        // another, each in its own order. So the player's own cards keep the indices they
        // already had and the added ones sit behind them, which is what makes this safe to do
        // on top of the single-character pool rather than from scratch.
        //
        // Each added pool is filtered by THIS player's unlock state and run mode, not by its own
        // character's: GetUnlockedCards is handed player.UnlockState either way.
        if (added != 0)
        {
            var parts = new List<CardEntry>(built);
            foreach (var extra in addedCharacters!)
                parts.AddRange(BuildPool(extra, unlocks, isMultiplayer));
            built = parts.ToArray();
        }

        cache[slot] = new PoolCache(unlocks, built);
        return built;
    }

    /// <summary>
    /// The colourless pool, gated and mode-filtered the same way a character's is.
    ///
    /// Not a character pool and not reachable from <see cref="PoolFor"/>: nothing indexes it by
    /// <see cref="Character"/>, and no card reward draws from it. The AllStar modifier is the
    /// only thing here that does. Cached per thread like the others, since the only thing it
    /// varies on is the unlock state.
    /// </summary>
    public static CardEntry[] ColorlessPool(
        UnlockState? unlocks = null, bool isMultiplayer = true)
    {
        var cache = _colorless ??= new PoolCache?[2];
        int slot = isMultiplayer ? 1 : 0;

        if (cache[slot] is { } hit && ReferenceEquals(hit.Unlocks, unlocks)) return hit.Pool;

        var built = BuildPool(CardPoolData.Colorless, "Colorless", unlocks, isMultiplayer);
        cache[slot] = new PoolCache(unlocks, built);
        return built;
    }

    [ThreadStatic] private static PoolCache?[]? _colorless;

    /// <summary>
    /// The pool a UNIFORM pick sees: everything that is not Basic and not Ancient, in pool
    /// order. What Specialized and All Star index into.
    ///
    /// Cached like the pools themselves, because this is read once per seed per player in a
    /// search and the filter is otherwise a LINQ pass and an allocation every time. The GPU
    /// uploads the same thing once as a flat index, which is the same idea in another shape.
    ///
    /// Written as the game's exclusion rather than as "the three reward rarities": they are the
    /// same set today, but the game's rule is the exclusion.
    /// </summary>
    public static CardEntry[] OfferablePool(
        Character character, UnlockState? unlocks = null, bool isMultiplayer = true)
    {
        var cache = _offerable ??= new PoolCache?[Enum.GetValues<Character>().Length * 2];
        int slot = ((int)character * 2) + (isMultiplayer ? 1 : 0);

        if (cache[slot] is { } hit && ReferenceEquals(hit.Unlocks, unlocks)) return hit.Pool;

        var built = Uniform(PoolFor(character, unlocks, isMultiplayer));
        cache[slot] = new PoolCache(unlocks, built);
        return built;
    }

    /// <summary>The same for the colourless pool, which All Star draws from.</summary>
    public static CardEntry[] OfferableColorlessPool(
        UnlockState? unlocks = null, bool isMultiplayer = true)
    {
        var cache = _offerableColorless ??= new PoolCache?[2];
        int slot = isMultiplayer ? 1 : 0;

        if (cache[slot] is { } hit && ReferenceEquals(hit.Unlocks, unlocks)) return hit.Pool;

        var built = Uniform(ColorlessPool(unlocks, isMultiplayer));
        cache[slot] = new PoolCache(unlocks, built);
        return built;
    }

    private static CardEntry[] Uniform(CardEntry[] pool) =>
        pool.Where(c => c.Rarity != CardRarity.Basic && c.Rarity != CardRarity.Ancient).ToArray();

    [ThreadStatic] private static PoolCache?[]? _offerable;
    [ThreadStatic] private static PoolCache?[]? _offerableColorless;

    /// <summary>
    /// The character's pool as a reward sees it: epoch-gated cards removed, then filtered for
    /// run mode. Multiplayer drops singleplayer-only cards, of which the game currently ships
    /// none; singleplayer drops multiplayer-only ones, of which it ships five per character.
    /// Both directions are what <c>CardFactory.FilterForPlayerCount</c> does.
    /// </summary>
    private static CardEntry[] BuildPool(
        Character character, UnlockState? unlocks, bool isMultiplayer) =>
        BuildPool(CardPoolData.For(character), character.ToString(), unlocks, isMultiplayer);

    /// <summary>
    /// The same filtering for any named pool, so the colourless one cannot drift from the
    /// character ones. <paramref name="gateKey"/> is the key into
    /// <see cref="CardPoolData.EpochGates"/>, which is the pool's own name rather than a
    /// character: the colourless pool has five epoch gates of its own.
    /// </summary>
    private static CardEntry[] BuildPool(
        CardEntry[] source, string gateKey, UnlockState? unlocks, bool isMultiplayer)
    {
        var pool = source.AsEnumerable();

        if (unlocks is not null &&
            CardPoolData.EpochGates.TryGetValue(gateKey, out var gates))
        {
            var locked = new HashSet<string>();
            foreach (var (epoch, cards) in gates)
                if (!unlocks.IsEpochRevealed(epoch))
                    foreach (var c in cards) locked.Add(c);

            if (locked.Count > 0) pool = pool.Where(c => !locked.Contains(c.TypeName));
        }

        return pool.Where(c => c.Mode != (isMultiplayer ? CardMode.SingleplayerOnly
                                                         : CardMode.MultiplayerOnly)).ToArray();
    }
}


