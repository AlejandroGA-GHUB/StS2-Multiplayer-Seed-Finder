using System.Text;

namespace Sts2.SeedFinder.Core.Map;

/// <summary>
/// Port of MegaCrit.Sts2.Core.Map.MapPathPruning.
///
/// Seven independently walked paths produce a map with a lot of redundancy: two routes that pass
/// through the same sequence of room types between the same two junctions are the same choice
/// twice, and offering both is a decision that is not really a decision. This finds those and
/// removes all but one.
///
/// Removing nodes can drop a room type below its target, so pruning and repair alternate, up to
/// three times. Repair is the only part that draws.
/// </summary>
internal static class MapPathPruning
{
    public static void PruneAndRepair(ActMap map, Rng rng)
    {
        for (int i = 0; i < 3; i++)
        {
            PruneDuplicateSegments(map, rng);
            if (!RepairPrunedPointTypes(map, rng)) break;
        }
    }

    // ---- Repair --------------------------------------------------------------------------------

    /// <summary>
    /// Top each type back up to its target by converting monsters. Returns whether anything
    /// changed, which is what tells the caller another prune pass may be needed.
    ///
    /// The order of these four is fixed and matters: each one draws, and each one consumes
    /// monsters the next might have wanted.
    /// </summary>
    private static bool RepairPrunedPointTypes(ActMap map, Rng rng)
    {
        bool repaired = false;
        repaired |= RepairPointType(map, MapPointType.Shop, map.Counts.Shops, rng);
        repaired |= RepairPointType(map, MapPointType.Elite, map.Counts.Elites, rng);
        repaired |= RepairPointType(map, MapPointType.RestSite, map.Counts.Rests, rng);
        repaired |= RepairPointType(map, MapPointType.Unknown, map.Counts.Unknowns, rng);
        return repaired;
    }

    private static bool RepairPointType(ActMap map, MapPointType type, int targetCount, Rng rng)
    {
        int missing = targetCount - map.GetAllMapPoints().Count(p => p.PointType == type);
        if (missing <= 0) return false;

        // Only unlocked monsters are candidates, which is what protects the three forced rows.
        var candidates = map.GetAllMapPoints()
            .Where(p => p.PointType == MapPointType.Monster && p.CanBeModified)
            .ToList();

        MapShuffle.Stable(candidates, rng, MapPointComparer.Instance);

        bool repaired = false;
        foreach (var point in candidates)
        {
            if (missing == 0) break;
            if (!map.IsValidPointType(type, point)) continue;
            point.PointType = type;
            missing--;
            repaired = true;
        }
        return repaired;
    }

    // ---- Finding duplicates --------------------------------------------------------------------

    private static void PruneDuplicateSegments(ActMap map, Rng rng)
    {
        int iterations = 0;
        var matching = FindMatchingSegments(map.StartingMapPoint);
        while (PrunePaths(map, matching, rng))
        {
            if (++iterations > 50)
                throw new InvalidOperationException($"Unable to prune matching segments in {iterations} iterations");
            matching = FindMatchingSegments(map.StartingMapPoint);
        }
    }

    /// <summary>
    /// Group every segment by a key describing its endpoints and the room types along it, then
    /// return the groups holding more than one. A SortedDictionary keyed ordinally keeps the
    /// groups in a fixed order, which matters because the next step shuffles and draws.
    /// </summary>
    private static List<List<MapPoint[]>> FindMatchingSegments(MapPoint start)
    {
        var segments = new SortedDictionary<string, List<MapPoint[]>>(StringComparer.Ordinal);
        foreach (var path in FindAllPaths(start))
            AddSegmentsToDictionary(path, segments);

        return segments.Values.Where(group => group.Count > 1).ToList();
    }

    /// <summary>
    /// Every route from here to the boss. Recursion terminates at the boss rather than at a leaf,
    /// so a second boss hanging off it is never walked into.
    /// </summary>
    private static List<List<MapPoint>> FindAllPaths(MapPoint current)
    {
        var paths = new List<List<MapPoint>>();

        if (current.PointType == MapPointType.Boss)
        {
            paths.Add(new List<MapPoint> { current });
            return paths;
        }

        foreach (var child in current.Children)
            foreach (var tail in FindAllPaths(child))
            {
                var path = new List<MapPoint>(tail.Count + 1) { current };
                path.AddRange(tail);
                paths.Add(path);
            }

        return paths;
    }

    private static void AddSegmentsToDictionary(IReadOnlyList<MapPoint> path, IDictionary<string, List<MapPoint[]>> segments)
    {
        for (int i = 0; i < path.Count - 1; i++)
        {
            if (!IsValidSegmentStart(path[i])) continue;

            for (int length = 2; length < path.Count - i; length++)
            {
                var end = path[i + length];
                if (!IsValidSegmentEnd(end)) continue;

                var segment = path.Skip(i).Take(length + 1).ToArray();
                string key = GenerateSegmentKey(segment);

                if (!segments.TryGetValue(key, out var group))
                    segments[key] = new List<MapPoint[]> { segment };
                else if (!group.Any(existing => Overlaps(existing, segment)))
                    group.Add(segment);
            }
        }
    }

    /// <summary>A segment starts where a route branches, or at the ancient.</summary>
    private static bool IsValidSegmentStart(MapPoint point) =>
        point.Children.Count > 1 || point.Coord.Row == 0;

    /// <summary>...and ends where routes rejoin.</summary>
    private static bool IsValidSegmentEnd(MapPoint point) => point.Parents.Count >= 2;

    /// <summary>
    /// Endpoints plus the room types between them. Two segments collide only if they start and end
    /// at the same places AND offer the same sequence, which is exactly what makes one of them
    /// redundant. The point type's INTEGER value goes into the key, so the enum's numbering is
    /// part of this behaviour.
    /// </summary>
    private static string GenerateSegmentKey(IReadOnlyList<MapPoint> segment)
    {
        var first = segment[0];
        var last = segment[^1];
        var key = new StringBuilder();

        // The ancient is the only point on row 0, so its column adds nothing.
        if (first.Coord.Row == 0)
            key.Append(first.Coord.Row).Append('-')
               .Append(last.Coord.Col).Append(',').Append(last.Coord.Row).Append('-');
        else
            key.Append(first.Coord.Col).Append(',').Append(first.Coord.Row).Append('-')
               .Append(last.Coord.Col).Append(',').Append(last.Coord.Row).Append('-');

        key.Append(string.Join(",", segment.Select(p => (int)p.PointType)));
        return key.ToString();
    }

    /// <summary>
    /// Whether two segments share an interior point. Segments that do are not really alternatives
    /// to each other, so only one of them is kept as a candidate for pruning.
    /// </summary>
    private static bool Overlaps(IReadOnlyList<MapPoint> a, IReadOnlyList<MapPoint> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        for (int i = 1; i <= a.Count - 2; i++)
            if (i < b.Count && ReferenceEquals(a[i], b[i])) return true;
        return false;
    }

    // ---- Removing them -------------------------------------------------------------------------

    /// <summary>
    /// Try to thin one group of duplicates. Returns true as soon as anything changed, because the
    /// paths have to be re-enumerated before any further decision is sound.
    /// </summary>
    private static bool PrunePaths(ActMap map, List<List<MapPoint[]>> matchingSegments, Rng rng)
    {
        foreach (var group in matchingSegments)
        {
            MapShuffle.Unstable(group, rng);
            if (PruneAllButLast(map, group) != 0) return true;
            if (BreakAParentChildRelationship(group)) return true;
        }
        return false;
    }

    private static int PruneAllButLast(ActMap map, IReadOnlyList<MapPoint[]> matches)
    {
        int pruned = 0;
        foreach (var match in matches)
        {
            if (pruned == matches.Count - 1) return pruned;
            if (PruneSegment(map, match)) pruned++;
        }
        return pruned;
    }

    /// <summary>
    /// Remove the interior of one redundant segment, one node at a time, but only where doing so
    /// cannot orphan anything: a node with a branch through it, or whose removal would strand a
    /// neighbour with no other route, is left alone.
    /// </summary>
    private static bool PruneSegment(ActMap map, MapPoint[] segment)
    {
        bool removed = false;

        for (int i = 0; i < segment.Length - 1; i++)
        {
            var point = segment[i];
            if (!IsInMap(map, point)) return true;

            if (point.Children.Count > 1 || point.Parents.Count > 1
                || point.Parents.Any(p => p.Children.Count == 1 && !IsRemoved(map, p)))
                continue;

            if (segment.Skip(i).Any(n => n.Children.Count > 1 && n.Parents.Count == 1)) continue;

            if (segment[^1].Parents.Count == 1) return false;

            if (point.Children.Where(c => !segment.Contains(c)).Any(c => c.Parents.Count == 1)) continue;

            RemovePoint(map, point);
            removed = true;
        }

        return removed;
    }

    private static void RemovePoint(ActMap map, MapPoint point)
    {
        map.Grid[point.Coord.Col, point.Coord.Row] = null;
        map.StartMapPoints.Remove(point);

        foreach (var child in point.Children.ToList()) point.RemoveChildPoint(child);
        foreach (var parent in point.Parents.ToList()) parent.RemoveChildPoint(point);
    }

    /// <summary>
    /// Whether a point is still part of the map. The ancient and the boss live outside the grid, so
    /// they are never "removed"; everything else is gone once its cell is null.
    /// </summary>
    private static bool IsInMap(ActMap map, MapPoint point)
    {
        if (!InGridBounds(map, point)) return point.PointType is MapPointType.Ancient or MapPointType.Boss;
        if (map.Grid[point.Coord.Col, point.Coord.Row] is not null) return true;
        return point.PointType is MapPointType.Ancient or MapPointType.Boss;
    }

    private static bool IsRemoved(ActMap map, MapPoint point) =>
        !InGridBounds(map, point) || map.Grid[point.Coord.Col, point.Coord.Row] is null;

    private static bool InGridBounds(ActMap map, MapPoint point) =>
        point.Coord.Col >= 0 && point.Coord.Col < map.ColumnCount
        && point.Coord.Row >= 0 && point.Coord.Row < map.RowCount;

    /// <summary>
    /// The fallback when nothing can be deleted outright: cut one edge instead, which is safe as
    /// long as the child still has another parent to reach it by.
    /// </summary>
    private static bool BreakAParentChildRelationship(List<MapPoint[]> matches)
    {
        foreach (var match in matches)
        {
            bool broke = false;
            for (int i = 0; i < match.Length - 1; i++)
            {
                var point = match[i];
                if (point.Children.Count < 2) continue;

                var child = match[i + 1];
                if (child.Parents.Count == 1) continue;

                point.RemoveChildPoint(child);
                broke = true;
            }
            if (broke) return true;
        }
        return false;
    }
}
