using System.Text;
using System.Text.Json;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Map;
using Sts2.SeedFinder.Core.Neow;
using Sts2.SeedFinder.Core.Saves;
using Sts2.SeedFinder.Web;
using Sts2.SeedFinder.Web.Assets;
using Sts2.SeedFinder.Core.Install;

// One-time export: decode every relic icon out of a local game install into a folder, so a
// deployed instance can serve art with the `bundled` provider and no game present.
// Read docs/web_app_specs.md section 4 before publishing the output anywhere.
if (args is [var flag, var target, ..] && flag is "--export-assets")
    return await AssetExport.RunAsync(target, args.Skip(2).FirstOrDefault());

// Anchor the content root to the exe rather than the working directory. The default is
// wherever you happened to launch from, which means the UI serves from `dotnet run` (that
// sets the working directory to the project folder) and 404s from every other entry point.
// The csproj copies wwwroot into the output directory to make this hold.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Pin the port so the address in the README is always the address you get. Without this the
// framework default (5000) applies, which is also whatever else on the machine grabbed it
// first. ASPNETCORE_URLS and --urls still win, so this is a default rather than a decree.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5173");
}

builder.Services.AddSingleton(AssetProviderFactory.Create(builder.Configuration));

// One planner for the process. Probing for a device and JIT-compiling the kernels costs far
// more than a search does, so doing it per request would make the accelerated path the slower
// one. Create() never throws: a machine with no usable GPU gets a planner that declines every
// search and the CPU path runs as before.
builder.Services.AddSingleton(Sts2.SeedFinder.Gpu.GpuSearchPlanner.Create());

var app = builder.Build();

// Serve the page ourselves so its links to app.js / app.css can carry a stamp of when those
// files last changed.
//
// Cache-Control alone is not enough to fix this. A browser that cached app.js *before* this
// app started sending cache headers applies its own heuristic freshness and will not even ask
// the server, so an update arrives as new HTML wired to old script and styling — which looks
// like the update half-landed rather than like a caching problem. A stamped URL is a different
// URL, so a changed file can never be answered from the cache of the old one.
app.Use(async (ctx, next) =>
{
    var root = app.Environment.WebRootPath;
    var index = root is null ? null : Path.Combine(root, "index.html");

    if (ctx.Request.Path != "/" && ctx.Request.Path != "/index.html"
        || index is null || !File.Exists(index))
    {
        await next();
        return;
    }

    long stamp = 0;
    foreach (var name in (string[])["app.js", "app.css"])
    {
        var path = Path.Combine(root!, name);
        if (File.Exists(path)) stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(path).Ticks);
    }
    var v = stamp.ToString("x");

    var html = (await File.ReadAllTextAsync(index, ctx.RequestAborted))
        .Replace("\"app.js\"", $"\"app.js?v={v}\"")
        .Replace("\"app.css\"", $"\"app.css?v={v}\"");

    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(html, ctx.RequestAborted);
});

app.UseDefaultFiles();

// Make the browser revalidate the UI files on every load. Without this it caches app.js
// heuristically, and a rebuilt or updated copy silently keeps running the old one — a
// confusing failure, since the server is serving the new file the whole time. "no-cache"
// still returns 304 when nothing changed, so this costs a conditional request, not a
// download.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

// Decode the icons up front rather than on first paint, when the browser asks for sixty at once.
if (app.Services.GetRequiredService<IGameAssetProvider>() is LocalGameAssetProvider local)
    local.WarmCache(app.Lifetime.ApplicationStopping);

// Predictions are computed for one game build. A patch can resize the content pools, pool size
// sets how many draws each shuffle costs, and every draw after that lands somewhere else — so
// the output stays plausible and stops being true. Silence is the worst outcome here, because
// art and descriptions keep updating from the user's install and a stale build looks healthy.
var installDir = GameInstall.Find(app.Configuration["Assets:GameDirectory"]);
var release = GameInstall.ReadRelease(installDir);
var verified = VerifiedBuild.Load();
var drift = DriftReport.For(release, verified);
var GameVersion = release.Version ?? verified.Version;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ---- Catalog: everything the UI needs to build its pickers, in one request ----------------

app.MapGet("/api/catalog", (IGameAssetProvider assets) =>
{
    string? Describe(string slug) => assets.TryGetText(slug)?.Description;

    RelicDto Neow(NeowRelic r, string group) => new(
        r.Slug, r.Name, group, RelicNotes.For(r),
        assets.AvailableSlugs.Contains(r.Slug), Describe(r.Slug));

    var ancients = Enum.GetValues<Ancient>().Select(a => new AncientDto(
        a.ToString(),
        a.ToString(),
        RelicNotes.ActsFor(a),
        RelicNotes.IsSeedDetermined(a),
        RelicNotes.DeckNoteFor(a),
        AncientOffers.AllRelics(a)
            .Select(r => new RelicDto(
                AncientOffers.Slug(r), AncientOffers.Display(r), a.ToString(),
                RelicNotes.ForAncientRelic(a, r),
                assets.AvailableSlugs.Contains(AncientOffers.Slug(r)),
                Describe(AncientOffers.Slug(r))))
            .OrderBy(r => r.Name).ToArray()))
        .ToArray();

    // Bosses and events, grouped so the pickers can show which Act 1 map each belongs to.
    // Nothing about these is per-player: one boss and one event order serve the whole lobby.
    // `text` is supplied for events only. Where the install has a title it wins over our
    // name-splitter, which is the same call cards make: splitting on capitals gets "Doors Of
    // Light And Dark" and "Welcome To Wongos", where the game says "Doors of Light and Dark"
    // and "Welcome to Wongo's".
    static ActThingDto[] Group(
        IEnumerable<(string TypeName, string Map)> pool,
        Func<string, AssetText?>? text = null) =>
        pool.GroupBy(x => x.TypeName)
            .Select(g =>
            {
                var slug = ActCatalog.Slug(g.Key);
                var loc = text?.Invoke(slug);
                return new ActThingDto(
                    slug,
                    string.IsNullOrWhiteSpace(loc?.Title) ? ActCatalog.Display(g.Key) : loc.Value.Title!,
                    g.Select(x => x.Map).Distinct().ToArray(),
                    string.IsNullOrWhiteSpace(loc?.Description) ? null : loc.Value.Description);
            })
            .OrderBy(x => x.Name)
            .ToArray();

    var actContent = ActCatalog.ActNumbers.Select(act => new ActContentDto(
        act,
        ActData.ByIndex[act - 1].Select(m => m.Name).ToArray(),
        Group(ActCatalog.Bosses(act)),
        Group(ActCatalog.Events(act), assets.TryGetEventText))).ToArray();

    // Singleplayer-only relics can never appear in co-op, so they are never offered as
    // choices — Silver Crucible in the curse branch and Winged Boots in the positive pool
    // both gate on Players.Count == 1. Offering them would let a user build a search that
    // cannot match by construction.
    static bool InCoop(NeowRelic r) => r.Availability != RelicAvailability.SingleplayerOnly;

    // One pool per character, because the card a player can be offered depends entirely on who
    // they picked. The game's own title is preferred over our name-splitter wherever the
    // install has one, since a few cards do not split on capitals the way a reader expects.
    //
    // The WHOLE reward pool, rares included. Rares used to be omitted because the only card
    // feature was the first fight, which can never roll one — but fight 2 can, so hiding them
    // here would make a reachable search unexpressible. The picker greys them out while the row
    // is still on fight 1, which is the same information without the dead end.
    var cardPools = CardCatalog.Characters.Select(c => new CardPoolDto(
        c.ToString(),
        CardCatalog.Offerable(c).Select(card =>
        {
            var slug = CardCatalog.Slug(card.TypeName);
            var text = assets.TryGetCardText(slug);
            return new CardDto(
                slug,
                text?.Title ?? CardCatalog.Display(card.TypeName),
                card.Rarity.ToString(),
                assets.AvailableCardSlugs.Contains(slug),
                text?.Description);
        }).ToArray())).ToArray();

    // Shop relics share the relic art and text tables with Neow's, so they need no new asset
    // path. The game's own title wins over our name-splitter, which loses apostrophes.
    var shopRelics = ShopRelics.All.Select(r =>
    {
        var text = assets.TryGetText(r.Slug);
        return new ShopRelicDto(
            r.Slug,
            text?.Title ?? ShopRelics.Display(r.Slug),
            ShopRelics.OwnerOf(r.Slug)?.ToString(),
            assets.AvailableSlugs.Contains(r.Slug),
            text?.Description);
    }).ToArray();

    // Chest relics are the shared pool's Common/Uncommon/Rare entries. No Character field: a
    // chest draws from the shared bag only, so nobody's own relic can reach one.
    var chestRelics = ChestRelics.All.Select(r =>
    {
        var text = assets.TryGetText(r.Slug);
        return new ChestRelicDto(
            r.Slug,
            text?.Title ?? ChestRelics.Display(r.Slug),
            r.Rarity,
            assets.AvailableSlugs.Contains(r.Slug),
            text?.Description);
    }).ToArray();

    // The Neow options that touch a player's card stream, with both numbers the UI needs: how
    // many cards the tool can name for it, and what taking it costs the fight rewards. Sent as
    // a table rather than hardcoded in the page, because both come out of Core and a second
    // copy in JavaScript would be free to drift after a patch.
    var neowCardRelics = NeowRelics.Curses.Concat(NeowRelics.Positives).Concat(NeowRelics.CoinFlips)
        .Where(InCoop)
        .Select(r => new NeowCardRelicDto(
            r.Slug,
            assets.TryGetText(r.Slug)?.Title ?? r.Name,
            NeowCardPayload.CardCount(r.Slug),
            CardRewardGenerator.NeowRewardDrawCost(r.Slug, Character.Ironclad),
            CardRewardGenerator.NeowRewardDrawCost(r.Slug, Character.Defect)))
        .Where(r => r.PayloadCards > 0 || r.Draws != 0)
        .ToArray();

    return Results.Json(new CatalogDto(
        GameVersion,
        AppVersion.Load().Version,
        CardRewardGenerator.MaxPredictableFight,
        drift.Warn ? drift.Message : null,
        assets.Kind.ToString().ToLowerInvariant(),
        assets.Status,
        NeowRelics.Curses.Where(InCoop).Select(r => Neow(r, "curse")).ToArray(),
        NeowRelics.Positives.Where(InCoop).Select(r => Neow(r, "positive")).ToArray(),
        NeowRelics.CoinFlips.Where(InCoop).Select(r => Neow(r, "coinflip")).ToArray(),
        ancients,
        // The slug is just the lowercased enum name, which is what the game names its
        // character-select art by, so no mapping table is needed.
        Enum.GetNames<Character>()
            .Select(n => new CharacterDto(n, n.ToLowerInvariant(),
                assets.AvailableCharacterSlugs.Contains(n.ToLowerInvariant())))
            .ToArray(),
        ActData.ByIndex[0].Select(a => a.Name).ToArray(),
        actContent,
        cardPools,
        shopRelics,
        chestRelics,
        // Lowercased, which is both what /api/asset/ancient serves by and what the UI keys on.
        assets.AvailableAncientSlugs.Select(s => s.ToLowerInvariant()).ToArray(),
        // Events carry their art the same way: a flat list rather than a flag on each event,
        // since an event appears once per act it can turn up in and the art does not.
        assets.AvailableEventSlugs.Select(s => s.ToLowerInvariant()).ToArray(),
        assets.AvailableBossSlugs.Select(s => s.ToLowerInvariant()).ToArray(),
        neowCardRelics), json);
});

// ---- The player's own profile --------------------------------------------------------------

// Reads progress.save so a search can be run against the account's real unlock state instead of
// the fully-unlocked guess. That guess is right for most players and quietly wrong for new ones:
// a locked epoch removes relics, a smaller relic bag costs fewer shuffle draws, and every draw
// after it lands somewhere else. The result is not slightly off, it is a different run.
//
// Also reports the run in progress, when there is one, because its lobby is exactly the set of
// inputs a search needs and would otherwise be retyped.
app.MapGet("/api/profile", (IConfiguration config) =>
{
    var configured = config["Saves:Directory"];
    var profile = ProfileReader.Read(configured);
    var run = ProfileReader.CurrentRun(configured);

    if (profile is null)
    {
        return Results.Json(new ProfileDto(
            Found: false,
            Path: null,
            SearchedIn: SaveLocations.Roots(configured).ToArray(),
            OverrideVariable: SaveLocations.OverrideVariable,
            RevealedEpochs: 0, TotalEpochs: 0, FullyUnlocked: false,
            DiscoveredActs: [], Lobby: null, Code: null), json);
    }

    return Results.Json(new ProfileDto(
        Found: true,
        Path: profile.Path,
        SearchedIn: [],
        OverrideVariable: SaveLocations.OverrideVariable,
        RevealedEpochs: profile.RevealedEpochs,
        TotalEpochs: profile.TotalEpochs,
        FullyUnlocked: profile.FullyUnlocked,
        DiscoveredActs: profile.DiscoveredActs.ToArray(),
        Lobby: run is null ? null : new LobbyDto(
            run.Seed, run.Characters.ToArray(), run.Ascension, run.IsMultiplayer),
        Code: UnlockCode.Encode(profile.Unlocks)), json);
});

// ---- Somebody else's unlock state ---------------------------------------------------------

// Unlock state is per player and only the local profile is readable, so a partner's has to
// arrive from their machine. The whole party's generation depends on it, not just theirs: the
// relic bags are shuffled off one shared stream in lobby order, so a partner whose pools are a
// different size moves every draw after their bag, and act generation comes after all of them.
//
// The file is read and thrown away. Nothing is stored and nothing leaves this machine, which
// matters because the thing being handed over is somebody else's save.
app.MapPost("/api/epochs/import", async (HttpRequest req, CancellationToken ct) =>
{
    // A progress.save is a few tens of KB. This cap is not defending a local server from an
    // attacker, it is failing fast and legibly when somebody drags in the wrong file.
    const int MaxBytes = 8 * 1024 * 1024;

    using var buffer = new MemoryStream();
    var chunk = new byte[64 * 1024];
    int read;
    while ((read = await req.Body.ReadAsync(chunk, ct)) > 0)
    {
        if (buffer.Length + read > MaxBytes)
            return Results.BadRequest(new
            {
                error = "that file is far bigger than a progress.save, so it is probably not one."
            });
        buffer.Write(chunk, 0, read);
    }

    // A save written with a byte-order mark parses fine as a file and not at all as a string.
    var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');

    var profile = ProfileReader.Parse(text, "imported");
    if (profile is null || profile.TotalEpochs == 0)
        return Results.BadRequest(new
        {
            error = "no epochs were found in that file. It should be progress.save, which sits "
                  + "beside the saves folder rather than inside it."
        });

    // Counted over the epochs that gate something we predict, not over every epoch the save
    // lists. A profile carries more than we model, and reporting its total here would put a
    // denominator on screen that the code beside it does not carry.
    var missing = UnlockCode.Missing(profile.Unlocks).ToArray();

    return Results.Json(new EpochImportDto(
        Code: UnlockCode.Encode(profile.Unlocks),
        Revealed: UnlockCode.RevealedCount(profile.Unlocks),
        Total: UnlockCode.Epochs.Count,
        FullyUnlocked: missing.Length == 0,
        Missing: missing), json);
});

// Reading back a code someone pasted, so the page can say what it means before a search runs
// rather than failing at search time with the row already filled in.
app.MapGet("/api/epochs/decode", (string? code) =>
{
    var state = UnlockCode.Decode(code);
    if (state is null)
        return Results.BadRequest(new
        {
            error = "that is not an unlock code this build can read. Codes are tied to the epochs "
                  + "a build knows about, so one made by a different version has to be imported again."
        });

    var missing = UnlockCode.Missing(state).ToArray();
    return Results.Json(new EpochImportDto(
        Code: UnlockCode.Encode(state),
        Revealed: UnlockCode.RevealedCount(state),
        Total: UnlockCode.Epochs.Count,
        FullyUnlocked: missing.Length == 0,
        Missing: missing), json);
});

// ---- Facts for a bug report -----------------------------------------------------------------

// Reports are filed by opening a prefilled GitHub issue in the user's own browser. Nothing is
// posted from here and no token exists, so this endpoint only gathers what the page cannot see
// for itself: the assembly hash behind the drift verdict, the accelerator actually chosen, and
// the baseline this build was verified against.
//
// Most "the seed was wrong" reports resolve to join order, player count, a patch, mods, or
// partial unlocks. All five are answerable from this block without a round trip, which is the
// whole reason for collecting it rather than asking.
app.MapGet("/api/report", (IConfiguration config, Sts2.SeedFinder.Gpu.GpuSearchPlanner planner) =>
{
    var profile = ProfileReader.Read(config["Saves:Directory"]);
    var repo = config["Updates:Repository"];

    return Results.Json(new ReportDto(
        Repository: string.IsNullOrWhiteSpace(repo) ? UpdateCheck.DefaultRepository : repo.Trim(),
        ToolVersion: AppVersion.Load().Version,
        GameVersion: GameVersion,
        VerifiedVersion: verified.Version,
        Drift: drift.Drift.ToString(),
        HasMods: release.HasMods,
        DriftWarning: drift.Warn ? drift.Message : null,
        Engine: planner.Status.Available ? planner.Status.Backend.ToLowerInvariant() : "cpu",
        Device: planner.Status.Available ? planner.Status.DeviceName : "-",
        ProfileFound: profile is not null,
        RevealedEpochs: profile?.RevealedEpochs ?? 0,
        TotalEpochs: profile?.TotalEpochs ?? 0,
        Platform: PlatformInfo.Describe()), json);
});

// ---- Is this copy still the newest one? -----------------------------------------------------

// Separate from the drift banner above, and the two are easy to confuse. Drift asks whether the
// GAME changed under a tool that is already installed. This asks whether the TOOL has moved on,
// which is the only one of the two a user can act on without understanding either.
//
// Behind an explicit request rather than run at startup: this is the one thing here that leaves
// the machine, so pressing the button is the consent.
app.MapGet("/api/update", async (IConfiguration config, CancellationToken ct) =>
    Results.Json(await UpdateCheck.RunAsync(config["Updates:Repository"], ct), json));

// ---- Relic and card art -------------------------------------------------------------------

app.MapGet("/api/asset/relic/{slug}", async (string slug, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetAsync(Path.GetFileNameWithoutExtension(slug), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

app.MapGet("/api/asset/card/{slug}", async (string slug, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetCardAsync(Path.GetFileNameWithoutExtension(slug), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

app.MapGet("/api/asset/character/{slug}", async (string slug, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetCharacterAsync(Path.GetFileNameWithoutExtension(slug), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

app.MapGet("/api/asset/ancient/{slug}", async (string slug, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetAncientAsync(Path.GetFileNameWithoutExtension(slug), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

app.MapGet("/api/asset/boss/{slug}", async (string slug, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetBossAsync(Path.GetFileNameWithoutExtension(slug), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

app.MapGet("/api/asset/mapicon/{name}", async (string name, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetMapIconAsync(Path.GetFileNameWithoutExtension(name), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

app.MapGet("/api/asset/event/{slug}", async (string slug, IGameAssetProvider assets, CancellationToken ct) =>
{
    var img = await assets.TryGetEventAsync(Path.GetFileNameWithoutExtension(slug), ct);
    return img is null ? Results.NotFound() : Results.File(img.Value.Bytes, img.Value.ContentType);
});

// ---- Inspect a single seed ---------------------------------------------------------------

app.MapGet("/api/explain", (HttpContext http, IConfiguration config, string seed, int players, string? characters) =>
{
    var canonical = SeedCodec.Canonicalize(seed);
    if (!SeedCodec.IsValid(canonical))
        return Results.BadRequest(new { error = $"'{seed}' is not a valid seed. Allowed characters: {SeedCodec.Alphabet}" });

    try
    {
        // The inspect panel honours ?pick= the same way a search does, so a seed looked at
        // with "P1 takes Arcane Scroll" set reads its card rewards one draw along.
        var unlocks = Query.LobbyUnlocks(http.Request.Query, players, config["Saves:Directory"]);

        return Results.Json(Predictions.Describe(
            canonical, players, Query.Characters(characters), null, Query.Ascension(http.Request.Query),
            unlocks.Run,
            Query.Int(http.Request.Query, "extraChests") ?? 0,
            Query.RewardPriorDraws(http.Request.Query, players, Query.Characters(characters)),
            unlocks.PerPlayer), json);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- The act maps ------------------------------------------------------------------------
// Each act map comes off its own rng stream ("act_n_map"), so this shares nothing with the
// prediction chain above and cannot be affected by it. It still needs the lobby, because act
// SELECTION depends on unlock state and the map is generated for whichever act was selected.
//
// All three acts are returned in one response. They are cheap next to a search, and the UI shows
// them side by side, so paging them would only add a round trip per column.

app.MapGet("/api/map", (HttpContext http, IConfiguration config, IGameAssetProvider assets,
                        string seed, int players, string? characters) =>
{
    var canonical = SeedCodec.Canonicalize(seed);
    if (!SeedCodec.IsValid(canonical))
        return Results.BadRequest(new { error = $"'{seed}' is not a valid seed. Allowed characters: {SeedCodec.Alphabet}" });

    try
    {
        var chars = Query.Characters(characters);
        var unlocks = Query.LobbyUnlocks(http.Request.Query, players, config["Saves:Directory"]);
        int ascension = Query.Ascension(http.Request.Query);
        bool isMultiplayer = players > 1;
        var runSeed = SeedCodec.RunSeed(canonical);

        var acts = RunGenerator.SelectActs(runSeed, unlocks.Run, isMultiplayer);
        var run = RunGenerator.GenerateRun(runSeed, unlocks.Run, isMultiplayer, chars, acts, ascension);

        var result = new List<MapActDto>();
        for (int i = 0; i < acts.Length; i++)
        {
            // Whether this act carries a second boss is a property of the RUN, not of the
            // ascension alone, so it is read back from generation rather than recomputed.
            bool hasSecondBoss = run.Acts[i].SecondBoss is not null;

            var map = ActMap.Generate(runSeed, i, acts[i], isMultiplayer, ascension, hasSecondBoss);

            // Slugs are sent only when the install actually has that art, so the page never
            // fires a request it knows will 404 and never leaves a broken node behind. Three
            // bosses are Spine-animated and have no still, so this is normally partial.
            string? BossArt(Encounter? e) =>
                e is not null && assets.AvailableBossSlugs.Contains(ActCatalog.Slug(e.Name))
                    ? ActCatalog.Slug(e.Name) : null;

            var ancientSlug = GameHash.SnakeCase(run.Acts[i].Ancient);

            result.Add(new MapActDto(
                Index: i + 1,
                Act: acts[i].Name,
                Boss: ActCatalog.Display(run.Acts[i].Boss.Name),
                SecondBoss: run.Acts[i].SecondBoss is { } sb ? ActCatalog.Display(sb.Name) : null,
                Ancient: run.Acts[i].Ancient,
                AncientArt: assets.AvailableAncientSlugs.Contains(ancientSlug) ? ancientSlug : null,
                BossArt: BossArt(run.Acts[i].Boss),
                SecondBossArt: BossArt(run.Acts[i].SecondBoss),
                Width: map.ColumnCount,
                Height: map.RowCount,
                Nodes: map.GetAllMapPoints().Select(ToNode).ToArray(),
                Start: ToNode(map.StartingMapPoint),
                BossNode: ToNode(map.BossMapPoint),
                SecondBossNode: map.SecondBossMapPoint is { } second ? ToNode(second) : null));
        }

        // Which room-type icons this install can serve, so the page uses real art where it
        // exists and its own drawn tokens where it does not, without probing for 404s.
        return Results.Json(new
        {
            seed = canonical,
            acts = result,
            icons = assets.AvailableMapIcons.ToArray(),
        }, json);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    static MapNodeDto ToNode(MapPoint p) => new(
        p.Coord.Col, p.Coord.Row,
        GameHash.Slugify(p.PointType.ToString()).ToLowerInvariant(),
        p.Children.Select(c => $"{c.Coord.Col},{c.Coord.Row}").ToArray());
});

// ---- Search, streamed --------------------------------------------------------------------
// Results arrive over Server-Sent Events so a long scan shows progress instead of hanging,
// and so cancelling actually stops the work rather than just abandoning the response.

app.MapGet("/api/search", async (HttpContext http, IConfiguration config) =>
{
    var q = http.Request.Query;
    var ct = http.RequestAborted;

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    // One writer at a time. Hits are sent from the request's own loop while progress ticks are
    // sent from a timer, and two interleaved writes would splice one event's frame into the
    // other's, which an EventSource reports as a transport error rather than as bad data.
    var writing = new SemaphoreSlim(1, 1);

    async Task Send(string evt, object payload)
    {
        await writing.WaitAsync(ct);
        try
        {
            await http.Response.WriteAsync(
                $"event: {evt}\ndata: {JsonSerializer.Serialize(payload, json)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
        finally
        {
            writing.Release();
        }
    }

    SearchCriteria criteria;
    int players;
    IReadOnlyList<Character> chars;
    try
    {
        (criteria, players, chars) = Query.BuildCriteria(q, config["Saves:Directory"]);
    }
    catch (ArgumentException ex)
    {
        await Send("error", new { error = ex.Message });
        return;
    }

    ulong start = Query.ULong(q, "start") ?? Query.RandomStart();
    ulong count = Query.ULong(q, "count") ?? 5_000_000;
    // Clamped server-side too, not just in the page. Every result streams a full run card with
    // art, so a few hundred is enough to make the browser crawl, and /api/search is reachable
    // without the page. The CLI is left uncapped: it prints text and the caller asked for it.
    int max = Math.Clamp((int)(Query.ULong(q, "results") ?? 25), 1, 100);

    // The pre-filter only narrows which indices get examined; every one it yields still goes
    // through the same criteria chain, so an accelerated search and a plain one return the
    // same set. Reported to the client so the UI can say which engine ran.
    var planner = app.Services.GetRequiredService<Sts2.SeedFinder.Gpu.GpuSearchPlanner>();
    var progress = new SearchProgress();
    bool accelerated = planner.TryPlan(criteria, start, count, ct, out var candidates, progress);

    await Send("start", new
    {
        start,
        count,
        results = max,
        engine = accelerated ? planner.Status.Backend.ToLowerInvariant() : "cpu",
        device = accelerated ? planner.Status.DeviceName : null,
    });

    int found = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // Progress on a timer rather than per hit. A search that has not found anything yet is
    // exactly the one whose rate the user wants to see, and it is the one that would otherwise
    // report nothing at all until it finished.
    using var ticking = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var ticker = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        try
        {
            while (await timer.WaitForNextTickAsync(ticking.Token))
                await Send("progress", new { scanned = progress.Scanned, seconds = sw.Elapsed.TotalSeconds });
        }
        catch (OperationCanceledException) { /* the search finished */ }
    }, CancellationToken.None);

    try
    {
        foreach (var hit in SeedSearcher.Search(criteria, start, count, max, ct, candidates, progress))
        {
            if (ct.IsCancellationRequested) break;
            found++;
            await Send("hit", Predictions.Describe(
                hit.Seed, players, chars, hit, criteria.Ascension, criteria.Unlocks,
                criteria.ExtraChestPicks, criteria.RewardPriorDraws(), criteria.PlayerUnlocks));
        }
    }
    catch (OperationCanceledException) { /* client navigated away or hit cancel */ }
    catch (ArgumentException ex)
    {
        await StopTicking();
        await Send("error", new { error = ex.Message });
        return;
    }

    await StopTicking();

    if (!ct.IsCancellationRequested)
        await Send("done", new
        {
            found,
            seconds = sw.Elapsed.TotalSeconds,
            scanned = progress.Scanned,
        });

    // Awaited rather than just cancelled, so the timer cannot still be mid-write when the final
    // event goes out. Send takes the same semaphore, but ordering matters as well as safety: a
    // progress tick arriving after "done" would leave a stale rate on the screen.
    async Task StopTicking()
    {
        await ticking.CancelAsync();
        await ticker;
    }
});

app.Run();
return 0;
