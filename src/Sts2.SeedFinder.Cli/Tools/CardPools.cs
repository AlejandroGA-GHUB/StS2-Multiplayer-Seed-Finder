using System.Collections;
using System.Text;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Regenerates <c>Core/Cards/CardPoolData.cs</c>: each character's pool in declaration order,
/// with rarity and multiplayer constraint.
///
/// Order is load-bearing. A card reward filters the pool to one rarity and then indexes into
/// what remains with <c>NextItem</c>, so a single misplaced entry changes every card predicted
/// after it.
/// </summary>
public static class CardPools
{
    public static Generated Generate(GameModels game, string gameVersion)
    {
        var characters = Enum.GetNames<Character>();

        var poolTypes = game.SubtypesOf("MegaCrit.Sts2.Core.Models.CardPoolModel")
            .Where(t => t.Name.EndsWith("CardPool", StringComparison.Ordinal))
            .ToDictionary(t => t.Name[..^"CardPool".Length], t => t, StringComparer.Ordinal);

        foreach (var name in characters)
            if (!poolTypes.ContainsKey(name))
                throw new StructuralChangeException(
                    $"this build has no {name}CardPool, so the Character enum no longer matches the game");

        var unlockAll = game.UnlockAll;

        List<(string Name, string Rarity, string Mode)> Read(string key)
        {
            var pool = game.CallGenericStatic("CardPool", poolTypes[key]);
            // GetUnlockedCards(unlockState, constraint) — pass the widest constraint so the
            // table holds everything and OUR filtering decides what a run can see.
            var method = pool.GetType().GetMethods()
                .First(m => m.Name == "GetUnlockedCards" && m.GetParameters().Length == 2);
            var constraintType = method.GetParameters()[1].ParameterType;
            var none = Enum.Parse(constraintType, "None");

            return ((IEnumerable)method.Invoke(pool, [unlockAll, none])!)
                .Cast<object>()
                .Select(c => (
                    c.GetType().Name,
                    GameModels.Str(c, "Rarity"),
                    GameModels.Str(c, "MultiplayerConstraint")))
                .ToList();
        }

        var pools = characters.Select(c => (Key: c, Cards: Read(c))).ToList();

        // Rarities and constraints must be nameable by our enums, or the emitted file will not
        // compile — better to say which value is new than to write something broken.
        foreach (var (key, cards) in pools)
        {
            foreach (var (name, rarity, mode) in cards)
            {
                if (!Enum.TryParse<CardRarity>(rarity, out _))
                    throw new StructuralChangeException(
                        $"{key} card {name} has rarity '{rarity}', which CardRarity does not have");
                if (!Enum.TryParse<CardMode>(mode, out _))
                    throw new StructuralChangeException(
                        $"{key} card {name} has multiplayer constraint '{mode}', which CardMode does not have");
            }
        }

        var gates = new Dictionary<string, List<(string Epoch, List<string> Cards)>>(StringComparer.Ordinal);
        foreach (var (key, baseline) in pools)
        {
            var rows = new List<(string, List<string>)>();
            var have = baseline.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var epochId in game.AllEpochIds)
            {
                var pool = game.CallGenericStatic("CardPool", poolTypes[key]);
                var method = pool.GetType().GetMethods()
                    .First(m => m.Name == "GetUnlockedCards" && m.GetParameters().Length == 2);
                var none = Enum.Parse(method.GetParameters()[1].ParameterType, "None");

                var without = ((IEnumerable)method.Invoke(pool, [game.UnlockAllExcept(epochId), none])!)
                    .Cast<object>().Select(c => c.GetType().Name).ToHashSet(StringComparer.Ordinal);

                var removed = have.Where(n => !without.Contains(n)).ToList();
                if (removed.Count > 0) rows.Add((EpochTypeName(epochId), removed));
            }
            if (rows.Count > 0) gates[key] = rows;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, cards) in pools) counts[key] = cards.Count;
        counts["EpochGates"] = gates.Values.Sum(v => v.Count);

        var distinct = pools.SelectMany(p => p.Cards.Select(c => c.Name))
            .ToHashSet(StringComparer.Ordinal).Count;

        return new Generated(
            Path.Combine("Cards", "CardPoolData.cs"),
            Emit(pools, gates, characters, gameVersion, counts),
            $"Cards/CardPoolData.cs   ({distinct} distinct cards across {pools.Count} pools)",
            counts);
    }

    private static string EpochTypeName(string epochId)
    {
        var sb = new StringBuilder();
        foreach (var part in epochId.Split('_', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part[1..].ToLowerInvariant());
        return sb.ToString();
    }

    private static string Emit(
        List<(string Key, List<(string Name, string Rarity, string Mode)> Cards)> pools,
        Dictionary<string, List<(string Epoch, List<string> Cards)>> gates,
        string[] characters,
        string gameVersion,
        IReadOnlyDictionary<string, int> counts)
    {
        var sb = new StringBuilder();
        sb.Append(Generated.Header(
            "Each character's card pool, in CardPoolModel.GenerateAllCards() order.",
            gameVersion, counts));
        sb.AppendLine();
        sb.AppendLine("using Sts2.SeedFinder.Core.Acts;");
        sb.AppendLine();
        sb.AppendLine("namespace Sts2.SeedFinder.Core.Cards;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>How a card is gated by run mode. Mirrors CardMultiplayerConstraint.</summary>");
        sb.AppendLine("public enum CardMode { None, MultiplayerOnly, SingleplayerOnly }");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Card rarity, in the game's enum order. The order matters: when a rolled rarity has no");
        sb.AppendLine("/// cards left, CardFactory walks to the next highest with wrapping.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public enum CardRarity { None, Basic, Common, Uncommon, Rare, Ancient, Event, Token, Status, Curse, Quest }");
        sb.AppendLine();
        sb.AppendLine("/// <summary>One entry of a character's card pool.</summary>");
        sb.AppendLine("/// <param name=\"TypeName\">The game's class name, e.g. <c>StrikeIronclad</c>.</param>");
        sb.AppendLine("/// <param name=\"Rarity\">Constructor-declared rarity.</param>");
        sb.AppendLine("/// <param name=\"Mode\">Whether the card is restricted to one run mode.</param>");
        sb.AppendLine("public sealed record CardEntry(string TypeName, CardRarity Rarity, CardMode Mode);");
        sb.AppendLine();
        sb.AppendLine("public static class CardPoolData");
        sb.AppendLine("{");

        foreach (var (key, cards) in pools)
        {
            var byRarity = cards.GroupBy(c => c.Rarity)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Count()}");
            sb.AppendLine($"    // {key}: {cards.Count} cards -> {string.Join(", ", byRarity)}");
            sb.AppendLine($"    public static readonly CardEntry[] {key} =");
            sb.AppendLine("    [");
            foreach (var (name, rarity, mode) in cards)
                sb.AppendLine($"        new(\"{name}\", CardRarity.{rarity}, CardMode.{mode}),");
            sb.AppendLine("    ];");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>Cards removed from a pool when the named epoch is NOT revealed.</summary>");
        sb.AppendLine("    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> EpochGates =");
        sb.AppendLine("        new Dictionary<string, IReadOnlyDictionary<string, string[]>>");
        sb.AppendLine("        {");
        foreach (var (key, rows) in gates)
        {
            sb.AppendLine($"            [\"{key}\"] = new Dictionary<string, string[]>");
            sb.AppendLine("            {");
            foreach (var (epoch, names) in rows)
                sb.AppendLine($"                [\"{epoch}\"] = [{string.Join(", ", names.Select(n => $"\"{n}\""))}],");
            sb.AppendLine("            },");
        }
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("    public static CardEntry[] For(Character c) => c switch");
        sb.AppendLine("    {");
        foreach (var c in characters) sb.AppendLine($"        Character.{c} => {c},");
        sb.AppendLine($"        _ => {characters[0]},");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
