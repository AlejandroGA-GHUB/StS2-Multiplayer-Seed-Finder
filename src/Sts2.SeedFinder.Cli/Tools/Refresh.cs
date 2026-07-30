using System.Text;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Regenerates the data tables from the installed game, so a patch that only moves content
/// around can be repaired with one command and no code.
///
/// See <see cref="GameModels"/> for why this reads the game by running it rather than by
/// parsing decompiled source.
/// </summary>
public static class Refresh
{
    /// <summary>
    /// A table that lost most of its rows is far more likely to be an extraction failure than a
    /// real patch, and writing it would replace working data with plausible-looking rubbish.
    /// Below this fraction of the previous count, refuse unless forced.
    /// </summary>
    private const double CollapseFloor = 0.5;

    public static int Run(string? gameDirArg, bool force, bool dryRun)
    {
        var install = gameDirArg ?? GameInstall.Find();
        var dll = GameInstall.AssemblyPath(install);
        if (dll is null)
        {
            Console.Error.WriteLine("Could not find your game's sts2.dll.");
            Console.Error.WriteLine("Pass the install folder:  sts2seed --refresh \"<path to Slay the Spire 2>\"");
            return 1;
        }

        var release = GameInstall.ReadRelease(install);
        var verified = VerifiedBuild.Load();
        Console.WriteLine($"Reading {dll}");
        Console.WriteLine($"Game {release.Version ?? "(unknown version)"}, "
                          + $"this checkout was built against {verified.Version}");
        if (release.HasMods)
            Console.WriteLine("WARNING: a mods folder is present. Modded content will be baked into "
                              + "the tables, and predictions will then only match a modded game.");
        Console.WriteLine();

        var source = FindCoreSourceDir();
        if (source is null)
        {
            Console.Error.WriteLine("Could not find src/Sts2.SeedFinder.Core next to this build.");
            Console.Error.WriteLine("Run --refresh from a source checkout rather than a copied exe.");
            return 1;
        }

        GameModels game;
        try
        {
            game = GameModels.Load(dll);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load the game assembly: {(ex.InnerException ?? ex).Message}");
            return 1;
        }
        Console.WriteLine($"Loaded {game.InjectedCount} models.\n");

        // Regenerating from a game whose hashing or RNG we no longer match would overwrite tables
        // that are correct for the build the user actually plays, with tables that cannot help
        // because nothing downstream can predict that game either. Refuse, and say what to do.
        var primitives = Primitives.Check(game);
        if (!primitives.Ok)
        {
            Console.Error.WriteLine("Not regenerating: this game's core functions do not match ours.");
            foreach (var problem in primitives.Problems) Console.Error.WriteLine("  " + problem);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Rewriting the tables cannot help while that is true, and would replace");
            Console.Error.WriteLine("tables that are correct for the build you play. Nothing was written.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("This needs a code change. See docs/PATCH_RECOVERY.md, and:");
            Console.Error.WriteLine("    sts2seed --show StringHelper.GetDeterministicHashCode");
            Console.Error.WriteLine("    sts2seed --show Rng.NextInt");
            return 3;
        }

        var outputs = new List<Generated>();
        try
        {
            outputs.Add(RelicPools.Generate(game, release.Version ?? "unknown"));
            outputs.Add(CardPools.Generate(game, release.Version ?? "unknown"));
            outputs.Add(ActTables.Generate(game, release.Version ?? "unknown"));
        }
        catch (StructuralChangeException ex)
        {
            // The generator's own assumptions no longer fit the game. Emitting the old shape
            // would produce tables that look fine and are wrong, which is the one outcome worth
            // engineering against.
            Console.Error.WriteLine("This patch changed something the generator assumes:");
            Console.Error.WriteLine("  " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine("That needs a code change, not a regeneration. Nothing was written.");
            Console.Error.WriteLine("See docs/PATCH_RECOVERY.md.");
            return 3;
        }

        int refused = 0, written = 0;
        foreach (var output in outputs)
        {
            var path = Path.Combine(source, output.RelativePath);
            var before = File.Exists(path) ? File.ReadAllText(path) : null;

            Console.WriteLine(output.Summary);
            foreach (var line in output.Diff(before)) Console.WriteLine("    " + line);

            if (!force && before is not null && output.Collapsed(before, CollapseFloor))
            {
                Console.WriteLine("    REFUSED: that is a drastic loss of rows, which usually means "
                                  + "extraction failed rather than the game changing.");
                Console.WriteLine("    Re-run with --force if you are sure.");
                refused++;
                continue;
            }

            if (dryRun) { Console.WriteLine("    (dry run, not written)"); continue; }

            File.WriteAllText(path, output.Text);
            written++;
        }

        Console.WriteLine();

        // Reported before any early exit: a dry run should tell you everything it found, and a
        // draw-order change is the finding a user most needs, since no amount of regenerating
        // will address it.
        ReportMethodChanges(dll, release.Version ?? "unknown");

        if (refused > 0)
        {
            Console.WriteLine($"{refused} table(s) refused. Nothing else changed.");
            return 2;
        }
        if (dryRun)
        {
            Console.WriteLine("Dry run: no files written.");
            return 0;
        }

        Console.WriteLine($"Wrote {written} file(s). Rebuild, then run:  sts2seed --verify-history");
        Console.WriteLine();
        Console.WriteLine("Two things this did NOT do, so you know what is left:");
        Console.WriteLine("  * Core/Ancients/AncientData.cs. Those pools are named to match the draw algorithms");
        Console.WriteLine("    beside them in AncientOffers.cs, so regenerating them mechanically risks emitting");
        Console.WriteLine("    names nothing compiles against. An Ancient that changed needs both read together.");
        Console.WriteLine("  * The DRAW ORDER, which lives in hand-written code and cannot be regenerated at all.");
        Console.WriteLine("    --verify-history against a real run is what tells you whether that changed.");
        return 0;
    }

    /// <summary>
    /// Names the mirrored game methods whose code changed since the recorded baseline.
    ///
    /// Regenerating tables cannot touch these: they are behaviour we re-expressed in our own
    /// code. Naming them is the whole of what a tool can do, and it is the difference between
    /// reading one method and reading the assembly.
    /// </summary>
    private static void ReportMethodChanges(string dll, string gameVersion)
    {
        var comparison = MethodSnapshots.Compare(dll, gameVersion, MethodSnapshots.Load());

        if (comparison.Incomparable is not null)
        {
            Console.WriteLine($"Draw order: not checked ({comparison.Incomparable}).");
            Console.WriteLine();
            return;
        }

        if (comparison.Changed.Count == 0 && comparison.Missing.Count == 0)
        {
            Console.WriteLine($"Draw order: all {MirrorMap.All.Length} mirrored methods unchanged.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("Draw order: THESE GAME METHODS CHANGED, and regenerating cannot fix them.");
        foreach (var m in comparison.Missing)
            Console.WriteLine($"    GONE     {MirrorMap.Short(m.GameType)}.{m.Method}  ->  {m.OurFile}");
        foreach (var m in comparison.Changed)
        {
            Console.WriteLine($"    CHANGED  {MirrorMap.Short(m.GameType)}.{m.Method}  ->  {m.OurFile}");
            Console.WriteLine($"             decides {m.Decides}");
        }
        Console.WriteLine();
        Console.WriteLine("    Read each with:  sts2seed --show <Type.Method>");
        Console.WriteLine();
    }

    /// <summary>
    /// The Core project's source directory, found by walking up from the running exe. Refresh
    /// writes C# that has to be compiled, so it only makes sense inside a checkout.
    /// </summary>
    private static string? FindCoreSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Sts2.SeedFinder.Core");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// Raised when the game no longer fits the shape the generator can express — a sixth character,
/// a fourth act, a new grab-bag rarity. These need the enum and the generator edited, so the
/// only safe action is to write nothing and say so.
/// </summary>
public sealed class StructuralChangeException(string message) : Exception(message);

/// <summary>One generated file, plus enough context to review it before it lands.</summary>
/// <param name="Counts">Named row counts, for the before/after diff.</param>
public sealed record Generated(
    string RelativePath, string Text, string Summary, IReadOnlyDictionary<string, int> Counts)
{
    /// <summary>
    /// Human-readable before/after. This is the review step: the tables come from the game
    /// rather than a regex now, so this is a sanity check rather than a safety net, but a
    /// silent rewrite of the file everything depends on is not something to offer.
    /// </summary>
    public IEnumerable<string> Diff(string? before)
    {
        var old = before is null ? null : ParseCounts(before);
        foreach (var (name, now) in Counts)
        {
            if (old is null) { yield return $"{name,-22} {now}"; continue; }
            var was = old.GetValueOrDefault(name, -1);
            yield return was == now
                ? $"{name,-22} {now}   unchanged"
                : was < 0 ? $"{name,-22} {now}   new"
                : $"{name,-22} {was} -> {now}";
        }
    }

    /// <summary>
    /// Whether the file on disk already holds this data.
    ///
    /// The header is excluded because it stamps the game version, which changes on a patch that
    /// altered nothing we read — comparing it would report every version bump as stale data.
    /// </summary>
    public bool MatchesFileBody(string existing) =>
        Body(existing).Equals(Body(Text), StringComparison.Ordinal);

    private static string Body(string file)
    {
        const string end = "// </auto-generated>";
        int at = file.IndexOf(end, StringComparison.Ordinal);
        var body = at < 0 ? file : file[(at + end.Length)..];
        return string.Join('\n', body.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
    }

    public bool Collapsed(string before, double floor)
    {
        var old = ParseCounts(before);
        return Counts.Any(c => old.TryGetValue(c.Key, out var was) && was > 4 && c.Value < was * floor);
    }

    /// <summary>
    /// Counts are recovered from the previous file's own header comment rather than by
    /// re-parsing its C#, so the diff never depends on the thing it is checking.
    /// </summary>
    private static Dictionary<string, int> ParseCounts(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("//   COUNT ", StringComparison.Ordinal)) continue;
            var parts = t["//   COUNT ".Length..].Split('=', 2);
            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var n))
                counts[parts[0].Trim()] = n;
        }
        return counts;
    }

    /// <summary>The header every generated file carries, including the counts the diff reads back.</summary>
    public static string Header(string what, string gameVersion, IReadOnlyDictionary<string, int> counts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"//   {what}");
        sb.AppendLine($"//   Read from Slay the Spire {gameVersion} by `sts2seed --refresh`, which asks the");
        sb.AppendLine("//   game's own model database rather than parsing its source. Do not hand-edit:");
        sb.AppendLine("//   order is load-bearing, and the next refresh will overwrite you.");
        sb.AppendLine("//");
        foreach (var (name, n) in counts) sb.AppendLine($"//   COUNT {name} = {n}");
        sb.AppendLine("// </auto-generated>");
        // Roslyn exempts files carrying an <auto-generated> header from the project's nullable
        // setting, so a `?` annotation in one warns (CS8669) unless the context is restated.
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        return sb.ToString();
    }
}
