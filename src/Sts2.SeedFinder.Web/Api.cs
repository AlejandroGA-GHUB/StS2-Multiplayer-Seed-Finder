using Microsoft.Extensions.Primitives;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Neow;
using Sts2.SeedFinder.Core.Saves;
using Sts2.SeedFinder.Web.Assets;

namespace Sts2.SeedFinder.Web;

// ---- Wire shapes --------------------------------------------------------------------------

/// <param name="Note">A gate worth knowing about before you search for it, or null.</param>
/// <param name="Description">What the relic does, in the game's own words, or null when the
/// text is unavailable (no install, or a bundled export without descriptions).</param>
public sealed record RelicDto(
    string Slug, string Name, string Group, string? Note, bool HasArt, string? Description);

public sealed record AncientDto(
    string Id, string Name, int[] Acts, bool SeedDetermined, string? DeckNote, RelicDto[] Relics);

/// <param name="Maps">
/// Which of the act's maps can produce this. Only Act 1 has more than one, and its two maps
/// have disjoint boss lists — so the UI can say that choosing a boss also pins the map.
/// </param>
/// <param name="Description">
/// For events, the text the game shows on arriving at one. Null for bosses, and for an event the
/// install has no localization for.
/// </param>
public sealed record ActThingDto(string Slug, string Name, string[] Maps, string? Description = null);

/// <summary>What one act can contain, for the boss and event pickers.</summary>
public sealed record ActContentDto(int Act, string[] Maps, ActThingDto[] Bosses, ActThingDto[] Events);

/// <param name="Rarity">Common, Uncommon or Rare — the only three a combat reward can roll.</param>
public sealed record CardDto(string Slug, string Name, string Rarity, bool HasArt, string? Description);

/// <summary>One character's reward pool, for that player's card picker.</summary>
public sealed record CardPoolDto(string Character, CardDto[] Cards);

/// <summary>
/// A relic that can fill a merchant's third slot.
/// </summary>
/// <param name="Character">
/// The character whose own pool contributes it, or null when it is shared. A character's relic
/// only ever reaches that character's shop, so the picker greys it out for everyone else.
/// </param>
public sealed record ShopRelicDto(
    string Slug, string Name, string? Character, bool HasArt, string? Description);

/// <summary>
/// A relic a treasure chest can offer: the shared pool's Common, Uncommon and Rare entries.
///
/// No Character field, unlike <see cref="ShopRelicDto"/>. A chest pulls from the SHARED bag, so
/// no character's own relic can ever be on the table and the party does not narrow the list.
/// </summary>
public sealed record ChestRelicDto(
    string Slug, string Name, string Rarity, bool HasArt, string? Description);

/// <summary>
/// A playable character. <paramref name="Name"/> is the enum name the rest of the API speaks;
/// <paramref name="Slug"/> is what the art endpoint takes.
/// </summary>
public sealed record CharacterDto(string Name, string Slug, bool HasArt);

/// <summary>
/// A Neow option that touches a player's card stream, and how.
/// </summary>
/// <param name="PayloadCards">
/// How many cards this tool can NAME for the relic: 1 for Arcane Scroll, 3 for Hefty Tablet,
/// 0 for the rest. Non-zero is what makes the payload picker appear on a Neow row.
/// </param>
/// <param name="Draws">
/// Draws it takes off that player's Rewards stream before the first fight rolls, which is what
/// shifts their card rewards. Zero for every option not listed here.
/// </param>
/// <param name="DefectDraws">
/// The same for a Defect. Only Scroll Boxes differs, by the two extra all-Claw checks it rolls.
/// </param>
public sealed record NeowCardRelicDto(
    string Slug, string Name, int PayloadCards, int Draws, int DefectDraws);

/// <param name="BossArtSlugs">Boss slugs the install can serve node art for.</param>
/// <param name="AncientArtSlugs">
/// Ancient slugs the install can serve node art for. Separate from <see cref="AncientDto"/>
/// because it covers Neow as well, which opens Act 1 but has no searchable offer and so is not
/// in the Ancients list at all.
/// </param>
/// <param name="DriftWarning">
/// Set when the installed game's logic differs from the build this checkout was verified
/// against, or when mods are installed. Either makes predictions wrong in a way that looks
/// fine, so it is stated at the top of the results rather than hidden in a tooltip.
/// </param>
/// <param name="AppVersion">
/// This tool's version, so the header can state it without anyone pressing the update button.
/// The check against GitHub is a separate, opt-in call: see <c>/api/update</c>.
/// </param>
/// <param name="MaxFight">
/// <see cref="Sts2.SeedFinder.Core.Cards.CardRewardGenerator.MaxPredictableFight"/>, so the card
/// picker offers exactly the fights the search will accept instead of carrying its own copy of
/// the number.
/// </param>
public sealed record CatalogDto(
    string GameVersion, string AppVersion, int MaxFight,
    string? DriftWarning, string AssetProvider, string AssetStatus,
    RelicDto[] NeowCurses, RelicDto[] NeowPositives, RelicDto[] NeowCoinFlip,
    AncientDto[] Ancients, CharacterDto[] Characters, string[] Act1Maps, ActContentDto[] ActContent,
    CardPoolDto[] CardPools, ShopRelicDto[] ShopRelics, ChestRelicDto[] ChestRelics,
    string[] AncientArtSlugs, string[] EventArtSlugs, string[] BossArtSlugs,
    NeowCardRelicDto[] NeowCardRelics);

/// <summary>
/// What the local game install knows about this player, for the "sync from my save" button.
/// </summary>
/// <param name="SearchedIn">
/// Where we looked, populated only when nothing was found. Shown rather than swallowed, because
/// "no save found" is otherwise indistinguishable from a bug, and the fix is usually to point
/// <paramref name="OverrideVariable"/> at the right folder.
/// </param>
/// <param name="FullyUnlocked">
/// Whether every epoch is revealed. When false the defaults are wrong, not merely optimistic:
/// locked content shrinks the relic pools, which changes how many draws each shuffle costs and
/// so moves every later draw in the run.
/// </param>
/// <param name="Lobby">The run in progress, if any, so its settings can be copied in.</param>
/// <param name="Code">
/// This profile's own epochs as a shareable code, so the person running the tool can hand their
/// state to someone else's lobby as easily as they can import one.
/// </param>
public sealed record ProfileDto(
    bool Found, string? Path, string[] SearchedIn, string OverrideVariable,
    int RevealedEpochs, int TotalEpochs, bool FullyUnlocked,
    string[] DiscoveredActs, LobbyDto? Lobby, string? Code);

/// <summary>
/// The OS and runtime, named the way somebody reading a bug report would name them.
/// </summary>
public static class PlatformInfo
{
    /// <summary>
    /// Windows 11 identifies itself as <c>10.0.x</c> through every version API .NET can reach:
    /// Microsoft kept the major version at 10 and separated the two releases by build number,
    /// 22000 being the first Windows 11 build. Reported raw, a report from Windows 11 reads
    /// "Windows 10", which is precisely the kind of small wrongness that makes a reader distrust
    /// the rest of the block.
    ///
    /// <c>Environment.OSVersion</c> rather than the description string because .NET Core 3.0 and
    /// later resolve it through RtlGetVersion, so compatibility shimming cannot lie to it.
    /// </summary>
    public static string Describe()
    {
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        if (OperatingSystem.IsWindows())
        {
            var v = Environment.OSVersion.Version;
            return $"Windows {(v.Build >= 22000 ? 11 : 10)} (build {v.Build}) / {framework}";
        }

        return $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} / {framework}";
    }
}

/// <summary>
/// One act's map, flattened for drawing.
///
/// Sent as a node list plus edges rather than as a grid, because the grid is mostly empty: a
/// 7-wide act map holds roughly 60 nodes across 16 rows of 7 cells. Coordinates are the ones the
/// game itself would save, post-layout, so what is drawn here is positioned the way the in-game
/// map is.
/// </summary>
/// <param name="Act">Display name, e.g. "Overgrowth".</param>
/// <param name="Boss">The boss this act ends with, so the map can be read without the results.</param>
/// <param name="SecondBoss">Only at A10, and only on the final act.</param>
/// <param name="AncientArt">
/// Slug for <c>/api/asset/ancient/</c>, or null when this install has no art for it. Sent as a
/// presence check rather than a URL so the page never requests art that is not there.
/// </param>
/// <param name="BossArt">Same, for <c>/api/asset/boss/</c>. Null for the Spine-animated bosses.</param>
public sealed record MapActDto(
    int Index, string Act, string Boss, string? SecondBoss, string Ancient,
    string? AncientArt, string? BossArt, string? SecondBossArt,
    int Width, int Height,
    MapNodeDto[] Nodes, MapNodeDto Start, MapNodeDto BossNode, MapNodeDto? SecondBossNode);

/// <param name="Type">Lowercase point type: monster, elite, shop, rest_site, treasure, unknown, boss, ancient.</param>
/// <param name="Children">Coordinates this node leads to, as "col,row" strings.</param>
public sealed record MapNodeDto(int Col, int Row, string Type, string[] Children);

/// <summary>
/// The machine half of a bug report: everything about this install that a reader would need and
/// a reporter would never type correctly.
///
/// Assembled server-side because most of it only exists here (the assembly hash, the accelerator
/// actually in use, the verified baseline), and returned as fields rather than as finished prose
/// so the wording lives with the rest of the page's copy.
///
/// Deliberately absent: the save file's PATH, which carries a Steam id, and the folders searched
/// when no save was found. GitHub issues are public, and neither adds anything a maintainer
/// could act on.
/// </summary>
/// <param name="Repository">
/// Where reports go, as "owner/repo". Follows <c>Updates:Repository</c> so a fork collects its
/// own rather than sending strangers' reports upstream.
/// </param>
public sealed record ReportDto(
    string Repository, string ToolVersion, string GameVersion, string VerifiedVersion,
    string Drift, bool HasMods, string? DriftWarning,
    string Engine, string Device,
    bool ProfileFound, int RevealedEpochs, int TotalEpochs,
    string Platform);

/// <summary>
/// What an imported partner's <c>progress.save</c> turned out to say.
/// </summary>
/// <param name="Missing">
/// The epochs that profile has NOT revealed, named for a reader. An empty list is the good news
/// case and says so plainly; a short list is the one worth reading, because those are exactly
/// the pools that will be a different size from the ones a default prediction assumes.
/// </param>
public sealed record EpochImportDto(
    string Code, int Revealed, int Total, bool FullyUnlocked, string[] Missing);

/// <param name="Characters">Character names in lobby order, which is what decides each slot's RNG.</param>
public sealed record LobbyDto(string? Seed, string[] Characters, int Ascension, bool IsMultiplayer);

public sealed record NeowOfferDto(int Slot, string[] Positives, string Curse);

/// <param name="Bosses">
/// Every boss the act ends with, in order: one normally, two on the final act at A10+.
/// </param>
/// <param name="Events">
/// The head of the act's event queue, in order. Empty until a character is picked for every
/// player, because it comes out of the same generation the bosses and Ancients do.
/// </param>
public sealed record ActDto(int Act, string Name, string[] Bosses, string Ancient, string[] Events);

/// <summary>One possible offer plus the deck state that produces it.</summary>
public sealed record BranchDto(string Condition, string[] Relics);

/// <summary>An Ancient's offer for one player. More than one branch means the deck decides.</summary>
public sealed record SlotOfferDto(int Slot, BranchDto[] Branches);

public sealed record AncientOfferDto(int Act, string Ancient, SlotOfferDto[] Slots);

/// <summary>
/// The card reward one player is offered after the first fight.
/// </summary>
/// <param name="Cards">Three slugs, in the order the reward screen shows them.</param>
/// <param name="Potion">Whether a potion drops alongside, which the same stream decides.</param>
/// <param name="Fight">
/// Which fight, 1-based. Fight 1 is forced by the map; anything beyond assumes the party walked
/// straight into another Monster room.
/// </param>
public sealed record FirstFightDto(int Slot, string[] Cards, bool Potion, int Fight = 1);

/// <summary>
/// The third-slot relic each of one player's shops will stock, in visit order. Index 0 is the
/// first merchant that player walks into, not a floor, so skipping a shop shifts the rest along.
/// </summary>
public sealed record ShopSequenceDto(int Slot, string[] Relics);

/// <summary>
/// One relic on the table at an act's treasure chest.
/// </summary>
/// <param name="Rarity">
/// Exact. It is rolled on a run-level stream nothing else in the game touches.
/// </param>
/// <param name="Alternates">
/// What arrives instead, in order, for each relic of this rarity already taken out of the shared
/// bag by an elite reward, a merchant's stock or a relic event. Empty when the deque is spent.
/// </param>
public sealed record ChestSlotDto(string Rarity, string Relic, string[] Alternates);

/// <summary>
/// An act's chest. Run-level, not per player: one relic is rolled per player and the whole party
/// votes on the set, so the seed fixes what is offered but not who ends up with it.
/// </summary>
public sealed record ChestDto(int Act, int Floor, ChestSlotDto[] Slots);

/// <summary>
/// What a Neow relic would hand this player if they took it.
/// </summary>
/// <param name="Cards">
/// One rare for Arcane Scroll, three for Hefty Tablet, in the order the factory produced them.
/// </param>
/// <remarks>
/// Reported for every offer that contains one of those two, whether or not the search asked
/// about it, because it is the thing the player is choosing between and it costs one draw to
/// answer.
/// </remarks>
public sealed record NeowPayloadDto(int Slot, string Relic, string[] Cards);

public sealed record SeedResultDto(
    string Seed, NeowOfferDto[] Neow, ActDto[] Acts, AncientOfferDto[] AncientOffers,
    FirstFightDto[] FirstFight, ShopSequenceDto[] Shops, ChestDto[] Chests,
    NeowPayloadDto[] NeowPayloads);

// ---- Query parsing ------------------------------------------------------------------------

public static class Query
{
    public static ulong? ULong(IQueryCollection q, string key) =>
        q.TryGetValue(key, out var v) && ulong.TryParse(v, out var n) ? n : null;

    public static int? Int(IQueryCollection q, string key) =>
        q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;

    public static IReadOnlyList<Character> Characters(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<Character>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<Character>(s, ignoreCase: true, out var c)
                ? c
                : throw new ArgumentException($"unknown character '{s}'"))
            .ToArray();
    }

    /// <summary>
    /// A random index far enough into the space that seeds use the full alphabet, with
    /// headroom so a scan cannot run off the end.
    /// </summary>
    public static ulong RandomStart()
    {
        var space = SeedCodec.SpaceSize() ?? ulong.MaxValue;
        return (ulong)System.Random.Shared.NextInt64(0, (long)Math.Min(space / 2, long.MaxValue));
    }

    /// <summary>
    /// Ascension level from the query. Only A10 changes generation, but it is read as a level
    /// rather than a boolean so the UI can match the lobby screen the user is looking at.
    /// </summary>
    public static int Ascension(IQueryCollection q)
    {
        var raw = q["ascension"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (!int.TryParse(raw.TrimStart('a', 'A'), out var n) || n < 0 || n > AscensionLevels.Max)
            throw new ArgumentException($"ascension must be 0-{AscensionLevels.Max}, got '{raw}'.");
        return n;
    }

    /// <summary>
    /// The unlock state a request should generate against.
    ///
    /// Defaults to the local profile's, falling back to fully unlocked when there is no save to
    /// read. That fallback is a guess, and the UI says so — pool sizes decide draw counts, so
    /// generating against the wrong ones is wrong from the first act, not merely imprecise.
    /// <c>?unlocks=all</c> forces the guess, for anyone predicting for a lobby that is not theirs.
    /// </summary>
    public static UnlockState Unlocks(IQueryCollection q, string? savesDirectory)
    {
        if (q["unlocks"].ToString() is "all") return new UnlockState();
        return ProfileReader.Read(savesDirectory)?.Unlocks ?? new UnlockState();
    }

    /// <summary>
    /// Everyone's unlock state: one per player, plus the run-level superset the game generates
    /// the shared parts against.
    ///
    /// Repeated ?epochs=&lt;player&gt;:&lt;code&gt; — e.g. epochs=p2:36-fffffffff — where the code
    /// comes from importing that player's <c>progress.save</c>. Anyone not named is assumed to
    /// match the local profile, which is the only thing knowable about an account on another
    /// machine and is what the tool assumed for everybody before this existed.
    ///
    /// Returns a null per-player list when nothing was imported. That is not a shortcut but the
    /// point: <c>RunGenerator</c> caches its bag plan on reference equality, and a list saying
    /// "everyone matches the run" would rebuild that plan for no change in the answer.
    /// </summary>
    public static (UnlockState Run, IReadOnlyList<UnlockState>? PerPlayer) LobbyUnlocks(
        IQueryCollection q, int players, string? savesDirectory)
    {
        var mine = Unlocks(q, savesDirectory);

        var imported = new UnlockState?[players];
        bool any = false;

        foreach (var raw in (StringValues)q["epochs"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0)
                throw new ArgumentException($"epochs wants <player>:<code>, got '{raw}'");

            var t = parts[0].StartsWith('p') || parts[0].StartsWith('P') ? parts[0][1..] : parts[0];
            if (!int.TryParse(t, out var n) || n < 1 || n > players)
                throw new ArgumentException($"'{parts[0]}' is not a player in a {players}-player lobby");

            imported[n - 1] = UnlockCode.Decode(parts[1])
                ?? throw new ArgumentException(
                    $"'{parts[1]}' is not an unlock code this build can read. Codes are tied to "
                    + "the epochs a build knows about, so one made by a different version has to "
                    + "be imported again.");
            any = true;
        }

        if (!any) return (mine, null);

        var perPlayer = new UnlockState[players];
        for (int i = 0; i < players; i++) perPlayer[i] = imported[i] ?? mine;

        // The game builds the run's state as the superset of every player's, and that is what
        // act generation, the Ancients and the shared chest bag all read.
        return (UnlockState.Union(perPlayer), perPlayer);
    }

    public static (SearchCriteria, int Players, IReadOnlyList<Character>) BuildCriteria(
        IQueryCollection q, string? savesDirectory = null)
    {
        int players = (int)(ULong(q, "players") ?? 2);
        if (players is < 2 or > 4)
            throw new ArgumentException("players must be 2-4, as this tool is co-op only.");

        var chars = Characters(q["characters"]);
        var ctx = new NeowContext
        {
            PlayerCount = players,
            AllCharactersUnlocked = q["allCharacters"] != "false",
            ScrollBoxesAvailable = q["scrollBoxes"] != "false",
        };

        var where = q["where"].ToString() switch
        {
            "curse" => OfferSlot.CurseOnly,
            "positive" => OfferSlot.PositiveOnly,
            _ => OfferSlot.Anywhere,
        };

        // The search-wide rule. Still read, because it is what a one-relic link carries and
        // what a row without its own rule falls back to.
        var (requirement, slots) = ParseRequire(q["require"].ToString(), players);

        var lobbyUnlocks = LobbyUnlocks(q, players, savesDirectory);

        // Repeated ?relic=<slug>[:<require>] — e.g. silken_tress, silken_tress:all,
        // golden_pearl:p2. Shaped like ?ancient= on purpose: wanting one relic for the whole
        // lobby and a different one for a single player is the ordinary co-op ask, and a single
        // shared rule cannot express it. A lone ?relic=silken_tress with ?require=all keeps
        // meaning exactly what it did before rows existed.
        var neowWanted = new List<NeowCriterion>();
        foreach (var raw in (StringValues)q["relic"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts[0].Length == 0 || parts[0] == "any") continue;

            var found = NeowRelics.Find(parts[0])
                ?? throw new ArgumentException($"unknown relic '{parts[0]}'");

            var (rowRule, rowSlots) = parts.Length >= 2 && parts[1].Length > 0
                ? ParseRequire(parts[1], players)
                : (requirement, slots);

            // A third field names the cards the relic itself hands over, comma separated:
            // relic=arcane_scroll:p1:corruption. Resolved against the pools of the players the
            // row is aimed at, since a card slug only means something inside a character's pool.
            var payload = parts.Length == 3 && parts[2].Length > 0
                ? ParsePayloadCards(parts[2], found, rowRule, rowSlots, chars, players)
                : null;

            neowWanted.Add(new NeowCriterion(found, rowRule, rowSlots, where, payload));
        }

        var act1 = q["act1"].ToString();
        if (string.IsNullOrWhiteSpace(act1) || act1 == "any") act1 = null;

        // Repeated ?ancient=<who>[:<relic>][:<require>] — e.g. vakuu, vakuu:fiddle,
        // vakuu:fiddle:all, vakuu:any:p1. Each row carries its own slot rule.
        var ancients = new List<AncientCriterion>();
        foreach (var raw in (StringValues)q["ancient"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (!AncientOffers.TryParse(parts[0], out var who))
                throw new ArgumentException($"unknown Ancient '{parts[0]}'");

            string? wanted = null;
            if (parts.Length >= 2 && parts[1].Length > 0 && parts[1] != "any")
            {
                wanted = AncientOffers.AllRelics(who)
                    .FirstOrDefault(r => AncientOffers.Slug(r).Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"{who} never offers '{parts[1]}'");
            }

            SlotRequirement? rowRule = null;
            IReadOnlyList<int>? rowSlots = null;
            if (parts.Length == 3 && parts[2].Length > 0)
                (rowRule, rowSlots) = ParseRequire(parts[2], players);

            ancients.Add(new AncientCriterion(who, wanted, rowRule, rowSlots));
        }

        var criteria = new SearchCriteria
        {
            NeowRelicsWanted = neowWanted,
            Act1 = act1,
            // ?cardOrder=any lets the picks land in any fight order. Default stays exact, so an
            // existing link keeps meaning what it meant.
            CardOrder = q["cardOrder"].ToString() == "any" ? CardOrder.AnyPermutation : CardOrder.Exact,
            Context = ctx,
            Requirement = requirement,
            RequiredSlots = slots,
            Where = where,
            Ancients = ancients,
            Bosses = ParseBosses(q),
            Events = ParseEvents(q),
            Cards = ParseCards(q, chars, players),
            ShopRelicsWanted = ParseShopRelics(q, players),
            ChestRelicsWanted = ParseChestRelics(q),
            ExtraChestPicks = ExtraChestPicks(q),
            NeowPicks = NeowPicks(q, players),
            Ascension = Ascension(q),
            Characters = chars,
            Unlocks = lobbyUnlocks.Run,
            PlayerUnlocks = lobbyUnlocks.PerPlayer,
        };
        return (criteria, players, chars);
    }

    /// <summary>
    /// Repeated ?chest=&lt;act&gt;:&lt;slug&gt;[:&lt;tolerance&gt;] — e.g. chest=1:vajra, or
    /// chest=2:vajra:3 to allow for three relics of that rarity having left the shared bag first.
    ///
    /// No player slot, unlike shops and cards: a chest is a shared pick, so what is on the table
    /// is a property of the seed while who takes it is not.
    /// </summary>
    private static IReadOnlyList<ChestRelicCriterion> ParseChestRelics(IQueryCollection q)
    {
        var wanted = new List<ChestRelicCriterion>();
        foreach (var raw in (StringValues)q["chest"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0)
                throw new ArgumentException($"chest wants <act>:<relic>[:<tolerance>], got '{raw}'");

            if (!int.TryParse(parts[0], out var act) || act is < 1 or > 3)
                throw new ArgumentException($"chest act must be 1, 2 or 3, got '{parts[0]}'");

            var relic = ChestRelics.Find(parts[1])
                ?? throw new ArgumentException($"no chest relic called '{parts[1]}'");

            int tolerance = 0;
            if (parts.Length == 3 && parts[2].Length > 0
                && (!int.TryParse(parts[2], out tolerance) || tolerance < 0))
                throw new ArgumentException($"chest tolerance must be 0 or higher, got '{parts[2]}'");

            wanted.Add(new ChestRelicCriterion(act, relic.Slug, tolerance));
        }
        return wanted;
    }

    /// <summary>
    /// The comma-separated card slugs on a ?relic= row, resolved to the game's type names.
    ///
    /// Which pools to look in follows the row's own slot rule, exactly as the validator does:
    /// a row for P1 resolves against P1's character, and a row for "any player" against
    /// anybody's. Resolving here rather than in Core keeps slugs a wire concern.
    /// </summary>
    private static IReadOnlyList<string> ParsePayloadCards(
        string csv, NeowRelic relic, SlotRequirement rule, IReadOnlyList<int> slots,
        IReadOnlyList<Character> chars, int players)
    {
        if (chars.Count != players)
            throw new ArgumentException(
                "naming the cards a Neow relic gives needs every player's character, because "
                + "they come out of that player's own rare pool.");

        var looking = rule switch
        {
            SlotRequirement.Specific => slots,
            SlotRequirement.All => Enumerable.Range(0, players).ToArray(),
            _ => Enumerable.Range(0, players).ToArray(),
        };

        var resolved = new List<string>();
        foreach (var name in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string? found = null;
            foreach (var slot in looking)
                if (CardCatalog.Find(chars[slot], name) is { } hit) { found = hit; break; }

            resolved.Add(found ?? throw new ArgumentException(
                $"'{name}' is not a card {relic.Name} can hand to "
                + (rule == SlotRequirement.Any
                    ? "anyone in this party"
                    : string.Join(" or ", looking.Select(x => $"P{x + 1}")))));
        }
        return resolved;
    }

    /// <summary>
    /// Repeated ?pick=&lt;slot&gt;:&lt;relic&gt; — which Neow option a player is assumed to take.
    ///
    /// Not a criterion: it asks nothing of the seed. It exists because the five card-drawing
    /// options draw off the same stream the fight rewards come from, so taking one shifts every
    /// card that player is later offered. The slot is 1-based on the wire, as everywhere else.
    /// </summary>
    public static IReadOnlyList<NeowPick> NeowPicks(IQueryCollection q, int players)
    {
        var picks = new List<NeowPick>();
        foreach (var raw in (StringValues)q["pick"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0 || parts[1] == "none") continue;

            var t = parts[0].StartsWith('p') || parts[0].StartsWith('P') ? parts[0][1..] : parts[0];
            if (!int.TryParse(t, out var n) || n < 1 || n > players)
                throw new ArgumentException($"'{parts[0]}' is not a player in a {players}-player lobby");

            var relic = NeowRelics.Find(parts[1])
                ?? throw new ArgumentException($"unknown relic '{parts[1]}'");

            picks.Add(new NeowPick(n - 1, relic.Slug));
        }
        return picks;
    }

    /// <summary>
    /// ?pick= as per-slot draws off the Rewards stream, which is the only thing a prediction
    /// does with it. Zero for a player who took nothing that hands out cards.
    /// </summary>
    public static int[] RewardPriorDraws(
        IQueryCollection q, int players, IReadOnlyList<Character> chars)
    {
        var draws = new int[players];
        if (chars.Count != players) return draws;

        foreach (var pick in NeowPicks(q, players))
            draws[pick.Slot] = Math.Max(
                CardRewardGenerator.NeowRewardDrawCost(pick.RelicSlug, chars[pick.Slot]), 0);

        return draws;
    }

    /// <summary>?extraChests=n — ? rooms that became treasure rooms before Act 1's chest.</summary>
    private static int ExtraChestPicks(IQueryCollection q)
    {
        var raw = q["extraChests"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (!int.TryParse(raw, out var n) || n < 0)
            throw new ArgumentException($"extraChests must be 0 or more, got '{raw}'");
        return n;
    }

    /// <summary>
    /// Repeated ?shop=&lt;slot&gt;:&lt;slug&gt;[:&lt;visit&gt;] — e.g. shop=1:belt_buckle, or
    /// shop=1:toolbox:2 for that player's second merchant. Slot and visit are both 1-based on
    /// the wire, as they are in the UI.
    /// </summary>
    private static IReadOnlyList<ShopRelicCriterion> ParseShopRelics(IQueryCollection q, int players)
    {
        var wanted = new List<ShopRelicCriterion>();
        foreach (var raw in (StringValues)q["shop"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0)
                throw new ArgumentException($"shop wants <player>:<relic>[:<visit>], got '{raw}'");

            int slot = -1;
            if (parts[0] is not ("any" or ""))
            {
                var t = parts[0].StartsWith('p') || parts[0].StartsWith('P') ? parts[0][1..] : parts[0];
                if (!int.TryParse(t, out var n) || n < 1 || n > players)
                    throw new ArgumentException($"'{parts[0]}' is not a player in a {players}-player lobby");
                slot = n - 1;
            }

            var relic = ShopRelics.Find(parts[1])
                ?? throw new ArgumentException($"no shop relic called '{parts[1]}'");

            int visit = 1;
            if (parts.Length == 3 && parts[2].Length > 0
                && (!int.TryParse(parts[2], out visit) || visit < 1))
                throw new ArgumentException($"shop visit must be 1 or higher, got '{parts[2]}'");

            wanted.Add(new ShopRelicCriterion(slot, relic.Slug, visit - 1));
        }
        return wanted;
    }

    /// <summary>
    /// Repeated ?card=&lt;slot&gt;:&lt;slug&gt; — e.g. card=1:anger for P1, or card=any:anger for
    /// whoever. The slot is 1-based on the wire, as it is in the UI.
    /// </summary>
    private static IReadOnlyList<CardCriterion> ParseCards(
        IQueryCollection q, IReadOnlyList<Character> chars, int players)
    {
        var cards = new List<CardCriterion>();
        foreach (var raw in (StringValues)q["card"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0)
                throw new ArgumentException($"card wants <player>:<card>[:<fight>], got '{raw}'");

            int slot = -1;
            if (parts[0] is not ("any" or ""))
            {
                var t = parts[0].StartsWith('p') || parts[0].StartsWith('P') ? parts[0][1..] : parts[0];
                if (!int.TryParse(t, out var n) || n < 1 || n > players)
                    throw new ArgumentException($"'{parts[0]}' is not a player in a {players}-player lobby");
                slot = n - 1;
            }

            // Resolving needs the character, since the pools differ. Searching without one is
            // already refused downstream, but naming the card is where the user notices.
            if (chars.Count != players)
                throw new ArgumentException(
                    "card criteria need a character for every player, because the pool is theirs.");

            var lookIn = slot < 0 ? Enumerable.Range(0, players) : [slot];
            var found = lookIn
                .Select(s => CardCatalog.Find(chars[s], parts[1]))
                .FirstOrDefault(x => x is not null);

            // Which fight, 1-based. Only fight 1 is guaranteed by the map; see CardCriterion.
            int fight = 1;
            if (parts.Length == 3 && parts[2].Length > 0
                && (!int.TryParse(parts[2], out fight)
                    || fight < 1 || fight > CardRewardGenerator.MaxPredictableFight))
                throw new ArgumentException(
                    $"card fight must be between 1 and {CardRewardGenerator.MaxPredictableFight}, "
                    + $"got '{parts[2]}'");

            cards.Add(new CardCriterion(slot, found
                ?? throw new ArgumentException(
                    $"no card called '{parts[1]}' in " +
                    (slot < 0 ? "any of the party's pools" : $"the {chars[slot]}'s pool")), fight));
        }
        return cards;
    }

    /// <summary>
    /// Repeated ?boss=&lt;act&gt;:[!]&lt;slug&gt; — e.g. boss=2:kaiser_crab, or boss=3:!queen to
    /// rule one out.
    /// </summary>
    private static IReadOnlyList<BossCriterion> ParseBosses(IQueryCollection q)
    {
        var bosses = new List<BossCriterion>();
        foreach (var raw in (StringValues)q["boss"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (act, name, _) = SplitAct(raw, "boss", "<act>:[!]<boss>");

            bool exclude = name.StartsWith('!');
            if (exclude) name = name[1..].Trim();

            bosses.Add(new BossCriterion(act, ActCatalog.FindBoss(act, name)
                ?? throw new ArgumentException($"Act {act} never ends with '{name}'"), exclude));
        }
        return bosses;
    }

    /// <summary>Repeated ?event=&lt;act&gt;:&lt;slug&gt;[:&lt;n&gt;] — e.g. event=1:trash_heap:5.</summary>
    private static IReadOnlyList<EventCriterion> ParseEvents(IQueryCollection q)
    {
        var events = new List<EventCriterion>();
        foreach (var raw in (StringValues)q["event"])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (act, name, tail) = SplitAct(raw, "event", "<act>:<event>[:<within-first>]");

            int within = 3;
            if (tail is not null && (!int.TryParse(tail, out within) || within < 1))
                throw new ArgumentException($"'{tail}' is not a position; it has to be 1 or more.");

            events.Add(new EventCriterion(act, ActCatalog.FindEvent(act, name)
                ?? throw new ArgumentException($"Act {act} never offers '{name}'"), within));
        }
        return events;
    }

    private static (int Act, string Name, string? Tail) SplitAct(string raw, string key, string shape)
    {
        var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var act))
            throw new ArgumentException($"{key} wants {shape}, got '{raw}'");
        if (!ActCatalog.ActNumbers.Contains(act))
            throw new ArgumentException($"there is no act {act}");
        return (act, parts[1], parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null);
    }

    private static (SlotRequirement, IReadOnlyList<int>) ParseRequire(string raw, int players)
    {
        raw = raw.Trim().ToLowerInvariant();
        if (raw is "" or "any") return (SlotRequirement.Any, Array.Empty<int>());
        if (raw == "all") return (SlotRequirement.All, Array.Empty<int>());

        var slots = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s =>
            {
                var t = s.StartsWith('p') ? s[1..] : s;
                if (!int.TryParse(t, out var n) || n < 1 || n > players)
                    throw new ArgumentException($"'{s}' is not a player in a {players}-player lobby");
                return n - 1;
            })
            .ToArray();
        return (SlotRequirement.Specific, slots);
    }
}

// ---- Turning core output into wire shapes -------------------------------------------------

public static class Predictions
{
    /// <summary>
    /// How much of each act's event order to send. The whole thing runs to ~28 entries per act,
    /// which across three acts and a page of results is a lot of wire for a tail nobody reaches.
    /// </summary>
    private const int EventsShown = 10;

    /// <summary>How many shop visits to send. Few runs fit more than five merchants.</summary>
    private const int ShopsShown = 5;

    /// <summary>
    /// How many fallbacks to send per chest slot. Enough to stay useful once a shop or an elite
    /// has drained the shared bag, without implying more precision than the prediction has.
    /// </summary>
    private const int ChestAlternatesShown = 4;

    /// <param name="extraChestPicks">
    /// ? rooms that became treasure rooms before Act 1's chest. Must match what the search used,
    /// or the chest shown would be a different one from the chest matched.
    /// </param>
    /// <param name="playerUnlocks">
    /// Each player's own state, when a partner's has been imported. Card pools and relic bags
    /// are per player, so a slot reads its own rather than the run's superset.
    /// </param>
    public static SeedResultDto Describe(
        string seed, int players, IReadOnlyList<Character> characters, SeedHit? hit = null,
        int ascension = 0, UnlockState? unlocks = null, int extraChestPicks = 0,
        int[]? priorDraws = null, IReadOnlyList<UnlockState>? playerUnlocks = null)
    {
        unlocks ??= new UnlockState();

        UnlockState Own(int slot) =>
            playerUnlocks is { } own && (uint)slot < (uint)own.Count ? own[slot] : unlocks;
        ulong runSeed = SeedCodec.RunSeed(seed);
        var ctx = new NeowContext { PlayerCount = players };

        var offers = hit?.OffersBySlot ?? NeowGenerator.PredictAllOffers(runSeed, ctx);
        var neow = offers.Select((o, i) => new NeowOfferDto(
            i, new[] { o.Positive1.Name, o.Positive2.Name }, o.Curse.Name)).ToArray();

        // Which map each act uses is decided by its own RNG, before and independently of the
        // party — so we can always answer that much, even with nobody picked yet. Bosses and
        // Ancients are the part that needs a full run.
        var selected = RunGenerator.SelectActs(runSeed, unlocks, isMultiplayer: true);

        if (characters.Count == 0)
            return new SeedResultDto(
                seed, neow,
                selected.Select((a, i) => new ActDto(i + 1, a.Name, [], "", [])).ToArray(),
                Array.Empty<AncientOfferDto>(),
                Array.Empty<FirstFightDto>(),
                Array.Empty<ShopSequenceDto>(),
                Array.Empty<ChestDto>(),
                // A payload is drawn from the character's own rare pool, so with nobody picked
                // there is nothing to say — unlike the offer itself, which is party-independent.
                Array.Empty<NeowPayloadDto>());

        if (characters.Count != players)
            throw new ArgumentException(
                $"pick a character for each of the {players} players, in lobby order.");

        // A search hit only carries materialised deques when the search asked about them, so
        // regenerate when it did not. That is the same cost as the run we would have built
        // anyway, and it only happens for the handful of seeds actually being shown.
        var run = hit?.Run is { ShopRelics: not null, Chests: not null } ready
            ? ready
            : RunGenerator.GenerateRun(runSeed, unlocks, isMultiplayer: true,
                                       characters, selected, ascension, withShopRelics: true,
                                       playerUnlocks: playerUnlocks,
                                       withChestRelics: true,
                                       extraChestPicksBefore: extraChestPicks);

        var acts = run.Acts.Select((a, i) => new ActDto(
            i + 1, a.Act.Name, a.Bosses.Select(b => ActCatalog.Display(b.Name)).ToArray(), a.Ancient,
            a.Events.Take(EventsShown).Select(ActCatalog.Display).ToArray())).ToArray();

        var ancientOffers = new List<AncientOfferDto>();
        for (int act = 0; act < run.Acts.Count; act++)
        {
            if (!AncientOffers.TryParse(run.Acts[act].Ancient, out var ancient)) continue;

            var slots = new List<SlotOfferDto>();
            for (int slot = 0; slot < players; slot++)
            {
                var branches = AncientOffers
                    .Branches(ancient, runSeed, slot, new AncientContext { ActIndex = act })
                    .Select(b => new BranchDto(b.Condition, b.Offer.Options.Select(AncientOffers.Display).ToArray()))
                    .ToArray();
                slots.Add(new SlotOfferDto(slot, branches));
            }
            ancientOffers.Add(new AncientOfferDto(act + 1, ancient.ToString(), slots.ToArray()));
        }

        // Row 1 of the map is always a normal fight, so every player has a first card reward
        // regardless of the route taken. It comes off a per-player stream that act generation
        // never touches, so it needs nothing from the run above.
        // Both fights come off ONE walk of each player's stream: fight 2 continues where fight 1
        // stopped and inherits both of its pity counters, so they cannot be computed apart.
        // What each player's assumed Neow pick already spent on that stream, computed by the
        // caller so a search and the seed it displays cannot disagree: naming the cards a relic
        // gives is itself a claim that the player took it, and only the criteria know that.
        var prior = priorDraws is { } given && given.Length >= players ? given : new int[players];

        // What Arcane Scroll or Hefty Tablet would hand each player, reported against the OFFER
        // rather than against a pick: it is what that player is deciding about, and it costs one
        // draw per card off a stream nothing else has touched yet.
        var payloads = new List<NeowPayloadDto>();
        for (int slot = 0; slot < players; slot++)
        {
            var offer = offers[slot];
            foreach (var relic in new[] { offer.Positive1, offer.Positive2, offer.Curse })
            {
                if (!NeowCardPayload.IsPredictable(relic.Slug)) continue;

                var cards = NeowCardPayload.Generate(
                    runSeed, slot, characters[slot], relic.Slug, Own(slot));
                if (cards.Count > 0)
                    payloads.Add(new NeowPayloadDto(slot, relic.Slug, cards.Select(c => c.Slug).ToArray()));
            }
        }

        var firstFight = Enumerable.Range(0, players).SelectMany(slot =>
            CardRewardGenerator
                .Hallway(runSeed, slot, characters[slot],
                         CardRewardGenerator.MaxPredictableFight, ascension, Own(slot),
                         prior[slot])
                .Fights
                .Select((reward, i) => new FirstFightDto(
                    slot, reward.Cards.Select(c => c.Slug).ToArray(), reward.HasPotion, i + 1)))
            .ToArray();

        // The merchant's third slot is hardcoded to Shop rarity and filling it draws no RNG, so
        // it is the back of the player's shuffled Shop deque, one taken per visit. The other two
        // slots roll against a pity counter the run has already moved, and are not predictable.
        var shops = (run.ShopRelics ?? []).Select((seq, slot) => new ShopSequenceDto(
            slot, seq.Take(ShopsShown).Select(r => r.Slug).ToArray())).ToArray();

        // One chest per act, at a map row nothing can reroute around. Rarity is exact; the relic
        // is the front of the SHARED bag, so the alternates carry what happens once earlier picks
        // have drained it.
        var chests = (run.Chests?.Slots ?? []).Select((slots, i) => new ChestDto(
            i + 1,
            i < ChestRelics.MultiplayerFloors.Length ? ChestRelics.MultiplayerFloors[i] : 0,
            slots.Select(s => new ChestSlotDto(
                s.Rarity,
                s.Expected?.Slug ?? "",
                s.Candidates.Skip(1).Take(ChestAlternatesShown).Select(r => r.Slug).ToArray())).ToArray()
        )).ToArray();

        return new SeedResultDto(
            seed, neow, acts, ancientOffers.ToArray(), firstFight, shops, chests, payloads.ToArray());
    }
}

// ---- Tooltip copy -------------------------------------------------------------------------

/// <summary>
/// The finder's own annotations, as opposed to the game's descriptions. Deliberately narrow:
/// anything the surrounding UI already says — which pool a relic is in, whether an Ancient's
/// offer depends on your deck — is stated once by the picker heading or the Ancient row, and
/// repeating it in a tooltip is noise. What survives here is the conditions that decide
/// whether a relic can show up for you at all.
/// </summary>
public static class RelicNotes
{
    public static string? For(NeowRelic r) => r.Availability switch
    {
        RelicAvailability.MultiplayerOnly => "Co-op only.",
        RelicAvailability.RequiresAllCharactersUnlocked => "Needs every character unlocked.",
        RelicAvailability.RequiresBundleableCardPool => "Needs 4+ commons and 2+ uncommons in your pool.",
        _ => null,
    };

    public static string? ForAncientRelic(Ancient a, string _) => null;

    public static int[] ActsFor(Ancient a) => a switch
    {
        Ancient.Orobas or Ancient.Pael or Ancient.Tezcatara => new[] { 2 },
        Ancient.Nonupeipe or Ancient.Tanx or Ancient.Vakuu => new[] { 3 },
        Ancient.Darv => new[] { 2, 3 },
        _ => Array.Empty<int>(),
    };

    /// <summary>Only Vakuu's offer is decided entirely by the seed.</summary>
    public static bool IsSeedDetermined(Ancient a) => a is Ancient.Vakuu or Ancient.Darv;

    public static string? DeckNoteFor(Ancient a) => a switch
    {
        Ancient.Vakuu => null,
        Ancient.Darv => null,
        Ancient.Tanx => "Adds Tri-Boomerang if your deck has 3+ Instinct-enchantable cards.",
        Ancient.Nonupeipe => "Adds Beautiful Bracelet if your deck has 4+ Swift-enchantable cards.",
        Ancient.Tezcatara => "Adds Nutritious Soup if a basic Strike is still in your deck.",
        Ancient.Pael => "Pool changes with Goopy-enchantable cards, removable cards, and whether you have an event pet.",
        Ancient.Orobas => "Archaic Tooth drops out if your transcendence starter card was removed.",
        _ => null,
    };
}

// ---- Provider selection -------------------------------------------------------------------

public static class AssetProviderFactory
{
    public static IGameAssetProvider Create(IConfiguration config)
    {
        var requested = Environment.GetEnvironmentVariable("STS2_ASSETS")
                        ?? config["Assets:Provider"]
                        ?? "local";

        // A provider that cannot load must degrade, never take the app down with it.
        try
        {
            switch (requested.Trim().ToLowerInvariant())
            {
                case "none":
                    return new NoAssetProvider("disabled by configuration");

                case "bundled":
                    var dir = config["Assets:Directory"]
                              ?? Path.Combine(AppContext.BaseDirectory, "assets");
                    return new BundledAssetProvider(dir);

                default:
                    return (IGameAssetProvider?)LocalGameAssetProvider.TryCreate(config["Assets:GameDirectory"])
                           ?? new NoAssetProvider(
                               "game install not found. Set Assets__GameDirectory to your " +
                               "\"Slay the Spire 2\" folder to show relic art");
            }
        }
        catch (Exception ex)
        {
            return new NoAssetProvider($"asset loading failed ({ex.GetType().Name}), showing monograms instead");
        }
    }
}
