namespace Sts2.SeedFinder.Core.Neow;

/// <summary>Which branch of Neow's offer a relic belongs to.</summary>
public enum NeowPool
{
    /// <summary>The take-a-curse-get-a-relic option. Exactly one is always offered.</summary>
    Curse,
    /// <summary>The always-considered positive pool. Two are offered.</summary>
    Positive,
    /// <summary>Added to the positive pool by a coin flip rather than being in it up front.</summary>
    CoinFlip,
}

/// <summary>
/// A relic Neow can offer. <paramref name="Availability"/> mirrors the game's
/// IsAllowed / IsAllowedAtNeow checks that actually gate Neow's pools.
/// </summary>
public sealed record NeowRelic(
    string Name,
    string Slug,
    NeowPool Pool,
    RelicAvailability Availability = RelicAvailability.Always)
{
    public override string ToString() => Name;
}

/// <summary>
/// The gating conditions that appear on Neow's relics. Each maps to a concrete check
/// in the game rather than being a general-purpose predicate system.
/// </summary>
public enum RelicAvailability
{
    Always,
    /// <summary>IsAllowed => Players.Count == 1. Never appears in co-op.</summary>
    SingleplayerOnly,
    /// <summary>IsAllowed => Players.Count > 1. Only appears in co-op.</summary>
    MultiplayerOnly,
    /// <summary>Kaleidoscope: needs every character's card pool unlocked.</summary>
    RequiresAllCharactersUnlocked,
    /// <summary>Scroll Boxes: needs >= 4 commons and >= 2 uncommons in your character's pool.</summary>
    RequiresBundleableCardPool,
}

/// <summary>
/// Neow's relic catalogue, in the game's declaration order (Neow.cs, StS2 v0.109.0).
/// Order is load-bearing: the RNG picks by index, so reordering silently breaks predictions.
/// </summary>
public static class NeowRelics
{
    /// <summary>Neow.CurseOptions — declaration order. Do not reorder.</summary>
    public static readonly IReadOnlyList<NeowRelic> Curses = new[]
    {
        new NeowRelic("Cursed Pearl",      "cursed_pearl",      NeowPool.Curse),
        new NeowRelic("Dowsing Rod",       "dowsing_rod",       NeowPool.Curse),
        new NeowRelic("Hefty Tablet",      "hefty_tablet",      NeowPool.Curse),
        new NeowRelic("Large Capsule",     "large_capsule",     NeowPool.Curse),
        new NeowRelic("Leafy Poultice",    "leafy_poultice",    NeowPool.Curse),
        new NeowRelic("Neow's Bones",      "neows_bones",       NeowPool.Curse),
        new NeowRelic("Neow's Sacrifice",  "neows_sacrifice",   NeowPool.Curse),
        new NeowRelic("Precarious Shears", "precarious_shears", NeowPool.Curse),
        new NeowRelic("Silken Tress",      "silken_tress",      NeowPool.Curse),
        new NeowRelic("Silver Crucible",   "silver_crucible",   NeowPool.Curse, RelicAvailability.SingleplayerOnly),
    };

    /// <summary>Neow.PositiveOptions — the base 14, in declaration order. Do not reorder.</summary>
    public static readonly IReadOnlyList<NeowRelic> Positives = new[]
    {
        new NeowRelic("Arcane Scroll",   "arcane_scroll",   NeowPool.Positive),
        new NeowRelic("Booming Conch",   "booming_conch",   NeowPool.Positive),
        new NeowRelic("Fishing Rod",     "fishing_rod",     NeowPool.Positive),
        new NeowRelic("Golden Pearl",    "golden_pearl",    NeowPool.Positive),
        new NeowRelic("Kaleidoscope",    "kaleidoscope",    NeowPool.Positive, RelicAvailability.RequiresAllCharactersUnlocked),
        new NeowRelic("Lead Paperweight","lead_paperweight",NeowPool.Positive),
        new NeowRelic("Lost Coffer",     "lost_coffer",     NeowPool.Positive),
        new NeowRelic("Massive Scroll",  "massive_scroll",  NeowPool.Positive, RelicAvailability.MultiplayerOnly),
        new NeowRelic("Neow's Torment",  "neows_torment",   NeowPool.Positive),
        new NeowRelic("New Leaf",        "new_leaf",        NeowPool.Positive),
        new NeowRelic("Phial Holster",   "phial_holster",   NeowPool.Positive),
        new NeowRelic("Precise Scissors","precise_scissors",NeowPool.Positive),
        new NeowRelic("Scroll Boxes",    "scroll_boxes",    NeowPool.Positive, RelicAvailability.RequiresBundleableCardPool),
        new NeowRelic("Winged Boots",    "winged_boots",    NeowPool.Positive, RelicAvailability.SingleplayerOnly),
    };

    // The three coin-flip pairs, added to the positive pool after the curse is picked.
    public static readonly NeowRelic LavaRock         = new("Lava Rock",         "lava_rock",         NeowPool.CoinFlip);
    public static readonly NeowRelic SmallCapsule     = new("Small Capsule",     "small_capsule",     NeowPool.CoinFlip);
    public static readonly NeowRelic NutritiousOyster = new("Nutritious Oyster", "nutritious_oyster", NeowPool.CoinFlip);
    public static readonly NeowRelic StoneHumidifier  = new("Stone Humidifier",  "stone_humidifier",  NeowPool.CoinFlip);
    public static readonly NeowRelic NeowsTalisman    = new("Neow's Talisman",   "neows_talisman",    NeowPool.CoinFlip);
    public static readonly NeowRelic Pomander         = new("Pomander",          "pomander",          NeowPool.CoinFlip);

    /// <summary>The three coin-flip pairs, in the order they are flipped for.</summary>
    public static readonly IReadOnlyList<NeowRelic> CoinFlips = new[]
    {
        LavaRock, SmallCapsule, NutritiousOyster, StoneHumidifier, NeowsTalisman, Pomander,
    };

    public static IEnumerable<NeowRelic> All => Curses.Concat(Positives).Concat(CoinFlips);

    /// <summary>
    /// The positive options each curse removes from the pool before the flips, because taking
    /// the curse would duplicate or undo them. Lives here rather than inside the generator so
    /// that predicting an offer and judging whether an offer is possible read the same table.
    /// Keyed and valued by slug.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Counterparts =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["cursed_pearl"]      = new[] { "golden_pearl" },
            ["hefty_tablet"]      = new[] { "arcane_scroll" },
            ["leafy_poultice"]    = new[] { "new_leaf" },
            ["precarious_shears"] = new[] { "precise_scissors" },
            ["neows_sacrifice"]   = new[] { "phial_holster", "lost_coffer" },
        };

    /// <summary>
    /// The other half of a relic's coin-flip pair, or null if it is not a coin-flip relic.
    /// Exactly one of each pair enters the pool, so no offer can ever hold both.
    /// <see cref="CoinFlips"/> is flat and in pair order, so partners are neighbours.
    /// </summary>
    public static NeowRelic? CoinFlipPartner(NeowRelic relic)
    {
        for (int i = 0; i < CoinFlips.Count; i += 2)
        {
            if (CoinFlips[i] == relic) return CoinFlips[i + 1];
            if (CoinFlips[i + 1] == relic) return CoinFlips[i];
        }
        return null;
    }

    public static NeowRelic? Find(string nameOrSlug)
    {
        var needle = Normalize(nameOrSlug);
        return All.FirstOrDefault(r => Normalize(r.Slug) == needle || Normalize(r.Name) == needle);
    }

    private static string Normalize(string s) =>
        s.Trim().ToLowerInvariant().Replace("'", "").Replace(" ", "").Replace("_", "");
}
