namespace Sts2.SeedFinder.Web.Assets;

/// <summary>Where relic art comes from. See docs/web_app_specs.md section 4 — this is a deliberate
/// boundary, not a convenience: game art is never committed to or served from this repo.</summary>
public enum AssetProviderKind
{
    /// <summary>No art. The UI renders generated monograms instead.</summary>
    None,

    /// <summary>Read from the player's own installed game, on their own machine.</summary>
    Local,

    /// <summary>Serve from a gitignored assets/ directory the operator populates themselves.</summary>
    Bundled,
}

/// <summary>An image ready to send to a browser, already in a format browsers understand.</summary>
public readonly record struct AssetImage(byte[] Bytes, string ContentType);

/// <summary>
/// Resolves relic and card slugs to images and text, or null when it has none to offer.
///
/// Cards arrive through their own members rather than sharing the relic ones: the two live in
/// different places in the .pck, and a slug can in principle name one of each.
/// </summary>
public interface IGameAssetProvider
{
    AssetProviderKind Kind { get; }

    /// <summary>Human-readable note for the UI: which source is active, and why if it fell back.</summary>
    string Status { get; }

    /// <summary>Slugs this provider can serve, so the UI never fires requests that 404.</summary>
    IReadOnlySet<string> AvailableSlugs { get; }

    Task<AssetImage?> TryGetAsync(string slug, CancellationToken ct);

    /// <summary>
    /// What the game says this relic does, or null when this source has no text. Descriptions
    /// come from the same place as the art and are subject to the same boundary, so a provider
    /// that cannot serve art generally cannot serve text either.
    /// </summary>
    AssetText? TryGetText(string slug) => null;

    /// <summary>Card slugs with portrait art. Empty for sources that only carry relics.</summary>
    IReadOnlySet<string> AvailableCardSlugs => new HashSet<string>();

    Task<AssetImage?> TryGetCardAsync(string slug, CancellationToken ct) =>
        Task.FromResult<AssetImage?>(null);

    /// <summary>The card's own title and rules text, as the game words them.</summary>
    AssetText? TryGetCardText(string slug) => null;

    /// <summary>Character slugs with portrait art, e.g. <c>ironclad</c>.</summary>
    IReadOnlySet<string> AvailableCharacterSlugs => new HashSet<string>();

    Task<AssetImage?> TryGetCharacterAsync(string slug, CancellationToken ct) =>
        Task.FromResult<AssetImage?>(null);

    /// <summary>Ancient slugs with node art, e.g. <c>neow</c>.</summary>
    IReadOnlySet<string> AvailableAncientSlugs => new HashSet<string>();

    Task<AssetImage?> TryGetAncientAsync(string slug, CancellationToken ct) =>
        Task.FromResult<AssetImage?>(null);

    /// <summary>Event slugs with illustration art, e.g. <c>zen_weaver</c>.</summary>
    IReadOnlySet<string> AvailableEventSlugs => new HashSet<string>();

    Task<AssetImage?> TryGetEventAsync(string slug, CancellationToken ct) =>
        Task.FromResult<AssetImage?>(null);

    /// <summary>The event's own title, and the text shown on arriving at it.</summary>
    AssetText? TryGetEventText(string slug) => null;

    /// <summary>Every event's text, for the catalog to send in one go.</summary>
    IReadOnlyDictionary<string, AssetText> AllEventText =>
        new Dictionary<string, AssetText>();
}

/// <summary>The always-works fallback. Never throws, never has art.</summary>
public sealed class NoAssetProvider(string status = "no art configured") : IGameAssetProvider
{
    public AssetProviderKind Kind => AssetProviderKind.None;
    public string Status { get; } = status;
    public IReadOnlySet<string> AvailableSlugs { get; } = new HashSet<string>();
    public Task<AssetImage?> TryGetAsync(string slug, CancellationToken ct) => Task.FromResult<AssetImage?>(null);
}

/// <summary>
/// Serves PNGs from a local directory, keyed by slug. This is the escape hatch for public
/// hosting: the operator populates the directory themselves, and .gitignore keeps it out of
/// the repo. Nothing here ships with the project.
/// </summary>
public sealed class BundledAssetProvider : IGameAssetProvider
{
    /// <summary>Descriptions exported alongside the icons, keyed by slug.</summary>
    public const string TextFileName = "relic_text.json";

    private readonly string _root;
    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, AssetText> _text;

    public BundledAssetProvider(string root)
    {
        _root = root;
        _text = ReadText(root);
        _files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<string, string>();

        AvailableSlugs = _files.Keys.ToHashSet();
        // The directory is an operator detail; a deployed page should not advertise a server path.
        Status = _files.Count > 0
            ? $"{_files.Count} relic icons"
            : $"no images found in {root}";
    }

    public AssetProviderKind Kind => AssetProviderKind.Bundled;
    public string Status { get; }
    public IReadOnlySet<string> AvailableSlugs { get; }

    public async Task<AssetImage?> TryGetAsync(string slug, CancellationToken ct)
    {
        if (!_files.TryGetValue(slug.ToLowerInvariant(), out var path) || !File.Exists(path)) return null;
        var type = path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/png";
        return new AssetImage(await File.ReadAllBytesAsync(path, ct), type);
    }

    public AssetText? TryGetText(string slug) =>
        _text.TryGetValue(slug.ToLowerInvariant(), out var t) ? t : null;

    private static Dictionary<string, AssetText> ReadText(string root)
    {
        var empty = new Dictionary<string, AssetText>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, TextFileName);
        if (!File.Exists(path)) return empty;

        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path));
            return map is null
                ? empty
                : map.ToDictionary(kv => kv.Key, kv => new AssetText(kv.Value), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return empty;   // a malformed export costs tooltips, not the app
        }
    }
}
