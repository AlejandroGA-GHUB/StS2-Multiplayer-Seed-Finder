using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// The host arrays behind <see cref="RunFilterView"/>, plus the scalars that go with them.
///
/// Built once per search. Everything here is a function of the criteria, the party and the
/// unlock state, none of which vary across the seed range, so the kernel is left with only what
/// actually depends on the seed.
/// </summary>
public sealed class RunFilterTables
{
    public required RunFilterParams Params { get; init; }

    public required int[] CandidateCount { get; init; }
    public required int[] CandidateOffset { get; init; }
    public required int[] Info { get; init; }
    public required ulong[] Conflict { get; init; }
    public required int[] BossId { get; init; }
    public required int[] AncientId { get; init; }
    public required int[] BossCriteria { get; init; }
    public required int[] EventCriteria { get; init; }
    public required int[] EventStartIndex { get; init; }
    public required int[] AncientCriteria { get; init; }
    public required int[] ShopProbes { get; init; }

    /// <summary>
    /// Flatten a search into run-stage tables, or return false when the stage does not apply.
    ///
    /// Declining is always safe and never wrong — the CPU chain decides every candidate anyway,
    /// so a search this cannot express simply runs as it did before. It declines when nothing
    /// asks for a boss, an event or an Ancient; when the party is unknown, since the relic bags
    /// that precede act generation are sized by it; and when any of the kernel's structural
    /// limits would be exceeded rather than silently truncating a pool or a criterion list.
    ///
    /// Shop and treasure-chest criteria do NOT enable this stage on their own. They come out of
    /// the same bags but need the deque CONTENTS rather than just the stream position, and they
    /// are not modelled here yet. A search that has them alongside a boss criterion is still
    /// accelerated on the boss, which is exactly what a narrowing pre-filter is allowed to do.
    /// </summary>
    public static bool TryBuild(SearchCriteria criteria, out RunFilterTables? tables)
    {
        tables = null;

        bool needsActs = criteria.Bosses.Count > 0 || criteria.Events.Count > 0 || criteria.Ancients.Count > 0;
        if (!needsActs && criteria.ShopRelicsWanted.Count == 0) return false;

        if (criteria.ShopRelicsWanted.Count > 30) return false;

        // The bag burn is sized by the party, so an unknown party means an unknown stream
        // position. Validate already requires this for run criteria; declining is the safe read.
        if (criteria.Characters.Count != criteria.PlayerCount) return false;

        var byIndex = ActData.ByIndex;
        int actCount = byIndex.Length;
        if (actCount > RunFilter.MaxPackedActs) return false;

        // Only the Ancient criteria use a bitmask, and they are a disjunction over acts, so they
        // are the only ones that cannot reject the moment they fail.
        if (criteria.Ancients.Count > 30) return false;

        var unlocks = criteria.Unlocks;

        // Act definitions, flattened in candidate order. The index into this list IS the value
        // the kernel computes from the act-selection roll, so nothing needs to translate it.
        var actDefs = new List<ActDefinition>();
        var candidateCount = new int[actCount];
        var candidateOffset = new int[actCount];
        for (int i = 0; i < actCount; i++)
        {
            candidateCount[i] = byIndex[i].Length;
            candidateOffset[i] = actDefs.Count;
            actDefs.AddRange(byIndex[i]);
        }

        var info = new int[actDefs.Count * RunFilter.Stride];
        var encounters = new List<Encounter>();
        var bossIds = new List<int>();
        var ancientIds = new List<int>();

        // Boss identity is an int because a kernel has no strings. Which int does not matter,
        // only that the criteria and the pools agree, so the first sighting fixes it.
        var bossIdOf = new Dictionary<string, int>(StringComparer.Ordinal);
        int NextBossId(string name)
        {
            if (!bossIdOf.TryGetValue(name, out int id))
            {
                id = bossIdOf.Count;
                bossIdOf[name] = id;
            }
            return id;
        }

        for (int a = 0; a < actDefs.Count; a++)
        {
            var act = actDefs[a];
            int at = a * RunFilter.Stride;

            info[at + RunFilter.FldEventCount] = act.EventsFor(unlocks).Length;

            if (!AppendPool(act.Weak, encounters, info, at, RunFilter.FldWeakStart)) return false;
            info[at + RunFilter.FldWeakDraws] = act.NumberOfWeakEncounters;

            if (!AppendPool(act.Regular, encounters, info, at, RunFilter.FldRegularStart)) return false;
            info[at + RunFilter.FldRegularDraws] =
                act.GetNumberOfRooms(isMultiplayer: true) - act.NumberOfWeakEncounters;

            if (!AppendPool(act.Elites, encounters, info, at, RunFilter.FldEliteStart)) return false;
            info[at + RunFilter.FldEliteDraws] = EliteEncountersPerAct;

            info[at + RunFilter.FldBossStart] = bossIds.Count;
            info[at + RunFilter.FldBossCount] = act.Bosses.Count;
            foreach (var boss in act.Bosses) bossIds.Add(NextBossId(boss.Name));

            var own = act.AncientsFor(unlocks);
            info[at + RunFilter.FldAncientStart] = ancientIds.Count;
            info[at + RunFilter.FldAncientCount] = own.Length;
            foreach (var name in own) ancientIds.Add(AncientIdOf(name));
        }

        // Boss criteria. A boss the criteria name but no map contains would be caught by
        // Validate long before this, so an unknown name here can only be a criterion that is
        // already impossible; giving it an id no pool holds keeps the kernel's answer right.
        var bossCriteria = new int[criteria.Bosses.Count * 3];
        for (int c = 0; c < criteria.Bosses.Count; c++)
        {
            var want = criteria.Bosses[c];
            bossCriteria[c * 3] = want.Act;
            bossCriteria[c * 3 + 1] = bossIdOf.TryGetValue(want.Boss, out int id) ? id : UnknownId;
            bossCriteria[c * 3 + 2] = want.Exclude ? 1 : 0;
        }

        // Event criteria, plus where each one's event starts in every map's filtered pool. The
        // map is not known until the seed is rolled, so all of them are resolved up front.
        var eventCriteria = new int[criteria.Events.Count * 2];
        var eventStart = new int[criteria.Events.Count * actDefs.Count];
        for (int c = 0; c < criteria.Events.Count; c++)
        {
            var want = criteria.Events[c];
            eventCriteria[c * 2] = want.Act;
            eventCriteria[c * 2 + 1] = want.WithinFirst;

            for (int a = 0; a < actDefs.Count; a++)
                eventStart[c * actDefs.Count + a] = Array.IndexOf(actDefs[a].EventsFor(unlocks), want.Event);
        }

        var ancientCriteria = new int[criteria.Ancients.Count];
        for (int c = 0; c < criteria.Ancients.Count; c++)
            ancientCriteria[c] = (int)criteria.Ancients[c].Ancient;

        int sharedCount = unlocks.IsEpochRevealed(ActData.SharedAncientEpoch) ? ActData.SharedAncients.Length : 0;
        if (sharedCount > RunFilter.MaxPackedTake) return false;

        var shopProbes = BuildShopProbes(criteria, unlocks);

        tables = new RunFilterTables
        {
            CandidateCount = candidateCount,
            CandidateOffset = candidateOffset,
            Info = info,
            Conflict = BuildConflicts(encounters, actDefs, info),
            BossId = bossIds.ToArray(),
            AncientId = ancientIds.ToArray(),
            BossCriteria = bossCriteria,
            EventCriteria = eventCriteria,
            EventStartIndex = eventStart,
            AncientCriteria = ancientCriteria,
            ShopProbes = shopProbes,
            Params = new RunFilterParams
            {
                UpFrontNameHash = GameHash.Deterministic(GameHash.SnakeCase("UpFront")),
                ActNameHash = GameHash.Deterministic("act_selection"),
                BagBurnDraws = RunGenerator.RelicBagDraws(criteria.Characters, unlocks),
                SharedAncientCount = sharedCount,
                SharedAncientId = sharedCount > 0 ? AncientIdOf(ActData.SharedAncients[0]) : UnknownId,

                // Which shared Ancient an act received is only knowable while there is at most
                // one of them to receive. Beyond that the shuffle would have to be materialised;
                // the draws are still burned correctly, so the rest of the stage stays valid,
                // and identity simply falls back to the CPU.
                SharedAncientKnown = sharedCount <= 1 ? 1 : 0,

                ActCount = actCount,
                ActDefCount = actDefs.Count,
                Ascension = criteria.Ascension,
                BossCriterionCount = criteria.Bosses.Count,
                EventCriterionCount = criteria.Events.Count,
                AncientCriterionCount = criteria.Ancients.Count,
                AncientAllMask = (1 << criteria.Ancients.Count) - 1,
                ShopProbeCount = shopProbes.Length / RunFilter.ShopStride,
                ShopAllMask = (1 << criteria.ShopRelicsWanted.Count) - 1,
                NeedsActs = needsActs ? 1 : 0,
                Active = 1,
            },
        };
        return true;
    }

    /// <summary>
    /// Elites drawn per act. Hardcoded in <c>ActModel.GenerateRooms</c> rather than read off the
    /// act, and mirrored here from <c>RunGenerator.GenerateAct</c>.
    /// </summary>
    private const int EliteEncountersPerAct = 15;

    /// <summary>
    /// An identity no pool ever holds, for names the tables do not know. Negative so it can
    /// never collide with a real boss id or an <see cref="Ancient"/> ordinal, and distinct from
    /// the -1 the kernel uses for "a shared Ancient we chose not to identify".
    /// </summary>
    private const int UnknownId = -2;

    /// <summary>
    /// One probe per (criterion, player whose deque could supply it).
    ///
    /// A criterion naming a slot produces one probe. "Any player" produces one per player who
    /// has that relic in their bag at all, and the kernel treats them as alternatives. A player
    /// who does not have it contributes nothing, which is the same answer the CPU reaches by
    /// looking at a deque that cannot hold it.
    ///
    /// The position asked for is where the SHUFFLE has to leave the relic, not where the shop
    /// shows it: the deque is reversed after shuffling because shops pull from the back, so the
    /// first shop a player enters takes what the shuffle settled LAST — which, this being a
    /// descending Fisher-Yates, is what it settled first.
    /// </summary>
    private static int[] BuildShopProbes(SearchCriteria criteria, UnlockState unlocks)
    {
        if (criteria.ShopRelicsWanted.Count == 0) return Array.Empty<int>();

        var deques = RunGenerator.ShopDeques(criteria.Characters, unlocks);
        var probes = new List<int>();

        for (int c = 0; c < criteria.ShopRelicsWanted.Count; c++)
        {
            var want = criteria.ShopRelicsWanted[c];

            for (int slot = 0; slot < deques.Length; slot++)
            {
                if (want.Slot >= 0 && want.Slot != slot) continue;
                if (deques[slot] is not { } deque) continue;

                int from = -1;
                for (int i = 0; i < deque.Relics.Count; i++)
                    if (string.Equals(deque.Relics[i].Slug, want.Relic, StringComparison.OrdinalIgnoreCase))
                    {
                        from = i;
                        break;
                    }
                if (from < 0) continue;

                int position = deque.Relics.Count - 1 - want.Visit;
                if (position < 0) continue;

                probes.Add(c);
                probes.Add(deque.DrawsBefore);
                probes.Add(deque.Relics.Count);
                probes.Add(from);
                probes.Add(position);
            }
        }
        return probes.ToArray();
    }

    private static int AncientIdOf(string name) =>
        AncientOffers.TryParse(name, out var ancient) ? (int)ancient : UnknownId;

    /// <summary>
    /// Append one encounter pool and record where it landed. Returns false if the pool is larger
    /// than a bag mask can hold, which no act is close to but which would silently drop entries
    /// if it ever happened.
    /// </summary>
    private static bool AppendPool(
        IReadOnlyList<Encounter> pool, List<Encounter> flattened, int[] info, int at, int startField)
    {
        if (pool.Count > RunFilter.MaxPoolSize) return false;

        info[at + startField] = flattened.Count;
        info[at + startField + 1] = pool.Count;
        flattened.AddRange(pool);
        return true;
    }

    /// <summary>
    /// The repeat-avoidance predicate, evaluated ahead of time for every encounter against every
    /// pool of its own act.
    ///
    /// One bit per pool entry: set when drawing that entry after this encounter would be
    /// rejected, which is <c>SharesTagsWith(last) || e == last</c>. The identity half only ever
    /// fires within a pool, but including it unconditionally costs nothing and means the kernel
    /// needs no second test.
    ///
    /// A pool a given encounter cannot precede simply gets a zero mask, which is the right answer
    /// anyway: an act's draws never see another act's encounters.
    /// </summary>
    private static ulong[] BuildConflicts(
        IReadOnlyList<Encounter> flattened, IReadOnlyList<ActDefinition> actDefs, int[] info)
    {
        var tags = new ulong[flattened.Count];
        for (int i = 0; i < flattened.Count; i++)
            foreach (var tag in flattened[i].Tags)
                tags[i] |= 1UL << (int)tag;

        var conflict = new ulong[flattened.Count * RunFilter.PoolKinds];
        var startFields = new[]
        {
            RunFilter.FldWeakStart, RunFilter.FldRegularStart, RunFilter.FldEliteStart,
        };

        for (int a = 0; a < actDefs.Count; a++)
        {
            int at = a * RunFilter.Stride;

            // Every encounter of this act, against every pool of this act.
            for (int source = 0; source < RunFilter.PoolKinds; source++)
            {
                int sourceStart = info[at + startFields[source]];
                int sourceCount = info[at + startFields[source] + 1];

                for (int e = sourceStart; e < sourceStart + sourceCount; e++)
                {
                    for (int target = 0; target < RunFilter.PoolKinds; target++)
                    {
                        int targetStart = info[at + startFields[target]];
                        int targetCount = info[at + startFields[target] + 1];

                        ulong mask = 0;
                        for (int j = 0; j < targetCount; j++)
                            if ((tags[targetStart + j] & tags[e]) != 0 || targetStart + j == e)
                                mask |= 1UL << j;

                        conflict[e * RunFilter.PoolKinds + target] = mask;
                    }
                }
            }
        }
        return conflict;
    }
}
