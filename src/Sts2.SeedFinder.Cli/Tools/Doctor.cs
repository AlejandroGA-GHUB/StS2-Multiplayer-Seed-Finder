using System.Reflection;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Diagnoses whether this checkout still predicts your game correctly, by LAYER, and says what
/// to do about it.
///
/// The layering is the point. A patch breaks different things in different ways, the repairs are
/// wildly different in cost, and — this is the part users cannot work out for themselves —
/// some predictions survive while others do not. Neow's offer and the Act 1 map run on their own
/// RNG streams over hardcoded lists, so they keep working through almost any content patch.
/// Telling somebody that is far more useful than a blanket "possibly stale".
///
/// It also always names the next command. That is the difference between documentation and a
/// runbook.
/// </summary>
public static class Doctor
{
    private enum Status { Ok, Fail, Skipped }

    private sealed record Layer(string Name, Status Status, string Detail);

    public static int Run(bool verbose)
    {
        var install = GameInstall.Find();
        var dll = GameInstall.AssemblyPath(install);
        var release = GameInstall.ReadRelease(install);
        var verified = VerifiedBuild.Load();
        var drift = DriftReport.For(release, verified);

        Console.WriteLine();
        Console.WriteLine($"  Your game       {release.Version ?? "(not found)"}");
        Console.WriteLine($"  Verified against {verified.Version}"
                          + (verified.VerifiedOn is null ? "" : $"  ({verified.VerifiedOn})"));
        if (release.HasMods)
            Console.WriteLine("  Mods            present  <- changes pool sizes, so predictions will not match");
        Console.WriteLine();

        if (dll is null)
        {
            Console.WriteLine("  Could not find your game, so nothing can be checked.");
            Console.WriteLine("  Set Assets__GameDirectory to your install folder, or pass it to --refresh.");
            return 1;
        }

        var layers = new List<Layer>();
        GameModels? game = null;
        try
        {
            game = GameModels.Load(dll);
        }
        catch (Exception ex)
        {
            layers.Add(new Layer("primitives (RNG, hashing)", Status.Fail,
                $"could not load the game assembly: {(ex.InnerException ?? ex).Message}"));
        }

        if (game is not null)
        {
            layers.Add(CheckPrimitives(game));
            // Only meaningful if the primitives agree: a broken hash makes every table look
            // wrong for a reason that has nothing to do with the tables.
            layers.Add(layers[^1].Status == Status.Ok
                ? CheckDataTables(game, release.Version ?? "unknown")
                : new Layer("data tables (pools, acts)", Status.Skipped, "not reached"));
            layers.Add(CheckDrawOrder(dll, release.Version ?? "unknown"));
        }

        foreach (var layer in layers)
            Console.WriteLine($"  {layer.Name,-30} {Label(layer.Status),-8} {layer.Detail}");
        Console.WriteLine();

        var dataBroken = layers.Any(l => l.Name.StartsWith("data") && l.Status == Status.Fail);
        var codeBroken = layers.Any(l =>
            (l.Name.StartsWith("primitives") || l.Name.StartsWith("draw")) && l.Status == Status.Fail);

        if (!dataBroken && !codeBroken)
        {
            Console.WriteLine("  Nothing looks broken.");
            Console.WriteLine();
            Console.WriteLine("  Proof needs a real run, because the assembled draw chain cannot be checked");
            Console.WriteLine("  without one, and the checks above are blind to CONTENT BEING ADDED.");
            Console.WriteLine("      sts2seed --verify-history      runs you have already finished");
            Console.WriteLine("      sts2seed --verify              a run in progress (start one, quit to menu)");
            return 0;
        }

        Console.WriteLine("  Still correct regardless: Neow's offer and the Act 1 map.");
        Console.WriteLine("  Those run on their own RNG streams over fixed lists, so a content patch");
        Console.WriteLine("  does not move them.");
        Console.WriteLine();
        Console.WriteLine($"  Fixable by command:  {(dataBroken ? "yes" : "no")}");
        Console.WriteLine($"  Needs a code edit:   {(codeBroken ? "yes" : "no")}");
        Console.WriteLine();

        if (dataBroken)
        {
            Console.WriteLine("      sts2seed --refresh        rewrite the tables from your game");
            Console.WriteLine("      then rebuild, and run this again");
        }
        if (codeBroken)
        {
            Console.WriteLine("      sts2seed --show <method>  read the game's version beside ours");
            Console.WriteLine("      see docs/PATCH_RECOVERY.md");
        }
        return 2;
    }

    private static string Label(Status s) => s switch
    {
        Status.Ok => "OK",
        Status.Fail => "FAIL",
        _ => "skipped",
    };

    private static Layer CheckPrimitives(GameModels game)
    {
        var result = Primitives.Check(game);
        return result.Ok
            ? new Layer("primitives (RNG, hashing)", Status.Ok, "")
            : new Layer("primitives (RNG, hashing)", Status.Fail, string.Join("; ", result.Problems.Take(2)));
    }

    /// <summary>
    /// Regenerates the tables in memory and compares them with what is committed. Exact, because
    /// it is the same code that would write them.
    /// </summary>
    private static Layer CheckDataTables(GameModels game, string gameVersion)
    {
        var source = FindCoreSourceDir();
        if (source is null)
            return new Layer("data tables (pools, acts)", Status.Skipped,
                "not a source checkout, so the tables cannot be compared");

        try
        {
            var stale = new List<string>();
            foreach (var output in new[]
                     {
                         RelicPools.Generate(game, gameVersion),
                         CardPools.Generate(game, gameVersion),
                         ActTables.Generate(game, gameVersion),
                     })
            {
                var path = Path.Combine(source, output.RelativePath);
                if (!File.Exists(path) || !output.MatchesFileBody(File.ReadAllText(path)))
                    stale.Add(Path.GetFileNameWithoutExtension(output.RelativePath));
            }

            return stale.Count == 0
                ? new Layer("data tables (pools, acts)", Status.Ok, "")
                : new Layer("data tables (pools, acts)", Status.Fail,
                    $"stale: {string.Join(", ", stale)}");
        }
        catch (StructuralChangeException ex)
        {
            return new Layer("data tables (pools, acts)", Status.Fail, ex.Message);
        }
        catch (Exception ex)
        {
            return new Layer("data tables (pools, acts)", Status.Fail,
                $"{(ex.InnerException ?? ex).GetType().Name} while reading the game");
        }
    }

    private static Layer CheckDrawOrder(string dll, string gameVersion)
    {
        var comparison = MethodSnapshots.Compare(dll, gameVersion, MethodSnapshots.Load());

        if (comparison.Incomparable is not null)
            return new Layer("draw order (hand-written)", Status.Skipped, comparison.Incomparable);

        int n = comparison.Changed.Count + comparison.Missing.Count;
        if (n == 0)
            return new Layer("draw order (hand-written)", Status.Ok,
                $"{MirrorMap.All.Length} mirrored methods unchanged");

        var names = comparison.Missing.Select(m => MirrorMap.Short(m.GameType) + "." + m.Method + " (gone)")
            .Concat(comparison.Changed.Select(m => MirrorMap.Short(m.GameType) + "." + m.Method));
        return new Layer("draw order (hand-written)", Status.Fail, string.Join(", ", names));
    }

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
