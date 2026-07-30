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
    public static IEnumerable<CardEntry> FirstFightOfferable(Character character, UnlockState? unlocks = null) =>
        CardRewardGenerator.PoolFor(character, unlocks).Where(c => FirstFightRarities.Contains(c.Rarity));

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
    public static IEnumerable<CardEntry> Offerable(Character character, UnlockState? unlocks = null) =>
        CardRewardGenerator.PoolFor(character, unlocks).Where(c => RewardRarities.Contains(c.Rarity));

    /// <summary>Resolve a user-typed name or slug against one character's offerable cards.</summary>
    public static string? Find(Character character, string nameOrSlug, UnlockState? unlocks = null)
    {
        var wanted = Normalize(nameOrSlug);
        return Offerable(character, unlocks)
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
