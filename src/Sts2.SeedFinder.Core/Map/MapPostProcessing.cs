namespace Sts2.SeedFinder.Core.Map;

/// <summary>
/// Port of MegaCrit.Sts2.Core.MapPostProcessing — the three passes that make a generated map
/// readable rather than correct.
///
/// None of these draws, so none of them can desynchronise anything. They only slide points between
/// columns, never adding, removing or retyping. They still have to be reproduced exactly, because
/// the game saves the map AFTER running them, so these coordinates are the ones a run save records
/// and the ones any comparison is against.
/// </summary>
internal static class MapPostProcessing
{
    /// <summary>
    /// Shift everything sideways when the map has drifted into one half of the grid, which happens
    /// whenever all seven paths start on the same side. Only fires when two whole columns at one
    /// edge are empty and the other edge is occupied.
    /// </summary>
    public static void CenterGrid(MapPoint?[,] grid)
    {
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);

        bool leftEmpty = IsColumnEmpty(grid, 0) && IsColumnEmpty(grid, 1);
        bool rightEmpty = IsColumnEmpty(grid, width - 1) && IsColumnEmpty(grid, width - 2);

        int shift = leftEmpty && !rightEmpty ? -1
                  : !leftEmpty && rightEmpty ? 1
                  : 0;
        if (shift == 0) return;

        // Walk away from the direction of travel so a cell is always vacated before it is filled.
        if (shift > 0)
        {
            for (int row = 0; row < height; row++)
                for (int col = width - 1; col >= 0; col--)
                    MoveTo(grid, col, row, col + shift, width);
        }
        else
        {
            for (int row = 0; row < height; row++)
                for (int col = 0; col < width; col++)
                    MoveTo(grid, col, row, col + shift, width);
        }
    }

    private static void MoveTo(MapPoint?[,] grid, int col, int row, int target, int width)
    {
        var point = grid[col, row];
        grid[col, row] = null;
        if (target < 0 || target >= width) return;

        grid[target, row] = point;
        if (point is not null) point.Coord = new MapCoord(target, row);
    }

    private static bool IsColumnEmpty(MapPoint?[,] grid, int col)
    {
        for (int row = 0; row < grid.GetLength(1); row++)
            if (grid[col, row] is not null) return false;
        return true;
    }

    /// <summary>
    /// Push nodes in a row apart until no one can gain by moving, so a row of four does not draw as
    /// four nodes squeezed into three columns. A node may only move to a column that keeps it
    /// within one column of every parent and child, which is what keeps the edges drawable.
    /// </summary>
    public static void SpreadAdjacentMapPoints(MapPoint?[,] grid)
    {
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);

        for (int row = 0; row < height; row++)
        {
            var rowPoints = new List<MapPoint>();
            for (int col = 0; col < width; col++)
                if (grid[col, row] is { } point) rowPoints.Add(point);

            bool moved;
            do
            {
                moved = false;
                foreach (var point in rowPoints)
                {
                    int current = point.Coord.Col;
                    int bestCol = current;
                    int bestGap = ComputeGap(current, rowPoints, point);

                    foreach (int candidate in AllowedPositions(point, width))
                    {
                        if (candidate == current) continue;
                        if (grid[candidate, row] is not null && !ReferenceEquals(grid[candidate, row], point)) continue;

                        int gap = ComputeGap(candidate, rowPoints, point);
                        if (gap <= bestGap) continue;

                        bestCol = candidate;
                        bestGap = gap;
                    }

                    if (bestCol == current) continue;

                    grid[current, row] = null;
                    grid[bestCol, row] = point;
                    point.Coord = new MapCoord(bestCol, row);
                    moved = true;
                }
            }
            while (moved);
        }
    }

    /// <summary>
    /// Columns this node could occupy: within one of every neighbour, intersected. Ascending, since
    /// ties keep the first candidate found and the game's set is built in ascending order.
    /// </summary>
    private static IEnumerable<int> AllowedPositions(MapPoint point, int width)
    {
        for (int col = 0; col < width; col++)
        {
            bool ok = true;
            foreach (var neighbour in point.Parents.Concat(point.Children))
                if (Math.Abs(neighbour.Coord.Col - col) > 1) { ok = false; break; }

            if (ok) yield return col;
        }
    }

    /// <summary>Distance from a candidate column to the nearest other node in the row.</summary>
    private static int ComputeGap(int candidateCol, List<MapPoint> rowPoints, MapPoint self)
    {
        int nearest = int.MaxValue;
        foreach (var other in rowPoints)
            if (!ReferenceEquals(other, self))
                nearest = Math.Min(nearest, Math.Abs(candidateCol - other.Coord.Col));
        return nearest;
    }

    /// <summary>
    /// Straighten a lone node that juts out: if a node with exactly one parent and one child sits
    /// further left than both, and the cell to its right is free, nudge it right. And the mirror.
    /// Turns a visible zigzag on a single-file stretch into a straight line.
    /// </summary>
    public static void StraightenPaths(MapPoint?[,] grid)
    {
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);

        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                var point = grid[col, row];
                if (point is null || point.Parents.Count != 1 || point.Children.Count != 1) continue;

                var parent = point.Parents[0];
                var child = point.Children[0];

                bool leftOfBoth = point.Coord.Col < child.Coord.Col && point.Coord.Col < parent.Coord.Col;
                bool rightOfBoth = point.Coord.Col > child.Coord.Col && point.Coord.Col > parent.Coord.Col;

                if (leftOfBoth && col < width - 1 && grid[col + 1, row] is null)
                {
                    point.Coord = new MapCoord(col + 1, row);
                    grid[col, row] = null;
                    grid[col + 1, row] = point;
                }

                if (rightOfBoth && col > 0 && grid[col - 1, row] is null)
                {
                    point.Coord = new MapCoord(col - 1, row);
                    grid[col, row] = null;
                    grid[col - 1, row] = point;
                }
            }
    }
}
