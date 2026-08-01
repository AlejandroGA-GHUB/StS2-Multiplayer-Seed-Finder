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
public sealed record CatalogDto(
    string GameVersion, string AppVersion,
    string? DriftWarning, string AssetProvider, string AssetStatus,
    RelicDto[] NeowCurses, RelicDto[] NeowPositives, RelicDto[] NeowCoinFlip,
    AncientDto[] Ancients, CharacterDto[] Characters, string[] Act1Maps, ActContentDto[] ActContent,
    CardPoolDto[] CardPools, ShopRelicDto[] ShopRelics, ChestRelicDto[] ChestRelics,
    string[] AncientArtSlugs, string[] EventArtSlugs);

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
public sealed record ProfileDto(
    bool Found, string? Path, string[] SearchedIn, string OverrideVariable,
    int RevealedEpochs, int TotalEpochs, bool FullyUnlocked,
    string[] DiscoveredActs, LobbyDto? Lobby);

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

public sealed record SeedResultDto(
    string Seed, NeowOfferDto[] Neow, ActDto[] Acts, AncientOfferDto[] AncientOffers,
    FirstFightDto[] FirstFight, ShopSequenceDto[] Shops, ChestDto[] Chests);

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

        NeowRelic? relic = null;
        var relicSlug = q["relic"].ToString();
        if (!string.IsNullOrWhiteSpace(relicSlug) && relicSlug != "any")
            relic = NeowRelics.Find(relicSlug) ?? throw new ArgumentException($"unknown relic '{relicSlug}'");

        var where = q["where"].ToString() switch
        {
            "curse" => OfferSlot.CurseOnly,
            "positive" => OfferSlot.PositiveOnly,
            _ => OfferSlot.Anywhere,
        };

        var requireRaw = q["require"].ToString();
        var (requirement, slots) = ParseRequire(requireRaw, players);

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
            Relic = relic,
            Act1 = act1,
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
            Ascension = Ascension(q),
            Characters = chars,
            Unlocks = Unlocks(q, savesDirectory),
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
    public static SeedResultDto Describe(
        string seed, int players, IReadOnlyList<Character> characters, SeedHit? hit = null,
        int ascension = 0, UnlockState? unlocks = null, int extraChestPicks = 0)
    {
        unlocks ??= new UnlockState();
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
                Array.Empty<ChestDto>());

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
        var firstFight = Enumerable.Range(0, players).SelectMany(slot =>
            CardRewardGenerator
                .Hallway(runSeed, slot, characters[slot],
                         CardRewardGenerator.MaxPredictableFight, ascension, unlocks)
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

        return new SeedResultDto(seed, neow, acts, ancientOffers.ToArray(), firstFight, shops, chests);
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
