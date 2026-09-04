using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Map;

/// <summary>
/// Port of MegaCrit.Sts2.Core.Map.StandardActMap — one act's node graph.
///
/// This is the only part of run generation that draws from its OWN stream. The game builds it with
/// <c>new Rng(runSeed, $"act_{n}_map")</c>, a throwaway generator that is never serialized and never
/// shared, so a map depends on nothing but the seed, the act, the player count and the ascension.
/// Nothing here can move a boss, a relic or a card reward, and nothing there can move a map.
///
/// Generation runs in four phases, and the rng is only touched by the first three:
///   1. Point type counts   — how many rests, unknowns, elites and shops to aim for
///   2. GenerateMap         — seven paths walked from row 1 to the last row, forming the graph
///   3. AssignPointTypes    — forced rows first, then the queued types onto legal positions
///   4. Prune, then lay out — remove duplicate routes, top the counts back up, then tidy columns
/// </summary>
public sealed class ActMap
{
    /// <summary>Grid width, and also the number of paths walked. Both are 7 in the game.</summary>
    public const int Width = 7;
    private const int PathCount = 7;

    private readonly MapPoint?[,] _grid;
    private readonly int _mapLength;
    private readonly Rng _rng;
    private readonly List<MapPoint> _startMapPoints = new();

    /// <summary>Types that may not sit in the bottom six rows.</summary>
    private static readonly MapPointType[] LowerRestrictions = { MapPointType.RestSite, MapPointType.Elite };

    /// <summary>Types that may not sit in the top three rows.</summary>
    private static readonly MapPointType[] UpperRestrictions = { MapPointType.RestSite };

    /// <summary>Types that may not appear twice along an edge.</summary>
    private static readonly MapPointType[] NeighbourRestrictions =
        { MapPointType.Elite, MapPointType.RestSite, MapPointType.Treasure, MapPointType.Shop };

    /// <summary>Types that may not appear twice among points sharing a parent.</summary>
    private static readonly MapPointType[] SiblingRestrictions =
        { MapPointType.RestSite, MapPointType.Monster, MapPointType.Unknown, MapPointType.Elite, MapPointType.Shop };

    public MapPoint StartingMapPoint { get; }
    public MapPoint BossMapPoint { get; }
    public MapPoint? SecondBossMapPoint { get; }
    public MapPointTypeCounts Counts { get; }

    public int ColumnCount => _grid.GetLength(0);
    public int RowCount => _grid.GetLength(1);

    /// <summary>
    /// StandardActMap.CreateFor. <paramref name="actIndex"/> is zero-based, matching
    /// RunState.CurrentActIndex, and the stream name it forms is one-based ("act_1_map").
    /// </summary>
    public static ActMap Generate(
        ulong runSeed, int actIndex, ActDefinition act,
        bool isMultiplayer, int ascension, bool hasSecondBoss) =>
        new(new Rng(runSeed, $"act_{actIndex + 1}_map"), act, isMultiplayer, ascension, hasSecondBoss);

    public ActMap(Rng mapRng, ActDefinition act, bool isMultiplayer, int ascension, bool hasSecondBoss)
    {
        _rng = mapRng;

        // In multiplayer the act is one room shorter, so the map is one row shorter. That single
        // integer is the ONLY thing the player count changes about a map.
        _mapLength = act.GetNumberOfRooms(isMultiplayer) + 1;
        _grid = new MapPoint?[Width, _mapLength];

        // First draws of the whole map, before any node exists.
        Counts = MapPointTypeCounts.For(act.Name, _rng, ascension);

        // The ancient sits below the grid and the boss above it, both centred. Neither is stored
        // in the grid, which is why GetAllMapPoints never returns them.
        StartingMapPoint = new MapPoint(ColumnCount / 2, 0) { PointType = MapPointType.Ancient };
        BossMapPoint = new MapPoint(ColumnCount / 2, RowCount) { PointType = MapPointType.Boss };
        if (hasSecondBoss)
            SecondBossMapPoint = new MapPoint(ColumnCount / 2, RowCount + 1) { PointType = MapPointType.Boss };

        GenerateMap();
        AssignPointTypes();
        MapPathPruning.PruneAndRepair(this, _rng);

        // Pure geometry from here: these three slide points sideways to make the drawing readable
        // and consume no rng at all, so a mistake in them cannot desynchronise anything.
        MapPostProcessing.CenterGrid(_grid);
        MapPostProcessing.SpreadAdjacentMapPoints(_grid);
        MapPostProcessing.StraightenPaths(_grid);
    }

    // ---- Grid access ---------------------------------------------------------------------------

    internal MapPoint?[,] Grid => _grid;
    internal List<MapPoint> StartMapPoints => _startMapPoints;

    /// <summary>
    /// Every point in the grid, COLUMN-MAJOR. The order matters: this feeds the type-assignment
    /// shuffle, and (col, row) is also how points sort, so the two agree by construction.
    /// </summary>
    public IEnumerable<MapPoint> GetAllMapPoints()
    {
        for (int col = 0; col < ColumnCount; col++)
            for (int row = 0; row < RowCount; row++)
                if (_grid[col, row] is { } point)
                    yield return point;
    }

    public IEnumerable<MapPoint> PointsInRow(int row)
    {
        if (row < 0 || row >= RowCount) yield break;
        for (int col = 0; col < ColumnCount; col++)
            if (_grid[col, row] is { } point)
                yield return point;
    }

    private MapPoint GetOrCreatePoint(int col, int row) =>
        _grid[col, row] ??= new MapPoint(col, row);

    // ---- Phase 2: the graph --------------------------------------------------------------------

    private void GenerateMap()
    {
        for (int i = 0; i < PathCount; i++)
        {
            var start = GetOrCreatePoint(_rng.NextInt(0, Width), 1);

            // Only the SECOND path is forced to start somewhere new. Every later path may reuse an
            // existing start, which is what lets the seven walks collapse into fewer visible routes.
            if (i == 1)
                while (_startMapPoints.Contains(start))
                    start = GetOrCreatePoint(_rng.NextInt(0, Width), 1);

            if (!_startMapPoints.Contains(start)) _startMapPoints.Add(start);
            PathGenerate(start);
        }

        foreach (var point in PointsInRow(RowCount - 1).ToList())
            point.AddChildPoint(BossMapPoint);

        if (SecondBossMapPoint is { } second)
            BossMapPoint.AddChildPoint(second);

        foreach (var point in PointsInRow(1).ToList())
            StartingMapPoint.AddChildPoint(point);
    }

    private void PathGenerate(MapPoint start)
    {
        var current = start;
        while (current.Coord.Row < _mapLength - 1)
        {
            var next = GetOrCreatePoint(GenerateNextCoord(current).Col, current.Coord.Row + 1);
            current.AddChildPoint(next);
            current = next;
        }
    }

    /// <summary>
    /// Where this path steps next: left, straight or right, tried in a shuffled order and taking
    /// the first that would not cross an existing edge. Left and right clamp to the grid, so at
    /// the edges two of the three options collapse onto the same column.
    /// </summary>
    private MapCoord GenerateNextCoord(MapPoint current)
    {
        int col = current.Coord.Col;
        int left = Math.Max(0, col - 1);
        int right = Math.Min(col + 1, Width - 1);

        var directions = new List<int> { -1, 0, 1 };
        MapShuffle.Stable(directions, _rng);

        foreach (int direction in directions)
        {
            int target = direction switch { -1 => left, 0 => col, _ => right };
            if (!HasInvalidCrossover(current, target))
                return new MapCoord(target, current.Coord.Row + 1);
        }

        throw new InvalidOperationException("Cannot find next node");
    }

    /// <summary>
    /// True when stepping to <paramref name="targetCol"/> would cross an edge already heading the
    /// other way, which would draw as an X. Moving straight up can never cross anything.
    /// </summary>
    private bool HasInvalidCrossover(MapPoint current, int targetCol)
    {
        int delta = targetCol - current.Coord.Col;
        if (delta == 0) return false;

        var neighbour = _grid[targetCol, current.Coord.Row];
        if (neighbour is null) return false;

        foreach (var child in neighbour.Children)
            if (child.Coord.Col - neighbour.Coord.Col == -delta)
                return true;

        return false;
    }

    // ---- Phase 3: what each node is ------------------------------------------------------------

    private void AssignPointTypes()
    {
        // Three rows are fixed before anything is rolled, and locked so the repair pass cannot take
        // them back. These are rows the rest of the tool already relies on: row 1 is why the first
        // fight is predictable, and the treasure row is the act's guaranteed chest.
        ForceRow(RowCount - 1, MapPointType.RestSite);
        ForceRow(RowCount - 7, MapPointType.Treasure);
        ForceRow(1, MapPointType.Monster);

        var queue = new Queue<MapPointType>();
        for (int i = 0; i < Counts.Rests; i++) queue.Enqueue(MapPointType.RestSite);
        for (int i = 0; i < Counts.Shops; i++) queue.Enqueue(MapPointType.Shop);
        for (int i = 0; i < Counts.Elites; i++) queue.Enqueue(MapPointType.Elite);
        for (int i = 0; i < Counts.Unknowns; i++) queue.Enqueue(MapPointType.Unknown);

        AssignRemainingTypesToRandomPoints(queue);

        foreach (var point in GetAllMapPoints())
            if (point.PointType == MapPointType.Unassigned)
                point.PointType = MapPointType.Monster;
    }

    private void ForceRow(int row, MapPointType type)
    {
        foreach (var point in PointsInRow(row))
        {
            point.PointType = type;
            point.CanBeModified = false;
        }
    }

    /// <summary>
    /// Deal the queued types onto unassigned points, in three passes.
    ///
    /// More than one pass is needed because a type that fits nowhere is rotated to the back of the
    /// queue rather than dropped, and a point that could take nothing is left unassigned. Filling
    /// other points changes both, so a later pass can place what an earlier one could not.
    /// </summary>
    private void AssignRemainingTypesToRandomPoints(Queue<MapPointType> queue)
    {
        for (int pass = 0; pass < 3 && queue.Count > 0; pass++)
        {
            var unassigned = GetAllMapPoints()
                .Where(p => p.PointType == MapPointType.Unassigned)
                .ToList();

            MapShuffle.Stable(unassigned, _rng, MapPointComparer.Instance);

            foreach (var point in unassigned)
            {
                if (queue.Count == 0) break;
                point.PointType = NextValidPointType(queue, point);
            }
        }
    }

    /// <summary>
    /// Pull the first queued type this point can legally take, rotating rejects to the back.
    /// Returns Unassigned when nothing in the queue fits, leaving the point for a later pass.
    /// </summary>
    private MapPointType NextValidPointType(Queue<MapPointType> queue, MapPoint point)
    {
        for (int i = 0; i < queue.Count; i++)
        {
            var type = queue.Dequeue();
            if (IsValidPointType(type, point)) return type;
            queue.Enqueue(type);
        }
        return MapPointType.Unassigned;
    }

    /// <summary>
    /// The placement rules, all of which must hold. The neighbour check looks at parents and
    /// children together, then at children once more; that redundancy is the game's shape, and
    /// removing it would change nothing except how easy this is to compare against the original.
    /// </summary>
    public bool IsValidPointType(MapPointType type, MapPoint point)
    {
        // Rests and elites stay out of the opening rows; rests also stay away from the boss, where
        // the forced last row already provides one.
        if (point.Coord.Row < 6 && LowerRestrictions.Contains(type)) return false;
        if (point.Coord.Row >= _mapLength - 3 && UpperRestrictions.Contains(type)) return false;

        if (NeighbourRestrictions.Contains(type))
        {
            foreach (var neighbour in point.Parents.Concat(point.Children))
                if (neighbour.PointType == type) return false;
            foreach (var child in point.Children)
                if (child.PointType == type) return false;
        }

        if (SiblingRestrictions.Contains(type))
            foreach (var sibling in point.Siblings())
                if (sibling.PointType == type) return false;

        return true;
    }
}
