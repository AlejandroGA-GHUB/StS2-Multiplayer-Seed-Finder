using System.Text.Json;
using Sts2.SeedFinder.Core.Acts;

namespace Sts2.SeedFinder.Core.Saves;

/// <summary>
/// What a profile's <c>progress.save</c> says about the account, reduced to the parts that
/// change predictions.
/// </summary>
/// <param name="Unlocks">
/// The unlock state to generate with. This is the whole point of reading the save: pool sizes
/// decide how many draws each shuffle costs, so a partially-unlocked account produces a
/// different run from the same seed. Assuming fully unlocked is right for most players and
/// silently wrong for new ones.
/// </param>
/// <param name="RevealedEpochs">How many of <paramref name="TotalEpochs"/> are revealed.</param>
/// <param name="DiscoveredActs">Act ids the account has seen, which only matters in solo play.</param>
public sealed record ProfileInfo(
    string Path,
    UnlockState Unlocks,
    int RevealedEpochs,
    int TotalEpochs,
    IReadOnlyList<string> DiscoveredActs)
{
    /// <summary>
    /// Whether every epoch is revealed, which is the case our defaults assume. When false, the
    /// difference is not cosmetic: predictions made against the wrong pool sizes are wrong from
    /// the first act onward.
    /// </summary>
    public bool FullyUnlocked => TotalEpochs > 0 && RevealedEpochs == TotalEpochs;
}

/// <summary>
/// The lobby a run in progress is using, read from <c>current_run(_mp).save</c>.
///
/// Useful because it is exactly the set of inputs a search needs and a user would otherwise
/// retype: who is playing, in which order, at which ascension.
/// </summary>
public sealed record CurrentRunInfo(
    string Path, string? Seed, IReadOnlyList<string> Characters, int Ascension, bool IsMultiplayer);

public static class ProfileReader
{
    /// <summary>Reads the most recently played profile, or null when none can be found.</summary>
    public static ProfileInfo? Read(string? configured = null)
    {
        var path = SaveLocations.ProgressSave(configured);
        return path is null ? null : ReadFile(path);
    }

    public static ProfileInfo? ReadFile(string path)
    {
        try { return Parse(File.ReadAllText(path), path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The same reading, over content that never came off this machine's disk — a partner's
    /// imported <c>progress.save</c>. <paramref name="path"/> only labels the result, so a
    /// caller holding bytes rather than a file can pass whatever names them.
    /// </summary>
    public static ProfileInfo? Parse(string json, string path)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            var revealed = new List<string>();
            int total = 0;

            if (root.TryGetProperty("epochs", out var epochs) && epochs.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in epochs.EnumerateArray())
                {
                    total++;
                    var state = e.TryGetProperty("state", out var st) ? st.GetString() : null;
                    var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (id is not null && string.Equals(state, "revealed", StringComparison.OrdinalIgnoreCase))
                        revealed.Add(id);
                }
            }

            var acts = new List<string>();
            if (root.TryGetProperty("discovered_acts", out var da) && da.ValueKind == JsonValueKind.Array)
                foreach (var a in da.EnumerateArray())
                    if (Entry(a.GetString()) is { Length: > 0 } name) acts.Add(name);

            var unlocks = UnlockState.FromRevealedEpochs(revealed) with { DiscoveredActs = acts.ToHashSet() };
            return new ProfileInfo(path, unlocks, revealed.Count, total, acts);
        }
    }

    /// <summary>The run currently in progress, or null when there is none.</summary>
    public static CurrentRunInfo? CurrentRun(string? configured = null)
    {
        var path = SaveLocations.CurrentRun(configured);
        if (path is null) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;

            var characters = new List<string>();
            if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in players.EnumerateArray())
                {
                    var id = p.TryGetProperty("character_id", out var c) ? c.GetString()
                           : p.TryGetProperty("character", out var c2) ? c2.GetString() : null;
                    var name = Entry(id);
                    if (name.Length > 0) characters.Add(Title(name));
                }
            }

            string? seed = null;
            if (root.TryGetProperty("rng", out var rng) && rng.TryGetProperty("seed", out var s))
                seed = s.GetString();

            int ascension = root.TryGetProperty("ascension", out var asc) && asc.ValueKind == JsonValueKind.Number
                ? asc.GetInt32() : 0;

            return new CurrentRunInfo(path, seed, characters, ascension, characters.Count > 1);
        }
    }

    /// <summary>ModelIds serialize as "TYPE.ENTRY"; only the entry is ever meaningful here.</summary>
    private static string Entry(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return "";
        int dot = modelId.IndexOf('.');
        return dot >= 0 ? modelId[(dot + 1)..] : modelId;
    }

    /// <summary>"IRONCLAD" to "Ironclad", which is the spelling the rest of the API uses.</summary>
    private static string Title(string entry) =>
        entry.Length == 0 ? entry : char.ToUpperInvariant(entry[0]) + entry[1..].ToLowerInvariant();
}
