namespace Sts2.SeedFinder.Core.Map;

/// <summary>
/// The two shuffles from MegaCrit.Sts2.Core.Extensions.ListExtensions. Map generation uses both,
/// and which one is used where changes the result, so they are kept apart rather than unified.
/// </summary>
internal static class MapShuffle
{
    /// <summary>
    /// <see cref="Rng.UnstableShuffle{T}(List{T})"/>. "Unstable" because the outcome depends on
    /// the order it was handed, so callers that want a reproducible result from an arbitrarily
    /// ordered list must sort first.
    /// </summary>
    public static void Unstable<T>(List<T> list, Rng rng) => rng.UnstableShuffle(list);

    /// <summary>
    /// Sort, then shuffle. The sort is what makes it "stable" in the game's sense: two lists
    /// holding the same items in different orders come out identical. Both cost the same single
    /// draw whatever the contents.
    /// </summary>
    public static void Stable<T>(List<T> list, Rng rng, IComparer<T>? comparer = null)
    {
        list.Sort(comparer);
        Unstable(list, rng);
    }
}

/// <summary>Points sort by (col, row), via MapPoint's CompareTo delegating to MapCoord's.</summary>
internal sealed class MapPointComparer : IComparer<MapPoint>
{
    public static readonly MapPointComparer Instance = new();
    public int Compare(MapPoint? x, MapPoint? y) =>
        x is null ? (y is null ? 0 : -1) : y is null ? 1 : x.Coord.CompareTo(y.Coord);
}
