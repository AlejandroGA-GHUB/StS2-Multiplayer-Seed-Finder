namespace Sts2.SeedFinder.Core.Acts;

/// <summary>
/// What one treasure chest offers the party.
/// </summary>
/// <param name="Act">The act whose guaranteed chest this is, 1-based.</param>
/// <param name="Rarity">
/// The rolled rarity for this slot. Exact whenever the chest INDEX is right — it comes off a
/// run-level stream nothing else in the game touches.
/// </param>
/// <param name="Candidates">
/// The relics of that rarity in deque order, front first. <c>Candidates[0]</c> is what the chest
/// offers if nothing has drained the shared bag yet; each relic obtained earlier in the run that
/// happens to sit ahead of it moves the answer one further down this list. See
/// <see cref="ChestRelics"/> for why that is not knowable in advance.
/// </param>
public sealed record ChestSlot(int Act, string Rarity, IReadOnlyList<PoolRelic> Candidates)
{
    /// <summary>The relic offered when the shared bag is undisturbed. Null only if the pool ran dry.</summary>
    public PoolRelic? Expected => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>
    /// Whether <paramref name="slug"/> could be this slot's relic, allowing for
    /// <paramref name="tolerance"/> relics of the same rarity having been taken ahead of it.
    /// Tolerance 0 means "the shared bag is untouched", which is the exact prediction.
    /// </summary>
    public bool CouldBe(string slug, int tolerance = 0)
    {
        int limit = Math.Min(Candidates.Count, tolerance + 1);
        for (int i = 0; i < limit; i++)
            if (Candidates[i].Slug == slug) return true;
        return false;
    }
}

/// <summary>Every chest in a run, in the order the party opens them.</summary>
/// <param name="Slots">
/// Grouped by act: <c>Slots[0]</c> is Act 1's chest, one entry per player slot. The pick is
/// SHARED — every player sees all of them and votes — so which player ends up with which is a
/// vote outcome, not a seed property. Treat each act's entry as an unordered set.
/// </param>
public sealed record ChestOffers(IReadOnlyList<IReadOnlyList<ChestSlot>> Slots)
{
    /// <summary>The relics on offer at the given act's chest, undisturbed-bag assumption.</summary>
    public IEnumerable<PoolRelic> ExpectedIn(int act)
    {
        if (act < 1 || act > Slots.Count) yield break;
        foreach (var slot in Slots[act - 1])
            if (slot.Expected is { } relic) yield return relic;
    }
}

/// <summary>
/// The relics a treasure chest puts on the table, per act.
///
/// Every act has exactly one chest and no route can skip it: <c>StandardActMap.AssignPointTypes</c>
/// forces the whole of row <c>GetRowCount() - 7</c> to <c>MapPointType.Treasure</c> with
/// <c>CanBeModified = false</c>, and the one flag that swaps that row for elites
/// (<c>shouldReplaceTreasureWithElites</c>, a Warden mode) is passed a hardcoded <c>false</c> at
/// its only call site. In co-op those chests sit at floors 9, 24 and 38.
///
/// <c>TreasureRoomRelicSynchronizer.BeginRelicPicking</c> is the whole of the draw. Per player,
/// in slot order:
/// <code>
/// rarity = RelicFactory.RollRarity(rng)                  // 1 NextFloat, run-level stream
/// relic  = sharedGrabBag.PullFromFront(rarity, runState) // consumes NO rng
/// </code>
/// Two things make that unusually predictable, and one makes it less so.
///
/// GOOD: the stream is <c>RunRngType.TreasureRoomRelics</c>, which is RUN-level and which nothing
/// else in the game draws from. The rarity roll is a plain <c>NextFloat</c> against fixed
/// thresholds — no pity counter, no ascension term. So a chest's rarities depend on exactly one
/// thing: how many chest picks came before it.
///
/// GOOD: the award phase is free in the ordinary case. It only draws when players contest a relic
/// (rock-paper-scissors) or leave one unclaimed (<c>StableShuffle</c> of the leftovers), and
/// <c>UnstableShuffle</c> of 0 or 1 element consumes nothing. Everyone picking a different relic
/// costs zero.
///
/// LESS GOOD, and the reason for <see cref="ChestSlot.Candidates"/>: <c>PullFromFront</c> takes the
/// front of the SHARED bag, and every relic anyone obtains — elite rewards, a merchant's stock,
/// relic events — calls <c>SharedRelicGrabBag.Remove</c> on it. Those removals hit arbitrary
/// positions, because the shared bag and each player's bag are independent shuffles. So the rarity
/// is exact but the identity holds only while the bag is untouched.
///
/// THE INDEX CAVEAT: a chest is not the only thing that runs <c>BeginRelicPicking</c> — a <c>?</c>
/// map point can resolve to a treasure room (<c>UnknownMapPointOdds</c>, base 2% and growing 2%
/// each time it is not rolled, reset between acts), and that consumes a full player-count of draws
/// and shifts every later chest. Verified: on seed <c>8NZJ8J63RAKH</c> a <c>?</c> at floor 6 became
/// a chest, and all three acts matched only once the first pick was accounted for. Hence
/// <paramref name="extraPicksBefore"/> — the party can see this happen, so it is a fact they can
/// state rather than one we have to guess.
/// </summary>
public static class ChestRelics
{
    /// <summary>Rarities a chest can roll, in <c>RelicFactory.RollRarity</c> threshold order.</summary>
    private const string Common = "Common", Uncommon = "Uncommon", Rare = "Rare";

    /// <summary>
    /// Every relic a chest can offer: the shared pool's Common, Uncommon and Rare entries. A chest
    /// draws from the SHARED bag only, so unlike shops the party's characters are irrelevant —
    /// nobody's character pool can reach the table.
    /// </summary>
    public static IReadOnlyList<PoolRelic> All { get; } =
        RelicPoolData.SharedRelics
            .Where(r => r.Rarity is Common or Uncommon or Rare)
            .ToList();

    /// <summary>Look a relic up by slug or type name, punctuation-insensitively.</summary>
    public static PoolRelic? Find(string nameOrSlug)
    {
        var needle = ShopRelics.Key(nameOrSlug);
        foreach (var r in All)
            if (ShopRelics.Key(r.Slug) == needle || ShopRelics.Key(r.Name) == needle) return r;
        return null;
    }

    /// <summary>"war_paint" to "War Paint". Unknown input is returned as given.</summary>
    public static string Display(string nameOrSlug)
    {
        var relic = Find(nameOrSlug);
        return relic is null ? nameOrSlug : ShopRelics.TitleCase(relic.Value.Slug);
    }

    /// <summary>
    /// The co-op floor of each act's guaranteed chest. Act 3's matters: it is exactly the boundary
    /// <c>RelicModel.IsBeforeAct3TreasureChest</c> tests (<c>TotalFloor &lt; 38</c> in multiplayer),
    /// so relics gated on it are dropped from the deques at the Act 3 chest but not before.
    /// </summary>
    public static readonly int[] MultiplayerFloors = { 9, 24, 38 };

    /// <summary>
    /// Relics whose <c>IsAllowed</c> is <c>IsBeforeAct3TreasureChest</c>, and which therefore leave
    /// the deques for good once the party reaches the Act 3 chest. <c>RemoveDisallowedRelicsFromDeques</c>
    /// runs before every pull, so this is a filter on the deque, not a skip of one entry.
    ///
    /// Only these matter for chests. The other <c>IsAllowed</c> overrides in the relic set are
    /// either Shop rarity (Dragon Fruit, which a chest never rolls) or gated on player count
    /// (Silver Crucible and Winged Boots are singleplayer-only, Massive Scroll multiplayer-only)
    /// and are not in the shared pool a chest draws from.
    /// </summary>
    public static readonly IReadOnlySet<string> DroppedAtAct3Chest = new HashSet<string>(StringComparer.Ordinal)
    {
        "AmethystAubergine", "BookOfFiveRings", "BowlerHat", "FrozenEgg", "Girya",
        "JuzuBracelet", "LastingCandy", "LuckyFysh", "MealTicket", "MoltenEgg",
        "OldCoin", "Planisphere", "Shovel", "ToxicEgg", "WhiteBeastStatue", "WhiteStar",
    };

    /// <summary>
    /// Rolls each act's chest off the run-level treasure stream.
    /// </summary>
    /// <param name="runSeed">The hashed run seed.</param>
    /// <param name="playerCount">Chest slots per act — one relic is rolled per player.</param>
    /// <param name="sharedDeques">
    /// The shared bag's Common/Uncommon/Rare deques in front-to-back order, as
    /// <c>RelicGrabBag.Populate</c> shuffled them upfront.
    /// </param>
    /// <param name="actCount">Acts in the run, normally 3.</param>
    /// <param name="extraPicksBefore">
    /// Treasure picks the party took before Act 1's chest — i.e. <c>?</c> rooms that resolved into
    /// treasure rooms. Each one costs a full <paramref name="playerCount"/> of draws. Pass the
    /// count seen so far to re-align a run in progress.
    /// </param>
    public static ChestOffers Generate(
        ulong runSeed,
        int playerCount,
        IReadOnlyDictionary<string, IReadOnlyList<PoolRelic>> sharedDeques,
        int actCount = 3,
        int extraPicksBefore = 0)
    {
        var rng = new Rng(runSeed, GameHash.SnakeCase("TreasureRoomRelics"));

        // The deques drain as the run goes: each pull is gone for good, including the pulls this
        // very method models, so Act 2 does not re-offer what Act 1 took. Tracked BY IDENTITY
        // rather than as a per-rarity count, because the Act 3 gate below removes entries too —
        // a count would then index into a shorter list and skip past a relic still on offer.
        var taken = new HashSet<string>(StringComparer.Ordinal);

        // A ? room that became a treasure room is a chest in every respect: it draws a rarity per
        // player AND takes those relics out of the shared bag. Burning only the rng draws would
        // get the later rarities right and the later relics wrong.
        for (int i = 0; i < extraPicksBefore * playerCount; i++)
        {
            string rarity = RollRarity(rng.NextFloat());
            var available = Available(sharedDeques, rarity, taken, afterGate: false);
            if (available.Count > 0) taken.Add(available[0].Name);
        }

        var acts = new List<IReadOnlyList<ChestSlot>>(actCount);

        for (int act = 1; act <= actCount; act++)
        {
            // At the Act 3 chest the party is on floor 38, so IsBeforeAct3TreasureChest is false
            // and everything gated on it has already been stripped from the deques.
            bool afterGate = act >= 3;
            var slots = new List<ChestSlot>(playerCount);

            for (int p = 0; p < playerCount; p++)
            {
                string rarity = RollRarity(rng.NextFloat());
                var candidates = Available(sharedDeques, rarity, taken, afterGate);

                slots.Add(new ChestSlot(act, rarity, candidates));

                // The undisturbed prediction is that the front went to this slot.
                if (candidates.Count > 0) taken.Add(candidates[0].Name);
            }
            acts.Add(slots);
        }
        return new ChestOffers(acts);
    }

    /// <summary>
    /// <c>RelicFactory.RollRarity</c> — a plain roll against fixed thresholds. Notably NOT the
    /// pity-counter path that card rarities use, and unaffected by ascension.
    /// </summary>
    public static string RollRarity(float roll) =>
        roll < 0.5f ? Common : roll < 0.83f ? Uncommon : Rare;

    /// <summary>
    /// What is left of a rarity's deque: the entries this run has not already handed out, minus
    /// anything the Act 3 gate has dropped.
    ///
    /// The empty-deque fallback of <c>GetAvailableDeque</c> (Common gives way to Uncommon, then to
    /// Rare) is deliberately not modelled — reaching it needs a deque of 25+ to run dry, which
    /// three chests cannot do. An exhausted rarity yields an empty candidate list instead, so the
    /// caller reports nothing rather than something wrong.
    /// </summary>
    private static IReadOnlyList<PoolRelic> Available(
        IReadOnlyDictionary<string, IReadOnlyList<PoolRelic>> deques,
        string rarity, IReadOnlySet<string> taken, bool afterGate)
    {
        if (!deques.TryGetValue(rarity, out var deque)) return Array.Empty<PoolRelic>();

        return deque
            .Where(r => !taken.Contains(r.Name))
            .Where(r => !afterGate || !DroppedAtAct3Chest.Contains(r.Name))
            .ToList();
    }
}
