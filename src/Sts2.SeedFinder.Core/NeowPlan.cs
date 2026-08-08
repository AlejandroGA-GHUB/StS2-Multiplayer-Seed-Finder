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
/// </summary>
public sealed class NeowPlan
{
    private readonly record struct Item(
        NeowRelic Relic, OfferSlot Where, int CurseIndex, bool AnySlot, IReadOnlyList<int> Slots);

    private readonly NeowContext _context;
    private readonly int _playerCount;
    private readonly int _curseCandidateCount;
    private readonly Item[] _items;

    private NeowPlan(NeowContext context, int curseCandidateCount, Item[] items)
    {
        _context = context;
        _playerCount = context.PlayerCount;
        _curseCandidateCount = curseCandidateCount;
        _items = items;
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
                    want.ResolveSlots(context.PlayerCount));
            })
            .OrderByDescending(i => i.CurseIndex >= 0)
            .ToArray();

        return new NeowPlan(context, curseCandidates.Count, items);
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
        if (item.CurseIndex >= 0)
        {
            var rng = new Rng(NeowGenerator.RngSeed(runSeed, slot));
            return rng.NextInt(0, _curseCandidateCount) == item.CurseIndex;
        }

        offers ??= new NeowOffer?[_playerCount];
        var offer = offers[slot] ??= NeowGenerator.PredictOffer(runSeed, slot, _context);
        return SeedSearcher.OfferSatisfies(offer, item.Relic, item.Where);
    }

    private static int IndexOf(IReadOnlyList<NeowRelic> candidates, NeowRelic relic)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i] == relic) return i;
        return -1;
    }
}
