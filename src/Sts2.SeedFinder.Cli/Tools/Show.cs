using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Prints a game method from the installed dll beside the path of the file that mirrors it.
///
/// This is the whole of the "algorithm changed" repair loop that can be automated. The edit
/// itself is a person reading two things and reconciling them; what this removes is having to
/// find them.
/// </summary>
public static class Show
{
    public static int Run(string? query, string? gameDirArg)
    {
        var install = gameDirArg ?? GameInstall.Find();
        var dll = GameInstall.AssemblyPath(install);
        if (dll is null)
        {
            Console.Error.WriteLine("Could not find your game's sts2.dll.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            Console.WriteLine("Game methods this project mirrors:\n");
            foreach (var group in MirrorMap.All.GroupBy(m => m.OurFile))
            {
                Console.WriteLine($"  {group.Key}");
                foreach (var m in group)
                    Console.WriteLine($"      {MirrorMap.Short(m.GameType)}.{m.Method}");
            }
            Console.WriteLine("\nsts2seed --show <Type.Method>   to print one.");
            return 0;
        }

        var matches = MirrorMap.Find(query).ToList();
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"'{query}' is not a method this project mirrors.");
            Console.Error.WriteLine("Run --show with no argument to list them.");
            return 1;
        }

        var decompiler = MethodSnapshots.CreateDecompiler(dll);
        foreach (var mirror in matches)
        {
            Console.WriteLine(new string('-', 78));
            Console.WriteLine($"GAME   {mirror.GameType}.{mirror.Method}");
            Console.WriteLine($"OURS   {mirror.OurFile}");
            Console.WriteLine($"SETS   {mirror.Decides}");
            Console.WriteLine(new string('-', 78));

            var code = MethodSnapshots.Decompile(decompiler, mirror);
            Console.WriteLine(code ?? "  (no such method in this build: it has been renamed or removed)");
            Console.WriteLine();
        }

        Console.WriteLine("Compare against the file above, and change ours to match the draw ORDER.");
        Console.WriteLine("--verify against a real run names the first draw index that disagrees.");
        return 0;
    }
}
