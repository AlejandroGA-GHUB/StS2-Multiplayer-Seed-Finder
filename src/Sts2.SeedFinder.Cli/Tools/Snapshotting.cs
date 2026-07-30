using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Records the current decompiled shape of every mirrored method, so future patches can be
/// diffed against it.
///
/// Deliberately a separate command from <c>--refresh</c>. Re-baselining says "this game build is
/// the one we agree with", which is only true after the run-based checks have passed. Folding it
/// into a repair step would let a user record agreement they never established.
/// </summary>
public static class Snapshotting
{
    /// <param name="checkOnly">
    /// Report whether the baseline is current and write nothing. Exists so repair.bat can ask
    /// the question at the moment it is actually relevant, rather than printing a line about
    /// snapshots that most people would have no way to interpret.
    /// </param>
    public static int Run(bool checkOnly = false)
    {
        var install = GameInstall.Find();
        var dll = GameInstall.AssemblyPath(install);
        if (dll is null)
        {
            Console.Error.WriteLine("Could not find your game's sts2.dll.");
            return 1;
        }

        var release = GameInstall.ReadRelease(install);
        var repo = FindRepoRoot();
        if (repo is null)
        {
            Console.Error.WriteLine("Run this from a source checkout: the baseline is a committed file.");
            return 1;
        }

        if (checkOnly)
        {
            var comparison = MethodSnapshots.Compare(dll, release.Version ?? "unknown",
                MethodSnapshots.Load());
            bool current = comparison.Incomparable is null
                           && comparison.Changed.Count == 0 && comparison.Missing.Count == 0;
            // 0 means the baseline matches this game; 2 means it does not and re-recording
            // would be a real decision rather than a no-op.
            return current ? 0 : 2;
        }

        Console.WriteLine($"Reading {dll}");
        var file = MethodSnapshots.Take(dll, release.Version ?? "unknown");
        var path = Path.Combine(VerifiedBuild.FolderIn(repo), MethodSnapshots.FileName);
        MethodSnapshots.Save(file, path);

        int missing = file.Methods.Values.Count(m => m.Hash == "MISSING");
        Console.WriteLine($"Recorded {file.Methods.Count} methods from {file.GameVersion} -> "
                          + Path.Combine(VerifiedBuild.Folder, MethodSnapshots.FileName));
        if (missing > 0)
        {
            Console.WriteLine($"WARNING: {missing} mirrored method(s) were not found in this build:");
            foreach (var m in file.Methods.Values.Where(m => m.Hash == "MISSING"))
                Console.WriteLine($"    {m.Key}");
            Console.WriteLine("Either they were renamed, or MirrorMap is out of date. Fix before relying on this.");
        }
        return 0;
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
