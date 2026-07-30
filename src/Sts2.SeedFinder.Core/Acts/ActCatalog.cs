namespace Sts2.SeedFinder.Core.Acts;

/// <summary>
/// The bosses and events a given act can produce, plus the naming both need.
///
/// Encounter type names carry their role as a suffix (CeremonialBeastBoss, NibbitsWeak), which
/// is how the generated tables identify them but not how anybody refers to them. Everything the
/// user types or reads goes through here so a boss is "Ceremonial Beast" / "ceremonial_beast"
/// in the CLI, the API and the UI alike.
/// </summary>
public static class ActCatalog
{
    private static readonly string[] RoleSuffixes = { "Boss", "Elite", "Normal", "Weak" };

    /// <summary>The type name without its role suffix — "CeremonialBeastBoss" to "CeremonialBeast".</summary>
    public static string BareName(string typeName)
    {
        foreach (var suffix in RoleSuffixes)
            if (typeName.Length > suffix.Length && typeName.EndsWith(suffix, StringComparison.Ordinal))
                return typeName[..^suffix.Length];
        return typeName;
    }

    /// <summary>"CeremonialBeastBoss" to "Ceremonial Beast".</summary>
    public static string Display(string typeName)
    {
        var bare = BareName(typeName);
        var sb = new System.Text.StringBuilder(bare.Length + 8);
        for (int i = 0; i < bare.Length; i++)
        {
            if (i > 0 && char.IsUpper(bare[i]) && !char.IsUpper(bare[i - 1])) sb.Append(' ');
            sb.Append(bare[i]);
        }
        return sb.ToString();
    }

    /// <summary>"CeremonialBeastBoss" to "ceremonial_beast".</summary>
    public static string Slug(string typeName) => GameHash.SnakeCase(BareName(typeName));

    /// <summary>Act numbers as the user sees them, 1-based.</summary>
    public static IEnumerable<int> ActNumbers => Enumerable.Range(1, ActData.ByIndex.Length);

    private static ActDefinition[] Candidates(int act) =>
        act >= 1 && act <= ActData.ByIndex.Length
            ? ActData.ByIndex[act - 1]
            : throw new ArgumentException($"there is no act {act}; this run has {ActData.ByIndex.Length}.");

    /// <summary>
    /// Every boss act <paramref name="act"/> can end with, paired with the map it belongs to.
    ///
    /// Only Act 1 has more than one candidate map, and its two maps have disjoint boss lists —
    /// so naming an Act 1 boss also pins the map, whether or not the map was asked for.
    /// </summary>
    public static IEnumerable<(string TypeName, string Map)> Bosses(int act) =>
        Candidates(act).SelectMany(a => a.Bosses.Select(b => (b.Name, a.Name)));

    /// <summary>
    /// Every event act <paramref name="act"/> can hand out: the map's own plus the shared pool
    /// that is appended to every act before the shuffle.
    ///
    /// One row PER MAP, so a shared event appears twice for Act 1. That is what makes
    /// "is this event reachable on that particular map" answerable; use
    /// <see cref="EventNames"/> when a plain list is what is wanted.
    /// </summary>
    public static IEnumerable<(string TypeName, string Map)> Events(int act) =>
        Candidates(act).SelectMany(a => a.Events.Concat(ActData.SharedEvents).Select(e => (e, a.Name)));

    /// <summary>The act's events, each named once however many of its maps carry it.</summary>
    public static IEnumerable<string> EventNames(int act) =>
        Events(act).Select(e => e.TypeName).Distinct();

    /// <summary>Resolves a user-supplied boss name or slug against one act. Null when no match.</summary>
    public static string? FindBoss(int act, string nameOrSlug) =>
        Find(Bosses(act).Select(b => b.TypeName), nameOrSlug);

    /// <summary>Resolves a user-supplied event name or slug against one act. Null when no match.</summary>
    public static string? FindEvent(int act, string nameOrSlug) => Find(EventNames(act), nameOrSlug);

    private static string? Find(IEnumerable<string> pool, string nameOrSlug)
    {
        var wanted = GameHash.SnakeCase(nameOrSlug.Trim().Replace(' ', '_')).Replace("__", "_");
        foreach (var typeName in pool)
            if (Slug(typeName).Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || typeName.Equals(nameOrSlug.Trim(), StringComparison.OrdinalIgnoreCase))
                return typeName;
        return null;
    }
}
