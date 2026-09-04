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

    // The run-history icon set: one 88px COLOURED icon for every Ancient, every boss, and every
    // room type, all in one folder. Preferred over images/packed/map/ancients/, which holds the
    // same Ancients as flat white silhouettes the game tints at runtime — those draw as featureless
    // blobs outside the game. "_outline" is a separate layer, so the pattern excludes it.
    [GeneratedRegex(@"^images/ui/run_history/([a-z0-9_]+)\.png\.import$")]
    private static partial Regex AncientSource();

    // One illustration per event, in a flat folder named by the same slug we use. A few entries
    // there are alternate states of an event rather than events (trial_started,
    // zen_weaver_phobia_mode, dense_vegetation_foreground), so the caller filters against the
    // act tables instead of trusting the folder.
    // The map's room-type icons. Unlike everything else here these are not files: they are
    // regions of a shared sprite sheet, so the .tres names the sheet and the rectangle to cut.
    //
    // Scoped to icons/ deliberately. The same atlas also carries map/ancients/ and
    // map/placeholder/, but neither is worth taking from here: the ancients are the same white
    // masks their standalone files are, and the boss sprites are inconsistent — Soul Fysh is
    // full colour while Vantom is a near-black silhouette. Tinting the standalone masks gives
    // every boss and ancient the same treatment, which reads better than a mix.
    [GeneratedRegex(@"^images/atlases/ui_atlas\.sprites/map/icons/([a-z0-9_]+)\.tres$")]
    private static partial Regex MapIconSource();

    /// <summary>The sheet an AtlasTexture points at.</summary>
    [GeneratedRegex(@"path=""res://(?<sheet>[^""]+\.png)""")]
    private static partial Regex AtlasSheet();

    /// <summary>Its rectangle. Godot writes these as floats, so the fraction is optional.</summary>
    [GeneratedRegex(@"region\s*=\s*Rect2\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\)")]
    private static partial Regex AtlasRegion();

    // Every boss, from the same run-history set, and the only source that covers all of them:
    // images/map/placeholder/ is missing Ceremonial Beast, the False Queen and The Insatiable,
    // whose map nodes are Spine-animated and ship no still there.
    [GeneratedRegex(@"^images/ui/run_history/([a-z0-9_]+)_boss\.png\.import$")]
    private static partial Regex BossSource();

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
    private readonly Dictionary<string, string> _bossTexturePaths;    // boss slug  -> pck path
    private readonly Dictionary<string, MapIconSprite> _mapIcons;     // icon name  -> sheet + rect
    private readonly ConcurrentDictionary<string, AssetImage?> _cache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _cardCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _charCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _ancientCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _eventCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _bossCache = new();
    private readonly ConcurrentDictionary<string, AssetImage?> _mapIconCache = new();

    /// <summary>
    /// Decoded sprite sheets, kept whole. A sheet is 2048x2048, so decoding one costs 16 MB of
    /// pixels — but every icon on the map is a rectangle of the SAME sheet, so decoding once and
    /// cutting from it is the difference between one decode and a dozen. Filled lazily, so an
    /// install whose maps are never opened never pays for it.
    /// </summary>
    private readonly ConcurrentDictionary<string, DecodedSheet?> _sheetCache = new();
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
    public IReadOnlySet<string> AvailableBossSlugs { get; }
    public IReadOnlySet<string> AvailableMapIcons => _mapIcons.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every description the install has, for exporting to a bundled directory.</summary>
    public IReadOnlyDictionary<string, AssetText> AllText => _text;

    private LocalGameAssetProvider(GodotPck pck, Dictionary<string, string> texturePaths, string status,
        IReadOnlySet<string> available, IReadOnlyDictionary<string, AssetText> text,
        Dictionary<string, string> cardTexturePaths, IReadOnlySet<string> cardsAvailable,
        IReadOnlyDictionary<string, AssetText> cardText,
        Dictionary<string, string> charTexturePaths, IReadOnlySet<string> charsAvailable,
        Dictionary<string, string> ancientTexturePaths, IReadOnlySet<string> ancientsAvailable,
        Dictionary<string, string> eventTexturePaths, IReadOnlySet<string> eventsAvailable,
        IReadOnlyDictionary<string, AssetText> eventText,
        Dictionary<string, string> bossTexturePaths, IReadOnlySet<string> bossesAvailable,
        Dictionary<string, MapIconSprite> mapIcons)
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
        _bossTexturePaths = bossTexturePaths;
        AvailableBossSlugs = bossesAvailable;
        _mapIcons = mapIcons;
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
        var (bossPaths, bossesServable) = FindBossTextures(pck);
        var mapIcons = FindMapIcons(pck);
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
        if (bossesServable.Count > 0) status += $", {bossesServable.Count} boss nodes";
        if (mapIcons.Count > 0) status += $", {mapIcons.Count} map icons";
        if (eventsServable.Count > 0) status += $", {eventsServable.Count} events";
        if (missingBorrowed.Count > 0)
            status += $" [borrowed art missing for {string.Join(", ", missingBorrowed)}"
                + " - the game may have moved it]";
        if (eventText.Count > 0) status += $" ({eventText.Count} described"
            + (vars.Events.Count > 0 ? $", {vars.Events.Count} with real numbers)" : ")");

        return new LocalGameAssetProvider(pck, chosen, status, servable, text,
            cardPaths, cardsServable, cardText, charPaths, charsServable,
            ancientPaths, ancientsServable, eventPaths, eventsServable, eventText,
            bossPaths, bossesServable, mapIcons);
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
    /// Each boss's map-node still, anchored on the act tables the same way the events are.
    ///
    /// Coverage is PARTIAL by design, not by accident: Ceremonial Beast, the False Queen and The
    /// Insatiable are animated on the map with Spine skeletons and ship no still image, so they
    /// simply never appear here and the map draws them as an ordinary boss node.
    /// </summary>
    private static (Dictionary<string, string> Paths, IReadOnlySet<string> Servable) FindBossTextures(GodotPck pck)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var known = Sts2.SeedFinder.Core.Acts.ActCatalog.ActNumbers
            .SelectMany(Sts2.SeedFinder.Core.Acts.ActCatalog.Bosses)
            .Select(b => Sts2.SeedFinder.Core.Acts.ActCatalog.Slug(b.TypeName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pck.Entries.Keys)
        {
            var m = BossSource().Match(path);
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

    /// <summary>One icon: which sheet it is on, and the rectangle to cut from it.</summary>
    public readonly record struct MapIconSprite(string SheetTexture, int X, int Y, int W, int H);

    /// <summary>A decoded sheet, held whole so many icons can be cut from one decode.</summary>
    private readonly record struct DecodedSheet(byte[] Rgba, int Width, int Height);

    /// <summary>
    /// The map's room-type icons — monsters, elites, campfires, shops, chests and the `?`.
    ///
    /// These are the one asset here that is not a file. The game packs them into a shared 2048px
    /// sheet and ships a small text resource per icon naming the sheet and a Rect2, so this reads
    /// the rectangles now and cuts the pixels on demand.
    ///
    /// It is worth having rather than drawing our own: these are the shapes players actually
    /// recognise, and a hand-drawn approximation is guesswork about someone else's art.
    /// </summary>
    private static Dictionary<string, MapIconSprite> FindMapIcons(GodotPck pck)
    {
        var icons = new Dictionary<string, MapIconSprite>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pck.Entries.Keys)
        {
            var m = MapIconSource().Match(path);
            if (!m.Success) continue;

            var raw = pck.Read(path);
            if (raw is null) continue;
            var text = System.Text.Encoding.UTF8.GetString(raw);

            var sheet = AtlasSheet().Match(text);
            var region = AtlasRegion().Match(text);
            if (!sheet.Success || !region.Success) continue;

            // The .tres points at the SOURCE png; the pixels live in whatever the importer
            // produced from it, so this takes the same .import detour every other asset does.
            var importRaw = pck.Read(sheet.Groups["sheet"].Value + ".import");
            if (importRaw is null) continue;

            var texture = ResolveImportTarget(pck, importRaw);
            if (texture is null) continue;

            static int Coord(Group g) => (int)Math.Round(double.Parse(
                g.Value, System.Globalization.CultureInfo.InvariantCulture));

            icons[m.Groups[1].Value] = new MapIconSprite(
                texture,
                Coord(region.Groups[1]), Coord(region.Groups[2]),
                Coord(region.Groups[3]), Coord(region.Groups[4]));
        }

        return icons;
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

    public Task<AssetImage?> TryGetMapIconAsync(string name, CancellationToken ct) =>
        Task.FromResult(_mapIconCache.GetOrAdd(name.ToLowerInvariant(), key =>
        {
            if (!_mapIcons.TryGetValue(key, out var sprite)) return null;

            var sheet = _sheetCache.GetOrAdd(sprite.SheetTexture, DecodeSheet);
            if (sheet is null) return null;

            var (rgba, w, h) = Crop(sheet.Value, sprite);
            return w <= 0 || h <= 0 ? null : new AssetImage(Png.EncodeRgba(rgba, w, h), "image/png");
        }));

    /// <summary>
    /// A whole sprite sheet as pixels. Only the block formats are handled: a sheet that arrived
    /// as webp or png would have to be decoded before it could be cut, and this build ships them
    /// as BPTC, so that path has never been needed.
    /// </summary>
    private DecodedSheet? DecodeSheet(string texturePath)
    {
        var raw = _pck.Read(texturePath);
        if (raw is null) return null;

        var tex = CompressedTexture.Parse(raw);
        if (tex is null) return null;

        var rgba = tex.Value.Kind switch
        {
            "rgba8" => tex.Value.Data,
            "bc7" => Bc7Decoder.Decode(tex.Value.Data, tex.Value.Width, tex.Value.Height),
            "dxt1" => S3tcDecoder.DecodeDxt1(tex.Value.Data, tex.Value.Width, tex.Value.Height),
            "dxt5" => S3tcDecoder.DecodeDxt5(tex.Value.Data, tex.Value.Width, tex.Value.Height),
            _ => null,
        };

        return rgba is null ? null : new DecodedSheet(rgba, tex.Value.Width, tex.Value.Height);
    }

    /// <summary>
    /// Cuts one rectangle out of a sheet, clamped to it. The clamp is not defensive noise: a
    /// patch that repacks the atlas without repacking the .tres files would otherwise read off
    /// the end of the buffer rather than simply producing a wrong-looking icon.
    /// </summary>
    private static (byte[] Rgba, int W, int H) Crop(DecodedSheet sheet, MapIconSprite sprite)
    {
        int x = Math.Clamp(sprite.X, 0, sheet.Width);
        int y = Math.Clamp(sprite.Y, 0, sheet.Height);
        int w = Math.Clamp(sprite.W, 0, sheet.Width - x);
        int h = Math.Clamp(sprite.H, 0, sheet.Height - y);
        if (w <= 0 || h <= 0) return (Array.Empty<byte>(), 0, 0);

        var cut = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(sheet.Rgba, ((y + row) * sheet.Width + x) * 4, cut, row * w * 4, w * 4);

        return (cut, w, h);
    }

    public Task<AssetImage?> TryGetBossAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Decode(slug, _bossTexturePaths, _bossCache));

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
