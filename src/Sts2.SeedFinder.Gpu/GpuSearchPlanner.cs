using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Decides whether a given search can be accelerated, and if so hands <c>SeedSearcher</c> a
/// stream of candidate indices instead of letting it walk the whole range.
///
/// Everything about this is optional and additive. When there is no device, or the criteria are
/// not ones the kernels model, <see cref="TryPlan"/> returns false and the caller searches
/// exactly as it did before. Nothing downstream needs to know which happened, because the
/// pre-filter only narrows the set of indices examined; the criteria chain that decides a match
/// is the same CPU code either way.
///
/// Owned for the lifetime of a process rather than per search: creating an ILGPU context and
/// JIT-compiling the kernel costs far more than a search does. The card pools are the exception
/// and are built per search, since they depend on the lobby's characters and unlock state.
/// </summary>
public sealed class GpuSearchPlanner : IDisposable
{
    private readonly GpuEngine? _engine;
    private readonly GpuNeowSearch? _search;

    /// <summary>
    /// A semaphore rather than a lock, because the consumer is <c>Parallel.ForEach</c> and it
    /// advances this enumerator from whichever worker thread is free. Monitor is thread-affine,
    /// so entering on one worker and leaving on another throws
    /// <c>SynchronizationLockException</c>; SemaphoreSlim has no thread affinity.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GpuStatus Status { get; }

    /// <summary>True when a device was found and the kernels are loaded.</summary>
    public bool Available => _engine is not null;

    private GpuSearchPlanner(GpuEngine? engine, GpuStatus status)
    {
        _engine = engine;
        Status = status;
        _search = engine is null ? null : new GpuNeowSearch(engine);
    }

    /// <summary>Probe once. Never throws; a machine with no GPU gets an unavailable planner.</summary>
    public static GpuSearchPlanner Create()
    {
        var engine = GpuEngine.TryCreate(out var status);
        return new GpuSearchPlanner(engine, status);
    }

    /// <summary>Device buffers that live only as long as one search.</summary>
    private sealed class CardResources : IDisposable
    {
        public required GpuCardPools Pools { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> Slot { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> Fight { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> TypeId { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> PoolOfSlot { get; init; }

        public CardCriteriaView View => new(Slot.View, Fight.View, TypeId.View, PoolOfSlot.View);

        public void Dispose()
        {
            Slot.Dispose();
            Fight.Dispose();
            TypeId.Dispose();
            PoolOfSlot.Dispose();
            Pools.Dispose();
        }
    }

    /// <summary>
    /// Try to build a candidate stream for these criteria.
    ///
    /// Two stages are modelled. The Neow stage covers a relic on the CURSE branch, mirroring
    /// <c>SeedSearcher</c>'s own fast path and for the same reason: the curse is Neow's first
    /// draw, so one draw settles it, and the curse and positive pools are disjoint so "anywhere
    /// in the offer" asks the same question as "curse branch". A positive relic is not
    /// accelerated. The card stage covers first- and second-fight card rewards.
    ///
    /// Either stage alone is enough to accelerate a search; when both apply they run in one pass.
    /// </summary>
    public bool TryPlan(
        SearchCriteria criteria,
        ulong startIndex,
        ulong count,
        CancellationToken cancellationToken,
        out IEnumerable<ulong>? candidates)
    {
        candidates = null;
        if (_engine is null || _search is null) return false;

        // Seed lengths other than twelve would need a differently shaped hash, and the kernels
        // are specialised to twelve. Searches never use another length, but saying so beats
        // producing wrong candidates if one ever does.
        if (criteria.SeedLength != GpuSeedString.Length) return false;

        var neow = default(NeowPrefilterParams);
        if (criteria.Relic is not null && criteria.Where != OfferSlot.PositiveOnly)
        {
            var required = SeedSearcher.ResolveRequiredSlots(criteria);
            bool anySlot = criteria.Requirement == SlotRequirement.Any;
            NeowPrefilterFactory.TryBuild(criteria.Context, criteria.Relic, anySlot, required, out neow);
        }

        var cards = default(CardFilterParams);
        CardResources? cardResources = null;
        if (criteria.Cards.Count > 0)
            cardResources = TryBuildCards(criteria, out cards);

        if (neow.Active == 0 && cards.Active == 0) return false;

        candidates = Stream(neow, cards, cardResources, startIndex, count, cancellationToken);
        return true;
    }

    /// <summary>
    /// Flatten the lobby's card pools and resolve each criterion to a global type id, or return
    /// null when the search cannot be expressed.
    ///
    /// It cannot be expressed when the characters are unknown (the pools ARE the character's),
    /// when a named card is in no pool this lobby has, or when there are more criteria than the
    /// kernel's satisfied-bitmask can hold. Declining is always safe; the CPU handles it.
    /// </summary>
    private CardResources? TryBuildCards(SearchCriteria criteria, out CardFilterParams p)
    {
        p = default;
        if (_engine is null) return null;
        if (criteria.Characters.Count != criteria.PlayerCount) return null;
        if (criteria.Cards.Count > 32) return null;

        var pools = new GpuCardPools(_engine.Accelerator, criteria.Characters, criteria.Unlocks);

        int n = criteria.Cards.Count;
        var slot = new int[n];
        var fight = new int[n];
        var typeId = new int[n];
        int deepest = 1;

        for (int i = 0; i < n; i++)
        {
            var want = criteria.Cards[i];
            typeId[i] = pools.TypeIdOf(want.Card);
            if (typeId[i] < 0) { pools.Dispose(); return null; }

            slot[i] = want.Slot;
            fight[i] = want.Fight;
            if (want.Fight > deepest) deepest = want.Fight;
        }

        p = new CardFilterParams
        {
            RewardsNameHash = GameHash.Deterministic(GameHash.SnakeCase("Rewards")),
            RareOdds = criteria.Ascension >= CardRewardGenerator.Scarcity ? 0.0149f : 0.03f,
            RarityGrowth = criteria.Ascension >= CardRewardGenerator.Scarcity ? 0.005f : 0.01f,
            PlayerCount = criteria.PlayerCount,
            CriterionCount = n,
            DeepestFight = deepest,
            AllMask = n == 32 ? -1 : (1 << n) - 1,
            Active = 1,
        };

        var acc = _engine.Accelerator;
        return new CardResources
        {
            Pools = pools,
            Slot = acc.Allocate1D(slot),
            Fight = acc.Allocate1D(fight),
            TypeId = acc.Allocate1D(typeId),
            PoolOfSlot = acc.Allocate1D(pools.PoolOfSlot),
        };
    }

    /// <summary>
    /// One search at a time per planner: the tile buffers on the device are shared, so two
    /// concurrent scans would interleave into each other's results.
    /// </summary>
    private IEnumerable<ulong> Stream(
        NeowPrefilterParams neow,
        CardFilterParams cards,
        CardResources? cardResources,
        ulong start,
        ulong count,
        CancellationToken cancellationToken)
    {
        // try/finally rather than a lock statement, both because `yield return` cannot appear
        // inside one and because the release has to survive a consumer that stops early:
        // abandoning the enumeration disposes the iterator, which runs the finally. The card
        // buffers are freed there too, which is why they are per search and not per planner.
        _gate.Wait(cancellationToken);
        try
        {
            var scan = cardResources is null
                ? _search!.Scan(neow, start, (long)count, cancellationToken)
                : _search!.Scan(neow, cards, cardResources.Pools.View, cardResources.View,
                    start, (long)count, cancellationToken);

            foreach (var index in scan) yield return index;
        }
        finally
        {
            cardResources?.Dispose();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _search?.Dispose();
        _engine?.Dispose();
        _gate.Dispose();
    }
}
