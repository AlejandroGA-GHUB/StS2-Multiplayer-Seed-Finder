using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Cards;

/// <summary>Which draw chain a Neow relic's payload follows. They are not variations of one.</summary>
public enum PayloadShape
{
    /// <summary>No modelled payload.</summary>
    None,

    /// <summary>Arcane Scroll and Hefty Tablet: N uniform picks over the character's rares.</summary>
    Rares,

    /// <summary>Massive Scroll: three rolled cards over a multiplayer-only pool, keep one.</summary>
    MassiveScroll,

    /// <summary>Scroll Boxes: two bundles of 2 commons and 1 uncommon, keep a whole bundle.</summary>
    ScrollBoxes,
}

/// <summary>
/// The cards a Neow relic hands a player the moment they take it.
///
/// Four of Neow's options are modelled here, in three different shapes. They are genuinely
/// different chains rather than one with parameters, which is why this reads as three walks and
/// not one loop: what is drawn, from which pool, and how many draws it costs all differ.
///
/// <code>
///   ArcaneScroll / HeftyTablet .AfterObtained       -> PayloadShape.Rares
///     CreateForReward(owner, N, options)              N = 1 and 3
///       pools  = the owner's own character pool
///       filter = Rarity == Rare
///       odds   = Uniform          flags = NoUpgradeRoll
///     1 draw a card: no rarity roll, no upgrade roll.
///
///   MassiveScroll.AfterObtained                     -> PayloadShape.MassiveScroll
///     CreateForReward(owner, 3, options)
///       pools  = the owner's character pool AND the Colourless pool, in that order
///       filter = MultiplayerConstraint == MultiplayerOnly
///       odds   = RegularEncounter    flags = none
///     3 draws a card: a rarity roll, the pick, and an upgrade roll.
///
///   ScrollBoxes.GenerateRandomBundles                -> PayloadShape.ScrollBoxes
///     Not the factory at all. Two bundles, each 2 commons then 1 uncommon, drawn with
///     NextItem over the character pool, against ONE used-card set shared by both bundles.
///     1 draw a card, plus a leading NextInt(100) per bundle for the Defect only.
/// </code>
///
/// Three properties are what make the <see cref="PayloadShape.Rares"/> pair the cheapest thing
/// this tool tests, and each removes a draw a fight reward pays for (established against
/// v0.110.1, re-read against v0.111.0):
///
/// <list type="bullet">
/// <item><b>Uniform odds mean no rarity roll at all.</b> <c>CreateForReward</c> branches on
/// <c>RarityOddsType == Uniform</c> and takes the candidates directly, never reaching
/// <c>RollForRarity</c>. So no <c>NextFloat</c> is spent, and — this is the part that surprises
/// people — <c>CardRarityOdds.CurrentValue</c> never moves. No payload can make the first fight
/// able to offer a Rare; see <see cref="CardRewardGenerator.CanOfferRare"/>. Massive Scroll's
/// roll DOES happen but goes through <c>RollWithBaseOdds</c>, which reads the base odds and
/// writes nothing back, so the conclusion holds for it too.</item>
/// <item><b>NoUpgradeRoll really removes the draw</b> rather than rolling and discarding it.
/// Massive Scroll does not set it, which is why it costs nine draws and not six.</item>
/// <item><b>The picks blacklist each other.</b> <c>CreateForReward</c>'s multi-card entry point
/// accumulates what it has produced and excludes it from every later pick.</item>
/// </list>
///
/// Every shape draws off <c>PlayerRng.Rewards</c>, before anything else in the run has touched
/// that stream, and costs exactly what <see cref="CardRewardGenerator.NeowRewardDrawCost"/>
/// records — so generating a payload and burning its cost leave the stream in the same place.
/// Neow's Bones is still unmodelled: it shuffles a lobby-dependent relic list, so even its cost
/// is unsettled.
/// </summary>
public static class NeowCardPayload
{
    /// <summary>Cards in one Scroll Boxes bundle, and the unit the player actually keeps.</summary>
    public const int BundleSize = 3;

    /// <summary>Bundles Scroll Boxes offers.</summary>
    public const int Bundles = 2;

    /// <summary>
    /// The Defect's chance, per bundle, that Scroll Boxes offers three Claws instead of a drawn
    /// bundle. <c>ScrollBoxes._clawBundleChancePercent</c>, rolled as <c>NextInt(100) &lt; 1</c>.
    /// </summary>
    private const int ClawBundlePercent = 1;

    /// <summary>Which chain this relic's payload follows.</summary>
    public static PayloadShape ShapeOf(string relicSlug) => relicSlug switch
    {
        "arcane_scroll" or "hefty_tablet" => PayloadShape.Rares,
        "massive_scroll" => PayloadShape.MassiveScroll,
        "scroll_boxes" => PayloadShape.ScrollBoxes,
        _ => PayloadShape.None,
    };

    /// <summary>
    /// How many cards this Neow relic puts in front of the player, or 0 for a relic whose
    /// payload we cannot predict. Not the same question as
    /// <see cref="CardRewardGenerator.NeowRewardDrawCost"/>, which every relic answers.
    ///
    /// Scroll Boxes counts all six, across both bundles: they are all offered, even though only
    /// one bundle is kept. Which of those a player can actually come away with is
    /// <see cref="Offers"/>'s problem, not this one's.
    /// </summary>
    public static int CardCount(string relicSlug) => relicSlug switch
    {
        "arcane_scroll" => 1,
        "hefty_tablet" => 3,
        "massive_scroll" => 3,
        "scroll_boxes" => Bundles * BundleSize,
        _ => 0,
    };

    /// <summary>Whether this relic's payload can be predicted at all.</summary>
    public static bool IsPredictable(string relicSlug) => CardCount(relicSlug) > 0;

    /// <summary>Neow relics whose payload this tool can name, in offer order.</summary>
    public static readonly string[] Predictable =
        ["arcane_scroll", "hefty_tablet", "massive_scroll", "scroll_boxes"];

    /// <summary>
    /// The pool a given relic's payload draws from. Three different pools, which is the other
    /// half of why these are separate chains rather than one with a count.
    /// </summary>
    public static CardEntry[] PoolOf(
        string relicSlug, Character character, UnlockState? unlocks = null, bool isMultiplayer = true) =>
        ShapeOf(relicSlug) == PayloadShape.MassiveScroll
            ? MassiveScrollPool(character, unlocks, isMultiplayer)
            : CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer);

    /// <summary>
    /// Draws the payload, writing POOL POSITIONS into <paramref name="picked"/> and returning
    /// how many it produced.
    ///
    /// Positions rather than cards, so the search path can compare interned ints and allocate
    /// nothing. <see cref="Generate"/> turns them into cards for display; <see cref="Offers"/>
    /// compares them directly.
    /// </summary>
    private static int Walk(
        Rng rng, Character character, string relicSlug, UnlockState? unlocks,
        bool isMultiplayer, int ascension, Span<int> picked)
    {
        var shape = ShapeOf(relicSlug);
        if (shape == PayloadShape.None) return 0;

        var pool = PoolOf(relicSlug, character, unlocks, isMultiplayer);
        var index = PoolIndex.For(pool);
        int count = 0;

        if (shape == PayloadShape.Rares)
        {
            int wanted = CardCount(relicSlug);
            for (int i = 0; i < wanted; i++)
            {
                int avail = CardRewardGenerator.CountAvailable(index, pool, picked[..count], CardRarity.Rare);

                // A character pool with fewer rares than the relic hands out cannot happen in
                // any shipped build (the smallest is 27), but a heavily epoch-gated pool is the
                // kind of thing a patch could produce, and NextInt(0, 0) would be wrong rather
                // than merely unlucky.
                if (avail <= 0) break;

                picked[count] = CardRewardGenerator.NthAvailable(
                    index, pool, picked[..count], CardRarity.Rare, rng.NextInt(0, avail));
                count++;
            }
            return count;
        }

        if (shape == PayloadShape.MassiveScroll)
        {
            for (int i = 0; i < BundleSize; i++)
            {
                var rarity = CardRewardGenerator.RollBaseRarityFor(
                    rng, index, pool, picked[..count], ascension);

                int avail = CardRewardGenerator.CountAvailable(index, pool, picked[..count], rarity);
                if (avail <= 0) break;

                int at = CardRewardGenerator.NthAvailable(
                    index, pool, picked[..count], rarity, rng.NextInt(0, avail));

                // Taken whatever it says, because NoUpgradeRoll is not set. At Neow the act
                // index is 0, so the odds are 0 and nothing is upgraded; the draw still has to
                // happen or everything after it reads one place along.
                rng.NextFloat();

                picked[count] = at;
                count++;
            }
            return count;
        }

        // Scroll Boxes.
        //
        // The used-card set is NOT the same thing as the output here, which is the one place in
        // this file where they come apart. A Claw bundle is handed over without going through
        // the pool at all: GenerateRandomBundles adds nothing to usedCardIds on that branch, so
        // the next bundle draws as though it had not happened. Treating the three Claws as
        // drawn made the following bundle's second common wrong, and the Oracle caught it.
        Span<int> used = stackalloc int[Bundles * BundleSize];
        int usedCount = 0;

        for (int bundle = 0; bundle < Bundles; bundle++)
        {
            if (character == Character.Defect && rng.NextInt(0, 100) < ClawBundlePercent)
            {
                int claw = Array.FindIndex(pool, e => e.TypeName == "Claw");
                if (claw < 0) return count;
                for (int i = 0; i < BundleSize; i++) { picked[count] = claw; count++; }
                continue;
            }

            for (int i = 0; i < BundleSize; i++)
            {
                var rarity = i < BundleSize - 1 ? CardRarity.Common : CardRarity.Uncommon;
                int avail = CardRewardGenerator.CountAvailable(index, pool, used[..usedCount], rarity);

                // IsAllowedAtNeow refuses the relic below four commons and two uncommons, so a
                // pool this short should never reach here. Stopping beats NextInt(0, 0).
                if (avail <= 0) return count;

                int at = CardRewardGenerator.NthAvailable(
                    index, pool, used[..usedCount], rarity, rng.NextInt(0, avail));

                picked[count] = at;
                count++;
                used[usedCount] = at;
                usedCount++;
            }
        }
        return count;
    }

    /// <summary>
    /// The cards <paramref name="relicSlug"/> would put in front of this player, in the order
    /// they were produced. Empty for a relic with no modelled payload.
    ///
    /// For Scroll Boxes that is bundle order: the first three are bundle one, the next three
    /// bundle two.
    /// </summary>
    public static IReadOnlyList<RewardCard> Generate(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        string relicSlug,
        UnlockState? unlocks = null,
        bool isMultiplayer = true,
        int ascension = 0)
    {
        Span<int> picked = stackalloc int[Bundles * BundleSize];
        var rng = CardRewardGenerator.RewardsRng(runSeed, playerSlotIndex);
        int count = Walk(rng, character, relicSlug, unlocks, isMultiplayer, ascension, picked);
        if (count == 0) return Array.Empty<RewardCard>();

        var pool = PoolOf(relicSlug, character, unlocks, isMultiplayer);
        var cards = new RewardCard[count];
        for (int i = 0; i < count; i++)
            cards[i] = new RewardCard(pool[picked[i]].TypeName, pool[picked[i]].Rarity, Upgraded: false);
        return cards;
    }

    /// <summary>
    /// Whether this player could come away with every wanted card, without allocating.
    ///
    /// Wanted cards are pool TYPE IDS, resolved once by the caller through
    /// <see cref="TypeIdOf"/> against THIS RELIC's pool. Ids are interned per pool, so an id
    /// from the character pool means nothing against Massive Scroll's.
    ///
    /// Reachability, not possession, and what "reachable" means differs by shape:
    ///
    /// <list type="bullet">
    /// <item><b>Rares and Massive Scroll</b> put every card in front of you at once and you keep
    /// one, so a wanted card only has to be among them. Naming two says "both were offered"
    /// rather than "I got both", and naming fewer is the looser search.</item>
    /// <item><b>Scroll Boxes</b> hands over a whole BUNDLE, so two wanted cards have to be in
    /// the SAME bundle. Asking for one from each is asking for something no player can take,
    /// and this returns false for it rather than matching on the pair merely being present.</item>
    /// </list>
    /// </summary>
    internal static bool Offers(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        string relicSlug,
        UnlockState? unlocks,
        ReadOnlySpan<int> wantedTypeIds,
        bool isMultiplayer = true,
        int ascension = 0)
    {
        if (wantedTypeIds.Length == 0) return true;
        if (wantedTypeIds.Length > CardCount(relicSlug)) return false;

        Span<int> picked = stackalloc int[Bundles * BundleSize];
        var rng = CardRewardGenerator.RewardsRng(runSeed, playerSlotIndex);
        int count = Walk(rng, character, relicSlug, unlocks, isMultiplayer, ascension, picked);
        if (count == 0) return false;

        var pool = PoolOf(relicSlug, character, unlocks, isMultiplayer);
        var index = PoolIndex.For(pool);
        int all = (1 << wantedTypeIds.Length) - 1;

        if (ShapeOf(relicSlug) != PayloadShape.ScrollBoxes)
        {
            int found = 0;
            for (int i = 0; i < count; i++)
            {
                int id = index.TypeIds[picked[i]];
                for (int w = 0; w < wantedTypeIds.Length; w++)
                    if (wantedTypeIds[w] == id) found |= 1 << w;
            }
            return found == all;
        }

        for (int start = 0; start + BundleSize <= count; start += BundleSize)
        {
            int found = 0;
            for (int i = start; i < start + BundleSize; i++)
            {
                int id = index.TypeIds[picked[i]];
                for (int w = 0; w < wantedTypeIds.Length; w++)
                    if (wantedTypeIds[w] == id) found |= 1 << w;
            }
            if (found == all) return true;
        }
        return false;
    }

    /// <summary>
    /// The type id of a card within THIS RELIC's pool, or -1 when that pool has no such card.
    /// Ids are interned per pool, which is why this takes the relic as well as the character.
    /// </summary>
    internal static int TypeIdOf(
        Character character, string relicSlug, UnlockState? unlocks, string typeName,
        bool isMultiplayer = true)
    {
        var pool = PoolOf(relicSlug, character, unlocks, isMultiplayer);
        for (int i = 0; i < pool.Length; i++)
            if (string.Equals(pool[i].TypeName, typeName, StringComparison.Ordinal))
                return PoolIndex.For(pool).TypeIds[i];
        return -1;
    }

    /// <summary>
    /// Every card this relic could put in front of this player, in pool order. What the picker
    /// offers and what the validator refuses against.
    /// </summary>
    public static IEnumerable<CardEntry> Offerable(
        Character character, string relicSlug,
        UnlockState? unlocks = null, bool isMultiplayer = true) =>
        ShapeOf(relicSlug) switch
        {
            PayloadShape.Rares => CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer)
                .Where(c => c.Rarity == CardRarity.Rare),

            PayloadShape.MassiveScroll => MassiveScrollPool(character, unlocks, isMultiplayer),

            PayloadShape.ScrollBoxes => CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer)
                .Where(c => c.Rarity is CardRarity.Common or CardRarity.Uncommon),

            _ => Array.Empty<CardEntry>(),
        };

    /// <summary>
    /// Massive Scroll's pool: the character's own entries then the Colourless ones, both
    /// filtered to multiplayer-only cards.
    ///
    /// Order matters and is the game's: <c>GetPossibleCards</c> is
    /// <c>CardPools.SelectMany(...)</c> over <c>[ownPool, ColorlessPool]</c>, so the character's
    /// come first. A pick is an index into one rarity's entries in that order, so swapping them
    /// would return different cards.
    /// </summary>
    public static CardEntry[] MassiveScrollPool(
        Character character, UnlockState? unlocks = null, bool isMultiplayer = true)
    {
        var cache = _massive ??= new (UnlockState? Unlocks, CardEntry[] Pool)?[
            Enum.GetValues<Character>().Length * 2];
        int slot = ((int)character * 2) + (isMultiplayer ? 1 : 0);

        if (cache[slot] is { } hit && ReferenceEquals(hit.Unlocks, unlocks)) return hit.Pool;

        var built = CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer)
            .Concat(CardRewardGenerator.ColorlessPool(unlocks, isMultiplayer))
            .Where(c => c.Mode == CardMode.MultiplayerOnly)
            .ToArray();

        cache[slot] = (unlocks, built);
        return built;
    }

    [ThreadStatic]
    private static (UnlockState? Unlocks, CardEntry[] Pool)?[]? _massive;
}
