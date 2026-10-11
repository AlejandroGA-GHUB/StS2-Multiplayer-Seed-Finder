using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Modifiers;
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
    private readonly GpuSeedScan? _search;

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
        _search = engine is null ? null : new GpuSeedScan(engine);
    }

    /// <summary>Probe once. Never throws; a machine with no GPU gets an unavailable planner.</summary>
    public static GpuSearchPlanner Create()
    {
        var engine = GpuEngine.TryCreate(out var status);
        return new GpuSearchPlanner(engine, status);
    }

    /// <summary>Act acceptance table for one search.</summary>
    private sealed class ActResources : IDisposable
    {
        public required MemoryBuffer1D<int, Stride1D.Dense> CandidateCount { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> Offset { get; init; }
        public required MemoryBuffer1D<byte, Stride1D.Dense> Accept { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> Forced { get; init; }

        public ActFilterView View => new(CandidateCount.View, Offset.View, Accept.View, Forced.View);

        public void Dispose()
        {
            CandidateCount.Dispose();
            Offset.Dispose();
            Accept.Dispose();
            Forced.Dispose();
        }
    }

    /// <summary>The Neow stage's device buffers for one search.</summary>
    private sealed class NeowResources : IDisposable
    {
        public required MemoryBuffer1D<int, Stride1D.Dense> Criteria { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> RemovedMask { get; init; }

        public NeowPrefilterView View => new(Criteria.View, RemovedMask.View);

        public void Dispose()
        {
            Criteria.Dispose();
            RemovedMask.Dispose();
        }
    }

    /// <summary>Device buffers that live only as long as one search.</summary>
    /// <summary>
    /// The <c>Fight</c> values that mark a modifier payload rather than a fight. Must match the
    /// constants the kernel switches on; they live here too so the two sides are written down
    /// next to the code that sets and reads them.
    /// </summary>
    private const int SpecializedKind = -1;
    private const int AllStarKind = -2;
    private const int DraftKind = -3;

    private sealed class CardResources : IDisposable
    {
        public required GpuCardPools Pools { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> Slot { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> Fight { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> TypeId { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> PoolOfSlot { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> PriorDraws { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> PayloadPicks { get; init; }
        public required MemoryBuffer1D<int, Stride1D.Dense> ColorlessOfSlot { get; init; }

        public CardCriteriaView View => new(
            Slot.View, Fight.View, TypeId.View, PoolOfSlot.View, PriorDraws.View, PayloadPicks.View,
            ColorlessOfSlot.View);

        public void Dispose()
        {
            Slot.Dispose();
            Fight.Dispose();
            TypeId.Dispose();
            PoolOfSlot.Dispose();
            PriorDraws.Dispose();
            PayloadPicks.Dispose();
            ColorlessOfSlot.Dispose();
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
        out IEnumerable<ulong>? candidates,
        SearchProgress? progress = null)
    {
        candidates = null;
        if (_engine is null || _search is null) return false;

        // A device that failed once stays failed. The accelerator is built once per process
        // because compiling the kernel costs more than a search does, so after a driver reset
        // that handle is dead and every later launch would fail the same way. Declining here
        // turns "broken until you restart the app" into "searches are slower from now on".
        if (_search.DeviceLost) return false;

        // Seed lengths other than twelve would need a differently shaped hash, and the kernels
        // are specialised to twelve. Searches never use another length, but saying so beats
        // producing wrong candidates if one ever does.
        if (criteria.SeedLength != GpuSeedString.Length) return false;

        // Acts first, because it is the cheapest stage and the only one that thins the criteria
        // still needing full run generation on the CPU.
        var actParams = default(ActFilterParams);
        ActResources? actResources = null;
        if (ActFilterFactory.TryBuild(criteria, out var counts, out var offsets, out var accept, out var forced))
        {
            var acc0 = _engine.Accelerator;
            actResources = new ActResources
            {
                CandidateCount = acc0.Allocate1D(counts),
                Offset = acc0.Allocate1D(offsets),
                Accept = acc0.Allocate1D(accept),
                Forced = acc0.Allocate1D(forced),
            };
            actParams = new ActFilterParams
            {
                ActNameHash = GameHash.Deterministic("act_selection"),
                ActCount = counts.Length,
                Active = 1,
            };
        }

        var neow = default(NeowPrefilterParams);
        NeowResources? neowResources = null;
        if (NeowPrefilterFactory.TryBuild(criteria, out neow, out var neowCriteria, out var neowRemoved))
            neowResources = new NeowResources
            {
                Criteria = _engine.Accelerator.Allocate1D(neowCriteria),
                RemovedMask = _engine.Accelerator.Allocate1D(neowRemoved),
            };

        // The card stage now answers the modifier payloads too, all of which come off the same
        // per-player Rewards stream, so a search made only of those is still worth building it
        // for. Without this a Draft-only search declined the stage and fell back to the CPU.
        var cards = default(CardFilterParams);
        CardResources? cardResources = null;
        if (criteria.Cards.Count > 0 || criteria.NeedsNeowPayloads
            || criteria.Specialized.Count > 0 || criteria.AllStar.Count > 0 || criteria.Draft.Count > 0)
            cardResources = TryBuildCards(criteria, out cards);

        var runStage = GpuRunStage.TryCreate(_engine, criteria);
        var run = runStage?.RunParams ?? default;

        if (actParams.Active == 0 && neow.Active == 0 && cards.Active == 0 && run.Active == 0)
        {
            actResources?.Dispose();
            neowResources?.Dispose();
            cardResources?.Dispose();
            runStage?.Dispose();
            return false;
        }

        var p = new SeedFilterParams { Acts = actParams, Neow = neow, Cards = cards, Run = run };
        candidates = Stream(p, actResources, neowResources, cardResources, runStage,
            startIndex, count, cancellationToken, progress);
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

        // All Star is the one card criterion that needs no character, because the colourless
        // pool is the same for everyone. Every other kind indexes a character's pool, so
        // without the party there is nothing to build; with only All Star asked, the character
        // pools are uploaded against a placeholder and never read, since the walks that would
        // read them are switched off below.
        bool knowParty = criteria.Characters.Count == criteria.PlayerCount;
        if (!knowParty)
        {
            if (criteria.AllStar.Count == 0) return null;
            if (criteria.Cards.Count > 0 || criteria.NeedsNeowPayloads
                || criteria.Specialized.Count > 0 || criteria.Draft.Count > 0) return null;
        }

        var party = knowParty
            ? criteria.Characters
            : Enumerable.Repeat(Character.Ironclad, criteria.PlayerCount).ToArray();

        // Payload criteria join a relic to the cards it hands out. The kernel tests only the
        // card half — the Neow stage answers the relic half, and for "any player" the two are
        // not joined at all, so a seed can pass here that the CPU then rejects. That is the
        // direction a pre-filter is allowed to be wrong in.
        var payloads = criteria.NeowCriteria.Where(x => x.Cards.Count > 0).ToList();

        // The three modifier payloads join in here rather than getting a stage each: all three
        // are drawn at Neow off the same per-player Rewards stream this stage already walks,
        // and all three answer the same question about the same slot.
        //
        // Declined outright when a prior count is unknowable, which is Sealed Deck sitting
        // ahead of them. The CPU refuses those searches anyway, but a kernel that guessed zero
        // would read every one of them from the wrong point in the stream.
        bool wantsSpecialized = criteria.Specialized.Count > 0;
        bool wantsAllStar = criteria.AllStar.Count > 0;
        bool wantsDraft = criteria.Draft.Count > 0;

        if (wantsSpecialized && criteria.SpecializedPriorDraws() is null) return null;
        if (wantsAllStar && criteria.AllStarPriorDraws() is null) return null;
        if (wantsDraft && criteria.DraftPriorDraws() is null) return null;

        int n = criteria.Cards.Count + payloads.Sum(x => x.Cards.Count)
              + criteria.Specialized.Count + criteria.AllStar.Count + criteria.Draft.Count;
        if (n > 32) return null;

        // Each slot's own unlocks, as CardsMatch and NeowPlan read them, not the run's union.
        var pools = new GpuCardPools(
            _engine.Accelerator, party, criteria.UnlocksBySlot(), criteria.IsMultiplayer,
            criteria.CharacterCardPools);

        var slot = new int[n];
        var fight = new int[n];
        var typeId = new int[n];

        // Taken from Core rather than recomputed. Under any-order this is deeper than the
        // criteria's own fights, and a kernel that walked the shallower distance would reject
        // seeds the CPU would have accepted, which is the one failure a pre-filter must not have.
        // Zero when nothing asks about a fight, which is what a payload-only search looks like.
        int deepest = criteria.Cards.Count == 0 ? 0 : SeedSearcher.DeepestFight(criteria);

        for (int i = 0; i < criteria.Cards.Count; i++)
        {
            var want = criteria.Cards[i];
            typeId[i] = pools.TypeIdOf(want.Card);
            if (typeId[i] < 0) { pools.Dispose(); return null; }

            slot[i] = want.Slot;
            fight[i] = want.Fight;
        }

        // How many payload cards each slot has to have drawn for it. A criterion naming no slot
        // has to be looked for on every player, so it arms them all.
        var payloadPicks = new int[criteria.PlayerCount];
        int next = criteria.Cards.Count;

        foreach (var want in payloads)
        {
            int picks = NeowCardPayload.CardCount(want.Relic.Slug);
            var armed = want.Requirement == SlotRequirement.Any
                ? Enumerable.Range(0, criteria.PlayerCount)
                : want.ResolveSlots(criteria.PlayerCount);
            foreach (var s in armed)
                if (s >= 0 && s < payloadPicks.Length) payloadPicks[s] = Math.Max(payloadPicks[s], picks);

            // "Any player" stays -1 here, exactly as a card criterion does: the kernel then
            // accepts a payload match from whichever slot produced it.
            int one = want.Requirement == SlotRequirement.Specific && (want.RequiredSlots?.Count ?? 0) == 1
                ? want.RequiredSlots![0]
                : -1;

            foreach (var card in want.Cards)
            {
                typeId[next] = pools.TypeIdOf(card);
                if (typeId[next] < 0) { pools.Dispose(); return null; }

                slot[next] = one;
                fight[next] = 0;      // 0 marks a payload rather than a fight
                next++;
            }
        }

        // The modifier payloads, each on the same footing as a card criterion: a slot, a kind
        // and a type id. The kind is what sends the kernel to the right walk.
        foreach (var want in criteria.Specialized)
        {
            typeId[next] = pools.TypeIdOf(want.Card);
            if (typeId[next] < 0) { pools.Dispose(); return null; }
            slot[next] = want.Slot;
            fight[next] = SpecializedKind;
            next++;
        }

        foreach (var want in criteria.AllStar)
        {
            typeId[next] = pools.TypeIdOf(want.Card);
            if (typeId[next] < 0) { pools.Dispose(); return null; }
            slot[next] = want.Slot;
            fight[next] = AllStarKind;
            next++;
        }

        foreach (var want in criteria.Draft)
        {
            typeId[next] = pools.TypeIdOf(want.Card);
            if (typeId[next] < 0) { pools.Dispose(); return null; }
            slot[next] = want.Slot;
            fight[next] = DraftKind;
            next++;
        }

        p = new CardFilterParams
        {
            RewardsNameHash = GameHash.Deterministic(GameHash.SnakeCase("Rewards")),
            RareOdds = criteria.Ascension >= CardRewardGenerator.Scarcity ? 0.0149f : 0.03f,
            RarityGrowth = criteria.Ascension >= CardRewardGenerator.Scarcity ? 0.005f : 0.01f,
            PlayerCount = criteria.PlayerCount,
            CriterionCount = n,
            // A payload-only search still has work to do even with no fight to walk.

            DeepestFight = deepest,
            AllMask = n == 32 ? -1 : (1 << n) - 1,
            Active = 1,
            AnyOrder = criteria.CardOrder == CardOrder.AnyPermutation ? 1 : 0,

            // -1 switches a walk off. The counts come from Core so the kernel cannot drift from
            // the number of cards the game actually hands out.
            SpecializedPrior = wantsSpecialized ? criteria.SpecializedPriorDraws()!.Value : -1,
            AllStarPrior = wantsAllStar ? criteria.AllStarPriorDraws()!.Value : -1,
            AllStarCards = AllStarPayload.Cards,
            DraftPrior = wantsDraft ? criteria.DraftPriorDraws()!.Value : -1,
            DraftRewards = DraftPayload.Rewards,
            DraftOptions = DraftPayload.OptionsPerReward,
        };

        var acc = _engine.Accelerator;
        return new CardResources
        {
            Pools = pools,
            Slot = acc.Allocate1D(slot),
            Fight = acc.Allocate1D(fight),
            TypeId = acc.Allocate1D(typeId),
            PoolOfSlot = acc.Allocate1D(pools.PoolOfSlot),
            PriorDraws = acc.Allocate1D(criteria.RewardPriorDraws()),
            PayloadPicks = acc.Allocate1D(payloadPicks),
            ColorlessOfSlot = acc.Allocate1D(pools.ColorlessPoolOfSlot),
        };
    }

    /// <summary>
    /// One search at a time per planner: the tile buffers on the device are shared, so two
    /// concurrent scans would interleave into each other's results.
    /// </summary>
    private IEnumerable<ulong> Stream(
        SeedFilterParams p,
        ActResources? actResources,
        NeowResources? neowResources,
        CardResources? cardResources,
        GpuRunStage? runStage,
        ulong start,
        ulong count,
        CancellationToken cancellationToken,
        SearchProgress? progress)
    {
        // try/finally rather than a lock statement, both because `yield return` cannot appear
        // inside one and because the release has to survive a consumer that stops early:
        // abandoning the enumeration disposes the iterator, which runs the finally. The card
        // buffers are freed there too, which is why they are per search and not per planner.
        _gate.Wait(cancellationToken);
        try
        {
            var views = new SeedFilterViews(
                actResources?.View ?? default,
                neowResources?.View ?? default,
                cardResources?.Pools.View ?? default,
                cardResources?.View ?? default,
                runStage?.View ?? default);

            foreach (var index in _search!.Scan(
                         p, views, start, (long)count, cancellationToken,
                         GpuSeedScan.DefaultTileSize, progress))
                yield return index;
        }
        finally
        {
            actResources?.Dispose();
            neowResources?.Dispose();
            cardResources?.Dispose();
            runStage?.Dispose();
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
