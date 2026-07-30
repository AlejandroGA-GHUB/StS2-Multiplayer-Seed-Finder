namespace Sts2.SeedFinder.Core.Acts;

/// <summary>
/// The relics that can fill a merchant's third slot: everything of <c>RelicRarity.Shop</c> in
/// the shared pool, plus the one each character contributes to their own.
///
/// Naming and lookup only — the draw itself lives in <see cref="RunGenerator"/>.
///
/// Display names are derived from the type name rather than read from the game's localization
/// table, so possessives lose their apostrophe ("Lees Waffle", "Dollys Mirror"). The web UI
/// resolves the real titles from the player's own install and overrides these; the CLI shows
/// the derived form. Matching is punctuation-insensitive, so either spelling is accepted.
/// </summary>
public static class ShopRelics
{
    /// <summary>Every relic that can appear in the third slot, for the given party.</summary>
    public static IReadOnlyList<PoolRelic> For(IEnumerable<Character> characters)
    {
        var list = RelicPoolData.SharedRelics.Where(IsShop).ToList();
        foreach (var c in characters.Distinct())
            list.AddRange(RelicPoolData.RelicsFor(c).Where(IsShop));
        return list;
    }

    /// <summary>Every shop relic in the game, regardless of party.</summary>
    public static IReadOnlyList<PoolRelic> All { get; } =
        RelicPoolData.SharedRelics
            .Concat(Enum.GetValues<Character>().SelectMany(RelicPoolData.RelicsFor))
            .Where(IsShop)
            .ToList();

    private static bool IsShop(PoolRelic r) => r.Rarity == "Shop";

    public static PoolRelic? Find(string nameOrSlug)
    {
        var needle = Normalize(nameOrSlug);
        foreach (var r in All)
            if (Normalize(r.Slug) == needle || Normalize(r.Name) == needle) return r;
        return null;
    }

    /// <summary>Words a title leaves lowercase unless they lead: "Sling of Courage".</summary>
    private static readonly HashSet<string> Minor =
        new(StringComparer.Ordinal) { "of", "the", "and", "a", "an", "in", "on", "to", "for" };

    /// <summary>"belt_buckle" or "BeltBuckle" to "Belt Buckle". Unknown input is returned as given.</summary>
    public static string Display(string nameOrSlug)
    {
        var relic = Find(nameOrSlug);
        return relic is null ? nameOrSlug : TitleCase(relic.Value.Slug);
    }

    /// <summary>"belt_buckle" to "Belt Buckle". Shared with the chest relics, which name the
    /// same way and for the same reason — the real titles come from the player's own install.</summary>
    internal static string TitleCase(string slug)
    {
        var words = slug.Split('_').Where(w => w.Length > 0).ToArray();
        return string.Join(' ', words.Select((w, i) =>
            i > 0 && Minor.Contains(w) ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Punctuation- and case-insensitive comparison of a relic name or slug.</summary>
    internal static string Key(string s) =>
        s.Trim().ToLowerInvariant().Replace("'", "").Replace(" ", "").Replace("_", "");

    /// <summary>
    /// Which character contributes this relic, or null when it is from the shared pool. The
    /// party has to include that character for the relic to be reachable at all, which is what
    /// makes an unreachable request worth rejecting up front rather than scanning to no answers.
    /// </summary>
    public static Character? OwnerOf(string nameOrSlug)
    {
        var relic = Find(nameOrSlug);
        if (relic is null) return null;
        foreach (var c in Enum.GetValues<Character>())
            if (RelicPoolData.RelicsFor(c).Any(r => r.Slug == relic.Value.Slug)) return c;
        return null;
    }

    private static string Normalize(string s) => Key(s);
}
