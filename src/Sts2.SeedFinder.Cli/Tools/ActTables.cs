using System.Collections;
using System.Text;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Regenerates <c>Core/Acts/ActData.cs</c>: each act's encounters (with their tags, which drive
/// the no-repeat retry loop), events, Ancients, room counts, and the shared pools appended to
/// every act.
///
/// Order is load-bearing throughout. Encounters go into a grab bag in declaration order and the
/// bag is indexed by weight, so a single moved entry changes the whole encounter sequence, which
/// then changes where every later draw lands.
/// </summary>
public static class ActTables
{
    /// <summary>
    /// Acts that exist as types but are not real content. <c>DeprecatedAct</c> is a tombstone for
    /// removed acts and must not enter the tables; it is excluded by <c>ModelDb.Acts</c> too, and
    /// checked here so a rename cannot silently let it through.
    /// </summary>
    private static readonly string[] NotContent = ["DeprecatedAct"];

    public static Generated Generate(GameModels game, string gameVersion)
    {
        var acts = ((IEnumerable)game.StaticProperty("MegaCrit.Sts2.Core.Models.ModelDb", "Acts"))
            .Cast<object>()
            .Where(a => !NotContent.Contains(a.GetType().Name))
            .OrderBy(a => GameModels.Int(a, "Index"))
            .ToList();

        if (acts.Count == 0)
            throw new StructuralChangeException("ModelDb.Acts is empty; the act model has been reshaped");

        // Every tag any encounter carries, so the emitted enum covers the game rather than our
        // memory of it. A new tag is absorbed silently; a removed one simply stops appearing.
        var tags = acts
            .SelectMany(a => GameModels.Many(a, "AllEncounters"))
            .SelectMany(e => GameModels.Many(e, "Tags").Select(t => t.ToString()!))
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var sharedEvents = GameModels.Names(
            game.StaticProperty("MegaCrit.Sts2.Core.Models.ModelDb", "AllSharedEvents") is var se
                ? new Wrapper(se) : throw new StructuralChangeException("no AllSharedEvents"),
            "Value").ToList();

        var sharedAncients = GameModels.Names(
            new Wrapper(game.StaticProperty("MegaCrit.Sts2.Core.Models.ModelDb", "AllSharedAncients")),
            "Value").ToList();

        var eventGates = ReadEventGates(game);
        var ancientGates = acts.ToDictionary(
            a => a.GetType().Name,
            a => ReadAncientGates(game, a),
            StringComparer.Ordinal);

        var sharedAncientEpoch = FindSharedAncientEpoch(game, sharedAncients);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in acts)
            counts[a.GetType().Name] = GameModels.Many(a, "AllEncounters").Count();
        counts["SharedEvents"] = sharedEvents.Count;
        counts["EncounterTags"] = tags.Count;

        var text = Emit(acts, tags, sharedEvents, sharedAncients, sharedAncientEpoch,
                        eventGates, ancientGates, gameVersion, counts);

        return new Generated(
            Path.Combine("Acts", "ActData.cs"), text,
            $"Acts/ActData.cs         ({acts.Count} acts, {tags.Count} encounter tags)",
            counts);
    }

    /// <summary>Lets the shared helpers read a bare collection as if it were a model member.</summary>
    private sealed class Wrapper(object value)
    {
        public object Value { get; } = value;
    }

    /// <summary>
    /// Events removed when an epoch is locked, read from the epoch types' own <c>Events</c> lists.
    /// <c>ActModel.GenerateRooms</c> filters with exactly these, so reading them is reading the
    /// rule rather than inferring it.
    /// </summary>
    private static Dictionary<string, string> ReadEventGates(GameModels game)
    {
        var gates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var epoch in game.SubtypesOf("MegaCrit.Sts2.Core.Timeline.EpochModel"))
        {
            var prop = epoch.GetProperty("Events",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop is null) continue;

            foreach (var e in ((IEnumerable)prop.GetValue(null)!).Cast<object>())
                gates[e.GetType().Name] = epoch.Name;
        }
        return gates;
    }

    /// <summary>
    /// Which epoch gates each of an act's Ancients, found by asking the act what it offers with
    /// one epoch withheld. Same trick as the relic pools, and for the same reason: the game owns
    /// the rule.
    /// </summary>
    private static Dictionary<string, string> ReadAncientGates(GameModels game, object act)
    {
        var method = act.GetType().GetMethod("GetUnlockedAncients")
            ?? throw new StructuralChangeException("ActModel.GetUnlockedAncients is gone");

        List<string> With(object unlockState) =>
            ((IEnumerable)method.Invoke(act, [unlockState])!)
                .Cast<object>().Select(a => a.GetType().Name).ToList();

        var baseline = With(game.UnlockAll);
        var gates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var epochId in game.AllEpochIds)
        {
            var without = With(game.UnlockAllExcept(epochId)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in baseline.Where(n => !without.Contains(n)))
                gates[name] = EpochTypeName(epochId);
        }
        return gates;
    }

    /// <summary>The epoch gating the shared Ancient, via <c>UnlockState.SharedAncients</c>.</summary>
    private static string FindSharedAncientEpoch(GameModels game, List<string> sharedAncients)
    {
        var prop = game.TypeNamed("MegaCrit.Sts2.Core.Unlocks.UnlockState").GetProperty("SharedAncients")!;

        foreach (var epochId in game.AllEpochIds)
        {
            var without = ((IEnumerable)prop.GetValue(game.UnlockAllExcept(epochId))!)
                .Cast<object>().Select(a => a.GetType().Name).ToHashSet(StringComparer.Ordinal);
            if (sharedAncients.Any(a => !without.Contains(a))) return EpochTypeName(epochId);
        }
        throw new StructuralChangeException(
            "no epoch gates the shared Ancient any more; ActData.SharedAncientEpoch has no answer");
    }

    private static string EpochTypeName(string epochId)
    {
        var sb = new StringBuilder();
        foreach (var part in epochId.Split('_', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part[1..].ToLowerInvariant());
        return sb.ToString();
    }

    private static string Emit(
        List<object> acts,
        List<string> tags,
        List<string> sharedEvents,
        List<string> sharedAncients,
        string sharedAncientEpoch,
        Dictionary<string, string> eventGates,
        Dictionary<string, Dictionary<string, string>> ancientGates,
        string gameVersion,
        IReadOnlyDictionary<string, int> counts)
    {
        var sb = new StringBuilder();
        sb.Append(Generated.Header(
            "Per-act generation tables: encounter pools, event pools, room counts, Ancients.",
            gameVersion, counts));
        sb.AppendLine();
        sb.AppendLine("namespace Sts2.SeedFinder.Core.Acts;");
        sb.AppendLine();
        sb.AppendLine("public enum RoomType { Monster, Elite, Boss, Event }");
        sb.AppendLine();
        sb.AppendLine("public enum EncounterTag");
        sb.AppendLine("{");
        foreach (var t in tags) sb.AppendLine($"    {t},");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("public sealed record Encounter(string Name, RoomType RoomType, bool IsWeak, EncounterTag[] Tags)");
        sb.AppendLine("{");
        sb.AppendLine("    public bool SharesTagsWith(Encounter? other) => other is not null && Tags.Intersect(other.Tags).Any();");
        sb.AppendLine("    public override string ToString() => Name;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("public sealed record ActDefinition(");
        sb.AppendLine("    string Name, int Index, int BaseNumberOfRooms, int NumberOfWeakEncounters,");
        sb.AppendLine("    Encounter[] Encounters, string[] Events, string[] Ancients,");
        sb.AppendLine("    IReadOnlyDictionary<string, string> AncientEpochGates)");
        sb.AppendLine("{");
        sb.AppendLine("    public IEnumerable<Encounter> Weak => Encounters.Where(e => e.RoomType == RoomType.Monster && e.IsWeak);");
        sb.AppendLine("    public IEnumerable<Encounter> Regular => Encounters.Where(e => e.RoomType == RoomType.Monster && !e.IsWeak);");
        sb.AppendLine("    public IEnumerable<Encounter> Elites => Encounters.Where(e => e.RoomType == RoomType.Elite);");
        sb.AppendLine("    public IEnumerable<Encounter> Bosses => Encounters.Where(e => e.RoomType == RoomType.Boss);");
        sb.AppendLine("    public int GetNumberOfRooms(bool isMultiplayer) => BaseNumberOfRooms - (isMultiplayer ? 1 : 0);");
        sb.AppendLine("    public int GetNumberOfFloors(bool isMultiplayer) => GetNumberOfRooms(isMultiplayer) + 2;");
        sb.AppendLine("    public override string ToString() => Name;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("public static class ActData");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>ModelDb.AllSharedEvents — appended to each act's own events before shuffling.</summary>");
        sb.AppendLine($"    public static readonly string[] SharedEvents = {{ {Join(sharedEvents)} }};");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Events removed unless their epoch is revealed.</summary>");
        sb.AppendLine("    public static readonly IReadOnlyDictionary<string, string> EventEpochGates =");
        sb.AppendLine("        new Dictionary<string, string> { "
                      + string.Join(", ", eventGates.OrderBy(k => k.Value, StringComparer.Ordinal)
                          .Select(k => $"[\"{k.Key}\"] = \"{k.Value}\"")) + " };");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Shared Ancients, distributed across acts 2+ before per-act generation.</summary>");
        sb.AppendLine($"    public static readonly string[] SharedAncients = {{ {Join(sharedAncients)} }};");
        sb.AppendLine($"    public const string SharedAncientEpoch = \"{sharedAncientEpoch}\";");
        sb.AppendLine();

        foreach (var act in acts)
        {
            var name = act.GetType().Name;
            var encounters = GameModels.Many(act, "AllEncounters").ToList();
            var gates = ancientGates[name];

            sb.AppendLine($"    public static readonly ActDefinition {name} = new(");
            sb.AppendLine($"        \"{name}\", {GameModels.Int(act, "Index")}, "
                          + $"{GameModels.Int(act, "BaseNumberOfRooms")}, "
                          + $"{GameModels.Int(act, "NumberOfWeakEncounters")},");
            sb.AppendLine("        Encounters: new Encounter[]");
            sb.AppendLine("        {");
            foreach (var e in encounters)
            {
                var encTags = GameModels.Many(e, "Tags").Select(t => t.ToString()!).ToList();
                var tagText = encTags.Count == 0
                    ? "Array.Empty<EncounterTag>()"
                    : "new[] { " + string.Join(", ", encTags.Select(t => "EncounterTag." + t)) + " }";
                sb.AppendLine($"            new(\"{e.GetType().Name}\", RoomType.{GameModels.Str(e, "RoomType")}, "
                              + $"{GameModels.Str(e, "IsWeak").ToLowerInvariant()}, {tagText}),");
            }
            sb.AppendLine("        },");
            sb.AppendLine($"        Events: new[] {{ {Join(GameModels.Names(act, "AllEvents"))} }},");
            sb.AppendLine($"        Ancients: new[] {{ {Join(GameModels.Names(act, "AllAncients"))} }},");
            sb.AppendLine("        AncientEpochGates: new Dictionary<string, string> { "
                          + string.Join(", ", gates.Select(g => $"[\"{g.Key}\"] = \"{g.Value}\"")) + " });");
            sb.AppendLine();
        }

        sb.AppendLine("    public static readonly ActDefinition[][] ByIndex =");
        sb.AppendLine("    {");
        foreach (var group in acts.GroupBy(a => GameModels.Int(a, "Index")).OrderBy(g => g.Key))
            sb.AppendLine($"        new[] {{ {string.Join(", ", group.Select(a => a.GetType().Name))} }},");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine($"    public static readonly ActDefinition[] All = "
                      + $"{{ {string.Join(", ", acts.Select(a => a.GetType().Name))} }};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Join(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => $"\"{n}\""));
}
