using System.Text.Json;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Map;

namespace Sts2.SeedFinder.Cli;

/// <summary>
/// Diffs a generated act map against the one the game wrote into the run save.
///
/// The save's own words for <c>SerializableActMap</c> are that it captures the full map topology so
/// it can be restored without regeneration, and that is exactly what makes it an oracle: every
/// point, its type, and its outgoing edges. There is nothing partial to reason around, so a match
/// here is conclusive in a way the history verifier's walked path never is.
///
/// One asymmetry worth knowing before reading a failure. The map RNG is a throwaway
/// <c>new Rng(seed, "act_n_map")</c> that the game never serializes, so unlike the UpFront stream
/// there is no counter to compare and no way to learn that we are "three draws out". A single
/// misplaced draw simply produces a different graph. When this fails, the first differing ROW is
/// usually the most useful thing on screen, because generation runs bottom to top.
/// </summary>
public static class MapVerifier
{
    /// <summary>
    /// Compare one act. Returns the number of failures, and prints nothing at all when the save
    /// holds no map for this act, which is the normal case for acts the run has not reached.
    /// </summary>
    public static int Verify(JsonElement act, ActMap mine, string label)
    {
        if (!act.TryGetProperty("saved_map", out var savedMap)) return 0;

        Console.WriteLine($"-- {label} map");

        int failures = 0;
        failures += CompareScalar("  width", savedMap, "width", mine.ColumnCount);
        failures += CompareScalar("  height", savedMap, "height", mine.RowCount);

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var point in savedMap.GetProperty("points").EnumerateArray())
        {
            var (col, row) = ReadCoord(point.GetProperty("coord"));
            expected[$"{col},{row}"] = Describe(
                point.GetProperty("type").GetString() ?? "?",
                ReadChildren(point));
        }

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var point in mine.GetAllMapPoints())
        {
            actual[$"{point.Coord.Col},{point.Coord.Row}"] = Describe(
                GameHash.Slugify(point.PointType.ToString()).ToLowerInvariant(),
                point.Children.Select(c => $"{c.Coord.Col},{c.Coord.Row}").ToList());
        }

        failures += ComparePoints(expected, actual);

        // The boss and ancient sit outside the grid and so are absent from GetAllMapPoints. They
        // are still worth checking, because the boss row is where every path terminates and a
        // wrong row count would show up here first.
        failures += CompareScalar("  boss row", savedMap.GetProperty("boss").GetProperty("coord"),
            "row", mine.BossMapPoint.Coord.Row);

        bool savedHasSecond = savedMap.TryGetProperty("second_boss", out var second)
                              && second.ValueKind is not JsonValueKind.Null;
        if (savedHasSecond || mine.SecondBossMapPoint is not null)
        {
            bool ok = savedHasSecond == (mine.SecondBossMapPoint is not null);
            Console.WriteLine($"  second boss: {(ok ? "MATCH" : "MISMATCH")}"
                              + $"  (save {(savedHasSecond ? "has one" : "has none")},"
                              + $" ours {(mine.SecondBossMapPoint is not null ? "has one" : "has none")})");
            if (!ok) failures++;
        }

        Console.WriteLine();
        return failures;
    }

    private static int ComparePoints(
        SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        var missing = expected.Keys.Where(k => !actual.ContainsKey(k)).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        var differing = expected.Keys
            .Where(k => actual.TryGetValue(k, out var v) && v != expected[k])
            .ToList();

        if (missing.Count == 0 && extra.Count == 0 && differing.Count == 0)
        {
            Console.WriteLine($"  points: MATCH  ({expected.Count} nodes, types and edges)");
            return 0;
        }

        Console.WriteLine($"  points: MISMATCH  ({expected.Count} in save, {actual.Count} in ours)");

        // Generation runs from row 1 upward, so the lowest differing row is the earliest place the
        // two diverged and everything above it is likely to be downstream noise.
        foreach (var key in Order(missing).Take(6))
            Console.WriteLine($"    only in save: {key}  {expected[key]}");
        foreach (var key in Order(extra).Take(6))
            Console.WriteLine($"    only in ours: {key}  {actual[key]}");
        foreach (var key in Order(differing).Take(6))
            Console.WriteLine($"    differs {key}: save {expected[key]}  ours {actual[key]}");

        return missing.Count + extra.Count + differing.Count;
    }

    /// <summary>Lowest row first, so the earliest divergence is what gets printed.</summary>
    private static IEnumerable<string> Order(IEnumerable<string> keys) =>
        keys.OrderBy(k => int.Parse(k.Split(',')[1])).ThenBy(k => int.Parse(k.Split(',')[0]));

    private static string Describe(string type, List<string> children) =>
        $"{type} -> [{string.Join(" ", children.OrderBy(c => c, StringComparer.Ordinal))}]";

    private static List<string> ReadChildren(JsonElement point)
    {
        var children = new List<string>();
        if (!point.TryGetProperty("children", out var list) || list.ValueKind is JsonValueKind.Null)
            return children;

        foreach (var child in list.EnumerateArray())
        {
            var (col, row) = ReadCoord(child);
            children.Add($"{col},{row}");
        }
        return children;
    }

    private static (int Col, int Row) ReadCoord(JsonElement coord) =>
        (coord.TryGetProperty("col", out var c) ? c.GetInt32() : 0,
         coord.TryGetProperty("row", out var r) ? r.GetInt32() : 0);

    private static int CompareScalar(string label, JsonElement parent, string property, int ours)
    {
        int saved = parent.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : -1;
        bool ok = saved == ours;
        Console.WriteLine($"{label}: {(ok ? "MATCH" : $"MISMATCH — save {saved}, ours {ours}")}");
        return ok ? 0 : 1;
    }
}
