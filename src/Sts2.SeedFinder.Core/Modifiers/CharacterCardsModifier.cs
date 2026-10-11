using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Modifiers;

/// <summary>
/// The CharacterCards modifier, which is five tickboxes rather than one.
///
/// It is the only modifier the game parameterises. <c>CharacterCards</c> carries a
/// <c>[SavedProperty] ModelId CharacterModel</c>, and the custom run screen expands it into one
/// tickbox per character: <c>NCustomRunModifiersList.GetAllModifiers</c> walks
/// <c>GoodModifiers.Concat(BadModifiers)</c> and, on reaching CharacterCards, yields a separate
/// mutable copy for every entry of <c>ModelDb.AllCharacters</c>. That is why a clean install
/// shows 20 tickboxes for 16 modifier classes, and why a lobby can have several of these on at
/// once: <c>MutuallyExclusiveModifiers</c> does not mention them, and <c>IsEquivalent</c>
/// compares the character, so they are five independent switches.
///
/// What one does: <c>ModifyCardRewardCreationOptions</c> unions that character's pool into the
/// options of any reward flagged <c>IsCardReward</c>. Every <c>CardReward</c> sets that flag in
/// its own constructor, so this reaches the hallway fight rewards this tool predicts, and
/// Draft's ten rewards too.
///
/// Three traps, each of which would produce a pool that looks right and indexes wrong.
/// </summary>
public static class CharacterCardsModifier
{
    /// <summary>
    /// <c>ModelDb.AllCharacters</c> order, which is NOT <see cref="Character"/>'s order.
    ///
    /// TRAP 1. The game lists Ironclad, Silent, Regent, Necrobinder, Defect; our enum is
    /// declared Ironclad, Silent, Defect, Regent, Necrobinder. The tickbox list is built in the
    /// game's order and <c>GetModifiersTickedOn</c> returns ticked boxes in that same order,
    /// which becomes <c>RunState.Modifiers</c> and therefore the order the hooks run in. With
    /// two or more of these on, the pools concatenate in hook order, so sorting by our enum
    /// instead would put the added cards in the wrong places and every pick after the first
    /// added pool would be a different card.
    /// </summary>
    public static readonly Character[] TickboxOrder =
    [
        Character.Ironclad,
        Character.Silent,
        Character.Regent,
        Character.Necrobinder,
        Character.Defect,
    ];

    /// <summary>
    /// The ticked characters as the run will hold them: de-duplicated and in
    /// <see cref="TickboxOrder"/>. What a caller passes in any other order is still read the way
    /// the game would read it.
    /// </summary>
    public static Character[] Normalize(IEnumerable<Character>? ticked)
    {
        if (ticked is null) return [];
        var seen = new HashSet<Character>(ticked);
        return seen.Count == 0 ? [] : TickboxOrder.Where(seen.Contains).ToArray();
    }

    /// <summary>
    /// The characters whose pools are actually ADDED to this player's rewards, in hook order.
    ///
    /// TRAP 2. A player playing the character whose box is ticked gets nothing from it. The hook
    /// is <c>options.CardPools.Union(new[] { CharacterCardPool })</c>, and <c>Union</c> drops an
    /// entry already present: the options start with the player's own
    /// <c>player.Character.CardPool</c>, which is the very same <c>CardPoolModel</c> instance
    /// that CharacterCards would add. So "Ironclad cards" doubles a Silent player's pool and
    /// does nothing at all for an Ironclad one, in the same lobby, off the same tickbox.
    /// </summary>
    public static Character[] AddedFor(Character own, IEnumerable<Character>? ticked)
    {
        var normalized = Normalize(ticked);
        return normalized.Length == 0 ? [] : normalized.Where(c => c != own).ToArray();
    }

    /// <summary>
    /// A stable bitmask of a normalized set, for cache keys. Indexed by
    /// <see cref="TickboxOrder"/> position so the mask means the same thing as the order.
    /// </summary>
    public static int Mask(IReadOnlyList<Character> normalized)
    {
        int mask = 0;
        for (int i = 0; i < normalized.Count; i++)
        {
            int bit = Array.IndexOf(TickboxOrder, normalized[i]);
            if (bit >= 0) mask |= 1 << bit;
        }
        return mask;
    }

    /// <summary>
    /// How many tickboxes the game shows for a given character count, which is what a user
    /// counts on screen. Five for a clean install, and more with modded characters, since
    /// <c>AllCharacters</c> is what the list expands over.
    ///
    /// TRAP 3, and the reason this is a comment rather than a feature: a modded character's
    /// tickbox unions a pool that does not exist in <c>sts2.dll</c>, so nothing here can predict
    /// a reward under it. Only the five vanilla characters are offered, and a lobby using a
    /// modded one is not predictable at all.
    /// </summary>
    public static int TickboxCount(int characterCount) =>
        RunModifiers.All.Length - 1 + characterCount;
}
