using System.Text.RegularExpressions;

namespace Sts2.SeedFinder.Core.Install;

/// <summary>
/// Finds the player's Slay the Spire 2 install.
///
/// This matters more than it looks: the app is distributed to people who own the game and
/// reads art from their own copy, so "I couldn't find your install" is the difference between
/// a working app and a grid of monograms. Steam lets users put games on any drive, so guessing
/// a couple of default paths is not enough — the library list has to be read.
/// </summary>
public static partial class GameInstall
{
    private const string FolderName = "Slay the Spire 2";

    [GeneratedRegex(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    /// <summary>An install directory containing SlayTheSpire2.pck, or null.</summary>
    public static string? Find(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Looks(configured) ? configured : null;

        foreach (var candidate in Candidates())
            if (Looks(candidate)) return candidate;

        return null;
    }

    private static bool Looks(string dir) =>
        File.Exists(Path.Combine(dir, "SlayTheSpire2.pck"));

    /// <summary>
    /// The installed game's version string, e.g. "v0.109.1", or null.
    ///
    /// Worth reading rather than hardcoding: predictions are version-specific, so a stamp that
    /// says one thing while the numbers came from another is worse than no stamp. A compiled-in
    /// constant silently goes stale the next time the user takes a patch.
    /// </summary>
    public static string? Version(string? installDir) => ReadRelease(installDir).Version;

    /// <summary>
    /// What an install says about itself.
    /// </summary>
    /// <param name="AssemblyHash">
    /// <c>main_assembly_hash</c> from release_info.json. Sharper than the version string,
    /// because it changes whenever game LOGIC changes — including in a hotfix that keeps the
    /// same version number, which a version comparison would wave through.
    /// </param>
    /// <param name="HasMods">
    /// Whether a mods folder is present. Mods append to the content pools through
    /// <c>ModHelper.ConcatModelsFromMods</c>, which changes pool SIZES, which changes how many
    /// draws each shuffle costs, which moves every draw after it. The result looks exactly like
    /// a port bug, so it is worth naming rather than debugging.
    /// </param>
    public readonly record struct Release(string? Version, long? AssemblyHash, bool HasMods)
    {
        /// <summary>True when there was no install to read, so nothing can be compared.</summary>
        public bool Missing => Version is null && AssemblyHash is null;
    }

    public static Release ReadRelease(string? installDir)
    {
        if (installDir is null) return default;

        string? version = null;
        long? hash = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(installDir, "release_info.json")));
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v)) version = v.GetString();
            if (root.TryGetProperty("main_assembly_hash", out var h)
                && h.ValueKind == System.Text.Json.JsonValueKind.Number)
                hash = h.GetInt64();
        }
        catch
        {
            // No release_info, or a reshaped one. Not fatal: callers fall back to the build we
            // were verified against, and say that is what they are doing.
        }

        bool mods = false;
        try { mods = Directory.Exists(Path.Combine(installDir, "mods")); }
        catch { /* unreadable is not modded */ }

        return new Release(version, hash, mods);
    }

    /// <summary>The game assembly every tool here reads, or null when it is not where it should be.</summary>
    public static string? AssemblyPath(string? installDir)
    {
        if (installDir is null) return null;
        foreach (var platform in new[] { "windows_x86_64", "linux_x86_64", "macos" })
        {
            var dll = Path.Combine(installDir, $"data_sts2_{platform}", "sts2.dll");
            if (File.Exists(dll)) return dll;
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        foreach (var steam in SteamRoots())
        {
            // The root library, then every extra library registered in libraryfolders.vdf.
            yield return Path.Combine(steam, "steamapps", "common", FolderName);

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            string text;
            try { text = File.ReadAllText(vdf); }
            catch { continue; }

            foreach (Match m in LibraryPath().Matches(text))
            {
                // Paths in the VDF are escaped, e.g. "D:\\SteamLibrary".
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                yield return Path.Combine(lib, "steamapps", "common", FolderName);
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            // Where Steam actually is, rather than where it usually is.
            var registered = ReadSteamPathFromRegistry();
            if (registered is not null) yield return registered;

            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                var root = Environment.GetEnvironmentVariable(env);
                if (root is not null) yield return Path.Combine(root, "Steam");
            }

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                yield return Path.Combine(drive.Name, "SteamLibrary");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(home, "Library", "Application Support", "Steam");
            }
            else
            {
                yield return Path.Combine(home, ".steam", "steam");
                yield return Path.Combine(home, ".local", "share", "Steam");
                yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam",
                    ".local", "share", "Steam");
            }
        }
    }

    private static string? ReadSteamPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;   // registry unavailable is not an error, just one fewer hint
        }
    }
}
