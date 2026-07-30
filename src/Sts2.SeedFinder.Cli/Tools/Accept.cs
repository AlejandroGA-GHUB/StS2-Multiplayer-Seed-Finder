using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Records the installed game as the build this checkout agrees with, which silences the drift
/// banner.
///
/// It refuses while any layer is failing. "Verified" has to mean something, or the next person
/// to clone this inherits a file asserting agreement that nobody established — and the whole
/// point of the banner is that a stale build looks healthy from the outside.
///
/// It cannot check the one thing that matters most, which is that a real run agrees; no headless
/// check can, and the layer checks are blind to content being ADDED. So it says so rather than
/// implying more confidence than it has.
/// </summary>
public static class Accept
{
    /// <param name="runAlreadyVerified">
    /// Set by repair.bat once --verify-history has passed, so the reminder to go and do
    /// that is not printed to somebody who just did it.
    /// </param>
    public static int Run(bool runAlreadyVerified = false)
    {
        var install = GameInstall.Find();
        var release = GameInstall.ReadRelease(install);

        if (release.Missing)
        {
            Console.Error.WriteLine("Could not read your game, so there is nothing to record.");
            return 1;
        }

        if (release.HasMods)
        {
            Console.Error.WriteLine("A mods folder is present. Recording a modded install as verified");
            Console.Error.WriteLine("would bake your mods into what this checkout claims to predict.");
            return 1;
        }

        // Re-run the cheap layer checks rather than trusting the caller to have done it.
        Console.WriteLine("Re-checking before recording...");
        if (Doctor.Run(verbose: false) != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Not recorded: something above is still failing.");
            return 2;
        }

        var repo = FindRepoRoot();
        if (repo is null)
        {
            Console.Error.WriteLine("Run this from a source checkout: verified-build.json is a committed file.");
            return 1;
        }

        var record = new VerifiedBuild(
            release.Version ?? "unknown",
            release.AssemblyHash,
            DateTime.UtcNow.ToString("yyyy-MM-dd"));
        record.Save(VerifiedBuild.FolderIn(repo));

        Console.WriteLine();
        Console.WriteLine($"Recorded {record.Version} in "
                          + Path.Combine(VerifiedBuild.Folder, VerifiedBuild.FileName));
        Console.WriteLine("Rebuild once so the app picks it up, and the banner will go.");
        if (!runAlreadyVerified)
        {
            Console.WriteLine();
            Console.WriteLine("If you want definitive proof rather than a clean set of checks, test it against");
            Console.WriteLine("a real run: the checks here cannot see content that was ADDED, and the");
            Console.WriteLine("assembled draw chain has no headless test.");
            Console.WriteLine("    sts2seed --verify-history     runs you have already finished");
            Console.WriteLine("    sts2seed --verify             start a run in game, quit to the menu");
        }

        // Only worth raising when the baseline is actually out of step. Suggesting it routinely
        // would be worse than noise: running it re-records whatever the game does now as
        // matching our code, which is exactly how you would erase the detector for a change you
        // had not fixed yet.
        SuggestSnapshotIfStale();
        return 0;
    }

    /// <summary>
    /// Mentions --snapshot only when the draw-order baseline no longer lines up, and explains
    /// what it does, because the name gives no clue and the timing matters.
    /// </summary>
    private static void SuggestSnapshotIfStale()
    {
        var dll = GameInstall.AssemblyPath(GameInstall.Find());
        if (dll is null) return;

        var release = GameInstall.ReadRelease(GameInstall.Find());
        var comparison = MethodSnapshots.Compare(dll, release.Version ?? "unknown", MethodSnapshots.Load());
        if (comparison.Incomparable is null && comparison.Changed.Count == 0 && comparison.Missing.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("One thing is still out of step. This checkout keeps a record of what the game's");
        Console.WriteLine("draw-order code looked like when it was last known good, and yours no longer");
        Console.WriteLine("matches that record. Once you have reconciled the differences in our own code:");
        Console.WriteLine("    sts2seed --snapshot           re-record it, so future patches diff against yours");
        Console.WriteLine("Do not run that before reconciling. It would record the game's current behaviour");
        Console.WriteLine("as matching ours, and you would lose the ability to detect the very change.");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Sts2.SeedFinder.Core")))
                return dir.FullName;
        return null;
    }
}
