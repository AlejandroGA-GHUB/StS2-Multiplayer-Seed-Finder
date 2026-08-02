using System.Runtime.InteropServices;

namespace Sts2.SeedFinder.Core.Acts;

/// <summary>
/// Unlock state that affects run generation. Defaults assume a fully-unlocked account,
/// which is the common case; set them explicitly when that is not true, because they
/// change generation and therefore the results.
/// </summary>
public sealed record UnlockState
{
    public bool NeowEpoch { get; init; } = true;
    public bool DarvEpoch { get; init; } = true;
    public bool OrobasEpoch { get; init; } = true;
    public bool Event1Epoch { get; init; } = true;
    public bool Event2Epoch { get; init; } = true;
    public bool Event3Epoch { get; init; } = true;

    /// <summary>
    /// Acts the player has already discovered. In singleplayer an undiscovered act is
    /// force-picked ahead of the RNG roll; in multiplayer that branch is skipped entirely,
    /// so this has no effect on co-op searches.
    /// </summary>
    public IReadOnlySet<string> DiscoveredActs { get; init; } = new HashSet<string>();

    /// <summary>
    /// Every epoch the profile reports revealed, as slugified ids. Null means "not known",
    /// in which case anything outside the named flags above is assumed revealed. Populated by
    /// <see cref="FromRevealedEpochs"/>, and consulted by gates the named flags do not cover —
    /// the per-character card epochs, of which there are three per character.
    /// </summary>
    public IReadOnlySet<string>? RevealedEpochIds { get; init; }

    public bool IsEpochRevealed(string epoch) => epoch switch
    {
        "NeowEpoch" => NeowEpoch,
        "DarvEpoch" => DarvEpoch,
        "OrobasEpoch" => OrobasEpoch,
        "Event1Epoch" => Event1Epoch,
        "Event2Epoch" => Event2Epoch,
        "Event3Epoch" => Event3Epoch,
        _ => RevealedEpochIds is null || RevealedEpochIds.Contains(GameHash.Slugify(epoch)),
    };

    /// <summary>
    /// Builds the state from the epoch ids a profile save reports as revealed — the ids are
    /// slugified type names ("EVENT3_EPOCH"), so we match on <see cref="GameHash.Slugify"/>.
    /// This replaces the fully-unlocked guess with the account's actual state.
    /// </summary>
    public static UnlockState FromRevealedEpochs(IEnumerable<string> revealedEpochIds)
    {
        var revealed = new HashSet<string>(revealedEpochIds, StringComparer.OrdinalIgnoreCase);
        bool Has(string name) => revealed.Contains(GameHash.Slugify(name));

        return new UnlockState
        {
            NeowEpoch = Has("NeowEpoch"),
            DarvEpoch = Has("DarvEpoch"),
            OrobasEpoch = Has("OrobasEpoch"),
            Event1Epoch = Has("Event1Epoch"),
            Event2Epoch = Has("Event2Epoch"),
            Event3Epoch = Has("Event3Epoch"),
            RevealedEpochIds = revealed,
        };
    }
}

/// <summary>
/// Ascension levels that change generation. There is exactly one: <c>AscensionLevel.DoubleBoss</c>
/// is the 10th entry of the game's enum and <c>AscensionManager.HasLevel</c> is <c>_level >= level</c>,
/// so A10 and above give the final act a second boss.
/// </summary>
public static class AscensionLevels
{
    public const int DoubleBoss = 10;

    /// <summary>`AscensionManager.maxAscensionAllowed`. StS2 caps at 10, not 20 as StS1 did.</summary>
    public const int Max = 10;
}

/// <summary>What generation produced for one act.</summary>
/// <param name="SecondBoss">
/// The extra boss A10+ adds, or null. Only the LAST act ever gets one
/// (<c>RunManager.GenerateRooms</c> gates on <c>i == State.Acts.Count - 1</c>).
/// </param>
public sealed record GeneratedAct(
    ActDefinition Act,
    Encounter Boss,
    string Ancient,
    IReadOnlyList<Encounter> NormalEncounters,
    IReadOnlyList<Encounter> EliteEncounters,
    IReadOnlyList<string> Events,
    Encounter? SecondBoss = null)
{
    /// <summary>Every boss this act will make you fight, in the order you meet them.</summary>
    public IEnumerable<Encounter> Bosses =>
        SecondBoss is null ? new[] { Boss } : new[] { Boss, SecondBoss };
}

/// <summary>The upfront-generated shape of a whole run.</summary>
/// <param name="UpFrontDraws">
/// Draws taken from the UpFront RNG across the whole of generation. The game serializes the
/// equivalent counter, so this is a single-integer check on our entire draw accounting.
/// </param>
/// <param name="ShopRelics">
/// Per player, the Shop-rarity relic each successive merchant will put in its third slot —
/// index 0 is the first shop that player visits, index 1 the second, and so on. Empty unless
/// generation was asked for it. See <see cref="RunGenerator.ShopRelicSequence"/> for why this
/// is knowable when the rest of a shop is not.
/// </param>
/// <param name="Chests">
/// Each act's treasure chest, or null unless generation was asked for it. Run-level, not
/// per-player: the chest is a shared pick, so who ends up with which relic is decided by the
/// party's votes rather than by the seed. See <see cref="ChestRelics"/>.
/// </param>
public sealed record GeneratedRun(
    IReadOnlyList<GeneratedAct> Acts,
    int UpFrontDraws,
    IReadOnlyList<IReadOnlyList<PoolRelic>>? ShopRelics = null,
    ChestOffers? Chests = null)
{
    public GeneratedAct this[int actIndex] => Acts[actIndex];
    public string AncientOf(int actIndex) => Acts[actIndex].Ancient;

    /// <summary>The third-slot relic at the <paramref name="visit"/>'th shop a player visits.</summary>
    public PoolRelic? ShopRelic(int slotIndex, int visit = 0)
    {
        var seq = ShopRelics;
        if (seq is null || slotIndex >= seq.Count || visit >= seq[slotIndex].Count) return null;
        return seq[slotIndex][visit];
    }
}

/// <summary>
/// Reproduces the run's upfront generation: act selection, then RunManager.GenerateRooms.
///
/// Everything runs off a single sequential <c>UpFront</c> RNG shared by all acts, and the
/// Ancient is rolled LAST within each act — after events, every encounter, and the boss.
/// So predicting which Ancient you meet in Act 2 or 3 requires reproducing all of it
/// faithfully; there is no shortcut to that draw.
///
/// Act selection is a separate RNG: <c>new Rng(hash(seed), "act_selection")</c>.
///
/// !! UNVERIFIED AGAINST THE GAME !!
/// Unlike the Neow path, the assembled chain cannot be checked headless — that needs a live
/// ModelDb/RunState. The primitives it stands on (GrabBag incl. its retry loop, and
/// UnstableShuffle) ARE oracle-verified against the game's own implementations.
///
/// Known limitation: epoch-gated relic removal is not modelled, so this assumes a
/// FULLY-UNLOCKED account. On a partial-unlock save the relic bags are smaller, which
/// shifts the whole UpFront stream and invalidates Act 2/3 output.
///
/// Treat Act 2/3 output as a hypothesis to be tested against a real co-op run.
/// </summary>
public static class RunGenerator
{
    /// <summary>
    /// ActModel.GetRandomList. In multiplayer the "force undiscovered acts first" branch is
    /// skipped, so act order is a pure RNG roll over unlocked candidates.
    /// </summary>
    public static ActDefinition[] SelectActs(ulong runSeed, UnlockState unlocks, bool isMultiplayer)
    {
        var rng = new Rng(runSeed, "act_selection");
        var chosen = new ActDefinition[ActData.ByIndex.Length];

        for (int i = 0; i < ActData.ByIndex.Length; i++)
        {
            var candidates = ActData.ByIndex[i];
            ActDefinition? forced = null;

            if (!isMultiplayer)
            {
                // Singleplayer only: the first unlocked, non-default, undiscovered act is
                // taken without consuming a draw. Co-op never takes this path.
                foreach (var act in candidates)
                {
                    if (!unlocks.DiscoveredActs.Contains(act.Name))
                    {
                        forced = act;
                        break;
                    }
                }
            }

            chosen[i] = forced ?? candidates[rng.NextInt(0, candidates.Length)];
        }
        return chosen;
    }

    /// <summary>
    /// RunManager.GenerateRooms — shared-Ancient distribution followed by per-act generation,
    /// all on one UpFront stream.
    /// </summary>
    public static GeneratedRun GenerateRun(
        ulong runSeed,
        UnlockState unlocks,
        bool isMultiplayer,
        IReadOnlyList<Character> characters,
        ActDefinition[]? acts = null,
        int ascension = 0,
        bool withShopRelics = false,
        IReadOnlyList<UnlockState>? playerUnlocks = null,
        bool withChestRelics = false,
        int extraChestPicksBefore = 0)
    {
        acts ??= SelectActs(runSeed, unlocks, isMultiplayer);
        var rng = new Rng(runSeed, GameHash.SnakeCase("UpFront"));

        // RunManager.InitializeNewRun runs BEFORE GenerateRooms and shuffles relic grab bags
        // off this same stream: once for the shared bag, then once per player. Deques are
        // materialised only when something reads them (Shop for merchants, the shared
        // Common/Uncommon/Rare for chests); the rest we merely burn, since a shuffle costs the
        // same draws either way and nothing downstream reads the contents.
        var (shopRelics, sharedDeques) =
            PopulateRelicGrabBags(rng, characters, unlocks, playerUnlocks, withShopRelics, withChestRelics);

        // Shared Ancients (Darv) are shuffled, then a prefix is handed to each act after the first.
        // One array, shuffled once, then handed out as windows over itself. The Take/Skip/ToList
        // version rebuilt two lists per act to express "the next `take`, then the rest"; an
        // offset says the same thing and the draws are identical, since `take` is still rolled
        // against how many remain.
        var shared = ActData.SharedAncients
            .Where(_ => unlocks.IsEpochRevealed(ActData.SharedAncientEpoch))
            .ToArray();
        Shuffle(shared, rng);

        var sharedSubsets = new ArraySegment<string>[acts.Length];
        for (int i = 0; i < acts.Length; i++) sharedSubsets[i] = ArraySegment<string>.Empty;

        int offset = 0;
        for (int i = 1; i < acts.Length; i++)
        {
            int take = rng.NextInt(shared.Length - offset + 1);
            sharedSubsets[i] = new ArraySegment<string>(shared, offset, take);
            offset += take;
        }

        var generated = new List<GeneratedAct>(acts.Length);
        for (int i = 0; i < acts.Length; i++)
            generated.Add(GenerateAct(acts[i], rng, unlocks, isMultiplayer, sharedSubsets[i]));

        // A10+ gives the FINAL act a second boss, drawn from that act's bosses minus the one
        // already picked (RunManager.cs:731). It is the last thing generation does, after that
        // act's Ancient, so it costs exactly one draw and shifts nothing before it — every
        // other prediction is identical with the mode on or off.
        if (ascension >= AscensionLevels.DoubleBoss && generated.Count > 0)
        {
            var last = generated[^1];
            var others = last.Act.Bosses.Where(b => b.Name != last.Boss.Name).ToList();
            if (others.Count > 0)
                generated[^1] = last with { SecondBoss = others[rng.NextInt(0, others.Count)] };
        }

        // Chests run off their own run-level stream, so this is independent of everything above
        // and costs nothing when not asked for.
        var chests = sharedDeques is null
            ? null
            : ChestRelics.Generate(runSeed, characters.Count, sharedDeques,
                                   generated.Count, extraChestPicksBefore);

        return new GeneratedRun(generated, rng.Counter, shopRelics, chests);
    }

    /// <summary>
    /// Why the third relic in a shop is predictable when the rest of the shop is not.
    ///
    /// <c>MerchantInventory.PopulateRelicEntries</c> builds three relic entries. The first two
    /// roll their rarity off <c>PlayerRng.Rewards</c>, whose pity counter every card reward
    /// taken this run has moved, so they are unknowable. The third is hardcoded:
    /// <code>
    /// new RelicRarity[3] { RollRarity(Player), RollRarity(Player), RelicRarity.Shop }
    /// </code>
    /// and <c>RelicFactory.PullNextRelicFromBack</c> consumes NO RNG at all — it takes the back
    /// of the player's Shop deque, which was shuffled upfront and which nothing else ever draws
    /// from, because <c>RollRarity</c> only ever returns Common, Uncommon or Rare. So each shop
    /// takes exactly one Shop relic off the back, and the sequence across a run is fixed at
    /// generation time.
    ///
    /// Restocking after a purchase does not disturb it: <c>RestockAfterPurchase</c> calls
    /// <c>RollRarity</c>, so a refilled slot is never Shop rarity.
    ///
    /// Two caveats, both narrow:
    /// <list type="bullet">
    /// <item>Dragon Fruit is the one Shop relic with an <c>IsAllowed</c> gate
    /// (<c>IsBeforeAct3TreasureChest</c>, floor 38 in co-op). If it is still in the deque when a
    /// party reaches a shop past that floor it is dropped rather than offered, shifting the rest
    /// of the sequence up by one. Only reachable in the final act.</item>
    /// <item>Obtaining a Shop-rarity relic outside a shop would remove it from the deque. No
    /// upfront-generated source does that: Neow deals in Ancient rarity, and chests and combat
    /// rewards all roll Common/Uncommon/Rare.</item>
    /// </list>
    /// </summary>
    public const string ShopRelicSequence =
        "Third shop slot: the back of the player's upfront-shuffled Shop-rarity deque, one per shop visit.";

    /// <summary>ActModel.GenerateRooms — the per-act draw sequence, in order.</summary>
    private static GeneratedAct GenerateAct(
        ActDefinition act, Rng rng, UnlockState unlocks, bool isMultiplayer,
        IReadOnlyList<string> sharedAncients)
    {
        // 1. Events: the act's own plus shared, minus epoch-locked, then shuffled.
        //    We only need the draws, not the result — but the count must be exact.
        //    The filtered set depends only on the act and the unlocks, so it is cached on the
        //    act; the copy here exists because the shuffle scrambles it and the result is handed
        //    out in GeneratedAct.
        var events = new List<string>(act.EventsFor(unlocks));
        Shuffle(events, rng);

        // 2. Weak, then regular encounters — both accumulate into the same list, so the
        //    tag-repeat check for the first regular draw sees the last weak encounter.
        //    The pools are read, never mutated, so they are passed straight through.
        var normals = new List<Encounter>();
        DrawEncounters(normals, act.Weak, act.NumberOfWeakEncounters, rng);

        DrawEncounters(normals, act.Regular,
            act.GetNumberOfRooms(isMultiplayer) - act.NumberOfWeakEncounters, rng);

        // 3. Elites — always 15, into their own list.
        var elites = new List<Encounter>();
        DrawEncounters(elites, act.Elites, 15, rng);

        // 4. Boss, then 5. Ancient.
        var boss = act.Bosses[rng.NextInt(0, act.Bosses.Count)];

        // The list this used to build existed only to be indexed once. Its contents were the
        // act's own gated Ancients followed by the shared ones it was handed, so the same index
        // reads out of whichever of the two it falls in. Concat order is what makes that safe.
        var own = act.AncientsFor(unlocks);
        int total = own.Length + sharedAncients.Count;
        string ancient = "(none)";
        if (total > 0)
        {
            int at = rng.NextInt(0, total);
            ancient = at < own.Length ? own[at] : sharedAncients[at - own.Length];
        }

        return new GeneratedAct(act, boss, ancient, normals, elites, events);
    }

    /// <summary>
    /// The refill-and-draw loop around ActModel.AddWithoutRepeatingTags. The bag is refilled
    /// with the whole pool whenever it empties, and each draw first tries to avoid repeating
    /// the previous encounter's tags before falling back to any entry.
    /// </summary>
    private static void DrawEncounters(List<Encounter> into, IReadOnlyList<Encounter> pool, int count, Rng rng)
    {
        if (pool.Count == 0) return;

        var bag = new GrabBag<Encounter>(pool.Count);

        // The predicate is built ONCE and reads a captured local that the loop reassigns, rather
        // than being rebuilt each iteration. A lambda closes over the variable, not its value, so
        // this sees each new `last` while costing one closure per call instead of one per draw.
        // Elites alone are 15 draws an act.
        Encounter? last = null;
        Func<Encounter, bool> avoidsLast = e => !e.SharesTagsWith(last) && e != last;

        for (int i = 0; i < count; i++)
        {
            if (!bag.Any())
                for (int p = 0; p < pool.Count; p++) bag.Add(pool[p], 1.0);

            last = into.Count > 0 ? into[^1] : null;
            var picked = bag.GrabAndRemove(rng, avoidsLast) ?? bag.GrabAndRemove(rng);
            if (picked is not null) into.Add(picked);
        }
    }

    /// <summary>
    /// RelicGrabBag.Populate, for the shared bag and then each player's, in that order.
    ///
    /// The bag buckets its relics into one deque per rarity and shuffles each. The deques live
    /// in a Dictionary keyed by rarity, so they are shuffled in the order the rarities are
    /// FIRST SEEN walking the pool — not alphabetically, and not in GrabBagRarities order.
    /// Getting that wrong would not change the draw TOTAL (a shuffle of n always costs n-1),
    /// which is why act generation was already right; it would only misroute which draws land
    /// in which deque, and that matters now that we read one of them.
    ///
    /// The shared bag is populated through the overload that skips the rarity filter, so it
    /// keeps Event/Ancient/Starter entries too. A player's bag is the shared pool plus their
    /// character's, filtered to the four grab-bag rarities.
    ///
    /// All five characters currently have identically sized pools, so it is PLAYER COUNT that
    /// shifts the stream here, not who is playing. That is a property of the data, not of the
    /// algorithm — the moment a patch gives one character a differently sized pool, party
    /// composition starts changing Act 2/3 results, and this code already handles that.
    /// </summary>
    /// <param name="playerUnlocks">
    /// Each player's OWN unlock state, which is what filters their bag —
    /// <c>RelicGrabBag.Populate(player, rng)</c> reads <c>player.UnlockState</c>, not the run's.
    /// <paramref name="unlocks"/> is the run-level state, which the game builds as the SUPERSET
    /// of every player's, and which governs the shared bag and everything in act generation.
    /// Null means everyone matches the run state, which is right whenever the lobby is fully
    /// unlocked and is the assumption a search has to make about strangers' profiles anyway.
    /// </param>
    /// <param name="withChestRelics">
    /// Materialise the shared bag's Common/Uncommon/Rare deques instead of burning them. These are
    /// what a treasure chest pulls from the FRONT of, and they are shared by the whole party —
    /// unlike the Shop deques above, which are per player.
    /// </param>
    /// <returns>
    /// Each player's Shop deque back-to-front, and the shared bag's combat-rarity deques front
    /// first. Either is null when not asked for.
    /// </returns>
    private static (IReadOnlyList<IReadOnlyList<PoolRelic>>? Shop,
                    IReadOnlyDictionary<string, IReadOnlyList<PoolRelic>>? Shared)
        PopulateRelicGrabBags(
            Rng rng, IReadOnlyList<Character> characters, UnlockState unlocks,
            IReadOnlyList<UnlockState>? playerUnlocks, bool withShopRelics, bool withChestRelics)
    {
        // Everything except the shuffling is the same for every seed in a search, so it is built
        // once and reused. What remains here is exactly the RNG consumption and the copies that
        // the shuffles have to own.
        var plan = PlanFor(characters, unlocks, playerUnlocks, withShopRelics, withChestRelics);

        // Shared bag: populated through the overload that skips the rarity filter, so it holds
        // every rarity and its deques are shuffled in first-seen order like any other bag.
        Dictionary<string, IReadOnlyList<PoolRelic>>? sharedDeques =
            withChestRelics ? new Dictionary<string, IReadOnlyList<PoolRelic>>(StringComparer.Ordinal) : null;

        for (int i = 0; i < plan.Shared.Length; i++)
        {
            var source = plan.SharedSources[i];
            if (source is not null)
            {
                var deque = new List<PoolRelic>(source);
                Shuffle(deque, rng);
                sharedDeques![plan.Shared[i].Rarity] = deque;   // chests pull from the FRONT, so no reverse
            }
            else
            {
                BurnShuffle(rng, plan.Shared[i].Count);
            }
        }

        var result = withShopRelics ? new List<IReadOnlyList<PoolRelic>>(characters.Count) : null;

        for (int p = 0; p < plan.Players.Length; p++)
        {
            var bag = plan.Players[p];
            for (int i = 0; i < bag.Layout.Length; i++)
            {
                var source = bag.Sources[i];
                if (source is not null)
                {
                    var deque = new List<PoolRelic>(source);
                    Shuffle(deque, rng);
                    deque.Reverse();   // shops pull from the BACK, one per visit
                    result!.Add(deque);
                }
                else
                {
                    BurnShuffle(rng, bag.Layout[i].Count);
                }
            }
        }
        return (result, sharedDeques);
    }

    /// <summary>
    /// How many UpFront draws the relic-bag shuffles take, before act generation starts.
    ///
    /// A shuffle of n costs exactly n-1 draws whatever it produces, and every bounded draw
    /// consumes one step of the stream regardless of its bound, so the whole of
    /// <see cref="PopulateRelicGrabBags"/> collapses to a single number for anything that only
    /// needs to arrive at the right stream position. Materialising or burning a deque changes
    /// nothing here, which is why the flags are not parameters.
    ///
    /// Exists for the accelerator, which cannot walk the bags: it has no dictionaries, no pool
    /// tables and no reason to carry them, since the relics themselves are only read by the shop
    /// and chest criteria. Deriving the count from the same <see cref="PlanFor"/> the CPU path
    /// uses is what stops the two drifting apart — a stream that starts one draw late produces a
    /// plausible run that is wrong in every act.
    /// </summary>
    public static int RelicBagDraws(
        IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks = null)
    {
        var plan = PlanFor(characters, unlocks, playerUnlocks, withShop: false, withChest: false);

        int draws = 0;
        foreach (var (_, count) in plan.Shared) draws += count > 1 ? count - 1 : 0;
        foreach (var bag in plan.Players)
            foreach (var (_, count) in bag.Layout) draws += count > 1 ? count - 1 : 0;
        return draws;
    }

    /// <summary>
    /// One player's Shop deque, located within the UpFront stream.
    /// </summary>
    /// <param name="DrawsBefore">
    /// Draws taken by every shuffle ahead of this one. Its own shuffle then costs
    /// <c>Relics.Count - 1</c>.
    /// </param>
    /// <param name="Relics">The deque's contents in POOL order, before shuffling.</param>
    public sealed record ShopDeque(int DrawsBefore, IReadOnlyList<PoolRelic> Relics);

    /// <summary>
    /// Where each player's Shop deque sits in the UpFront stream, and what goes into it.
    ///
    /// The companion to <see cref="RelicBagDraws"/>, and for the same consumer. A shop relic is
    /// decided by one shuffle out of the dozen or so the bags perform, so an accelerator that
    /// wants to know where a particular relic lands needs to know how far into the stream that
    /// shuffle starts — and nothing else about the bags at all.
    ///
    /// Null for a player with no Shop-rarity relics, which cannot happen with the current pools
    /// but is the honest answer rather than an empty deque that reads as "shuffled to nothing".
    /// </summary>
    public static ShopDeque?[] ShopDeques(
        IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks = null)
    {
        var plan = PlanFor(characters, unlocks, playerUnlocks, withShop: true, withChest: false);
        var result = new ShopDeque?[characters.Count];

        int draws = 0;
        foreach (var (_, count) in plan.Shared) draws += count > 1 ? count - 1 : 0;

        for (int p = 0; p < plan.Players.Length; p++)
        {
            var bag = plan.Players[p];
            for (int i = 0; i < bag.Layout.Length; i++)
            {
                if (bag.Sources[i] is { } source) result[p] = new ShopDeque(draws, source);
                draws += bag.Layout[i].Count > 1 ? bag.Layout[i].Count - 1 : 0;
            }
        }
        return result;
    }

    /// <summary>The rarities <c>RelicFactory.RollRarity</c> can return, and so the only shared
    /// deques a chest ever reads.</summary>
    private static readonly HashSet<string> ChestRarities =
        new(StringComparer.Ordinal) { "Common", "Uncommon", "Rare" };

    /// <summary>
    /// Everything about the relic bags that does NOT depend on the seed: which deques exist, in
    /// which order, how large each is, and the exact relics in the ones we materialise.
    ///
    /// All of that is a function of the party, the unlock states and which deques were asked for,
    /// so it is the same for every seed in a search. It used to be rebuilt per seed: three walks
    /// building a List and a Dictionary, plus a Concat().ToList() of the whole relic pool for each
    /// player. Only the shuffling is actually per seed.
    /// </summary>
    private sealed class BagPlan
    {
        public required Character[] Characters;
        public required UnlockState Unlocks;
        public required IReadOnlyList<UnlockState>? PlayerUnlocks;
        public required bool WithShop;
        public required bool WithChest;

        /// <summary>Shared bag deques, in first-seen rarity order.</summary>
        public required (string Rarity, int Count)[] Shared;

        /// <summary>
        /// Parallel to <see cref="Shared"/>. Non-null only where a deque is materialised rather
        /// than burned, holding its relics in pool order ready to be copied and shuffled.
        /// </summary>
        public required PoolRelic[]?[] SharedSources;

        public required PlayerBag[] Players;

        public bool Matches(
            IReadOnlyList<Character> characters, UnlockState unlocks,
            IReadOnlyList<UnlockState>? playerUnlocks, bool withShop, bool withChest)
        {
            if (WithShop != withShop || WithChest != withChest) return false;
            if (!ReferenceEquals(Unlocks, unlocks) || !ReferenceEquals(PlayerUnlocks, playerUnlocks)) return false;
            if (Characters.Length != characters.Count) return false;
            for (int i = 0; i < Characters.Length; i++)
                if (Characters[i] != characters[i]) return false;
            return true;
        }
    }

    /// <summary>One player's bag deques and, for any that get materialised, their contents.</summary>
    private sealed class PlayerBag
    {
        public required (string Rarity, int Count)[] Layout;
        public required PoolRelic[]?[] Sources;
    }

    /// <summary>
    /// One cached plan per thread, matched by reference on the unlock states.
    ///
    /// Per thread rather than shared, for two reasons: the search runs under Parallel.For and a
    /// shared entry would be contended, and the web app can have two searches with different
    /// parties in flight at once, which on a single shared slot would thrash. Thread-local also
    /// bounds the memory by worker count, where a keyed cache would grow with every request,
    /// since UnlockState holds sets that compare by reference.
    ///
    /// A miss simply rebuilds, which is what every seed used to do, so the worst case is today.
    /// </summary>
    [ThreadStatic] private static BagPlan? _bagPlan;

    private static BagPlan PlanFor(
        IReadOnlyList<Character> characters, UnlockState unlocks,
        IReadOnlyList<UnlockState>? playerUnlocks, bool withShop, bool withChest)
    {
        var cached = _bagPlan;
        if (cached is not null && cached.Matches(characters, unlocks, playerUnlocks, withShop, withChest))
            return cached;

        var sharedRun = Unlocked(RelicPoolData.SharedRelics, "Shared", unlocks);

        var sharedLayout = DequeSizes(sharedRun, filtered: false).ToArray();
        var sharedSources = new PoolRelic[]?[sharedLayout.Length];
        for (int i = 0; i < sharedLayout.Length; i++)
            if (withChest && ChestRarities.Contains(sharedLayout[i].Rarity))
                sharedSources[i] = OfRarity(sharedRun, sharedLayout[i].Rarity);

        var players = new PlayerBag[characters.Count];

        var plan = new BagPlan
        {
            Characters = characters.ToArray(),
            Unlocks = unlocks,
            PlayerUnlocks = playerUnlocks,
            WithShop = withShop,
            WithChest = withChest,
            Shared = sharedLayout,
            SharedSources = sharedSources,
            Players = players,
        };

        for (int p = 0; p < characters.Count; p++)
        {
            var mine = playerUnlocks is not null && p < playerUnlocks.Count ? playerUnlocks[p] : unlocks;
            var shared = ReferenceEquals(mine, unlocks)
                ? sharedRun
                : Unlocked(RelicPoolData.SharedRelics, "Shared", mine);
            var own = Unlocked(RelicPoolData.RelicsFor(characters[p]), RelicPoolData.PoolKey(characters[p]), mine);

            var bag = new PoolRelic[shared.Count + own.Count];
            for (int i = 0; i < shared.Count; i++) bag[i] = shared[i];
            for (int i = 0; i < own.Count; i++) bag[shared.Count + i] = own[i];

            var layout = DequeSizes(bag, filtered: true).ToArray();
            var sources = new PoolRelic[]?[layout.Length];
            for (int i = 0; i < layout.Length; i++)
                if (withShop && layout[i].Rarity == "Shop")
                    sources[i] = OfRarity(bag, layout[i].Rarity);

            players[p] = new PlayerBag { Layout = layout, Sources = sources };
        }

        _bagPlan = plan;
        return plan;
    }

    /// <summary>The pool's entries of one rarity, in pool order. Plain loop rather than LINQ so
    /// the result is a right-sized array with no intermediate.</summary>
    private static PoolRelic[] OfRarity(IReadOnlyList<PoolRelic> pool, string rarity)
    {
        int n = 0;
        for (int i = 0; i < pool.Count; i++) if (pool[i].Rarity == rarity) n++;

        var result = new PoolRelic[n];
        for (int i = 0, at = 0; i < pool.Count; i++)
            if (pool[i].Rarity == rarity) result[at++] = pool[i];
        return result;
    }

    /// <summary>Drops the relics an unrevealed epoch gates, keeping the surviving pool order.</summary>
    private static IReadOnlyList<PoolRelic> Unlocked(
        IReadOnlyList<PoolRelic> pool, string poolKey, UnlockState unlocks)
    {
        if (!RelicPoolData.EpochGates.TryGetValue(poolKey, out var gates)) return pool;

        HashSet<string>? locked = null;
        foreach (var (epoch, names) in gates)
        {
            if (unlocks.IsEpochRevealed(epoch)) continue;
            locked ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names) locked.Add(n);
        }
        return locked is null ? pool : pool.Where(r => !locked.Contains(r.Name)).ToList();
    }

    /// <summary>
    /// The deques a pool produces, in the order the game's Dictionary hands them back: first
    /// seen while walking the pool. <paramref name="filtered"/> applies the player-bag rarity
    /// filter; the shared bag skips it.
    /// </summary>
    private static List<(string Rarity, int Count)> DequeSizes(IEnumerable<PoolRelic> pool, bool filtered)
    {
        var order = new List<(string, int)>(6);
        var index = new Dictionary<string, int>(6, StringComparer.Ordinal);

        foreach (var relic in pool)
        {
            if (filtered && Array.IndexOf(RelicPoolData.GrabBagRarities, relic.Rarity) < 0) continue;

            if (index.TryGetValue(relic.Rarity, out int at))
                order[at] = (relic.Rarity, order[at].Item2 + 1);
            else
            {
                index[relic.Rarity] = order.Count;
                order.Add((relic.Rarity, 1));
            }
        }
        return order;
    }

    private static void BurnShuffle(Rng rng, int count)
    {
        for (int n = count; n > 1; n--) rng.NextInt(n);
    }

    /// <summary>ListExtensions.UnstableShuffle — reverse Fisher-Yates, exact draw order.</summary>
    /// <summary>
    /// Reverse Fisher-Yates, matching <c>ListExtensions.UnstableShuffle</c>. Descending, so the
    /// BACK of the list settles first and the FRONT settles last, which is why shop relics (drawn
    /// from the back) and chest relics (drawn from the front) behave differently under a partial
    /// pass. Oracle-verified.
    ///
    /// Takes a span so arrays and lists share one implementation, and so the list case indexes
    /// backing storage directly rather than through List's bounds-checked indexer twice a swap.
    /// </summary>
    private static void Shuffle<T>(Span<T> items, Rng rng)
    {
        int n = items.Length;
        while (n > 1)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (items[k], items[n]) = (items[n], items[k]);
        }
    }

    private static void Shuffle<T>(List<T> list, Rng rng) =>
        Shuffle(CollectionsMarshal.AsSpan(list), rng);
}
