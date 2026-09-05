using System.Collections.Concurrent;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Modifiers;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Core;

/// <summary>Which player slots must satisfy a criterion.</summary>
public enum SlotRequirement
{
    /// <summary>At least one player in the lobby satisfies it.</summary>
    Any,
    /// <summary>Every player in the lobby satisfies it.</summary>
    All,
    /// <summary>Exactly the slots listed in <see cref="SearchCriteria.RequiredSlots"/>.</summary>
    Specific,
}

/// <summary>
/// How a player's card picks map onto fights.
///
/// The picker assigns fights by pick order, so three cards mean the first in fight 1, the
/// second in fight 2, the third in fight 3. That is a single assignment out of the k! that
/// would satisfy "I want to be offered all three", and pinning it costs roughly a factor of
/// k! in how many seeds you have to scan.
/// </summary>
public enum CardOrder
{
    /// <summary>Each pick must land in the fight its position names.</summary>
    Exact,

    /// <summary>
    /// The picks must land one per fight across the first k fights, in any assignment. Still
    /// one card per fight, so a player is genuinely offered all of them; only which fight
    /// produces which is free.
    /// </summary>
    AnyPermutation,
}

/// <summary>Where in Neow's offer a relic is allowed to appear.</summary>
public enum OfferSlot
{
    /// <summary>Anywhere in the three options.</summary>
    Anywhere,
    /// <summary>Only as the curse-branch option.</summary>
    CurseOnly,
    /// <summary>Only as one of the two positive options.</summary>
    PositiveOnly,
}

/// <summary>
/// One Neow requirement: a relic, and which player slots have to be offered it.
///
/// Per-criterion rather than one relic with one search-wide rule, for the same reason
/// <see cref="AncientCriterion"/> is: "Silken Tress for everyone, and Golden Pearl for P2"
/// is an ordinary thing to want out of a co-op lobby, and a single shared rule cannot say it.
///
/// <paramref name="Where"/> is almost never worth setting. Neow's curse and positive pools are
/// disjoint, so the relic already decides its own branch and constraining it can only agree
/// with that or contradict it.
///
/// <paramref name="PayloadCards"/> asks about what the relic HANDS you rather than about the
/// offer: the rares an Arcane Scroll or a Hefty Tablet would produce, which
/// <see cref="Cards.NeowCardPayload"/> draws off the same player's Rewards stream. It rides on
/// this criterion rather than being one of its own because the two questions are joined by the
/// player: "somebody is offered Arcane Scroll, and somebody's scroll gives Corruption" is a
/// weaker thing than asking both of the SAME player, and the weaker one is not what anyone
/// means. Cards are unordered — Hefty Tablet shows three at once and you keep one.
/// </summary>
public sealed record NeowCriterion(
    NeowRelic Relic,
    SlotRequirement Requirement = SlotRequirement.Any,
    IReadOnlyList<int>? RequiredSlots = null,
    OfferSlot Where = OfferSlot.Anywhere,
    IReadOnlyList<string>? PayloadCards = null)
{
    /// <summary>Cards the relic's payload must include. Never null; empty is the normal case.</summary>
    public IReadOnlyList<string> Cards => PayloadCards ?? Array.Empty<string>();

    /// <summary>Slots this criterion has to hold for. Empty for <see cref="SlotRequirement.Any"/>.</summary>
    public IReadOnlyList<int> ResolveSlots(int playerCount) => Requirement switch
    {
        SlotRequirement.All => Enumerable.Range(0, playerCount).ToArray(),
        SlotRequirement.Specific => RequiredSlots ?? Array.Empty<int>(),
        _ => Array.Empty<int>(),
    };

    public override string ToString()
    {
        string what = Cards.Count == 0
            ? Relic.Name
            : $"{Relic.Name} giving {string.Join(" and ", Cards.Select(CardCatalog.Display))}";

        return Requirement switch
        {
            SlotRequirement.All => $"{what}, for every player",
            SlotRequirement.Specific =>
                $"{what}, for {string.Join(" and ", (RequiredSlots ?? []).Select(s => $"P{s + 1}"))}",
            _ => $"{what}, for any player",
        };
    }
}

/// <summary>
/// One Ancient requirement. <paramref name="Relic"/> null means "this Ancient shows up at
/// all, whatever it offers" — the deliberately loose case.
///
/// <paramref name="Requirement"/> is per-criterion rather than inherited from the Neow search:
/// wanting Silken Tress for everyone but Vakuu's Fiddle for P1 alone is a normal thing to ask,
/// and folding both into one setting makes that unexpressible. Null inherits the search-wide
/// requirement.
/// </summary>
public sealed record AncientCriterion(
    Ancient Ancient,
    string? Relic = null,
    SlotRequirement? Requirement = null,
    IReadOnlyList<int>? RequiredSlots = null)
{
    public override string ToString()
    {
        var what = Relic is null ? Ancient.ToString() : $"{Ancient} offering {AncientOffers.Display(Relic)}";
        return Requirement switch
        {
            SlotRequirement.All => $"{what}, for every player",
            SlotRequirement.Specific => $"{what}, for {string.Join(" and ", (RequiredSlots ?? []).Select(s => $"P{s + 1}"))}",
            SlotRequirement.Any => $"{what}, for any player",
            _ => what,
        };
    }
}

/// <summary>
/// A boss one act must, or must not, end with. <paramref name="Act"/> is 1-based, as it is
/// everywhere the user sees it.
///
/// A boss is a property of the run rather than of a player, so unlike Neow and the Ancients
/// there is no slot rule here — everyone in the lobby fights the same one.
///
/// The test is against the act's whole boss SET, which is one boss normally and two on the
/// final act at A10+. So two include criteria on that act pin the pair, and one exclude keeps
/// a boss out of both slots.
/// </summary>
public sealed record BossCriterion(int Act, string Boss, bool Exclude = false)
{
    public override string ToString() =>
        $"Act {Act} {(Exclude ? "does not have" : "has")} {ActCatalog.Display(Boss)}";
}

/// <summary>
/// An event required near the front of one act's event order.
///
/// Generation shuffles the act's whole event pool once and the act then hands them out from the
/// front, so the ORDER is fixed by the seed but how far down it you get is not: each event room
/// takes the next entry that is currently allowed and not already seen this run, and half the
/// events gate themselves on HP, gold, deck or act. So a match means "near the front of the
/// queue", which is necessary for seeing it early but not sufficient.
/// </summary>
public sealed record EventCriterion(int Act, string Event, int WithinFirst = 3)
{
    public override string ToString() =>
        $"Act {Act} has {ActCatalog.Display(Event)} in the first {WithinFirst} of its event order";
}

/// <summary>
/// A card one player's combat reward must offer. <paramref name="Slot"/> is 0-based;
/// -1 means any player in the lobby.
///
/// This one is per-player by nature rather than by choice: every player rolls their own reward
/// off their own <c>Rewards</c> stream, seeded from their lobby slot, so P1 and P2 fighting the
/// same monsters are offered completely different cards.
///
/// It also carries an assumption the others do not — that the player's Neow pick took no draws
/// off that stream. See <see cref="Cards.CardRewardGenerator"/>.
///
/// <paramref name="Fight"/> is 1-based. Fight 1 is guaranteed by the map and needs no
/// assumption; fight 2 additionally assumes the party walks straight from the first Monster room
/// into another, with no shop, elite, event or rest between them.
/// </summary>
public sealed record CardCriterion(int Slot, string Card, int Fight = 1)
{
    public override string ToString() =>
        $"{(Slot < 0 ? "Any player" : $"P{Slot + 1}")} is offered {CardCatalog.Display(Card)} "
        + (Fight <= 1 ? "after the first fight" : $"after fight {Fight}");
}

/// <summary>
/// A card a player must start five copies of, via the Specialized run modifier.
///
/// Per player, like a card reward and for the same reason: the draw is off that player's own
/// Rewards stream and reads their character's pool, so P1 and P2 get different cards from one
/// seed. A slot of -1 means any player.
///
/// This is the only modifier payload the finder can name. See
/// <see cref="Modifiers.SpecializedPayload"/> for why it reduces to a single draw, and
/// <see cref="Modifiers.RunModifiers"/> for the two modifiers that can make it unpredictable.
/// </summary>
public sealed record SpecializedCriterion(int Slot, string Card)
{
    public override string ToString() =>
        $"{(Slot < 0 ? "Any player" : $"P{Slot + 1}")} starts with 5x {CardCatalog.Display(Card)}";
}

/// <summary>
/// A Neow option a player is ASSUMED to take, which is a different claim from any criterion
/// here: nothing about the seed decides it, the player does.
///
/// It exists because five of Neow's options draw cards the moment they are taken, off the same
/// <c>Rewards</c> stream the fight rewards come from, so taking one moves every card that
/// player is offered for the rest of the predictable hallway. The cost is in
/// <see cref="Cards.CardRewardGenerator.NeowRewardDrawCost"/>; everything else Neow can hand out
/// costs nothing and needs no pick.
///
/// A pick affects only its own slot, and only the card rewards. Nothing else in a run reads
/// that stream, so a wrong guess here cannot move a boss, an Ancient or a shop relic.
/// </summary>
public sealed record NeowPick(int Slot, string RelicSlug)
{
    public override string ToString() =>
        $"P{Slot + 1} takes {Neow.NeowRelics.Find(RelicSlug)?.Name ?? RelicSlug} at Neow";
}

/// <summary>
/// A relic a player's shop must stock in its third slot.
///
/// That slot is the one predictable thing about a merchant. Its rarity is hardcoded to
/// <c>RelicRarity.Shop</c> rather than rolled, and filling it draws no RNG at all — it takes
/// the back of the player's Shop deque, shuffled during upfront generation. The other two
/// slots roll their rarity off a pity counter that every card reward taken so far has moved,
/// so they are not knowable from the seed. See <see cref="RunGenerator.ShopRelicSequence"/>.
///
/// Per player, like the card reward, because each player has their own bag and their own
/// merchant. <paramref name="Visit"/> is which shop, 0 being the first one that player walks
/// into — not a floor. Skipping a shop shifts everything after it.
/// </summary>
public sealed record ShopRelicCriterion(int Slot, string Relic, int Visit = 0)
{
    public override string ToString() =>
        $"{(Slot < 0 ? "Any player" : $"P{Slot + 1}")} is offered {ShopRelics.Display(Relic)} "
        + $"at their {Ordinal(Visit + 1)} shop";

    private static string Ordinal(int n) => n switch
    {
        1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth", _ => $"{n}th",
    };
}

/// <summary>
/// A relic an act's treasure chest must put on the table.
///
/// Run-level, not per player, and deliberately so: the chest is a SHARED pick. It rolls one relic
/// per player and every player votes on the whole set, so which player walks away with which is
/// decided at the table, not by the seed. Asking for two relics in the same act therefore means
/// "both are in that chest", which is satisfiable up to the player count.
///
/// <paramref name="Act"/> is 1-based. Every act has exactly one chest and no route can skip it.
///
/// <paramref name="Tolerance"/> is what makes this usable past Act 1. The rarity roll is exact,
/// but the relic itself is the front of the SHARED bag, and every relic anyone picks up earlier in
/// the run — elite rewards, a merchant's stock, relic events — removes an entry from that bag. A
/// tolerance of n accepts the relic if it is within the first n+1 of its rarity still standing, so
/// 0 means "assume nobody has taken anything yet". See <see cref="ChestRelics"/>.
/// </summary>
public sealed record ChestRelicCriterion(int Act, string Relic, int Tolerance = 0)
{
    public override string ToString()
    {
        string with = Tolerance > 0 ? $" (allowing {Tolerance} taken earlier)" : "";
        return $"Act {Act}'s chest offers {ChestRelics.Display(Relic)}{with}";
    }
}

public sealed record SearchCriteria
{
    /// <summary>
    /// A single Neow relic to require, kept because it is the shape every caller used before
    /// Neow took a list. It is folded into <see cref="NeowCriteria"/> alongside
    /// <see cref="NeowRelicsWanted"/> rather than being an alternative to it, so a caller
    /// setting both gets both required instead of one of them silently disappearing.
    /// </summary>
    public NeowRelic? Relic { get; init; }

    /// <summary>
    /// Neow relics to require, each with its own slot rule. Prefer this to <see cref="Relic"/>.
    /// </summary>
    public IReadOnlyList<NeowCriterion> NeowRelicsWanted { get; init; } = Array.Empty<NeowCriterion>();

    /// <summary>
    /// Every Neow requirement this search carries, whichever way it was expressed. This is what
    /// the scan, the validator and the accelerator all read, so none of them has to know that
    /// the singular form exists.
    /// </summary>
    public IReadOnlyList<NeowCriterion> NeowCriteria =>
        Relic is null
            ? NeowRelicsWanted
            : NeowRelicsWanted
                .Prepend(new NeowCriterion(Relic, Requirement, RequiredSlots, Where))
                .ToArray();

    public required NeowContext Context { get; init; }

    /// <summary>
    /// Which Act 1 map to require — "Overgrowth" or "Underdocks". Null accepts either.
    ///
    /// Act 1 is the only act with a choice; acts 2 and 3 have a single candidate each. The
    /// roll comes off its own <c>act_selection</c> RNG rather than the UpFront stream, so
    /// testing it costs three draws against roughly four hundred for a full run — which is
    /// why the search checks it before anything else.
    /// </summary>
    public string? Act1 { get; init; }

    /// <summary>
    /// Ancients that must appear, optionally offering a specific relic. Checking these means
    /// generating the whole run, which is far more expensive than a Neow-only search — so
    /// the Neow filter, when present, always runs first.
    /// </summary>
    public IReadOnlyList<AncientCriterion> Ancients { get; init; } = Array.Empty<AncientCriterion>();

    /// <summary>Per-act boss requirements. Like the Ancients, these need the whole run generated.</summary>
    public IReadOnlyList<BossCriterion> Bosses { get; init; } = Array.Empty<BossCriterion>();

    /// <summary>Per-act event-order requirements. Also run-level, see <see cref="EventCriterion"/>.</summary>
    public IReadOnlyList<EventCriterion> Events { get; init; } = Array.Empty<EventCriterion>();

    /// <summary>
    /// Cards a player's first combat reward must offer. Far cheaper to test than the run
    /// criteria — about fourteen draws per player against four hundred — so these are checked
    /// before generation, not after.
    /// </summary>
    public IReadOnlyList<CardCriterion> Cards { get; init; } = Array.Empty<CardCriterion>();

    /// <summary>
    /// Which Neow option each player is assumed to take. Not a criterion — an input, like the
    /// party or the ascension. See <see cref="NeowPick"/>.
    /// </summary>
    public IReadOnlyList<NeowPick> NeowPicks { get; init; } = Array.Empty<NeowPick>();

    /// <summary>
    /// Run modifiers the lobby has ticked on, i.e. a Custom run. An input rather than a
    /// criterion: no seed decides them, the host does.
    ///
    /// Any modifier at all changes what Neow is. <c>Neow.GenerateInitialOptions</c> opens with
    /// <c>if (RunState.Modifiers.Count &lt;= 0)</c> and only then builds the curse branch, the
    /// positive pool and the coin flips; otherwise the player gets one forced option per
    /// modifier and no relic offer at all. So this and any Neow criterion are mutually
    /// exclusive, and <see cref="Validate"/> says so rather than quietly predicting an offer
    /// that will not happen.
    /// </summary>
    public IReadOnlyList<RunModifier> Modifiers { get; init; } = Array.Empty<RunModifier>();

    /// <summary>Cards a player must start five copies of. Needs Specialized in <see cref="Modifiers"/>.</summary>
    public IReadOnlyList<SpecializedCriterion> Specialized { get; init; } = Array.Empty<SpecializedCriterion>();

    /// <summary>
    /// Rewards draws already spent when Specialized takes its option, or null when that cannot
    /// be known. Null only happens with Draft or SealedDeck, the two Neow-option modifiers that
    /// sit ahead of Specialized in the game's list and have no fixed draw count.
    /// </summary>
    public int? SpecializedPriorDraws() =>
        RunModifiers.PriorRewardDraws(RunModifier.Specialized, Modifiers);

    /// <summary>
    /// What this run's modifiers cost every player's Rewards stream before their first fight,
    /// or null when a modifier with no fixed cost is on. Zero for an ordinary run.
    /// </summary>
    public int? ModifierRewardDraws() => RunModifiers.TotalNeowRewardDraws(Modifiers);

    /// <summary>
    /// Draws each player's Neow pick takes off their <c>Rewards</c> stream before the first
    /// fight rolls, indexed by slot. Zero for the great majority of picks.
    ///
    /// Two sources, and the criterion wins. Naming the cards an Arcane Scroll gives P1 asserts
    /// that P1 TOOK the scroll, so their fight rewards have to be read one draw along; leaving
    /// that to a separate switch the user could forget would quietly predict the wrong cards.
    /// Only a criterion that PINS a slot can do this: "for any player" does not say whose
    /// stream moves, so it sets nothing and the explicit pick stands.
    ///
    /// Computed once per search rather than per seed — it depends on the party and the
    /// criteria, neither of which the scan changes.
    /// </summary>
    public int[] RewardPriorDraws()
    {
        var draws = new int[PlayerCount];

        void Set(int slot, string slug)
        {
            if (slot < 0 || slot >= draws.Length) return;
            var character = slot < Characters.Count ? Characters[slot] : Character.Ironclad;
            int cost = CardRewardGenerator.NeowRewardDrawCost(slug, character);

            // -1 is Neow's Bones, whose shuffle length this tool does not model. Treating it as
            // zero would be a silent lie; the validator rejects it before we get here.
            draws[slot] = Math.Max(cost, 0);
        }

        foreach (var pick in NeowPicks) Set(pick.Slot, pick.RelicSlug);

        foreach (var want in NeowCriteria)
        {
            if (want.Cards.Count == 0 || want.Requirement == SlotRequirement.Any) continue;
            foreach (var slot in want.ResolveSlots(PlayerCount)) Set(slot, want.Relic.Slug);
        }

        // A Custom run's forced options draw off this same stream, and before any fight. Added
        // rather than assigned only for symmetry: a modifier and a Neow pick cannot coexist, so
        // everything above is zero whenever this is not.
        //
        // Null means a modifier with no fixed cost is on, and there is no honest number to use.
        // Validate rejects that combination alongside any card criterion, so reaching here with
        // one means no card is being predicted and the value is never read.
        if (RunModifiers.TotalNeowRewardDraws(Modifiers) is { } fromModifiers)
            for (int slot = 0; slot < draws.Length; slot++) draws[slot] += fromModifiers;

        return draws;
    }

    /// <summary>
    /// Relics a player's shop must stock in its third slot. These come out of the relic bags
    /// that upfront generation shuffles, so they need the run generated — but only the bag half
    /// of it, which is why <see cref="NeedsShopRelics"/> is tracked apart from the act criteria.
    /// </summary>
    public IReadOnlyList<ShopRelicCriterion> ShopRelicsWanted { get; init; } = Array.Empty<ShopRelicCriterion>();

    /// <summary>
    /// Relics an act's treasure chest must offer. Like the shop relics these come out of the
    /// upfront-shuffled bags, but from the SHARED one, and the rarity that selects between its
    /// deques is rolled on a run-level stream — so these are run-level criteria with no slot rule.
    /// </summary>
    public IReadOnlyList<ChestRelicCriterion> ChestRelicsWanted { get; init; } = Array.Empty<ChestRelicCriterion>();

    /// <summary>
    /// Treasure picks the party takes before Act 1's chest, i.e. <c>?</c> rooms that resolve into
    /// treasure rooms. Each shifts every chest by one, so it is an input rather than an assumption.
    /// Zero is right for the great majority of runs — the base chance is 2% per <c>?</c> room.
    /// </summary>
    public int ExtraChestPicks { get; init; }

    /// <summary>Whether anything here can only be answered by generating the run.</summary>
    public bool NeedsRun => Ancients.Count > 0 || Bosses.Count > 0 || Events.Count > 0
                            || ShopRelicsWanted.Count > 0 || ChestRelicsWanted.Count > 0;

    /// <summary>Whether generation has to materialise the Shop deques rather than burn them.</summary>
    public bool NeedsShopRelics => ShopRelicsWanted.Count > 0;

    /// <summary>Whether generation has to materialise the shared bag's combat-rarity deques.</summary>
    public bool NeedsChestRelics => ChestRelicsWanted.Count > 0;

    /// <summary>
    /// Whether the party has to be known. Card rewards need it because the pool is the
    /// character's, even though they do not need the run generated. Chests do not read any
    /// character pool, but they still need the party SIZE, which the context carries.
    /// </summary>
    public bool NeedsCharacters => NeedsRun || Cards.Count > 0 || ShopRelicsWanted.Count > 0
                                   || NeedsNeowPayloads || NeowPicks.Count > 0
                                   || Specialized.Count > 0;

    /// <summary>
    /// Whether any Neow criterion asks about the cards its relic hands out. Those come off the
    /// character's own rare pool, so they need the party even though they need no run.
    /// </summary>
    public bool NeedsNeowPayloads => NeowCriteria.Any(n => n.Cards.Count > 0);

    /// <summary>Party in lobby order. Required whenever <see cref="NeedsCharacters"/>.</summary>
    public IReadOnlyList<Character> Characters { get; init; } = Array.Empty<Character>();

    /// <summary>
    /// Ascension level. Generation ignores it entirely below 10; at A10+ (<c>DoubleBoss</c>)
    /// the final act gains a second boss, which is one extra draw at the very end of the
    /// stream and therefore changes nothing else about the run.
    /// </summary>
    public int Ascension { get; init; }

    /// <summary>
    /// The state the RUN generates against, which is the superset of everybody's. Build it with
    /// <see cref="UnlockState.Union"/> whenever <see cref="PlayerUnlocks"/> is set, because act
    /// generation and the shared chest bag read this one.
    /// </summary>
    public UnlockState Unlocks { get; init; } = new();

    /// <summary>
    /// Each player's OWN unlock state, in lobby order, or null when everyone is assumed to match
    /// <see cref="Unlocks"/>.
    ///
    /// Worth setting whenever it is actually known, because the cost of getting it wrong is not
    /// confined to the player it is wrong about: the bags are shuffled off the shared UpFront
    /// stream in lobby order, so a partner whose pools are a different size moves every draw
    /// after their bag, and act generation comes after all of them.
    ///
    /// Held by reference for the whole search — <c>RunGenerator</c> caches its bag plan on
    /// reference equality, so handing it a fresh list per seed would rebuild the plan every time.
    /// </summary>
    public IReadOnlyList<UnlockState>? PlayerUnlocks { get; init; }

    /// <summary>
    /// One player's own state, falling back to the run's. Card pools are per player too, so
    /// anything reading a character's cards wants this rather than <see cref="Unlocks"/>.
    /// </summary>
    public UnlockState UnlocksFor(int slot) =>
        PlayerUnlocks is { } own && (uint)slot < (uint)own.Count ? own[slot] : Unlocks;

    /// <summary>Every slot's own state, sized to the lobby. For callers that resolve it once.</summary>
    public UnlockState[] UnlocksBySlot()
    {
        var result = new UnlockState[PlayerCount];
        for (int i = 0; i < result.Length; i++) result[i] = UnlocksFor(i);
        return result;
    }

    public SlotRequirement Requirement { get; init; } = SlotRequirement.Any;
    public IReadOnlyList<int> RequiredSlots { get; init; } = Array.Empty<int>();
    public OfferSlot Where { get; init; } = OfferSlot.Anywhere;

    /// <summary>Whether card picks are pinned to their pick order. See <see cref="CardOrder"/>.</summary>
    public CardOrder CardOrder { get; init; } = CardOrder.Exact;
    public int SeedLength { get; init; } = SeedCodec.DefaultLength;

    public int PlayerCount => Context.PlayerCount;
}

/// <summary>
/// How far a scan has got, so a caller can report a rate while it is still running.
///
/// Counts seeds EXAMINED, not seeds that reached the criteria chain. On an accelerated search
/// those are wildly different numbers — the pre-filter may look at a billion seeds and hand
/// forward a few thousand — and the one a rate should be quoted against is the first. Reporting
/// the second would say a GPU search was slower than a CPU one while being two hundred times
/// faster.
///
/// Advanced in batches rather than per seed. A contended interlocked increment on every seed
/// would itself be a measurable share of the scan it is measuring.
/// </summary>
public sealed class SearchProgress
{
    private long _scanned;

    /// <summary>Seeds examined so far.</summary>
    public long Scanned => Interlocked.Read(ref _scanned);

    public void Advance(long seeds) => Interlocked.Add(ref _scanned, seeds);
}

/// <summary>
/// <paramref name="Run"/> is present only when the search had Ancient criteria.
///
/// <paramref name="OffersBySlot"/> is EMPTY for a Custom run rather than absent, because such a
/// run has no Neow offer at all: any modifier replaces the three options with one forced option
/// per modifier. Reporting the offer a normal run would have made is the one way this tool could
/// print something confidently false, so it prints nothing instead.
///
/// <paramref name="SpecializedCards"/> is the card each player starts five copies of, by slot,
/// and is null unless the Specialized modifier is on.
/// </summary>
public sealed record SeedHit(
    string Seed,
    NeowOffer[] OffersBySlot,
    GeneratedRun? Run = null,
    string?[]? SpecializedCards = null)
{
    public IEnumerable<int> MatchingSlots(NeowRelic relic, OfferSlot where) =>
        OffersBySlot.Index()
            .Where(x => SeedSearcher.OfferSatisfies(x.Item, relic, where))
            .Select(x => x.Index);
}

public static class SeedSearcher
{
    internal static bool OfferSatisfies(NeowOffer offer, NeowRelic relic, OfferSlot where) => where switch
    {
        OfferSlot.CurseOnly => offer.Curse == relic,
        OfferSlot.PositiveOnly => offer.Positive1 == relic || offer.Positive2 == relic,
        _ => offer.Contains(relic),
    };

    /// <summary>
    /// Scan a contiguous range of the seed-string space and yield matches.
    /// Ranges are deterministic, so a search can be sharded or resumed by index.
    ///
    /// <paramref name="candidateIndices"/> replaces the contiguous walk with a supplied stream
    /// of indices, which is how an accelerator plugs in without <c>Core</c> knowing one exists.
    /// The contract is deliberately narrow: a pre-filter may only NARROW which indices get
    /// looked at, never decide anything. Every index it yields is still put through the whole
    /// criteria chain here, so a pre-filter that returns too much costs time and a pre-filter
    /// that returns something wrong is caught. The failure it cannot catch is a pre-filter that
    /// returns too LITTLE, which is why the GPU path is gated on a differential harness that
    /// compares hit sets rather than sampling hits.
    ///
    /// <paramref name="startIndex"/> and <paramref name="count"/> still describe the range being
    /// searched when indices are supplied; they are what the caller reports as progress.
    /// </summary>
    public static IEnumerable<SeedHit> Search(
        SearchCriteria criteria,
        ulong startIndex,
        ulong count,
        int maxResults,
        CancellationToken cancellationToken = default,
        IEnumerable<ulong>? candidateIndices = null,
        SearchProgress? progress = null)
    {
        Validate(criteria);

        var ctx = criteria.Context;
        int playerCount = ctx.PlayerCount;

        // Every Neow requirement, prepared once. The curse fast path, the cheapest-first
        // ordering and the per-slot offer cache all live in there — see NeowPlan.
        var neowPlan = NeowPlan.Build(criteria);

        bool NeowMatches(ulong runSeed) => neowPlan.Matches(runSeed);

        // What each player's Neow pick costs their Rewards stream before the first fight ever
        // rolls. Resolved once: it depends on the party and the criteria, not on the seed.
        var priorDraws = criteria.RewardPriorDraws();

        // Each player's first card reward, tested against that player's own Rewards stream.
        // Roughly fourteen draws per slot, so this sits between the Neow filter and the run.
        bool CardsMatch(ulong runSeed)
        {
            if (criteria.Cards.Count == 0) return true;

            // Walk each player's stream once, as far as the deepest fight anyone asked about,
            // and cache it — fight 2 is a continuation of fight 1's stream, not a fresh one, so
            // computing them separately would both duplicate work and risk them disagreeing.
            int deepest = DeepestFight(criteria);
            var offered = new HallwayRewards?[playerCount];
            HallwayRewards For(int slot) => offered[slot] ??= CardRewardGenerator.Hallway(
                runSeed, slot, criteria.Characters[slot], deepest, criteria.Ascension,
                criteria.UnlocksFor(slot), priorDraws[slot]);

            bool Offers(int slot, CardCriterion want) =>
                For(slot).Fight(want.Fight)?.Cards.Any(c => c.TypeName == want.Card) == true;

            bool OffersAt(int slot, CardCriterion want, int fight) =>
                For(slot).Fight(fight)?.Cards.Any(c => c.TypeName == want.Card) == true;

            // AnyPermutation asks whether a player's k picks can be laid across the first k
            // fights one apiece. That is a bipartite perfect matching, and k is at most
            // MaxPredictableFight, so trying the assignments outright is both correct and
            // cheaper than building a matching algorithm for three elements.
            bool GroupMatches(int slot, List<CardCriterion> group)
            {
                int k = group.Count;
                bool Place(int i, int usedFights)
                {
                    if (i == k) return true;
                    for (int f = 0; f < k; f++)
                    {
                        if ((usedFights & (1 << f)) != 0) continue;
                        if (!OffersAt(slot, group[i], f + 1)) continue;
                        if (Place(i + 1, usedFights | (1 << f))) return true;
                    }
                    return false;
                }
                return Place(0, 0);
            }

            if (criteria.CardOrder == CardOrder.AnyPermutation)
            {
                foreach (var group in criteria.Cards.GroupBy(c => c.Slot))
                {
                    var picks = group.ToList();

                    // A slot of -1 means "any player", so the whole group has to fit on ONE
                    // player rather than being spread across the lobby.
                    bool ok = group.Key >= 0
                        ? GroupMatches(group.Key, picks)
                        : Enumerable.Range(0, playerCount).Any(s => GroupMatches(s, picks));
                    if (!ok) return false;
                }
                return true;
            }

            foreach (var want in criteria.Cards)
            {
                bool ok = want.Slot >= 0
                    ? Offers(want.Slot, want)
                    : Enumerable.Range(0, playerCount).Any(s => Offers(s, want));
                if (!ok) return false;
            }
            return true;
        }

        // One draw per player, so this is the cheapest criterion in the tool and sits ahead of
        // everything except act selection. Resolved once per search: the prior-draw count
        // depends on which modifiers are ticked, not on the seed.
        int specializedPrior = criteria.Specialized.Count > 0
            ? criteria.SpecializedPriorDraws() ?? 0
            : 0;

        bool SpecializedMatches(ulong runSeed)
        {
            if (criteria.Specialized.Count == 0) return true;

            bool Starts(int slot, SpecializedCriterion want) =>
                SpecializedPayload.Predict(
                    runSeed, slot, criteria.Characters[slot], criteria.UnlocksFor(slot),
                    isMultiplayer: true, specializedPrior)?.TypeName == want.Card;

            foreach (var want in criteria.Specialized)
            {
                bool ok = want.Slot >= 0
                    ? Starts(want.Slot, want)
                    : Enumerable.Range(0, playerCount).Any(s => Starts(s, want));
                if (!ok) return false;
            }
            return true;
        }

        // What each player actually starts with, for the result card. Only computed for a hit,
        // so this costs one draw per player on the seeds that survived everything else.
        string?[]? SpecializedFor(ulong runSeed)
        {
            if (!criteria.Modifiers.Contains(RunModifier.Specialized)) return null;
            if (criteria.Characters.Count < playerCount) return null;
            if (criteria.SpecializedPriorDraws() is not { } prior) return null;

            var cards = new string?[playerCount];
            for (int slot = 0; slot < playerCount; slot++)
                cards[slot] = SpecializedPayload.Predict(
                    runSeed, slot, criteria.Characters[slot], criteria.UnlocksFor(slot),
                    isMultiplayer: true, prior)?.TypeName;
            return cards;
        }

        // Which map each act drew is known from act selection alone, three draws, so a boss or
        // event that map cannot produce rejects the seed before the ~400-draw run generation.
        // Only Act 1 has two candidates, so this only ever bites there — but that is half the
        // seeds, and it costs a lookup.
        bool MapsCouldSatisfy(ActDefinition[] acts)
        {
            // Only the "must have" side pre-filters. An excluded boss the map cannot produce
            // is trivially satisfied, so rejecting on it would throw away every valid seed.
            foreach (var want in criteria.Bosses)
                if (!want.Exclude && !acts[want.Act - 1].Bosses.Any(b => b.Name == want.Boss))
                    return false;

            foreach (var want in criteria.Events)
                if (!acts[want.Act - 1].Events.Contains(want.Event)
                    && !ActData.SharedEvents.Contains(want.Event)) return false;

            return true;
        }

        // Act criteria need the full run generated, which costs ~20x a Neow check, so this only
        // ever runs on seeds the (cheap) Neow, Act 1 and map filters already accepted. The acts
        // already selected for those checks are reused rather than rolled again.
        GeneratedRun? RunIfActsMatch(ulong runSeed, ActDefinition[]? acts)
        {
            if (!criteria.NeedsRun) return null;

            var run = RunGenerator.GenerateRun(
                runSeed, criteria.Unlocks, isMultiplayer: true, criteria.Characters, acts,
                criteria.Ascension, criteria.NeedsShopRelics,
                playerUnlocks: criteria.PlayerUnlocks,
                withChestRelics: criteria.NeedsChestRelics,
                extraChestPicksBefore: criteria.ExtraChestPicks);

            // Shop relics come out of the bags, which are shuffled before act generation, so
            // they are already decided by the time we get here — and they are a plain lookup
            // rather than a scan, so test them ahead of everything else.
            foreach (var want in criteria.ShopRelicsWanted)
            {
                var slots = want.Slot >= 0
                    ? new[] { want.Slot }
                    : Enumerable.Range(0, playerCount).ToArray();

                bool ok = slots.Any(s =>
                    run.ShopRelic(s, want.Visit) is { } got &&
                    string.Equals(got.Slug, want.Relic, StringComparison.OrdinalIgnoreCase));
                if (!ok) return null;
            }

            // Chest relics, grouped by act. Asking for two relics in one act means two DIFFERENT
            // slots of that chest must supply them, so this is an assignment problem rather than
            // a test of each criterion on its own — see ChestSatisfies.
            if (criteria.ChestRelicsWanted.Count > 0)
            {
                if (run.Chests is null) return null;
                foreach (var byAct in criteria.ChestRelicsWanted.GroupBy(w => w.Act))
                {
                    if (byAct.Key < 1 || byAct.Key > run.Chests.Slots.Count) return null;
                    if (!ChestSatisfies(run.Chests.Slots[byAct.Key - 1], byAct.ToList())) return null;
                }
            }

            // Cheapest first: a boss is one string compare, an event a short prefix scan, and
            // an Ancient's offer is a fresh RNG chain per player.
            foreach (var want in criteria.Bosses)
            {
                bool present = run.Acts[want.Act - 1].Bosses.Any(b => b.Name == want.Boss);
                if (present == want.Exclude) return null;
            }

            foreach (var want in criteria.Events)
                if (!run.Acts[want.Act - 1].Events.Take(want.WithinFirst).Contains(want.Event))
                    return null;

            foreach (var want in criteria.Ancients)
            {
                // Which act it turned up in matters: Darv's relic pool is gated on the act.
                int actIndex = -1;
                for (int i = 0; i < run.Acts.Count; i++)
                {
                    if (AncientOffers.TryParse(run.Acts[i].Ancient, out var a) && a == want.Ancient)
                    {
                        actIndex = i;
                        break;
                    }
                }
                if (actIndex < 0) return null;
                if (want.Relic is null) continue;

                // This criterion's own slot rule, falling back to the search-wide one.
                var rule = want.Requirement ?? criteria.Requirement;
                var slots = rule switch
                {
                    SlotRequirement.All => Enumerable.Range(0, playerCount).ToArray(),
                    SlotRequirement.Specific => (want.Requirement is null
                        ? criteria.RequiredSlots
                        : want.RequiredSlots ?? []).ToArray(),
                    _ => Array.Empty<int>(),
                };

                var ctxA = new AncientContext { ActIndex = actIndex };
                bool ok = rule == SlotRequirement.Any
                    ? Enumerable.Range(0, playerCount).Any(s => SlotOffers(runSeed, s, want, ctxA))
                    : slots.Length > 0 && slots.All(s => SlotOffers(runSeed, s, want, ctxA));
                if (!ok) return null;
            }
            return run;
        }

        // A relic counts as offered if ANY branch produces it. Several Ancients gate a pool
        // on deck state we cannot know from the seed; treating those as misses would throw
        // away real hits. The CLI labels whether a hit is guaranteed or branch-dependent.
        bool SlotOffers(ulong runSeed, int slot, AncientCriterion want, AncientContext ctxA) =>
            AncientOffers.Branches(want.Ancient, runSeed, slot, ctxA)
                .Any(b => b.Offer.Options.Contains(want.Relic!, StringComparer.OrdinalIgnoreCase));

        // Four results of slack per requested hit, so producers rarely block on the consumer,
        // but clamped: a large --results would otherwise overflow int and hand BlockingCollection
        // a negative capacity, which throws before a single seed is scanned.
        int capacity = (int)Math.Clamp((long)Math.Max(maxResults, 1) * 4, 4L, 65_536L);
        var results = new BlockingCollection<SeedHit>(capacity);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int found = 0;

        var producer = Task.Run(() =>
        {
            try
            {
                var options = new ParallelOptions
                {
                    CancellationToken = cts.Token,
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                };

                void Evaluate(ulong index)
                {
                    if (cts.IsCancellationRequested) return;

                    var seed = SeedCodec.FromIndex(index, criteria.SeedLength);
                    ulong runSeed = SeedCodec.RunSeed(seed);

                    // Cheapest filter first: act selection is three draws on its own RNG.
                    ActDefinition[]? acts = null;
                    if (criteria.Act1 is not null || criteria.NeedsRun)
                    {
                        acts = RunGenerator.SelectActs(runSeed, criteria.Unlocks, isMultiplayer: true);
                        if (criteria.Act1 is not null
                            && !acts[0].Name.Equals(criteria.Act1, StringComparison.OrdinalIgnoreCase)) return;
                        if (!MapsCouldSatisfy(acts)) return;
                    }

                    if (!SpecializedMatches(runSeed)) return;
                    if (!NeowMatches(runSeed)) return;
                    if (!CardsMatch(runSeed)) return;

                    var run = RunIfActsMatch(runSeed, acts);
                    if (criteria.NeedsRun && run is null) return;

                    results.Add(new SeedHit(
                        seed,
                        // A Custom run reaches none of Neow's own options, so there is no offer
                        // to report. See SeedHit.
                        criteria.Modifiers.Count > 0
                            ? Array.Empty<NeowOffer>()
                            : NeowGenerator.PredictAllOffers(runSeed, ctx),
                        run,
                        SpecializedFor(runSeed)), cts.Token);
                    if (Interlocked.Increment(ref found) >= maxResults) cts.Cancel();
                }

                if (candidateIndices is null)
                {
                    // Counted per partition rather than per seed. Parallel.For hands each worker
                    // a chunk at a time, so the local total is folded in often enough to watch
                    // live while costing one interlocked add per chunk instead of per seed.
                    //
                    // Only this branch counts. When indices are supplied the scan happened
                    // somewhere else and already reported what it looked at; counting arrivals
                    // here would measure the pre-filter's OUTPUT and call it throughput.
                    Parallel.For(
                        0L, (long)count, options,
                        () => 0L,
                        (i, _, local) =>
                        {
                            Evaluate(startIndex + (ulong)i);
                            return local + 1;
                        },
                        local => progress?.Advance(local));
                }
                else
                {
                    Parallel.ForEach(candidateIndices, options, Evaluate);
                }
            }
            catch (OperationCanceledException) { /* expected on early exit */ }
            finally { results.CompleteAdding(); }
        }, CancellationToken.None);

        int emitted = 0;
        foreach (var hit in results.GetConsumingEnumerable())
        {
            yield return hit;
            if (++emitted >= maxResults) { cts.Cancel(); break; }
        }

        try { producer.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Rough expected match rate, for sizing a scan. Estimated by sampling rather than derived,
    /// because the positive pool's size varies with the curse rolled.
    ///
    /// With several Neow relics this samples the criteria TOGETHER rather than multiplying a
    /// rate per criterion. They are not independent — an offer holds three relics, so asking one
    /// player for two of them is a very different question from asking two players for one
    /// each — and a product would report the joint case as far rarer than it is.
    /// </summary>
    public static double MatchProbability(SearchCriteria criteria, int sampleSize = 20000)
    {
        // Act 1 is one of two candidates, and the roll is independent of everything else.
        double actFactor = criteria.Act1 is null ? 1.0 : 1.0 / ActData.ByIndex[0].Length;

        var plan = NeowPlan.Build(criteria);
        if (plan.IsEmpty) return actFactor;

        int hits = 0;
        for (ulong i = 0; i < (ulong)sampleSize; i++)
        {
            ulong runSeed = SeedCodec.RunSeed(SeedCodec.FromIndex(i * 2654435761uL));
            if (plan.Matches(runSeed)) hits++;
        }

        // Floored rather than allowed to be zero: this figure sizes a scan and divides into a
        // seed count, and a sample of 20k says nothing useful about a 1-in-a-billion search
        // beyond "rare".
        return actFactor * Math.Max(hits / (double)sampleSize, 1e-9);
    }

    /// <summary>
    /// Reject criteria that are malformed or that no seed could satisfy. Called by
    /// <see cref="Search"/>, and public so a caller can fail before printing a plan for a
    /// search that is about to be refused.
    /// </summary>
    public static void Validate(SearchCriteria c)
    {
        // Named by criterion rather than by flag: this runs in the web app as well as the CLI,
        // and a list of command-line switches is not an instruction anyone can follow there.
        // NeedsCharacters is deliberately not the test here: a Neow pick sets it without asking
        // anything of the seed, so a search carrying only picks would otherwise be accepted and
        // then match everything.
        if (c.NeowCriteria.Count == 0 && c.Act1 is null
            && !c.NeedsRun && c.Cards.Count == 0 && c.ShopRelicsWanted.Count == 0
            && c.Specialized.Count == 0)
            throw new ArgumentException(
                "Nothing to search for. Set at least one criterion: a Neow relic, the Act 1 map, "
                + "an Ancient's offer, a boss, an event, a card reward, a shop relic, a "
                + "treasure chest or a Specialized starting card.");

        if (c.Act1 is not null && !ActData.ByIndex[0].Any(a => a.Name.Equals(c.Act1, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"'{c.Act1}' is not an Act 1 map. Choose one of: " +
                string.Join(", ", ActData.ByIndex[0].Select(a => a.Name)));

        if (c.Ascension < 0 || c.Ascension > AscensionLevels.Max)
            throw new ArgumentException(
                $"ascension {c.Ascension} is not a level; use 0-{AscensionLevels.Max}.");

        if (c.NeedsCharacters && c.Characters.Count != c.PlayerCount)
            throw new ArgumentException(
                $"boss, event, card and Ancient criteria need --characters with exactly {c.PlayerCount} " +
                "entries (one per player, in lobby order), because generation depends on the " +
                "party and card rewards come out of each character's own pool.");

        ValidateModifierCriteria(c);
        ValidateActCriteria(c);
        ValidateCardCriteria(c);
        ValidateShopCriteria(c);
        ValidateChestCriteria(c);

        foreach (var a in c.Ancients)
        {
            if (a.Relic is null) continue;
            var pool = AncientOffers.AllRelics(a.Ancient);
            if (!pool.Contains(a.Relic, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"{a.Ancient} never offers '{AncientOffers.Display(a.Relic)}'. It can offer: " +
                    string.Join(", ", pool.Select(AncientOffers.Slug)));
        }

        foreach (var want in c.NeowCriteria)
        {
            var relic = want.Relic;

            if (!c.Context.IsAllowed(relic))
                throw new ArgumentException(
                    $"'{relic.Name}' can never be offered under these settings " +
                    $"({c.PlayerCount} players, availability: {relic.Availability}).");

            if (want.Where == OfferSlot.CurseOnly && relic.Pool != NeowPool.Curse)
                throw new ArgumentException($"'{relic.Name}' is not a curse-branch relic.");

            if (want.Where == OfferSlot.PositiveOnly && relic.Pool == NeowPool.Curse)
                throw new ArgumentException($"'{relic.Name}' is only ever offered as the curse option.");

            if (want.Requirement == SlotRequirement.Specific)
                foreach (var slot in want.RequiredSlots ?? [])
                    if (slot < 0 || slot >= c.PlayerCount)
                        throw new ArgumentException(
                            $"'{relic.Name}' is required for P{slot + 1}, but the lobby has " +
                            $"{c.PlayerCount} player{(c.PlayerCount == 1 ? "" : "s")}.");
        }

        ValidateNeowCombinations(c);
        ValidateNeowPayloads(c);
    }

    /// <summary>
    /// Rejects payload and pick requests that no run could produce.
    ///
    /// Payloads are the narrowest thing this tool predicts: two relics, a fixed count each, and
    /// a pool of nothing but that character's rares. Every way of getting it wrong is therefore
    /// something the user can be told precisely, rather than left to discover as a search that
    /// finds nothing.
    /// </summary>
    /// <summary>
    /// The three ways a Specialized search can be incoherent, each said as the thing the user
    /// has to change rather than as a rule number.
    /// </summary>
    private static void ValidateModifierCriteria(SearchCriteria c)
    {
        if (c.Modifiers.Count > 0 && c.NeowCriteria.Count > 0)
            throw new ArgumentException(
                "A Custom run has no Neow offer to search for. Any modifier at all replaces "
                + "Neow's three options with one forced option per modifier, so a Neow relic and "
                + "a run modifier can never both be satisfied. Drop one of them.");

        // A pick names one of Neow's own options, which a Custom run never reaches.
        if (c.Modifiers.Count > 0 && c.NeowPicks.Count > 0)
            throw new ArgumentException(
                "A Custom run has no Neow options to take, so there is no pick to assume. "
                + "Remove the assumed pick, or the modifier.");

        // Every modifier's forced option draws off the same stream the fight rewards come from,
        // so a cost we cannot count is a card reward we cannot predict either.
        if (c.Cards.Count > 0 && c.ModifierRewardDraws() is null)
            throw new ArgumentException(
                "Card rewards cannot be predicted alongside Draft or Sealed Deck. Both hand out "
                + "cards off the same stream the fight rewards come from, and neither draws a "
                + "fixed number of them, so every later card in the run moves by an amount the "
                + "seed does not decide.");

        if (c.Specialized.Count == 0) return;

        if (!c.Modifiers.Contains(RunModifier.Specialized))
            throw new ArgumentException(
                "A starting card only exists with the Specialized modifier ticked on. Enable it, "
                + "or drop the starting card.");

        if (c.SpecializedPriorDraws() is null)
            throw new ArgumentException(
                "Specialized cannot be predicted alongside Draft or Sealed Deck. Both take their "
                + "Neow option first and neither has a fixed number of draws, so where "
                + "Specialized lands in the stream is not knowable before the run is played.");

        foreach (var want in c.Specialized)
        {
            if (want.Slot >= c.PlayerCount)
                throw new ArgumentException(
                    $"P{want.Slot + 1} is not in a {c.PlayerCount}-player lobby.");

            var slots = want.Slot >= 0
                ? new[] { want.Slot }
                : Enumerable.Range(0, c.PlayerCount).ToArray();

            foreach (int slot in slots)
            {
                if (slot >= c.Characters.Count) continue;
                var pool = SpecializedPayload.Pool(c.Characters[slot], c.UnlocksFor(slot));
                if (pool.Any(e => e.TypeName == want.Card)) continue;

                // "Any player" is only impossible when NOBODY could be given it, so keep
                // looking before rejecting.
                if (want.Slot < 0 && slots.Any(o => o != slot && o < c.Characters.Count
                        && SpecializedPayload.Pool(c.Characters[o], c.UnlocksFor(o))
                            .Any(e => e.TypeName == want.Card)))
                    break;

                throw new ArgumentException(
                    $"Specialized can never hand {CardCatalog.Display(want.Card)} to "
                    + (want.Slot < 0 ? "anyone in this lobby" : $"P{want.Slot + 1}")
                    + ". It draws from that player's own pool, and only Common, Uncommon and "
                    + "Rare cards are reachable.");
            }
        }
    }

    private static void ValidateNeowPayloads(SearchCriteria c)
    {
        foreach (var want in c.NeowCriteria)
        {
            if (want.Cards.Count == 0) continue;

            int capacity = NeowCardPayload.CardCount(want.Relic.Slug);
            if (capacity == 0)
                throw new ArgumentException(
                    $"{want.Relic.Name} does not hand out cards this tool can name. Only "
                    + string.Join(" and ", NeowCardPayload.Predictable.Select(s => NeowRelics.Find(s)!.Name))
                    + " do: they run the ordinary reward factory over your own rares, with the "
                    + "rarity and upgrade rolls removed.");

            if (want.Cards.Count > capacity)
                throw new ArgumentException(
                    $"{want.Relic.Name} hands out {capacity} card{(capacity == 1 ? "" : "s")}, so it "
                    + $"cannot give all {want.Cards.Count} of those at once.");

            var dupes = want.Cards.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0)
                throw new ArgumentException(
                    $"{want.Relic.Name} cannot give two of {CardCatalog.Display(dupes[0])}: each pick "
                    + "is blacklisted from the ones after it.");

            // Whose pool has to hold the card, and how many of them. "For any player" needs
            // somebody in the party who could be handed it; a row that pins slots needs EVERY
            // one of them, because it asks the same thing of each. Rare pools differ by
            // character, so "for every player" is where an impossible ask usually comes from.
            var slots = want.Requirement == SlotRequirement.Any
                ? Enumerable.Range(0, c.PlayerCount).ToList()
                : want.ResolveSlots(c.PlayerCount).ToList();
            bool everySlot = want.Requirement != SlotRequirement.Any;

            foreach (var card in want.Cards)
            {
                bool Holds(int s, Func<Character, UnlockState?, IEnumerable<CardEntry>> pool) =>
                    s < c.Characters.Count && pool(c.Characters[s], c.UnlocksFor(s)).Any(e => e.TypeName == card);

                bool reachable = everySlot
                    ? slots.Count > 0 && slots.All(s => Holds(s, NeowCardPayload.Offerable))
                    : slots.Any(s => Holds(s, NeowCardPayload.Offerable));
                if (reachable) continue;

                // Split apart because the two are different mistakes: a card from the wrong
                // character's pool, and a card of the wrong rarity entirely.
                bool inSomePool = slots.Any(s => Holds(s, CardCatalog.Offerable));

                throw new ArgumentException(inSomePool && !everySlot
                    ? $"{want.Relic.Name} only ever gives Rares, and {CardCatalog.Display(card)} is not one."
                    : $"{CardCatalog.Display(card)} is not in the rare pool of "
                      + (want.Requirement == SlotRequirement.Any
                            ? "anyone in this party"
                            : string.Join(" and ", slots.Select(x => $"P{x + 1} ({(x < c.Characters.Count ? c.Characters[x].ToString() : "?")})")))
                      + ", so no seed can hand it over.");
            }
        }

        foreach (var pick in c.NeowPicks)
        {
            if (pick.Slot < 0 || pick.Slot >= c.PlayerCount)
                throw new ArgumentException(
                    $"a Neow pick names P{pick.Slot + 1}, but the lobby has {c.PlayerCount} players.");

            var relic = NeowRelics.Find(pick.RelicSlug)
                ?? throw new ArgumentException($"'{pick.RelicSlug}' is not a relic Neow offers.");

            // Neow's Bones shuffles a list whose length depends on the lobby's own Neow pool,
            // and that shuffle is not modelled. Guessing zero would silently predict the wrong
            // cards for every fight that player has.
            if (CardRewardGenerator.NeowRewardDrawCost(relic.Slug, Character.Ironclad) < 0)
                throw new ArgumentException(
                    $"{relic.Name} shifts the card rewards by an amount this tool does not model "
                    + "yet, so it cannot be assumed as a pick. Leave it unset and read the card "
                    + "rewards as though nothing was taken.");
        }

        foreach (var group in c.NeowPicks.GroupBy(p => p.Slot).Where(g => g.Count() > 1))
            throw new ArgumentException(
                $"P{group.Key + 1} has {group.Count()} Neow picks. A player takes one option.");

        // A criterion that names a payload has already said which relic that player takes, so a
        // pick naming a different one is two contradictory claims about the same stream.
        foreach (var want in c.NeowCriteria)
        {
            if (want.Cards.Count == 0 || want.Requirement == SlotRequirement.Any) continue;
            foreach (var slot in want.ResolveSlots(c.PlayerCount))
                foreach (var pick in c.NeowPicks)
                    if (pick.Slot == slot && !string.Equals(pick.RelicSlug, want.Relic.Slug, StringComparison.Ordinal))
                        throw new ArgumentException(
                            $"P{slot + 1} cannot both take {NeowRelics.Find(pick.RelicSlug)?.Name ?? pick.RelicSlug} "
                            + $"and be handed cards by {want.Relic.Name}: naming the cards a relic gives "
                            + "says that player took it.");
        }
    }

    /// <summary>
    /// Rejects Neow demands that no single offer could ever satisfy, rather than scanning
    /// forever for one. This only became reachable when Neow took a list of relics: one relic
    /// can always be offered to somebody, but two aimed at the same player can contradict.
    ///
    /// Only criteria that PIN a slot are considered. <see cref="SlotRequirement.Any"/> asks for
    /// somebody in the lobby, so it can move to whichever player is free and constrains nothing.
    /// </summary>
    private static void ValidateNeowCombinations(SearchCriteria c)
    {
        var bySlot = new Dictionary<int, List<NeowRelic>>();
        foreach (var want in c.NeowCriteria)
        {
            if (want.Requirement == SlotRequirement.Any) continue;
            foreach (var slot in want.ResolveSlots(c.PlayerCount))
            {
                var list = bySlot.TryGetValue(slot, out var existing) ? existing : bySlot[slot] = [];
                if (!list.Contains(want.Relic)) list.Add(want.Relic);
            }
        }

        foreach (var (slot, relics) in bySlot)
        {
            string who = $"P{slot + 1}";

            // An offer is exactly one curse and two positives, so the shape alone rules some
            // asks out before any seed is looked at.
            var curses = relics.Where(r => r.Pool == NeowPool.Curse).ToList();
            if (curses.Count > 1)
                throw new ArgumentException(
                    $"{who} cannot be offered {Join(curses)} at once: Neow offers exactly one " +
                    "curse-branch relic per player, so only one of those can be in an offer.");

            var positives = relics.Where(r => r.Pool != NeowPool.Curse).ToList();
            if (positives.Count > 2)
                throw new ArgumentException(
                    $"{who} cannot be offered {Join(positives)} at once: Neow offers exactly two " +
                    "positive options per player.");

            // Exactly one of each coin-flip pair is ever added to the pool.
            foreach (var relic in positives)
                if (NeowRelics.CoinFlipPartner(relic) is { } partner && positives.Contains(partner))
                    throw new ArgumentException(
                        $"{who} cannot be offered both {relic.Name} and {partner.Name}: Neow flips " +
                        "a coin between those two, so exactly one of them enters the pool.");

            // Taking a curse removes its counterpart from the positive pool outright.
            foreach (var curse in curses)
            {
                if (!NeowRelics.Counterparts.TryGetValue(curse.Slug, out var counterparts)) continue;
                foreach (var blocked in positives)
                    if (counterparts.Contains(blocked.Slug, StringComparer.Ordinal))
                        throw new ArgumentException(
                            $"{who} cannot be offered both {curse.Name} and {blocked.Name}: " +
                            $"{curse.Name} removes {blocked.Name} from the positive pool.");
            }
        }

        static string Join(IEnumerable<NeowRelic> relics) =>
            string.Join(" and ", relics.Select(r => r.Name));
    }

    /// <summary>
    /// Whether one chest can satisfy every relic asked of it, each from a DIFFERENT slot — the
    /// chest holds one relic per player, so "Vajra and War Paint in Act 2" needs both to be there
    /// at once rather than either one twice.
    ///
    /// The tolerance is a claim about the RUN, not about a relic: it says how far the shared bag
    /// had already been drained when the party reached this chest. So it is chosen ONCE here and
    /// every want is tested against that one choice, rather than each want picking the drain
    /// count that happens to suit it. Testing them separately accepts contradictions — two relics
    /// that are each reachable, at drain counts that cannot both be true of the same run, and so
    /// can never share a chest.
    ///
    /// One count per RARITY rather than one for the whole chest, because the deques drain
    /// independently: a run that lost two Commons to elite rewards need not have lost a single
    /// Rare. Slots sharing a rarity DO shift together, and each slot's own candidate list already
    /// has the earlier slots' pulls removed, so the same index reads correctly across all of them.
    ///
    /// Cost is (max tolerance + 1) ^ (distinct rarities in the chest), which is 1 for the default
    /// tolerance of 0 and at most a few dozen otherwise, against at most four slots.
    /// </summary>
    private static bool ChestSatisfies(IReadOnlyList<ChestSlot> slots, IReadOnlyList<ChestRelicCriterion> wants)
    {
        if (wants.Count > slots.Count) return false;

        // Folded to one number for the chest. A per-relic tolerance is not expressible against a
        // single drain count, and the highest is the permissive reading of a mixed request.
        int max = 0;
        foreach (var w in wants) max = Math.Max(max, w.Tolerance);

        var rarities = new List<string>(3);
        foreach (var s in slots)
            if (!rarities.Contains(s.Rarity)) rarities.Add(s.Rarity);

        // Odometer over one drain count per rarity, every combination up to `max`.
        var drained = new int[rarities.Count];
        while (true)
        {
            if (SatisfiedAt(slots, wants, rarities, drained)) return true;

            int k = 0;
            while (k < drained.Length && ++drained[k] > max) drained[k++] = 0;
            if (k == drained.Length) return false;
        }
    }

    /// <summary>
    /// Whether the wants are all met with the drain counts fixed. Each slot now holds exactly ONE
    /// relic, so this is multiset containment and a greedy walk is sufficient: slots holding the
    /// same relic are interchangeable, and slots holding a different one can never help.
    /// </summary>
    private static bool SatisfiedAt(
        IReadOnlyList<ChestSlot> slots, IReadOnlyList<ChestRelicCriterion> wants,
        List<string> rarities, int[] drained)
    {
        Span<bool> used = stackalloc bool[slots.Count];

        foreach (var want in wants)
        {
            bool matched = false;
            for (int s = 0; s < slots.Count && !matched; s++)
            {
                if (used[s]) continue;
                if (slots[s].At(drained[rarities.IndexOf(slots[s].Rarity)])?.Slug != want.Relic) continue;
                used[s] = true;
                matched = true;
            }
            if (!matched) return false;
        }
        return true;
    }

    /// <summary>
    /// Rejects chest requests no seed can satisfy: a relic that a chest can never hold (Shop
    /// rarity, or a character's own relic — chests draw from the shared bag only), and asking a
    /// single chest for more relics than it has slots.
    /// </summary>
    private static void ValidateChestCriteria(SearchCriteria c)
    {
        foreach (var want in c.ChestRelicsWanted)
        {
            if (ChestRelics.Find(want.Relic) is null)
                throw new ArgumentException(
                    $"'{want.Relic}' is not a relic a treasure chest can offer. Chests roll "
                    + "Common, Uncommon or Rare from the SHARED pool, so Shop relics and each "
                    + "character's own relics can never appear in one.");

            if (want.Act < 1 || want.Act > 3)
                throw new ArgumentException($"act must be 1, 2 or 3; got {want.Act}.");

            if (want.Tolerance < 0)
                throw new ArgumentException("chest tolerance cannot be negative.");
        }

        foreach (var byAct in c.ChestRelicsWanted.GroupBy(w => w.Act))
        {
            if (byAct.Count() > c.PlayerCount)
                throw new ArgumentException(
                    $"Act {byAct.Key}'s chest holds one relic per player, so a {c.PlayerCount}-player "
                    + $"lobby cannot have {byAct.Count()} named relics in it.");

            var dupes = byAct.GroupBy(w => w.Relic).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0)
                throw new ArgumentException(
                    $"Act {byAct.Key}'s chest cannot hold two of {ChestRelics.Display(dupes[0])}: "
                    + "each relic is pulled out of the shared bag for good.");
        }
    }

    /// <summary>
    /// Rejects shop requests no seed can satisfy. Two ways that happens, and both would
    /// otherwise scan forever: a relic that is not Shop rarity at all (it will never reach that
    /// slot, whatever else is true of the seed), and a character's own shop relic asked of a
    /// player who is not that character.
    /// </summary>
    private static void ValidateShopCriteria(SearchCriteria c)
    {
        foreach (var want in c.ShopRelicsWanted)
        {
            var relic = ShopRelics.Find(want.Relic);
            if (relic is null)
                throw new ArgumentException(
                    $"'{want.Relic}' is not a shop relic. Only RelicRarity.Shop relics reach the "
                    + "third slot; the other two slots roll a rarity that depends on run state and "
                    + "cannot be searched. Choose one of: "
                    + string.Join(", ", ShopRelics.All.Select(r => r.Slug)));

            if (want.Visit < 0)
                throw new ArgumentException("shop visit must be 1 or higher.");

            if (want.Slot >= c.PlayerCount)
                throw new ArgumentException(
                    $"P{want.Slot + 1} is not in a {c.PlayerCount}-player lobby.");

            // A character's own shop relic is only in that character's bag.
            var owner = ShopRelics.OwnerOf(want.Relic);
            if (owner is null) continue;

            if (want.Slot >= 0)
            {
                if (want.Slot < c.Characters.Count && c.Characters[want.Slot] != owner)
                    throw new ArgumentException(
                        $"{ShopRelics.Display(want.Relic)} is {owner}'s own relic, so it can only "
                        + $"appear in {owner}'s shop. P{want.Slot + 1} is playing {c.Characters[want.Slot]}.");
            }
            else if (c.Characters.Count > 0 && !c.Characters.Contains(owner.Value))
            {
                throw new ArgumentException(
                    $"{ShopRelics.Display(want.Relic)} is {owner}'s own relic, and nobody in this "
                    + "party is playing them.");
            }
        }
    }

    /// <summary>
    /// Rejects boss and event requirements that no seed can satisfy, so an impossible search
    /// says so at once instead of scanning millions of seeds and reporting nothing found.
    ///
    /// Act 1 is where this earns its keep, because it is the only act with a choice of map and
    /// the two maps share neither their bosses nor most of their events. Asking for the
    /// Overgrowth's boss alongside an Underdocks event is a search with no answers, and the
    /// map filter would quietly reject every seed rather than say so.
    /// </summary>
    private static void ValidateActCriteria(SearchCriteria c)
    {
        foreach (var want in c.Bosses)
            if (!ActCatalog.Bosses(want.Act).Any(b => b.TypeName == want.Boss))
                throw new ArgumentException(
                    $"Act {want.Act} never ends with '{ActCatalog.Display(want.Boss)}'. It can be: " +
                    string.Join(", ", ActCatalog.Bosses(want.Act).Select(b => ActCatalog.Slug(b.TypeName))));

        foreach (var want in c.Events)
        {
            if (want.WithinFirst < 1)
                throw new ArgumentException(
                    $"an event has to be within at least the first 1 of the order, got {want.WithinFirst}.");

            if (!ActCatalog.EventNames(want.Act).Contains(want.Event))
                throw new ArgumentException(
                    $"Act {want.Act} never offers '{ActCatalog.Display(want.Event)}'. It can offer: " +
                    string.Join(", ", ActCatalog.EventNames(want.Act).Select(ActCatalog.Slug)));
        }

        // Each act's criteria have to fit on ONE of its maps — checking them one at a time
        // would pass a boss and an event that are individually reachable but never together.
        int lastAct = ActData.ByIndex.Length;
        foreach (var act in ActCatalog.ActNumbers)
        {
            var bosses = c.Bosses.Where(b => b.Act == act).ToList();
            var events = c.Events.Where(e => e.Act == act).ToList();
            if (bosses.Count == 0 && events.Count == 0) continue;

            // How many bosses this act will actually have: two on the final act at A10+.
            int slots = act == lastAct && c.Ascension >= AscensionLevels.DoubleBoss ? 2 : 1;
            var include = bosses.Where(b => !b.Exclude).Select(b => b.Boss).Distinct().ToList();
            var exclude = bosses.Where(b => b.Exclude).Select(b => b.Boss).ToHashSet();

            if (include.Intersect(exclude).FirstOrDefault() is { } both)
                throw new ArgumentException(
                    $"Act {act} cannot both have and not have {ActCatalog.Display(both)}.");

            if (include.Count > slots)
                throw new ArgumentException(slots == 1
                    ? $"Act {act} has one boss, so it cannot be all of " +
                      $"{string.Join(", ", include.Select(ActCatalog.Display))}. " +
                      "Double Boss (Ascension 10) gives the final act two."
                    : $"Act {act} has {slots} bosses, so it cannot be all {include.Count} of those.");

            // Maps this act could draw, once the Act 1 choice is honoured.
            var maps = ActData.ByIndex[act - 1]
                .Where(m => act != 1 || c.Act1 is null || m.Name.Equals(c.Act1, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Excluding more bosses than the act can spare leaves it nothing to draw. Worth
            // its own message: it is arithmetic about one act, not a map mismatch.
            if (exclude.Count > 0 && maps.All(m => m.Bosses.Count(x => !exclude.Contains(x.Name)) < slots))
                throw new ArgumentException(
                    $"Act {act} draws {slots} boss{(slots == 1 ? "" : "es")} from {maps[0].Bosses.Count()}, " +
                    $"so ruling out {string.Join(", ", exclude.Select(ActCatalog.Display))} leaves too few.");

            var fitting = maps
                .Where(m => include.All(b => m.Bosses.Any(x => x.Name == b)))
                .Where(m => m.Bosses.Count(x => !exclude.Contains(x.Name)) >= slots)
                .Where(m => events.All(e => m.Events.Contains(e.Event) || ActData.SharedEvents.Contains(e.Event)))
                .ToList();

            if (fitting.Count > 0) continue;

            var asked = include.Select(ActCatalog.Display)
                .Concat(exclude.Select(b => "not " + ActCatalog.Display(b)))
                .Concat(events.Select(e => ActCatalog.Display(e.Event)));
            var pinned = act == 1 && c.Act1 is not null ? maps[0].Name : null;
            throw new ArgumentException(
                $"no Act {act} map has all of {string.Join(", ", asked)}" +
                (pinned is null ? "" : $" on the {pinned}") + ", so no seed can match." +
                (act == 1 ? " Act 1's two maps share neither bosses nor most events." : ""));
        }
    }

    /// <summary>
    /// Rejects card requirements no seed can satisfy. Two shapes of impossible:
    /// a card the slot's character does not have, and more than three cards asked of one slot
    /// when a reward only ever offers three.
    /// </summary>
    private static void ValidateCardCriteria(SearchCriteria c)
    {
        foreach (var want in c.Cards)
        {
            if (want.Slot >= c.PlayerCount)
                throw new ArgumentException(
                    $"there is no P{want.Slot + 1} in a {c.PlayerCount}-player lobby.");

            // "Any player" only needs SOMEONE who could be offered it.
            var slots = want.Slot < 0 ? Enumerable.Range(0, c.PlayerCount) : [want.Slot];
            if (slots.Any(s => CardCatalog.Offerable(c.Characters[s], c.UnlocksFor(s))
                    .Any(e => e.TypeName == want.Card)))
                continue;

            var who = want.Slot < 0
                ? "no character in the party"
                : $"the {c.Characters[want.Slot]}";
            throw new ArgumentException(
                $"{CardCatalog.Display(want.Card)} is not in {who}'s card pool, so no seed can offer it.");
        }

        // A rare is in the pool but out of reach on floor 1, so asking for one there would
        // otherwise scan the whole range and report nothing found, which reads as a bad seed
        // range. From fight 2 the pity offset has grown past zero and a rare is reachable, so
        // this only rejects the first fight.
        foreach (var want in c.Cards.Where(w => !CardRewardGenerator.CanOfferRare(w.Fight)))
        {
            var slots = want.Slot < 0 ? Enumerable.Range(0, c.PlayerCount) : [want.Slot];
            if (slots.Any(s => CardCatalog.FirstFightOfferable(c.Characters[s], c.UnlocksFor(s))
                    .Any(e => e.TypeName == want.Card)))
                continue;

            throw new ArgumentException(
                $"{CardCatalog.Display(want.Card)} is Rare, and the first fight of a run can never " +
                "offer one: the rare odds carry a penalty that only wears off over later rewards. " +
                "Ask for it after fight 2 instead.");
        }

        foreach (var want in c.Cards)
            if (want.Fight is < 1 or > CardRewardGenerator.MaxPredictableFight)
                throw new ArgumentException(
                    $"fight must be between 1 and {CardRewardGenerator.MaxPredictableFight}; "
                    + $"got {want.Fight}.");

        // Any-order spends one fight per pick, so a slot cannot ask for more picks than there
        // are predictable fights. Without this the search would run to the end of the range and
        // report nothing found, which reads as a bad seed range rather than as an impossible ask.
        if (c.CardOrder == CardOrder.AnyPermutation)
        {
            foreach (var group in c.Cards.GroupBy(x => x.Slot))
            {
                if (group.Count() <= CardRewardGenerator.MaxPredictableFight) continue;
                throw new ArgumentException(
                    $"in any order, each card has to come from a different fight, and only "
                    + $"{CardRewardGenerator.MaxPredictableFight} fights are predictable. "
                    + $"{(group.Key < 0 ? "One player" : $"P{group.Key + 1}")} has "
                    + $"{group.Count()} cards selected, so no seed can offer them one apiece.");
            }
        }

        // Three cards per reward, so more than three named for one player AND one fight can
        // never all land. Grouped by fight as well as slot: the same player being offered three
        // cards in fight 1 and three more in fight 2 is perfectly satisfiable.
        foreach (var group in c.Cards.GroupBy(x => (x.Slot, x.Fight)).Where(g => g.Key.Slot >= 0))
        {
            var distinct = group.Select(x => x.Card).Distinct().Count();
            if (distinct > 3)
                throw new ArgumentException(
                    $"a card reward offers three cards, so P{group.Key.Slot + 1} cannot be offered all " +
                    $"{distinct} of those at once.");
        }
    }

    /// <summary>
    /// How far each player's Rewards stream has to be walked to answer these card criteria.
    ///
    /// Usually the deepest fight anyone named. Under <see cref="CardOrder.AnyPermutation"/> it is
    /// also at least the largest number of picks on ONE slot, because that mode lays a slot's k
    /// picks across the first k fights whatever fight the criteria themselves say.
    ///
    /// That second clause is not a refinement, it is the difference between working and silently
    /// finding nothing. Criteria do not have to carry a fight at all — the CLI defaults every
    /// `--card` to fight 1 — so three any-order picks would otherwise walk one fight, be asked to
    /// place three cards across three fights, and fail on every seed in the space with no error.
    ///
    /// Public, and used by the accelerator as well, for the same reason
    /// <see cref="ResolveRequiredSlots"/> is: the GPU stage walks the stream this far too, and a
    /// second copy of the rule that drifted shallow would make the pre-filter reject valid seeds.
    /// </summary>
    public static int DeepestFight(SearchCriteria criteria)
    {
        if (criteria.Cards.Count == 0) return 1;

        int deepest = criteria.Cards.Max(c => c.Fight);
        if (criteria.CardOrder != CardOrder.AnyPermutation) return deepest;

        int widest = criteria.Cards.GroupBy(c => c.Slot).Max(g => g.Count());
        return Math.Max(deepest, widest);
    }

    /// <summary>
    /// Which slots a Neow criterion has to hold for. Public because an accelerator has to
    /// build the same slot mask, and a second copy of this rule would be free to drift.
    /// </summary>
    public static IReadOnlyList<int> ResolveRequiredSlots(SearchCriteria criteria) => criteria.Requirement switch
    {
        SlotRequirement.All => Enumerable.Range(0, criteria.PlayerCount).ToArray(),
        SlotRequirement.Specific => criteria.RequiredSlots,
        _ => Array.Empty<int>(),
    };
}
