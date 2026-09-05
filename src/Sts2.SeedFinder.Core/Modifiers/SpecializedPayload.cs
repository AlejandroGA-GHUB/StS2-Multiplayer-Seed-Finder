using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Saves;

namespace Sts2.SeedFinder.Core.Modifiers;

/// <summary>
/// What the Specialized modifier hands a player: one card, five copies of it.
///
/// This is the smallest predictable thing in the whole tool. The game's
/// <c>Specialized.ObtainCards</c> draws a single card and then clones it five times, and the
/// draw itself collapses to one <c>NextInt</c> because three separate things fall away:
///
///   * <c>RarityOddsType.Uniform</c> takes a branch in <c>CardFactory.CreateForReward</c> that
///     skips the rarity roll entirely and simply filters the pool to everything that is not
///     Basic and not Ancient.
///   * <c>ForNonCombatWithUniformOdds</c> sets <c>NoUpgradeRoll</c>, and <c>WithFlags</c> ORs
///     rather than replaces, so the upgrade roll is skipped as well.
///   * The <c>rng</c> parameter <c>ObtainCards</c> receives is never used. The draw comes off
///     <c>options.RngOverride ?? player.PlayerRng.Rewards</c>, and nothing sets an override.
///
/// So it is <c>pool[rng.NextInt(0, pool.Length)]</c> on the player's own Rewards stream, which is
/// the same stream the fight card rewards come from and the same one a card-drawing Neow option
/// would move. Verified against a real custom run: seed QHU3PZ4H3CF7, Ironclad, A0, index 47 of
/// 80 gives Not Yet, and the save holds five copies of Not Yet.
///
/// Two things it is NOT affected by, both worth knowing because they look like they should be.
/// <c>CreateForReward</c> runs <c>Hook.ModifyCardRewardCreationOptions</c> before drawing, and
/// both modifiers that implement it decline this call: BigGameHunter wants
/// <c>Source == Encounter</c> and this is <c>Other</c>, and CharacterCards wants the
/// <c>IsCardReward</c> flag, which this does not set. So the pool is the character's own no
/// matter what else is ticked on.
/// </summary>
public static class SpecializedPayload
{
    /// <summary>
    /// The cards Specialized can land on, in pool order: the character's pool, epoch-gated
    /// entries removed, filtered for player count, then everything that is not Basic or Ancient.
    ///
    /// That last filter is written as the game writes it rather than as "the three reward
    /// rarities". They are the same set today, since a character pool holds nothing else, but
    /// the game's rule is the exclusion and this should keep matching it if a pool ever grows.
    /// </summary>
    public static CardEntry[] Pool(
        Character character, UnlockState? unlocks = null, bool isMultiplayer = true) =>
        CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer)
            .Where(c => c.Rarity != CardRarity.Basic && c.Rarity != CardRarity.Ancient)
            .ToArray();

    /// <summary>
    /// The card this player starts with five of, or null when the pool is empty.
    ///
    /// <paramref name="priorRewardDraws"/> is how much of the Rewards stream is already spent
    /// when this option is taken, which is zero unless another Neow-option modifier sits ahead of
    /// Specialized in the run's modifier list. See
    /// <see cref="RunModifiers.PriorRewardDraws"/>, which is what works that out.
    /// </summary>
    public static CardEntry? Predict(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        UnlockState? unlocks = null,
        bool isMultiplayer = true,
        int priorRewardDraws = 0)
    {
        var pool = Pool(character, unlocks, isMultiplayer);
        if (pool.Length == 0) return null;

        var rng = CardRewardGenerator.RewardsRng(runSeed, playerSlotIndex);

        // Each earlier option is its own uniform pick, one draw apiece, so this is exact rather
        // than an approximation of what those modifiers would have drawn. Any primitive burns
        // the same single word from the generator, so NextFloat here matches the NextInt those
        // picks actually make; it is what CardRewardGenerator.Burn uses for the same reason.
        for (int i = 0; i < priorRewardDraws; i++) rng.NextFloat();

        return pool[rng.NextInt(0, pool.Length)];
    }
}
