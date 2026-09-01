using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Core;

/// <summary>
/// A search's Neow requirements, prepared once so the scan does not redo per-seed setup, and
/// shared so there is only one copy of what "this seed satisfies Neow" means.
///
/// Two things here are about speed rather than meaning, and both matter more now that a search
/// can carry several relics at once:
///
/// CURSE FAST PATH, per criterion. The curse is Neow's FIRST draw, so a curse-branch relic is
/// settled by one <c>NextInt</c> against roughly twenty draws, three coin flips and a shuffle to
/// build the whole offer. The test keys on the relic's own pool rather than on what the caller
/// asked for: the curse and positive pools are disjoint, so for a curse relic "anywhere in the
/// offer" and "curse branch only" are the same question and both take the cheap answer.
///
/// ORDERING. Criteria are sorted so every fast one runs before any slow one. A search for a
/// curse relic plus a positive relic rejects most seeds on the curse alone, and paying for a
/// full offer first would throw that away.
///
/// The slow path caches each slot's offer for the seed being tested, because two positive-branch
/// criteria aimed at the same player would otherwise generate that player's offer twice.
///
/// PAYLOADS ride along here rather than living in a stage of their own, and the reason is the
/// "for any player" rule. "Somebody is offered Arcane Scroll" and "somebody's scroll would give
/// Corruption" are two questions a separate stage would answer about two different players, and
/// a seed satisfying them apart satisfies nobody at the table. Tested per slot, they are one
/// question about one player. See <see cref="NeowCardPayload"/>.
/// </summary>
public sealed class NeowPlan
{
    private readonly record struct Item(
        NeowRelic Relic, OfferSlot Where, int CurseIndex, bool AnySlot, IReadOnlyList<int> Slots,
        int[][]? WantedBySlot);

    private readonly NeowContext _context;
    private readonly int _playerCount;
    private readonly int _curseCandidateCount;
    private readonly Item[] _items;
    private readonly IReadOnlyList<Character> _characters;
    private readonly UnlockState? _unlocks;

    private NeowPlan(
        NeowContext context, int curseCandidateCount, Item[] items,
        IReadOnlyList<Character> characters, UnlockState? unlocks)
    {
        _context = context;
        _playerCount = context.PlayerCount;
        _curseCandidateCount = curseCandidateCount;
        _items = items;
        _characters = characters;
        _unlocks = unlocks;
    }

    /// <summary>True when the search has no Neow requirement, so every seed passes this stage.</summary>
    public bool IsEmpty => _items.Length == 0;

    public static NeowPlan Build(SearchCriteria criteria)
    {
        var context = criteria.Context;
        var curseCandidates = NeowGenerator.CurseCandidates(context);

        var items = criteria.NeowCriteria
            .Select(want =>
            {
                // PositiveOnly is excluded from the fast path for safety rather than because it
                // could be wrong: Validate already rejects pairing it with a curse relic.
                int curseIndex = IndexOf(curseCandidates, want.Relic);
                bool fast = curseIndex >= 0 && want.Where != OfferSlot.PositiveOnly;
                return new Item(
                    want.Relic,
                    want.Where,
                    fast ? curseIndex : -1,
                    want.Requirement == SlotRequirement.Any,
                    want.ResolveSlots(context.PlayerCount),
                    WantedTypeIds(want, criteria));
            })
            // Cheapest first, and a payload is a handful of draws on top of whatever settled the
            // relic, so it goes behind the bare form of the same branch.
            .OrderBy(i => (i.CurseIndex >= 0 ? 0 : 2) + (i.WantedBySlot is null ? 0 : 1))
            .ToArray();

        return new NeowPlan(
            context, curseCandidates.Count, items, criteria.Characters, criteria.Unlocks);
    }

    /// <summary>Does this run seed satisfy every Neow criterion?</summary>
    public bool Matches(ulong runSeed)
    {
        if (_items.Length == 0) return true;

        // Allocated only if a criterion actually needs a full offer, which the common
        // all-curse-relics search never does.
        NeowOffer?[]? offers = null;

        foreach (var item in _items)
        {
            bool ok;
            if (item.AnySlot)
            {
                ok = false;
                for (int slot = 0; slot < _playerCount && !ok; slot++)
                    ok = SlotMatches(runSeed, slot, item, ref offers);
            }
            else
            {
                ok = true;
                foreach (var slot in item.Slots)
                    if (!SlotMatches(runSeed, slot, item, ref offers)) { ok = false; break; }
            }
            if (!ok) return false;
        }
        return true;
    }

    private bool SlotMatches(ulong runSeed, int slot, in Item item, ref NeowOffer?[]? offers)
    {
        if (!OfferMatches(runSeed, slot, item, ref offers)) return false;
        if (item.WantedBySlot is null) return true;

        // The offer put the relic in front of this player; now ask what taking it would hand
        // them. One draw for an Arcane Scroll, three for a Hefty Tablet, off a stream nothing
        // else has touched yet.
        return NeowCardPayload.Offers(
            runSeed, slot, _characters[slot], item.Relic.Slug, _unlocks, item.WantedBySlot[slot]);
    }

    private bool OfferMatches(ulong runSeed, int slot, in Item item, ref NeowOffer?[]? offers)
    {
        if (item.CurseIndex >= 0)
        {
            var rng = new Rng(NeowGenerator.RngSeed(runSeed, slot));
            return rng.NextInt(0, _curseCandidateCount) == item.CurseIndex;
        }

        offers ??= new NeowOffer?[_playerCount];
        var offer = offers[slot] ??= NeowGenerator.PredictOffer(runSeed, slot, _context);
        return SeedSearcher.OfferSatisfies(offer, item.Relic, item.Where);
    }

    /// <summary>
    /// The wanted payload cards as pool type ids, one row per player slot, or null when this
    /// criterion asks about no cards.
    ///
    /// Resolved per slot because type ids are only comparable within one character's pool, and
    /// a lobby is normally several different pools. A slot whose character cannot hold the card
    /// gets an id of -1, which nothing draws, so an impossible ask fails that slot rather than
    /// matching it — <c>Validate</c> rejects the outright impossible cases before a scan starts.
    /// </summary>
    private static int[][]? WantedTypeIds(NeowCriterion want, SearchCriteria criteria)
    {
        if (want.Cards.Count == 0) return null;

        var rows = new int[criteria.PlayerCount][];
        for (int slot = 0; slot < rows.Length; slot++)
        {
            // Unreachable: NeedsCharacters covers payload criteria, so Validate has already
            // demanded a full party. An id of -1 is nonetheless the safe answer rather than an
            // empty row, which would read as "nothing wanted" and match everything.
            if (slot >= criteria.Characters.Count) { rows[slot] = [-1]; continue; }

            var character = criteria.Characters[slot];
            rows[slot] = want.Cards
                .Select(c => NeowCardPayload.TypeIdOf(character, criteria.Unlocks, c))
                .ToArray();
        }
        return rows;
    }

    private static int IndexOf(IReadOnlyList<NeowRelic> candidates, NeowRelic relic)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i] == relic) return i;
        return -1;
    }
}
