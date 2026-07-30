using System.Text;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Regenerates <c>Core/Acts/RelicPoolData.cs</c>: the ordered contents of every relic pool,
/// each relic's rarity, and what each epoch gates.
///
/// Two consumers, both order-sensitive. Draw accounting, because a shuffle of n costs n-1 and
/// the counts decide where every later draw lands. And the shop's third relic slot, which is
/// literally the back of a shuffled deque.
/// </summary>
public static class RelicPools
{
    private const string RelicPoolNamespace = "MegaCrit.Sts2.Core.Models.RelicPools.";

    public static Generated Generate(GameModels game, string gameVersion)
    {
        var characters = Enum.GetNames<Character>();

        // A character the game has and our enum does not cannot be expressed by regenerating:
        // Character is a C# enum, and every table keys off it. Say so and write nothing.
        var poolTypes = game.SubtypesOf("MegaCrit.Sts2.Core.Models.RelicPoolModel")
            .Where(t => t.Name.EndsWith("RelicPool", StringComparison.Ordinal))
            .ToDictionary(t => t.Name[..^"RelicPool".Length], t => t, StringComparer.Ordinal);

        foreach (var name in characters)
            if (!poolTypes.ContainsKey(name))
                throw new StructuralChangeException(
                    $"this build has no {name}RelicPool, so the Character enum no longer matches the game");

        var gamesCharacters = poolTypes.Keys
            .Where(k => k is not ("Shared" or "Deprecated" or "Event" or "Fallback"))
            .ToList();
        var extra = gamesCharacters.Except(characters, StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
            throw new StructuralChangeException(
                $"your game has character relic pools this build does not know: {string.Join(", ", extra)}. "
                + "Add them to the Character enum and to the generator first");

        // Everything is read through the game's own GetUnlockedRelics, so a filter we have never
        // heard of applies itself.
        var unlockAll = game.UnlockAll;

        List<(string Name, string Rarity)> Read(string poolKey)
        {
            var pool = game.CallGenericStatic("RelicPool", poolTypes[poolKey]);
            var method = pool.GetType().GetMethod("GetUnlockedRelics")!;
            var relics = (System.Collections.IEnumerable)method.Invoke(pool, [unlockAll])!;
            return relics.Cast<object>()
                .Select(r => (r.GetType().Name, GameModels.Str(r, "Rarity")))
                .ToList();
        }

        var pools = new List<(string Key, List<(string Name, string Rarity)> Relics)>
        {
            ("Shared", Read("Shared")),
        };
        foreach (var c in characters) pools.Add((c, Read(c)));

        // Epoch gates, recovered by asking what a locked epoch removes rather than transcribing
        // a list. The game decides, so there is nothing here to get wrong.
        var gates = new Dictionary<string, List<(string Epoch, List<string> Relics)>>(StringComparer.Ordinal);
        foreach (var (key, baseline) in pools)
        {
            var rows = new List<(string, List<string>)>();
            var have = baseline.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var epochId in game.AllEpochIds)
            {
                var pool = game.CallGenericStatic("RelicPool", poolTypes[key]);
                var method = pool.GetType().GetMethod("GetUnlockedRelics")!;
                var without = ((System.Collections.IEnumerable)method.Invoke(pool, [game.UnlockAllExcept(epochId)])!)
                    .Cast<object>().Select(r => r.GetType().Name).ToHashSet(StringComparer.Ordinal);

                var removed = have.Where(n => !without.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (removed.Count > 0) rows.Add((EpochTypeName(epochId), removed));
            }
            if (rows.Count > 0) gates[key] = rows;
        }

        var grabBagRarities = ReadGrabBagRarities(game);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, relics) in pools) counts[key] = relics.Count;
        counts["EpochGates"] = gates.Values.Sum(v => v.Count);

        var text = Emit(pools, gates, grabBagRarities, characters, gameVersion, counts);

        var shopTotal = pools.Sum(p => p.Relics.Count(r => r.Rarity == "Shop"));
        return new Generated(
            Path.Combine("Acts", "RelicPoolData.cs"), text,
            $"Acts/RelicPoolData.cs   ({shopTotal} shop relics across {pools.Count} pools)",
            counts);
    }

    /// <summary>
    /// <c>RelicGrabBag._rarities</c> — which deques a player's bag keeps. A fifth entry would
    /// change bag sizes and therefore every draw, and cannot be absorbed by regenerating.
    /// </summary>
    private static string[] ReadGrabBagRarities(GameModels game)
    {
        var field = game.TypeNamed("MegaCrit.Sts2.Core.Runs.RelicGrabBag")
            .GetField("_rarities", System.Reflection.BindingFlags.NonPublic
                                   | System.Reflection.BindingFlags.Static)
            ?? throw new StructuralChangeException("RelicGrabBag._rarities is gone; the bag has been reshaped");

        var got = ((System.Collections.IEnumerable)field.GetValue(null)!)
            .Cast<object>().Select(o => o.ToString()!).ToArray();

        var known = RelicPoolData.GrabBagRarities;
        if (got.Length != known.Length || got.Except(known, StringComparer.Ordinal).Any())
            throw new StructuralChangeException(
                $"the relic grab bag now holds [{string.Join(", ", got)}] rather than "
                + $"[{string.Join(", ", known)}]");

        // Keep our declared order: it is the filter order, not the deque order.
        return known;
    }

    /// <summary>"IRONCLAD3_EPOCH" back to the "Ironclad3Epoch" our UnlockState asks about.</summary>
    private static string EpochTypeName(string epochId)
    {
        var sb = new StringBuilder();
        foreach (var part in epochId.Split('_', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part[1..].ToLowerInvariant());
        return sb.ToString();
    }

    private static string Emit(
        List<(string Key, List<(string Name, string Rarity)> Relics)> pools,
        Dictionary<string, List<(string Epoch, List<string> Relics)>> gates,
        string[] grabBagRarities,
        string[] characters,
        string gameVersion,
        IReadOnlyDictionary<string, int> counts)
    {
        var sb = new StringBuilder();
        sb.Append(Generated.Header(
            "The ordered contents of every relic pool, with rarities and epoch gates.",
            gameVersion, counts));
        sb.AppendLine();
        sb.AppendLine("namespace Sts2.SeedFinder.Core.Acts;");
        sb.AppendLine();
        sb.AppendLine($"public enum Character {{ {string.Join(", ", characters)} }}");
        sb.AppendLine();
        sb.AppendLine("/// <summary>One relic as it sits in a pool, with the grab-bag rarity that buckets it.</summary>");
        sb.AppendLine("public readonly record struct PoolRelic(string Name, string Slug, string Rarity);");
        sb.AppendLine();
        sb.AppendLine("public static class RelicPoolData");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Rarities a PLAYER's grab bag keeps, per RelicGrabBag._rarities. The shared");
        sb.AppendLine("    /// bag is populated through the overload that skips this filter, so it holds every rarity.</summary>");
        sb.AppendLine($"    public static readonly string[] GrabBagRarities = {{ {string.Join(", ", grabBagRarities.Select(Quote))} }};");
        sb.AppendLine();

        foreach (var (key, relics) in pools)
        {
            var byRarity = relics.GroupBy(r => r.Rarity)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Count()}");
            sb.AppendLine($"    // {key}: {relics.Count} relics -> {string.Join(", ", byRarity)}");
            sb.AppendLine($"    public static readonly PoolRelic[] {key}Relics =");
            sb.AppendLine("    {");
            foreach (var (name, rarity) in relics)
                sb.AppendLine($"        new({Quote(name)}, {Quote(GameHash.SnakeCase(name))}, {Quote(rarity)}),");
            sb.AppendLine("    };");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>Relics dropped from a pool when the named epoch is NOT revealed, by pool.");
        sb.AppendLine("    /// Removal preserves the surviving order, which is what the deques are built from.</summary>");
        sb.AppendLine("    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> EpochGates =");
        sb.AppendLine("        new Dictionary<string, IReadOnlyDictionary<string, string[]>>");
        sb.AppendLine("        {");
        foreach (var (key, rows) in gates)
        {
            sb.AppendLine($"            [{Quote(key)}] = new Dictionary<string, string[]>");
            sb.AppendLine("            {");
            foreach (var (epoch, names) in rows)
                sb.AppendLine($"                [{Quote(epoch)}] = new[] {{ {string.Join(", ", names.Select(Quote))} }},");
            sb.AppendLine("            },");
        }
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("    public static PoolRelic[] RelicsFor(Character c) => c switch");
        sb.AppendLine("    {");
        foreach (var c in characters) sb.AppendLine($"        Character.{c} => {c}Relics,");
        sb.AppendLine($"        _ => {characters[0]}Relics,");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The pool key EpochGates uses for a character.</summary>");
        sb.AppendLine("    public static string PoolKey(Character c) => c.ToString();");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s + "\"";
}
