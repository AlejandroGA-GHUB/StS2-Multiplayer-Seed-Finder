using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Cards;

/// <summary>
/// The cards a Neow relic hands a player the moment they take it.
///
/// Two of Neow's options are modelled here, and they are the two that are the ordinary reward
/// factory with two of its three draws deleted:
///
/// <code>
///   ArcaneScroll.AfterObtained / HeftyTablet.AfterObtained:
///     CardFactory.CreateForReward(owner, N, options)
///       pools   = the owner's own character pool, and nothing else
///       filter  = c.Rarity == Rare
///       odds    = CardRarityOddsType.Uniform
///       flags   = NoUpgradeRoll
///     N = 1 for Arcane Scroll, 3 for Hefty Tablet. Nothing else differs between them.
/// </code>
///
/// Three properties of that call are what make a payload predictable, and each removes a draw
/// the fight reward pays for (established against v0.110.1, re-read against v0.111.0):
///
/// <list type="bullet">
/// <item><b>Uniform odds mean no rarity roll at all.</b> <c>CreateForReward</c> branches on
/// <c>RarityOddsType == Uniform</c> and takes the candidates directly, never reaching
/// <c>RollForRarity</c>. So no <c>NextFloat</c> is spent, and — this is the part that surprises
/// people — <c>CardRarityOdds.CurrentValue</c> never moves. A payload cannot make the first
/// fight able to offer a Rare; see <see cref="CardRewardGenerator.CanOfferRare"/>.</item>
/// <item><b>NoUpgradeRoll really removes the draw</b> rather than rolling and discarding it.</item>
/// <item><b>The picks blacklist each other.</b> <c>CreateForReward</c>'s multi-card entry point
/// accumulates what it has produced and excludes it from every later pick, so Hefty Tablet's
/// second pick sees the rare pool one entry shorter and its third two shorter. Same blacklist
/// as a fight reward, so this reuses the same helpers.</item>
/// </list>
///
/// What is left is one <c>NextItem</c> per card, off <c>PlayerRng.Rewards</c>, before anything
/// else in the run has touched that stream. That is why a payload criterion is the cheapest
/// thing this tool can test: one draw settles an Arcane Scroll.
///
/// It is also exactly the cost <see cref="CardRewardGenerator.NeowRewardDrawCost"/> records, so
/// generating a payload and burning its cost leave the stream in the same place. The three
/// relics whose payloads are NOT modelled (Massive Scroll, Scroll Boxes, Neow's Bones) still
/// have to be paid for, which is what that table is for.
/// </summary>
public static class NeowCardPayload
{
    /// <summary>
    /// How many cards this Neow relic hands out through the modelled path, or 0 for a relic
    /// whose payload we cannot predict. Not the same question as
    /// <see cref="CardRewardGenerator.NeowRewardDrawCost"/>, which every relic answers.
    /// </summary>
    public static int CardCount(string relicSlug) => relicSlug switch
    {
        "arcane_scroll" => 1,
        "hefty_tablet" => 3,
        _ => 0,
    };

    /// <summary>Whether this relic's payload can be predicted at all.</summary>
    public static bool IsPredictable(string relicSlug) => CardCount(relicSlug) > 0;

    /// <summary>Neow relics whose payload this tool can name, in offer order.</summary>
    public static readonly string[] Predictable = ["arcane_scroll", "hefty_tablet"];

    /// <summary>
    /// The cards <paramref name="relicSlug"/> would hand this player, in the order the factory
    /// produced them. Empty for a relic with no modelled payload.
    ///
    /// The rarity is always Rare and nothing is ever upgraded, so the
    /// <see cref="RewardCard"/> flags carry no information here; the shape is shared with the
    /// fight reward so the UI can render both the same way.
    /// </summary>
    public static IReadOnlyList<RewardCard> Generate(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        string relicSlug,
        UnlockState? unlocks = null)
    {
        int count = CardCount(relicSlug);
        if (count == 0) return Array.Empty<RewardCard>();

        var rng = CardRewardGenerator.RewardsRng(runSeed, playerSlotIndex);
        var pool = CardRewardGenerator.PoolFor(character, unlocks);
        var index = PoolIndex.For(pool);

        var cards = new List<RewardCard>(count);
        Span<int> taken = stackalloc int[3];
        int takenCount = 0;

        for (int i = 0; i < count; i++)
        {
            int avail = CardRewardGenerator.CountAvailable(index, pool, taken[..takenCount], CardRarity.Rare);

            // A character pool with fewer rares than the relic hands out cannot happen in any
            // shipped build (the smallest is 27), but a heavily epoch-gated pool is the kind of
            // thing a patch could produce, and drawing NextInt(0, 0) would be wrong rather than
            // merely unlucky.
            if (avail <= 0) break;

            int picked = CardRewardGenerator.NthAvailable(
                index, pool, taken[..takenCount], CardRarity.Rare, rng.NextInt(0, avail));

            cards.Add(new RewardCard(pool[picked].TypeName, pool[picked].Rarity, Upgraded: false));
            taken[takenCount++] = picked;
        }

        return cards;
    }

    /// <summary>
    /// Whether this player's payload holds every wanted card, without allocating.
    ///
    /// Wanted cards are pool TYPE IDS rather than names, resolved once by the caller against
    /// this slot's own <see cref="PoolIndex"/>. Type ids are what the game's blacklist compares,
    /// and comparing ints is what makes this usable in a scan.
    /// </summary>
    internal static bool Offers(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        string relicSlug,
        UnlockState? unlocks,
        ReadOnlySpan<int> wantedTypeIds)
    {
        int count = CardCount(relicSlug);
        if (wantedTypeIds.Length > count) return false;

        var rng = CardRewardGenerator.RewardsRng(runSeed, playerSlotIndex);
        var pool = CardRewardGenerator.PoolFor(character, unlocks);
        var index = PoolIndex.For(pool);

        Span<int> taken = stackalloc int[3];
        int takenCount = 0;

        // One bit per wanted card, so a payload that draws the same card twice cannot satisfy
        // two different criteria. It cannot happen — the blacklist forbids it — but the mask
        // costs nothing and states the intent.
        int found = 0;
        int all = (1 << wantedTypeIds.Length) - 1;

        for (int i = 0; i < count; i++)
        {
            int avail = CardRewardGenerator.CountAvailable(index, pool, taken[..takenCount], CardRarity.Rare);
            if (avail <= 0) break;

            int picked = CardRewardGenerator.NthAvailable(
                index, pool, taken[..takenCount], CardRarity.Rare, rng.NextInt(0, avail));

            int id = index.TypeIds[picked];
            for (int w = 0; w < wantedTypeIds.Length; w++)
                if (wantedTypeIds[w] == id) found |= 1 << w;

            taken[takenCount++] = picked;
        }

        return found == all;
    }

    /// <summary>
    /// The pool type id of a card in this character's pool, or -1 when the pool has no such
    /// card. Ids are only comparable within one pool, which is why this takes the character.
    /// </summary>
    internal static int TypeIdOf(Character character, UnlockState? unlocks, string typeName)
    {
        var pool = CardRewardGenerator.PoolFor(character, unlocks);
        for (int i = 0; i < pool.Length; i++)
            if (string.Equals(pool[i].TypeName, typeName, StringComparison.Ordinal))
                return PoolIndex.For(pool).TypeIds[i];
        return -1;
    }

    /// <summary>Every rare this character could be handed by a payload, in pool order.</summary>
    public static IEnumerable<CardEntry> Offerable(Character character, UnlockState? unlocks = null) =>
        CardRewardGenerator.PoolFor(character, unlocks).Where(c => c.Rarity == CardRarity.Rare);
}
