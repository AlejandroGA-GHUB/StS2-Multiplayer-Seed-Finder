using System.Text;
using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Cards;

/// <summary>
/// Naming and lookup over <see cref="CardPoolData"/>. The game's class names are the source of
/// truth, but nothing a user reads should ever show one.
///
/// Two character-suffixed families need care: <c>StrikeIronclad</c> and <c>DefendSilent</c>
/// are per-character basics, and they are the only place a display name would otherwise read
/// "Strike Ironclad". They never appear in a reward (Basic rarity is unreachable from a rarity
/// roll), but they are in the pool and so in the catalog.
/// </summary>
public static class CardCatalog
{
    public static readonly Character[] Characters =
        [Character.Ironclad, Character.Silent, Character.Defect, Character.Regent, Character.Necrobinder];

    /// <summary>Rarities a combat card reward can actually offer.</summary>
    public static readonly CardRarity[] RewardRarities =
        [CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare];

    /// <summary>
    /// Rarities the FIRST fight can offer, which is a shorter list. The rare threshold is the
    /// base odds plus a pity offset that starts at -0.05 and grows by 0.01 a draw, so it is
    /// still at or below zero on all three of that fight's draws and a roll can never fall
    /// under it. Rares only become reachable once later rewards have pushed the offset up.
    /// </summary>
    public static readonly CardRarity[] FirstFightRarities = [CardRarity.Common, CardRarity.Uncommon];

    /// <summary>Every card the first fight could offer this character, in pool order.</summary>
    public static IEnumerable<CardEntry> FirstFightOfferable(
        Character character, UnlockState? unlocks = null, bool isMultiplayer = true) =>
        CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer)
            .Where(c => FirstFightRarities.Contains(c.Rarity));

    /// <summary>"StrikeIronclad" -> "strike_ironclad". The game's own id form.</summary>
    public static string Slug(string typeName) => GameHash.SnakeCase(typeName);

    /// <summary>"BattleTrance" -> "Battle Trance".</summary>
    public static string Display(string typeName)
    {
        var sb = new StringBuilder(typeName.Length + 8);
        for (int i = 0; i < typeName.Length; i++)
        {
            if (i > 0 && char.IsUpper(typeName[i]) && (char.IsLower(typeName[i - 1]) || char.IsDigit(typeName[i - 1])))
                sb.Append(' ');
            sb.Append(typeName[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every card a reward could offer this character, in pool order. Basics and Ancients are
    /// dropped: a rarity roll only ever yields Common, Uncommon or Rare, so nothing else is
    /// reachable even though it sits in the pool.
    /// </summary>
    /// <param name="addedCharacters">
    /// Pools the CharacterCards modifier appends for this player, from
    /// <c>CharacterCardsModifier.AddedFor</c>. A card reward sets the IsCardReward flag the hook
    /// looks for, so these cards really are offerable and a picker that left them out would
    /// refuse a criterion the search can satisfy.
    /// </param>
    public static IEnumerable<CardEntry> Offerable(
        Character character, UnlockState? unlocks = null, bool isMultiplayer = true,
        IReadOnlyList<Character>? addedCharacters = null) =>
        CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer, addedCharacters)
            .Where(c => RewardRarities.Contains(c.Rarity));

    /// <summary>Resolve a user-typed name or slug against one character's offerable cards.</summary>
    public static string? Find(
        Character character, string nameOrSlug, UnlockState? unlocks = null, bool isMultiplayer = true,
        IReadOnlyList<Character>? addedCharacters = null)
    {
        var wanted = Normalize(nameOrSlug);
        return Offerable(character, unlocks, isMultiplayer, addedCharacters)
            .Select(c => c.TypeName)
            .FirstOrDefault(t => Normalize(Slug(t)) == wanted || Normalize(t) == wanted);
    }

    /// <summary>
    /// Every colourless card AllStar can hand out, in pool order. No character, because the
    /// colourless pool is the same whoever is playing.
    /// </summary>
    public static IEnumerable<CardEntry> Colorless(
        UnlockState? unlocks = null, bool isMultiplayer = true) =>
        CardRewardGenerator.ColorlessPool(unlocks, isMultiplayer)
            .Where(c => RewardRarities.Contains(c.Rarity));

    /// <summary>
    /// Resolve a user-typed name or slug against an ARBITRARY pool.
    ///
    /// The character-keyed overloads above cannot answer for a pool that is not one character's:
    /// Massive Scroll draws from the character pool and the colourless pool together, so a
    /// colourless card it genuinely hands over is not findable by character. Resolving against
    /// the pool the caller is actually asking about is the only thing that works for all of
    /// them.
    /// </summary>
    public static string? FindIn(IEnumerable<CardEntry> pool, string nameOrSlug)
    {
        var wanted = Normalize(nameOrSlug);
        foreach (var c in pool)
            if (Normalize(Slug(c.TypeName)) == wanted || Normalize(c.TypeName) == wanted)
                return c.TypeName;
        return null;
    }

    /// <summary>Resolve a user-typed name or slug against the colourless pool.</summary>
    public static string? FindColorless(
        string nameOrSlug, UnlockState? unlocks = null, bool isMultiplayer = true)
    {
        var wanted = Normalize(nameOrSlug);
        return Colorless(unlocks, isMultiplayer)
            .Select(c => c.TypeName)
            .FirstOrDefault(t => Normalize(Slug(t)) == wanted || Normalize(t) == wanted);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
