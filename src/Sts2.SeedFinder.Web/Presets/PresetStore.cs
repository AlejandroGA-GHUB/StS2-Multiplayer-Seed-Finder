using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2.SeedFinder.Web.Presets;

/// <summary>
/// A saved search, as it sits on disk.
/// </summary>
/// <param name="Schema">
/// Format version, so a later shape can be migrated rather than guessed at. Bump it only when
/// the file itself changes shape, not when the UI gains a criterion: <paramref name="State"/> is
/// deliberately opaque here and a new field in it is not a new schema.
/// </param>
/// <param name="GameVersion">
/// The game build the preset was saved against. Carried because a preset names cards, relics,
/// bosses and events by slug, and a patch can retire any of them. The loader uses this to say
/// "saved on v0.111.0" when something no longer resolves, which is the difference between a
/// puzzling silent drop and an obvious one.
/// </param>
/// <param name="State">
/// The page's own criteria state, stored verbatim. The UI already round-trips this object
/// through its reset path, so loading a preset is the same two steps as clearing the form.
/// </param>
public sealed record Preset(
    int Schema,
    string Name,
    DateTimeOffset SavedAt,
    string? AppVersion,
    string? GameVersion,
    JsonElement State);

/// <summary>
/// What the page posts to save one: a name and the criteria state behind it. Nothing else,
/// because everything the file also carries (when, which build, which game version) is the
/// server's to record rather than the page's to claim.
/// </summary>
public sealed record PresetRequest(string Name, JsonElement State);

/// <summary>One row of the preset list, without the state it would cost nothing to send twice.</summary>
public sealed record PresetSummary(
    string Name, string File, DateTimeOffset SavedAt, string? AppVersion, string? GameVersion);

/// <summary>
/// Saved searches, one JSON file each, in a folder of our own under the user's data directory.
///
/// Three decisions worth not re-litigating:
///
/// **A folder of our own, not the game's.** `Core/Saves/SaveLocations` finds
/// `%APPDATA%\SlayTheSpire2`, but that directory belongs to the game and this project only ever
/// READS there. Presets go in `%APPDATA%\Sts2SeedFinder` instead, so nothing we write lands in a
/// directory MegaCrit owns.
///
/// **Outside the app folder, which is the whole point.** A release is downloaded and extracted,
/// so anything beside the executable is orphaned by the next update, and anything under
/// `bin/` is deleted by a clean rebuild. The user data directory survives both, the same way the
/// player's game saves survive a game patch.
///
/// **Plain JSON, one file per preset.** A database would make them opaque: not copyable to
/// another machine, not readable when something goes wrong, not fixable by hand after a patch
/// retires a card. It would also be the first third-party dependency in this project.
/// </summary>
public sealed class PresetStore
{
    /// <summary>Current <see cref="Preset.Schema"/>.</summary>
    public const int Schema = 1;

    /// <summary>
    /// Points the store somewhere else entirely. Like <c>STS2_SAVE_DIR</c>, an override that
    /// cannot be created is an error rather than a reason to fall back: silently writing to the
    /// default would save presets somewhere the user is not looking.
    /// </summary>
    public const string OverrideVariable = "STS2_PRESET_DIR";

    private const string AppFolder = "Sts2SeedFinder";
    private const string PresetFolder = "presets";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Where presets live. Reported to the UI even when nothing is saved yet.</summary>
    public string Directory { get; }

    /// <summary>Whether the path came from <see cref="OverrideVariable"/> rather than discovery.</summary>
    public bool IsOverridden { get; }

    public PresetStore(IConfiguration? config = null)
    {
        var configured = Environment.GetEnvironmentVariable(OverrideVariable)
                         ?? config?["Presets:Directory"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory = Path.GetFullPath(configured.Trim());
            IsOverridden = true;
            return;
        }

        // ApplicationData is Roaming on Windows and the XDG data dir elsewhere, which is the
        // same place Core/Saves looks for the game's own user files. Empty only on a system
        // with no profile at all, where the app's own folder is the only thing left to use.
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = AppContext.BaseDirectory;

        Directory = Path.Combine(baseDir, AppFolder, PresetFolder);
    }

    /// <summary>
    /// Every saved preset, newest first, or an empty list when nothing has been saved.
    ///
    /// Never creates the directory. A user who has not saved anything should not find a folder
    /// in their profile for a feature they have not used.
    /// </summary>
    public IReadOnlyList<PresetSummary> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var rows = new List<PresetSummary>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            // A file that will not parse is skipped rather than failing the whole list: one bad
            // preset should not hide the others, and the user can see the folder to fix it.
            try
            {
                var preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), Json);
                if (preset is null || string.IsNullOrWhiteSpace(preset.Name)) continue;

                rows.Add(new PresetSummary(
                    preset.Name, Path.GetFileName(path), preset.SavedAt,
                    preset.AppVersion, preset.GameVersion));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Intentionally quiet. See above.
            }
        }

        return rows.OrderByDescending(r => r.SavedAt).ToList();
    }

    /// <summary>One preset by name, or null when it is not there.</summary>
    public Preset? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;

        try { return JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Writes a preset, replacing any with the same name. Creates the directory on the way,
    /// which is the only point at which it comes into existence.
    /// </summary>
    public Preset Save(string name, JsonElement state, string? appVersion, string? gameVersion)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("a preset needs a name.");
        if (trimmed.Length > 60) throw new ArgumentException("that name is too long, keep it under 60 characters.");

        var preset = new Preset(Schema, trimmed, DateTimeOffset.Now, appVersion, gameVersion, state);

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathFor(trimmed), JsonSerializer.Serialize(preset, Json));
        return preset;
    }

    /// <summary>Removes a preset. Returns false when there was nothing to remove.</summary>
    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// The file a name maps to.
    ///
    /// The name is slugged rather than used as-is, because it comes from a text box and ends up
    /// in a path: a name with a slash or a colon in it would either fail to write or write
    /// somewhere else entirely. Two names that slug the same collide, which is the same thing
    /// that happens when you save twice under one name, and is what the user asked for.
    /// </summary>
    private string PathFor(string name) => Path.Combine(Directory, Slug(name) + ".json");

    /// <summary>"Ironclad + Silent, Silken Tress" -> "ironclad-silent-silken-tress".</summary>
    internal static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;

        // Everything was punctuation. Better a dull filename than an empty one.
        return sb.Length == 0 ? "preset" : sb.ToString();
    }
}
