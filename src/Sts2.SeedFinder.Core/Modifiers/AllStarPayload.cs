using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Saves;

namespace Sts2.SeedFinder.Core.Modifiers;

/// <summary>
/// What the AllStar modifier hands a player: five colourless cards.
///
/// Specialized five times over, drawing from a different pool. <c>AllStar.ObtainCards</c> loops
/// five times and each iteration builds its own <c>CardCreationOptions</c> and makes its own
/// <c>CardFactory.CreateForReward(player, 1, options)</c> call, so:
///
///   * the blacklist is FRESH each time, because it lives inside one CreateForReward call. The
///     five cards can repeat, which is the opposite of a card reward's three;
///   * <c>ForNonCombatWithUniformOdds</c> takes the Uniform branch, skipping the rarity roll and
///     filtering the pool to everything that is not Basic and not Ancient, and sets
///     <c>NoUpgradeRoll</c>;
///   * so each card is one draw, five in total, matching
///     <see cref="RunModifiers.NeowRewardDraws"/>.
///
/// The pool is <c>ModelDb.CardPool&lt;ColorlessCardPool&gt;()</c>, not the character's, so unlike
/// every other card this tool predicts it does not depend on who is playing. It holds 65 cards,
/// 40 Uncommon and 25 Rare, with no Commons at all: a uniform pick over that is 25-in-65 to be a
/// Rare, which makes Rares easier to find here than anywhere else in the tool.
///
/// It also sets <c>NoCardPoolModifications</c>, which is what makes it immune to CharacterCards:
/// that hook returns early on the flag. So a lobby with character pools added still gets plain
/// colourless cards here.
/// </summary>
public static class AllStarPayload
{
    /// <summary>How many cards AllStar hands out, which is also its draw cost.</summary>
    public const int Cards = 5;

    /// <summary>
    /// The cards AllStar can land on, in pool order: the colourless pool, epoch-gated entries
    /// removed for this player, filtered for run mode, then everything that is not Basic or
    /// Ancient.
    ///
    /// The last filter is written as the game writes it rather than as "the reward rarities".
    /// Today the pool holds only Uncommons and Rares, so it removes nothing, but the game's rule
    /// is the exclusion and this should keep matching it if the pool ever grows a Basic.
    /// </summary>
    public static CardEntry[] Pool(UnlockState? unlocks = null, bool isMultiplayer = true) =>
        CardRewardGenerator.ColorlessPool(unlocks, isMultiplayer)
            .Where(c => c.Rarity != CardRarity.Basic && c.Rarity != CardRarity.Ancient)
            .ToArray();

    /// <summary>
    /// The five cards this player starts with, in the order they are drawn. Repeats are
    /// possible and meaningful: each draw sees the whole pool again.
    /// </summary>
    /// <param name="priorRewardDraws">
    /// How much of the Rewards stream is already spent when AllStar takes its option. AllStar
    /// sits late in the modifier list, so Specialized (1 draw) and Insanity (30) both land ahead
    /// of it when they are on. <see cref="RunModifiers.PriorRewardDraws"/> works it out.
    /// </param>
    public static CardEntry[] Predict(
        ulong runSeed,
        int playerSlotIndex,
        UnlockState? unlocks = null,
        bool isMultiplayer = true,
        int priorRewardDraws = 0)
    {
        var pool = Pool(unlocks, isMultiplayer);
        if (pool.Length == 0) return [];

        var rng = CardRewardGenerator.RewardsRng(runSeed, playerSlotIndex);
        for (int i = 0; i < priorRewardDraws; i++) rng.NextFloat();

        var cards = new CardEntry[Cards];
        for (int i = 0; i < Cards; i++) cards[i] = pool[rng.NextInt(0, pool.Length)];
        return cards;
    }

    /// <summary>
    /// Whether this player's five cards cover every card asked for, counting repeats: asking for
    /// the same card twice asks for it to be drawn twice.
    /// </summary>
    public static bool Covers(IReadOnlyList<CardEntry> drawn, IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0) return true;
        if (wanted.Count > drawn.Count) return false;

        Span<bool> used = stackalloc bool[drawn.Count];
        foreach (var want in wanted)
        {
            int at = -1;
            for (int i = 0; i < drawn.Count; i++)
                if (!used[i] && drawn[i].TypeName == want) { at = i; break; }

            if (at < 0) return false;
            used[at] = true;
        }
        return true;
    }
}
