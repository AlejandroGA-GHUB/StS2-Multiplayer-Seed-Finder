namespace Sts2.SeedFinder.Core.Map;

/// <summary>
/// Port of MegaCrit.Sts2.Core.Map.MapPointType. The integer values matter: path pruning builds a
/// string key out of them, so renumbering this silently changes which segments count as duplicates.
/// </summary>
public enum MapPointType
{
    Unassigned = 0,
    Unknown = 1,
    Shop = 2,
    Treasure = 3,
    RestSite = 4,
    Monster = 5,
    Elite = 6,
    Boss = 7,
    Ancient = 8,
}

/// <summary>A grid position. Sorts by (col, row), which is what StableShuffle orders points by.</summary>
public readonly record struct MapCoord(int Col, int Row) : IComparable<MapCoord>
{
    public int CompareTo(MapCoord other) => (Col, Row).CompareTo((other.Col, other.Row));
    public override string ToString() => $"({Col},{Row})";
}

/// <summary>
/// One node of an act map, with its edges.
///
/// The game holds <c>Children</c> and <c>parents</c> in <c>HashSet&lt;MapPoint&gt;</c>, and their
/// ITERATION ORDER is load-bearing rather than incidental: path enumeration walks Children, the
/// resulting segment lists are fed to an UnstableShuffle, and an unstable shuffle's result depends
/// on the order it was handed. So the sets are reproduced here as insertion-ordered lists.
///
/// That is faithful because of a property of the generator rather than of HashSet: every edge is
/// added during GenerateMap, and pruning only ever REMOVES. .NET's HashSet enumerates its entry
/// array in slot order, and slots are only recycled by an insert following a remove, which never
/// happens here. Were anything ever to add an edge after pruning began, this would have to model
/// the free list instead.
///
/// Identity is by reference, matching the game: MapPoint declares no public Equals or GetHashCode
/// override, so its HashSets compare by reference too. The generator's GetOrCreatePoint keeps
/// exactly one instance per coordinate, which is what makes that safe.
/// </summary>
public sealed class MapPoint(int col, int row)
{
    private readonly List<MapPoint> _children = new();
    private readonly List<MapPoint> _parents = new();

    /// <summary>Mutable: the post-processing passes slide points sideways after generation.</summary>
    public MapCoord Coord { get; set; } = new(col, row);

    public MapPointType PointType { get; set; } = MapPointType.Unassigned;

    /// <summary>
    /// False on the rows the generator forces (row 1, the treasure row, the last row). Set by
    /// AssignPointTypes, and read by the repair pass so it cannot overwrite a fixed room.
    /// </summary>
    public bool CanBeModified { get; set; } = true;

    public IReadOnlyList<MapPoint> Children => _children;
    public IReadOnlyList<MapPoint> Parents => _parents;

    public void AddChildPoint(MapPoint child)
    {
        if (_children.Contains(child)) return;
        _children.Add(child);
        child._parents.Add(this);
    }

    public void RemoveChildPoint(MapPoint child)
    {
        _children.Remove(child);
        child._parents.Remove(this);
    }

    /// <summary>Every other point sharing a parent with this one.</summary>
    public IEnumerable<MapPoint> Siblings()
    {
        foreach (var parent in _parents)
            foreach (var sibling in parent._children)
                if (!ReferenceEquals(sibling, this))
                    yield return sibling;
    }

    public override string ToString() => $"Point[{Coord.Col},{Coord.Row}]";
}
