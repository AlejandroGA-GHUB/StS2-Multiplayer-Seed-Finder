namespace Sts2.SeedFinder.Core.Saves;

/// <summary>
/// Finds the player's Slay the Spire 2 save files.
///
/// The layout is Godot's, with the game's own account scoping on top:
/// <code>
///   &lt;userDataDir&gt;/&lt;platform&gt;/&lt;accountId&gt;/profile&lt;N&gt;/saves/
///       progress.save        epochs revealed, acts discovered, stats
///       current_run.save     the run in progress, if any
///       current_run_mp.save  the co-op run in progress, if any
///       history/*.run        every finished run
/// </code>
/// <c>&lt;platform&gt;</c> is "steam" today, and <c>&lt;accountId&gt;</c> is the platform user id, so
/// neither can be guessed. Everything below the user data dir is therefore discovered by
/// walking rather than by construction.
///
/// The user data dir itself is Godot's, for a project that sets a custom user dir name:
/// <c>%APPDATA%\SlayTheSpire2</c> on Windows, <c>~/Library/Application Support/SlayTheSpire2</c>
/// on macOS, <c>~/.local/share/SlayTheSpire2</c> on Linux. Steam does not relocate these the way
/// it relocates installs, so unlike the game directory there is no library list to consult — but
/// an explicit override still exists, because people do move their AppData and because Proton
/// puts the whole thing inside a Wine prefix.
/// </summary>
public static class SaveLocations
{
    private const string AppFolder = "SlayTheSpire2";

    /// <summary>
    /// Environment variable that overrides discovery entirely. Point it at the directory that
    /// CONTAINS the platform folder (the one with "steam" inside it), or at a profile's own
    /// saves folder — both are accepted, since the difference is not obvious from outside.
    /// </summary>
    public const string OverrideVariable = "STS2_SAVE_DIR";

    /// <summary>Candidate save roots, best guess first. Existence is not checked here.</summary>
    public static IEnumerable<string> Roots(string? configured = null)
    {
        var explicitPath = configured;
        if (string.IsNullOrWhiteSpace(explicitPath))
            explicitPath = Environment.GetEnvironmentVariable(OverrideVariable);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
            yield break;   // an explicit answer that is wrong should fail loudly, not fall back
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (appData.Length > 0) yield return Path.Combine(appData, AppFolder);
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                yield return Path.Combine(home, "Library", "Application Support", AppFolder);
            else
                yield return Path.Combine(home, ".local", "share", AppFolder);

            // Proton keeps a Windows-shaped AppData inside the game's prefix. The app id is
            // stable, and this is the only place a Linux player's saves can be.
            yield return Path.Combine(home, ".steam", "steam", "steamapps", "compatdata",
                "2868840", "pfx", "drive_c", "users", "steamuser", "AppData", "Roaming", AppFolder);
        }
    }

    /// <summary>The first save root that exists, or null.</summary>
    public static string? FindRoot(string? configured = null) =>
        Roots(configured).FirstOrDefault(Directory.Exists);

    /// <summary>
    /// Every profile directory holding a progress.save, newest first.
    ///
    /// Newest rather than lowest-numbered because profile1 is not necessarily the one in use:
    /// the game allows several, and the one a player has just been playing is the one they mean.
    /// </summary>
    public static IReadOnlyList<string> Profiles(string? configured = null)
    {
        var root = FindRoot(configured);
        if (root is null) return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(root, "progress.save", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(f => Path.GetDirectoryName(f)!)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The most recently played profile's progress.save, or null.</summary>
    public static string? ProgressSave(string? configured = null)
    {
        var profile = Profiles(configured).FirstOrDefault();
        return profile is null ? null : Path.Combine(profile, "progress.save");
    }

    /// <summary>
    /// The newest run in progress, co-op or solo, or null. Both names are searched because a
    /// co-op run writes current_run_mp.save while a solo run writes current_run.save, and which
    /// one is current is decided by the timestamp rather than by preference.
    /// </summary>
    public static string? CurrentRun(string? configured = null) =>
        Newest(configured, "current_run*.save");

    /// <summary>Finished runs, newest first. These are what <c>--verify-history</c> reads.</summary>
    public static IReadOnlyList<string> History(string? configured = null)
    {
        var root = FindRoot(configured);
        if (root is null) return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(root, "*.run", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? Newest(string? configured, string pattern)
    {
        var root = FindRoot(configured);
        if (root is null) return null;

        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
