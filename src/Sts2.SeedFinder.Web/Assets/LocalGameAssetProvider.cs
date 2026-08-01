using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// Serves relic art out of the player's own installed copy of the game.
///
/// Nothing is redistributed: the bytes never leave the machine that already owns them, and
/// nothing is written into the repo. Decoded images are cached in memory only. This is the
/// same posture as a mod reading game files, and it is what makes art possible at all without
/// shipping Mega Crit's assets. See docs/web_app_specs.md section 4.
///
/// The game names its relic textures by the same slug we do (silken_tress.png, fiddle.png),
/// so the mapping is direct.
/// </summary>
public sealed partial class LocalGameAssetProvider : IGameAssetProvider
{
    // Godot imports one variant per GPU family: a plain .ctex (WebP or raw), plus .bptc.ctex
    // and/or .s3tc.ctex. Matching only .bptc silently loses whichever relics ship s3tc-only.
    [GeneratedRegex(@"^\.godot/imported/([a-z0-9_]+)\.png-[0-9a-f]+(?:\.([a-z0-9]+))?\.ctex$")]
    private static partial Regex ImportedTexture();

    [GeneratedRegex(@"^images/relics/([a-z0-9_]+)\.png(\.import)?$")]
    private static partial Regex RelicSource();

    // Card art is one PNG per card under a per-pool directory, unlike the flat relic folder.
    // Some pools also carry a "beta/" subfolder of older art, sometimes for a card that has no
    // final portrait at all, so the extra level has to be walked rather than skipped. Capturing
    // it lets the ranking below prefer finished art wherever both exist.
    [GeneratedRegex(@"^images/packed/card_portraits/[a-z0-9_]+/(beta/)?([a-z0-9_]+)\.png\.import$")]
    private static partial Regex CardSource();

    // Head-and-shoulders portraits, the same art the game's character select shows.
    [GeneratedRegex(@"^images/packed/character_select/char_select_([a-z0-9_]+)\.png\.import$")]
    private static partial Regex CharacterSource();

    // The Ancients' map-node icons. The full-body portraits (images/ancients/) exist too, but
    // these are already icon-shaped and icon-sized, which is what the UI wants. "_outline" is a
    // separate silhouette layer the map draws underneath, so the pattern excludes it.
    [GeneratedRegex(@"^images/packed/map/ancients/ancient_node_([a-z0-9_]+)\.png\.import$")]
    private static partial Regex AncientSource();

    // One illustration per event, in a flat folder named by the same slug we use. A few entries
    // there are alternate states of an event rather than events (trial_started,
    // zen_weaver_phobia_mode, dense_vegetation_foreground), so the caller filters against the
    // act tables instead of trusting the folder.
    [GeneratedRegex(@"^images/events/([a-z0-9_]+)\.png\.import$")]
    private static partial Regex EventSource();

    /// <summary>
    /// The two events with no illustration of their own, pointed at the closest thing the game
    /// actually has. Both are borrowed deliberately rather than left as lettered tiles:
    ///
    /// The Merchant??? is a monster wearing a shop as a disguise, so it is staged out of the
    /// merchant room's own props rather than drawn as a scene — the only whole picture of the
    /// merchant anywhere is the icon the run summary uses, which is icon-shaped and icon-sized
    /// already, and is exactly who you think you are meeting.
    ///
    /// The Lantern Key has its quest card's portrait, which is the object the event is about.
    /// </summary>
    private static readonly (string Slug, string Source)[] BorrowedEventArt =
    {
        ("fake_merchant", "images/ui/game_over_screen/run_summary_merchant.png.import"),
        ("the_lantern_key", "images/packed/card_portraits/quest/lantern_key.png.import"),
    };

    /// <summary>
    /// Relics the game draws more than once, so no image carries the bare slug the flat basename
    /// lookup wants. Named here as slug pairs rather than pck paths, because the source is a
    /// relic image already resolved by the ordinary route.
    ///
    /// Yummy Cookie is the only one: it is cut per character (yummy_cookie_ironclad and four
    /// more), and a picker shows one icon per relic regardless of who would be offered it. The
    /// Ironclad cut is the stand-in. Serving the icon for the slot's own character is possible
    /// and would mean passing the character down to the asset request, which is more plumbing
    /// than one relic's flavour is worth.
    /// </summary>
    private static readonly (string Slug, string Source)[] BorrowedRelicArt =
    {
        ("yummy_cookie", "yummy_cookie_ironclad"),
    };

    // The .import file names the exact imported texture. Cards and characters are resolved
    // through it rather than by basename (as relics are) because a card and a relic can share a
    // slug, and their imported files differ only by a content hash we could not tell apart.
    //
    // The variant suffix is part of the KEY, not just the filename: an image imported for one
    // GPU family is written as `path.bptc="…"` rather than `path="…"`. Missing that reported
    // every event as having no art at all, since events are imported that way and relics — which
    // resolve by basename instead — never exercised this path.
    [GeneratedRegex(@"path(?:\.(?<variant>[a-z0-9]+))?=""res://(?<file>\.godot/imported/[^""]+\.ctex)""")]
    private static partial Regex ImportTarget();

    private readonly GodotPck _pck;
    private readonly Dictionary<string, string> _texturePaths;       // relic slug -> pck path
    private readonly Dictionary<string, string> _cardTexturePaths;   // card slug  -> pck path
    private readonly Dictionary<string, string> _charTexturePaths;   // character  -> pck path
    private readonly Dictionary<string, string> _ancientTexturePaths; // ancient   -> pck path
    private readonly Dictionary<string, string> _eventTexturePaths;   // event slug -> pck path
    private readonly ConcurrentDictionary<string, AssetImage?> _cache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _cardCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _charCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _ancientCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _eventCache = new();
    private readonly IReadOnlyDictionary<string, AssetText> _text;
    private readonly IReadOnlyDictionary<string, AssetText> _cardText;
    private readonly IReadOnlyDictionary<string, AssetText> _eventText;

    public AssetProviderKind Kind => AssetProviderKind.Local;
    public string Status { get; }
    public IReadOnlySet<string> AvailableSlugs { get; }
    public IReadOnlySet<string> AvailableCardSlugs { get; }
    public IReadOnlySet<string> AvailableCharacterSlugs { get; }
    public IReadOnlySet<string> AvailableAncientSlugs { get; }
    public IReadOnlySet<string> AvailableEventSlugs { get; }

    /// <summary>Every description the install has, for exporting to a bundled directory.</summary>
    public IReadOnlyDictionary<string, AssetText> AllText => _text;

    private LocalGameAssetProvider(GodotPck pck, Dictionary<string, string> texturePaths, string status,
        IReadOnlySet<string> available, IReadOnlyDictionary<string, AssetText> text,
        Dictionary<string, string> cardTexturePaths, IReadOnlySet<string> cardsAvailable,
        IReadOnlyDictionary<string, AssetText> cardText,
        Dictionary<string, string> charTexturePaths, IReadOnlySet<string> charsAvailable,
        Dictionary<string, string> ancientTexturePaths, IReadOnlySet<string> ancientsAvailable,
        Dictionary<string, string> eventTexturePaths, IReadOnlySet<string> eventsAvailable,
        IReadOnlyDictionary<string, AssetText> eventText)
    {
        _pck = pck;
        _texturePaths = texturePaths;
        Status = status;
        AvailableSlugs = available;
        _text = text;
        _cardTexturePaths = cardTexturePaths;
        AvailableCardSlugs = cardsAvailable;
        _cardText = cardText;
        _charTexturePaths = charTexturePaths;
        AvailableCharacterSlugs = charsAvailable;
        _ancientTexturePaths = ancientTexturePaths;
        AvailableAncientSlugs = ancientsAvailable;
        _eventTexturePaths = eventTexturePaths;
        AvailableEventSlugs = eventsAvailable;
        _eventText = eventText;
    }

    public AssetText? TryGetText(string slug) =>
        _text.TryGetValue(slug, out var t) ? t : null;

    public AssetText? TryGetCardText(string slug) =>
        _cardText.TryGetValue(slug, out var t) ? t : null;

    public AssetText? TryGetEventText(string slug) =>
        _eventText.TryGetValue(slug, out var t) ? t : null;

    public IReadOnlyDictionary<string, AssetText> AllEventText => _eventText;

    public static LocalGameAssetProvider? TryCreate(string? gameDirectory)
    {
        var dir = GameInstall.Find(gameDirectory);
        if (dir is null) return null;

        var pck = GodotPck.TryOpen(Path.Combine(dir, "SlayTheSpire2.pck"));
        if (pck is null) return null;

        // Only slugs that have a source image under images/relics/ are real relics; the
        // imported folder also holds UI chrome, epoch art and atlases we do not want.
        var relicSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in pck.Entries.Keys)
        {
            var m = RelicSource().Match(path);
            if (m.Success) relicSlugs.Add(m.Groups[1].Value);
        }

        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var chosenRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in pck.Entries.Keys)
        {
            var m = ImportedTexture().Match(path);
            if (!m.Success) continue;

            var slug = m.Groups[1].Value;
            if (!relicSlugs.Contains(slug)) continue;

            int rank = VariantRank(m.Groups[2].Success ? m.Groups[2].Value : null);
            if (!chosenRank.TryGetValue(slug, out int best) || rank < best)
            {
                chosen[slug] = path;
                chosenRank[slug] = rank;
            }
        }

        // Probe what actually decodes, so the UI never renders a broken image element.
        var servable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int undecodable = 0;
        foreach (var (slug, path) in chosen)
        {
            var raw = pck.Read(path);
            if (raw is null) continue;
            var tex = CompressedTexture.Parse(raw);
            if (tex is null) { undecodable++; continue; }

            if (tex.Value.Kind is "webp" or "png" or "rgba8" or "bc7" or "dxt1" or "dxt5") servable.Add(slug);
            else undecodable++;
        }

        // Point the multi-cut relics at their stand-in, after the probe so the alias inherits a
        // source already known to decode. Skipped if the game ever ships the bare slug itself,
        // which would then win on its own.
        foreach (var (slug, source) in BorrowedRelicArt)
        {
            if (servable.Contains(slug) || !servable.Contains(source)) continue;
            chosen[slug] = chosen[source];
            servable.Add(slug);
        }

        // The .pck holds the description templates; the numbers that go in them live in the
        // game's assembly next door. Without the second half every quantity reads "X".
        var vars = RelicVars.Read(dir);
        var text = GameText.ReadRelics(pck, vars.Relics, vars.ModelRefs);

        var (cardPaths, cardsServable) = FindCardTextures(pck);
        var cardText = GameText.ReadCards(pck, vars.Cards);
        var (charPaths, charsServable) = FindCharacterTextures(pck);
        var (ancientPaths, ancientsServable) = FindAncientTextures(pck);
        var missingBorrowed = new List<string>();
        var (eventPaths, eventsServable) = FindEventTextures(pck, missingBorrowed);

        var eventText = GameText.ReadEvents(pck, vars.Events, vars.ModelRefs);

        var status = undecodable > 0
            ? $"{servable.Count} of {chosen.Count} relic icons from your game install ({undecodable} in an unsupported format)"
            : $"{servable.Count} relic icons from your game install";
        if (text.Count > 0) status += $", {text.Count} descriptions";
        if (vars.Relics.Count > 0) status += $" ({vars.Relics.Count} with real numbers)";
        if (cardsServable.Count > 0) status += $", {cardsServable.Count} card portraits";
        if (charsServable.Count > 0) status += $", {charsServable.Count} characters";
        if (ancientsServable.Count > 0) status += $", {ancientsServable.Count} Ancients";
        if (eventsServable.Count > 0) status += $", {eventsServable.Count} events";
        if (missingBorrowed.Count > 0)
            status += $" [borrowed art missing for {string.Join(", ", missingBorrowed)}"
                + " - the game may have moved it]";
        if (eventText.Count > 0) status += $" ({eventText.Count} described"
            + (vars.Events.Count > 0 ? $", {vars.Events.Count} with real numbers)" : ")");

        return new LocalGameAssetProvider(pck, chosen, status, servable, text,
            cardPaths, cardsServable, cardText, charPaths, charsServable,
            ancientPaths, ancientsServable, eventPaths, eventsServable, eventText);
    }

    /// <summary>
    /// Maps card slugs to their imported textures by reading each source's .import file, which
    /// names its target outright. That is 900-odd small reads at startup, but it is exact, and
    /// the alternative — matching on the imported file's basename, as the relic path does — is
    /// ambiguous the moment a card and a relic share a slug.
    /// </summary>
    private static (Dictionary<string, string> Paths, IReadOnlySet<string> Servable) FindCardTextures(GodotPck pck)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fromBeta = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pck.Entries.Keys)
        {
            var m = CardSource().Match(path);
            if (!m.Success) continue;

            var slug = m.Groups[2].Value;
            bool beta = m.Groups[1].Success;

            // Final art wins over the beta copy of the same card; beta only fills gaps.
            if (beta && paths.ContainsKey(slug) && !fromBeta.Contains(slug)) continue;

            var raw = pck.Read(path);
            if (raw is null) continue;

            var texture = ResolveImportTarget(pck, raw);
            if (texture is null) continue;

            paths[slug] = texture;
            if (beta) fromBeta.Add(slug); else fromBeta.Remove(slug);
        }

        return (paths, Probe(pck, paths));
    }

    /// <summary>
    /// The playable characters' portraits, from the art the game's own character select uses.
    ///
    /// Matched against the <c>Character</c> enum rather than by scraping the folder, because
    /// that directory is mostly the select screen's own chrome — a button mask, an outline, a
    /// lock badge, a player icon, and the "random" option — and a <c>char_select_*</c> pattern
    /// happily reports all of it as characters. Anchoring on the enum also means the
    /// "_locked" silhouettes drop out for free, and that a patch adding more chrome cannot
    /// break this.
    /// </summary>
    private static (Dictionary<string, string> Paths, IReadOnlySet<string> Servable) FindCharacterTextures(GodotPck pck)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var playable = Enum.GetNames<Sts2.SeedFinder.Core.Acts.Character>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pck.Entries.Keys)
        {
            var m = CharacterSource().Match(path);
            if (!m.Success || !playable.Contains(m.Groups[1].Value)) continue;

            var raw = pck.Read(path);
            if (raw is null) continue;

            var texture = ResolveImportTarget(pck, raw);
            if (texture is null) continue;

            paths[m.Groups[1].Value] = texture;
        }

        return (paths, Probe(pck, paths));
    }

    /// <summary>
    /// The Ancients' map-node icons, anchored on the <c>Ancient</c> enum for the same reason the
    /// characters are anchored on theirs: that folder also holds each node's outline layer, and a
    /// bare <c>ancient_node_*</c> pattern would report those as Ancients too.
    /// </summary>
    private static (Dictionary<string, string> Paths, IReadOnlySet<string> Servable) FindAncientTextures(GodotPck pck)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The enum covers the seven act-opening Ancients that have a searchable OFFER. Neow is
        // an Ancient too — it opens Act 1 — but it is modelled in Core/Neow with its own
        // generator and so is absent from that enum, which would otherwise cost it its icon.
        var known = Enum.GetNames<Sts2.SeedFinder.Core.Ancients.Ancient>()
            .Append("Neow")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pck.Entries.Keys)
        {
            var m = AncientSource().Match(path);
            if (!m.Success || !known.Contains(m.Groups[1].Value)) continue;

            var raw = pck.Read(path);
            if (raw is null) continue;

            var texture = ResolveImportTarget(pck, raw);
            if (texture is null) continue;

            paths[m.Groups[1].Value] = texture;
        }

        return (paths, Probe(pck, paths));
    }

    /// <summary>
    /// Each event's illustration, anchored on the act tables for the same reason the characters
    /// and Ancients are anchored on their enums: that folder also holds alternate states of an
    /// event (<c>trial_started</c>, <c>zen_weaver_phobia_mode</c>) and a foreground layer, none
    /// of which are events a search can ask for.
    ///
    /// Two events in the tables have no illustration of their own — the lantern key and the fake
    /// merchant, which borrows the merchant's. They fall back to a lettered tile like anything
    /// else without art.
    /// </summary>
    private static (Dictionary<string, string> Paths, IReadOnlySet<string> Servable) FindEventTextures(
        GodotPck pck, List<string> missingBorrowed)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var known = Sts2.SeedFinder.Core.Acts.ActCatalog.ActNumbers
            .SelectMany(Sts2.SeedFinder.Core.Acts.ActCatalog.EventNames)
            .Select(Sts2.SeedFinder.Core.Acts.ActCatalog.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pck.Entries.Keys)
        {
            var m = EventSource().Match(path);
            if (!m.Success || !known.Contains(m.Groups[1].Value)) continue;

            var raw = pck.Read(path);
            if (raw is null) continue;

            var texture = ResolveImportTarget(pck, raw);
            if (texture is null) continue;

            paths[m.Groups[1].Value] = texture;
        }

        // Only fills gaps, so a patch that gives either event real art of its own wins silently.
        // A borrowed path that stops resolving is REPORTED rather than swallowed: these two point
        // into odd corners of the .pck (a game-over screen icon, a quest card portrait), so they
        // are likelier to move than the folders the patterns above walk, and the symptom would
        // otherwise just be a lettered tile nobody connects to a game update.
        foreach (var (slug, source) in BorrowedEventArt)
        {
            if (paths.ContainsKey(slug)) continue;

            var raw = pck.Read(source);
            var texture = raw is null ? null : ResolveImportTarget(pck, raw);
            if (texture is null) { missingBorrowed.Add(slug); continue; }

            paths[slug] = texture;
        }

        return (paths, Probe(pck, paths));
    }

    /// <summary>
    /// Rank of an imported variant, lowest wins. A plain .ctex may hold WebP, which the browser
    /// decodes itself at full quality; block-compressed forms cost us a decode and are lossy.
    /// </summary>
    private static int VariantRank(string? variant) => variant switch
    {
        null or "" => 0,
        "bptc" => 1,
        "s3tc" => 2,
        _ => 3,
    };

    /// <summary>
    /// The best imported texture a .import file names, or null when it names none this install
    /// actually contains. A file can name one per GPU family, so this picks rather than takes
    /// the first.
    /// </summary>
    private static string? ResolveImportTarget(GodotPck pck, byte[] importFile)
    {
        string? best = null;
        int bestRank = int.MaxValue;

        foreach (Match m in ImportTarget().Matches(System.Text.Encoding.UTF8.GetString(importFile)))
        {
            var file = m.Groups["file"].Value;
            if (!pck.Entries.ContainsKey(file)) continue;

            int rank = VariantRank(m.Groups["variant"].Success ? m.Groups["variant"].Value : null);
            if (rank < bestRank) { best = file; bestRank = rank; }
        }
        return best;
    }

    /// <summary>Which of these actually decode, so the UI never renders a broken image element.</summary>
    private static IReadOnlySet<string> Probe(GodotPck pck, Dictionary<string, string> paths)
    {
        var servable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slug, path) in paths)
        {
            var raw = pck.Read(path);
            if (raw is null) continue;
            if (CompressedTexture.Parse(raw) is { } tex &&
                tex.Kind is "webp" or "png" or "rgba8" or "bc7" or "dxt1" or "dxt5")
                servable.Add(slug);
        }
        return servable;
    }

    public Task<AssetImage?> TryGetAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Decode(slug, _texturePaths, _cache));

    public Task<AssetImage?> TryGetCardAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Decode(slug, _cardTexturePaths, _cardCache));

    public Task<AssetImage?> TryGetCharacterAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Decode(slug, _charTexturePaths, _charCache));

    public Task<AssetImage?> TryGetAncientAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Decode(slug, _ancientTexturePaths, _ancientCache));

    /// <summary>Event art is a full-screen illustration, so it is shrunk to a tile on the way out.</summary>
    public Task<AssetImage?> TryGetEventAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Decode(slug, _eventTexturePaths, _eventCache, EventMaxEdge));

    /// <summary>
    /// Sized by the SHORT edge, which is what a square tile actually crops from: at 2.1:1 this
    /// leaves about 70 px of height, enough for a 26 px tile on a 2x display. Sizing by the long
    /// edge instead would have left the crop soft.
    /// </summary>
    private const int EventMaxEdge = 160;

    /// <param name="maxEdge">
    /// Longest edge to serve, or 0 for the texture's own size. Relic icons and portraits are
    /// already icon-sized, but an event's art is the full-screen illustration — 3440x1616, a
    /// 1.3 MB PNG and 22 MB of pixels, for a tile drawn at 26 px. Shrinking it here rather than
    /// in the browser is what keeps the picker's first paint and the cache reasonable.
    /// </param>
    private AssetImage? Decode(
        string slug,
        IReadOnlyDictionary<string, string> texturePaths,
        ConcurrentDictionary<string, AssetImage?> cache,
        int maxEdge = 0)
    {
        return cache.GetOrAdd(slug.ToLowerInvariant(), key =>
        {
            if (!texturePaths.TryGetValue(key, out var path)) return null;

            var raw = _pck.Read(path);
            if (raw is null) return null;

            var tex = CompressedTexture.Parse(raw);
            if (tex is null) return null;

            return tex.Value.Kind switch
            {
                // Browsers decode these themselves — no work for us, and no quality loss. Left
                // whole even when maxEdge is set: resizing would mean decoding it ourselves,
                // which is the cost the passthrough exists to avoid.
                "webp" => new AssetImage(tex.Value.Data, "image/webp"),
                "png" => new AssetImage(tex.Value.Data, "image/png"),

                "rgba8" => ToPng(tex.Value.Data, tex.Value, maxEdge),

                "bc7" => ToPng(Bc7Decoder.Decode(tex.Value.Data, tex.Value.Width, tex.Value.Height), tex.Value, maxEdge),
                "dxt1" => ToPng(S3tcDecoder.DecodeDxt1(tex.Value.Data, tex.Value.Width, tex.Value.Height), tex.Value, maxEdge),
                "dxt5" => ToPng(S3tcDecoder.DecodeDxt5(tex.Value.Data, tex.Value.Width, tex.Value.Height), tex.Value, maxEdge),

                _ => null,
            };
        });
    }

    private static AssetImage ToPng(byte[] rgba, CompressedTexture.Texture tex, int maxEdge = 0)
    {
        var (pixels, w, h) = (rgba, tex.Width, tex.Height);
        if (maxEdge > 0)
        {
            (pixels, w, h) = CropToSubject(pixels, w, h);
            (pixels, w, h) = Downscale(pixels, w, h, maxEdge);
        }
        return new AssetImage(Png.EncodeRgba(pixels, w, h), "image/png");
    }

    /// <summary>
    /// Crops a wide illustration to the square the tile will actually show, choosing WHERE by
    /// content rather than by a fixed offset.
    ///
    /// Needed because these are cinematic 2.1:1 scenes composed around a dialogue panel, and the
    /// subject is not reliably anywhere: the crystal sphere's reader is centred, the battleworn
    /// dummy sits a third of the way in, and Trial's is off to one side with most of the frame
    /// near-black. A single object-position guess produced good tiles for some events and solid
    /// black squares for others, so the window is scored instead — highest luminance variance
    /// wins, which finds detail against flat background rather than merely finding brightness.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) CropToSubject(byte[] rgba, int w, int h)
    {
        int side = Math.Min(w, h);
        if (w == side) return (rgba, w, h);

        // Coarse sampling: this runs on full-resolution art, and the answer only has to be good
        // to within a few pixels of a window that is then shrunk to well under a hundred.
        const int Step = 8;
        int bestLeft = 0;
        double bestScore = -1;

        for (int left = 0; left + side <= w; left += Step * 4)
        {
            double sum = 0, sumSq = 0;
            int n = 0;
            for (int y = 0; y < h; y += Step)
            {
                for (int x = left; x < left + side; x += Step)
                {
                    int i = (y * w + x) * 4;
                    // Rec. 601 luma, weighted by alpha so transparent margins do not read as
                    // detail. Integer weights keep this cheap in the inner loop.
                    double lum = (rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114) / 1000.0
                                 * (rgba[i + 3] / 255.0);
                    sum += lum;
                    sumSq += lum * lum;
                    n++;
                }
            }
            if (n == 0) continue;

            double mean = sum / n;
            double score = sumSq / n - mean * mean;
            if (score > bestScore) { bestScore = score; bestLeft = left; }
        }

        var cropped = new byte[side * h * 4];
        for (int y = 0; y < h; y++)
            Array.Copy(rgba, (y * w + bestLeft) * 4, cropped, y * side * 4, side * 4);

        return (cropped, side, h);
    }

    /// <summary>
    /// Box-average downscale by a whole-number factor, which is all this needs: the target is a
    /// small tile, so picking the smallest factor that fits under <paramref name="maxEdge"/>
    /// lands close enough, and averaging whole source blocks avoids the aliasing that sampling
    /// one pixel per block would give on detailed art.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) Downscale(byte[] rgba, int w, int h, int maxEdge)
    {
        int factor = Math.Max(1, (Math.Max(w, h) + maxEdge - 1) / maxEdge);
        if (factor == 1) return (rgba, w, h);

        int dw = Math.Max(1, w / factor), dh = Math.Max(1, h / factor);
        var outPixels = new byte[dw * dh * 4];

        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = y * factor; sy < Math.Min((y + 1) * factor, h); sy++)
                {
                    for (int sx = x * factor; sx < Math.Min((x + 1) * factor, w); sx++)
                    {
                        int i = (sy * w + sx) * 4;
                        r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; a += rgba[i + 3];
                        n++;
                    }
                }
                int o = (y * dw + x) * 4;
                outPixels[o] = (byte)(r / n);
                outPixels[o + 1] = (byte)(g / n);
                outPixels[o + 2] = (byte)(b / n);
                outPixels[o + 3] = (byte)(a / n);
            }
        }
        return (outPixels, dw, dh);
    }

    /// <summary>
    /// Decodes every icon once in the background. Each one costs a block-decompression pass
    /// plus a PNG encode, which is fast individually but adds up to a visibly empty grid when
    /// sixty of them are requested at once on first paint.
    /// </summary>
    public void WarmCache(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct,
            };
            Parallel.ForEach(_texturePaths.Keys, options, slug => Decode(slug, _texturePaths, _cache));
            Parallel.ForEach(_cardTexturePaths.Keys, options, slug => Decode(slug, _cardTexturePaths, _cardCache));
            Parallel.ForEach(_charTexturePaths.Keys, options, slug => Decode(slug, _charTexturePaths, _charCache));
            Parallel.ForEach(_ancientTexturePaths.Keys, options,
                slug => Decode(slug, _ancientTexturePaths, _ancientCache));
            // Events are warmed too, despite being the most expensive to decode, because they
            // are all requested at once the moment the event picker opens. What makes it
            // affordable is that they are downscaled before caching.
            Parallel.ForEach(_eventTexturePaths.Keys, options,
                slug => Decode(slug, _eventTexturePaths, _eventCache, EventMaxEdge));
        }, ct);
}
