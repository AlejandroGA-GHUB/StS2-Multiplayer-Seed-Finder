using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Decompiles the game methods we mirror, so a patch that changes one can be NAMED rather than
/// hunted for.
///
/// Why decompiled text rather than raw IL: IL carries metadata tokens, which shift between
/// builds even when the code is identical, so an IL hash would report every method as changed
/// on every patch and be worth nothing. Decompiled C# is token-independent, and it is also what
/// a person has to read in order to do the repair.
///
/// This does not repair anything, and cannot. Reimplementing changed behaviour is writing code.
/// What it removes is the search.
/// </summary>
public static class MethodSnapshots
{
    public const string FileName = "method-snapshots.json";

    /// <summary>
    /// Snapshots are hashes of decompiler output, so they are only comparable against the same
    /// decompiler. Recorded and checked, because a package bump would otherwise look exactly
    /// like the game changing every method at once.
    /// </summary>
    private static string DecompilerVersion =>
        typeof(CSharpDecompiler).Assembly.GetName().Version?.ToString() ?? "unknown";

    public sealed record Snapshot(string Key, string Hash, int Lines);

    public sealed record SnapshotFile(
        string GameVersion, string DecompilerVersion, Dictionary<string, Snapshot> Methods);

    // ---- decompiling -------------------------------------------------------------------

    /// <summary>
    /// The decompiled source of every overload of one mirrored method, concatenated.
    /// Returns null when the type or method is gone, which is itself a finding.
    /// </summary>
    public static string? Decompile(CSharpDecompiler decompiler, Mirror mirror)
    {
        ITypeDefinition? type;
        try
        {
            type = decompiler.TypeSystem.MainModule.Compilation
                .FindType(new FullTypeName(mirror.GameType)).GetDefinition();
        }
        catch
        {
            return null;
        }
        if (type is null) return null;

        var methods = type.Methods.Concat<IMember>(type.Properties)
            .Where(m => m.Name == mirror.Method)
            .ToList();
        if (methods.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var m in methods)
        {
            try { sb.AppendLine(decompiler.DecompileAsString(m.MetadataToken)); }
            catch (Exception ex) { sb.AppendLine($"// could not decompile: {ex.GetType().Name}"); }
        }
        return sb.ToString();
    }

    public static CSharpDecompiler CreateDecompiler(string assemblyPath)
    {
        var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
        {
            // Sugar that varies with decompiler heuristics adds diff noise without adding
            // meaning. Off, so a snapshot changes when the CODE changes.
            UseLambdaSyntax = false,
            UseExpressionBodyForCalculatedGetterOnlyProperties = false,
            ShowXmlDocumentation = false,
        };
        var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, targetFramework: null);
        return new CSharpDecompiler(assemblyPath, resolver, settings);
    }

    /// <summary>
    /// Normalises before hashing: comments and blank lines carry no behaviour, and whitespace
    /// differences would otherwise be reported as a change to read.
    /// </summary>
    private static string Normalise(string code)
    {
        var lines = code.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
        return string.Join("\n", lines);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    // ---- taking and comparing ------------------------------------------------------------

    public static SnapshotFile Take(string assemblyPath, string gameVersion)
    {
        var decompiler = CreateDecompiler(assemblyPath);
        var methods = new Dictionary<string, Snapshot>(StringComparer.Ordinal);

        foreach (var mirror in MirrorMap.All)
        {
            var key = $"{mirror.GameType}.{mirror.Method}";
            var code = Decompile(decompiler, mirror);
            if (code is null)
            {
                methods[key] = new Snapshot(key, "MISSING", 0);
                continue;
            }
            var normalised = Normalise(code);
            methods[key] = new Snapshot(key, Hash(normalised), normalised.Split('\n').Length);
        }

        return new SnapshotFile(gameVersion, DecompilerVersion, methods);
    }

    public static SnapshotFile? Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        try
        {
            return JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(SnapshotFile file, string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(file,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    /// <param name="Changed">Mirrors whose decompiled body differs from the recorded snapshot.</param>
    /// <param name="Missing">Mirrors whose game method has vanished. Always worth a look.</param>
    /// <param name="Incomparable">
    /// Set when there is no baseline, or it was taken with a different decompiler, so a clean
    /// result would be meaningless. Reported rather than quietly passing.
    /// </param>
    public sealed record Comparison(
        IReadOnlyList<Mirror> Changed, IReadOnlyList<Mirror> Missing, string? Incomparable);

    public static Comparison Compare(string assemblyPath, string gameVersion, SnapshotFile? baseline)
    {
        if (baseline is null)
            return new Comparison([], [], $"no {FileName} to compare against");

        if (!string.Equals(baseline.DecompilerVersion, DecompilerVersion, StringComparison.Ordinal))
            return new Comparison([], [],
                $"{FileName} was taken with decompiler {baseline.DecompilerVersion}, this build has "
                + $"{DecompilerVersion}. Re-baseline on a known-good game build before trusting it.");

        var now = Take(assemblyPath, gameVersion);
        var changed = new List<Mirror>();
        var missing = new List<Mirror>();

        foreach (var mirror in MirrorMap.All)
        {
            var key = $"{mirror.GameType}.{mirror.Method}";
            if (!now.Methods.TryGetValue(key, out var current)) continue;
            if (current.Hash == "MISSING") { missing.Add(mirror); continue; }
            if (!baseline.Methods.TryGetValue(key, out var was)) continue;
            if (was.Hash != current.Hash) changed.Add(mirror);
        }

        return new Comparison(changed, missing, null);
    }
}
