using System.Text.Json;

namespace Sts2.SeedFinder.Core.Install;

/// <summary>
/// The game build this checkout's data tables and algorithms were checked against, read from
/// <c>verified-build.json</c> beside the executable.
///
/// It is a file rather than a constant so that a user who repairs a patch can record the fact
/// without touching code, and so a fork can carry its own answer.
/// </summary>
/// <param name="Version">Version string of the build we were verified against, e.g. "v0.109.1".</param>
/// <param name="AssemblyHash">
/// <c>main_assembly_hash</c> of that build. This is the comparison that matters; the version
/// string is only for saying something human afterwards.
/// </param>
public sealed record VerifiedBuild(string Version, long? AssemblyHash, string? VerifiedOn)
{
    public const string FileName = "verified-build.json";

    /// <summary>
    /// Where the committed copies live in a checkout, kept together rather than loose at the
    /// root. Note this is only for WRITING: both files are copied flat beside the executable at
    /// build time, so everything that reads them still looks in <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public const string Folder = "baselines";

    /// <summary>The baselines folder inside a checkout, created if this is the first write.</summary>
    public static string FolderIn(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, Folder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Fallback when the file is missing, so a broken checkout degrades to "assume the build we
    /// shipped against" rather than crashing on startup.
    /// </summary>
    public static readonly VerifiedBuild Fallback = new("v0.109.1", 195020890, "2026-07-28");

    public static VerifiedBuild Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(version)) return Fallback;

            long? hash = root.TryGetProperty("assemblyHash", out var h) && h.ValueKind == JsonValueKind.Number
                ? h.GetInt64() : null;
            var on = root.TryGetProperty("verifiedOn", out var d) ? d.GetString() : null;
            return new VerifiedBuild(version, hash, on);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Fallback;
        }
    }

    public void Save(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        var json = JsonSerializer.Serialize(new
        {
            version = Version,
            assemblyHash = AssemblyHash,
            verifiedOn = VerifiedOn,
            note = "The game build this checkout was checked against. See docs/PATCH_RECOVERY.md.",
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

/// <summary>How far the installed game has drifted from what this checkout was verified against.</summary>
public enum Drift
{
    /// <summary>No install to compare with. Nothing can be said either way.</summary>
    Unknown,
    /// <summary>Same logic. Predictions are as good as they ever were.</summary>
    Match,
    /// <summary>
    /// A different version, but identical game logic. Common for asset-only or UI patches, and
    /// the reason the hash is compared rather than the version.
    /// </summary>
    VersionOnly,
    /// <summary>Game logic differs. Predictions past Neow are unverified until repaired.</summary>
    Logic,
}

/// <param name="Message">
/// One paragraph for a human, or null when there is nothing to say. Written to be read by
/// somebody who does not know what an assembly hash is.
/// </param>
public sealed record DriftReport(
    Drift Drift, string? InstalledVersion, string VerifiedVersion, bool HasMods, string? Message)
{
    /// <summary>Whether anything at all should be shown to the user.</summary>
    public bool Warn => Drift == Drift.Logic || HasMods;

    /// <summary>
    /// Compares an install against the recorded baseline.
    ///
    /// The hash decides. A version bump with matching logic is not worth alarming anyone about,
    /// and matching versions with different logic is exactly the case a version check misses.
    /// </summary>
    public static DriftReport For(GameInstall.Release release, VerifiedBuild verified)
    {
        if (release.Missing)
            return new DriftReport(Drift.Unknown, null, verified.Version, release.HasMods, null);

        // Presence of the folder is all that can be seen from here, and the two kinds of mod
        // differ completely: content mods resize the pools and invalidate everything past Neow,
        // cosmetic ones touch nothing a seed decides. So this names the distinction and leaves
        // the call to the reader rather than declaring the predictions wrong.
        var mods = release.HasMods
            ? "A mods folder is present. Mods that add relics, cards, events or encounters change "
              + "pool sizes and shift every draw after them, so predictions will not match your "
              + "game. Purely cosmetic mods, such as art and reskins, are fine."
            : null;

        Drift drift;
        string? message;

        if (release.AssemblyHash is { } installed && verified.AssemblyHash is { } known)
        {
            if (installed == known)
            {
                drift = release.Version == verified.Version ? Drift.Match : Drift.VersionOnly;
                message = null;   // same logic: nothing that affects predictions has changed
            }
            else
            {
                drift = Drift.Logic;
                message = $"Your game is {release.Version}. This build was checked against "
                          + $"{verified.Version}, and the game's logic has changed since. Neow offers "
                          + "and the Act 1 map are still correct; everything else is unverified. "
                          + "Run repair.bat.";
            }
        }
        else
        {
            // No hash on one side or the other, so fall back to the version string. Weaker, and
            // said as such rather than presented as a verdict.
            bool same = release.Version == verified.Version;
            drift = same ? Drift.Match : Drift.Logic;
            message = same ? null
                : $"Your game is {release.Version}; this build was checked against {verified.Version}. "
                  + "The game did not report a logic hash, so this cannot be narrowed further. "
                  + "Run repair.bat.";
        }

        return new DriftReport(drift, release.Version, verified.Version, release.HasMods,
            Join(message, mods));
    }

    private static string? Join(string? a, string? b) =>
        a is null ? b : b is null ? a : a + "\n" + b;
}
