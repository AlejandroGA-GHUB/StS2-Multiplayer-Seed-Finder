using System.Runtime.InteropServices;
using ILGPU;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Everything the run stage reads, flattened. One array per kind rather than one per field:
/// the per-act scalars all live in <see cref="Info"/> at a fixed stride, because a kernel
/// signature has a size limit and thirteen separate views of three integers each would spend it
/// on nothing.
/// </summary>
public readonly struct RunFilterView
{
    /// <summary>How many candidates each act index has. Act 1 has two, acts 2 and 3 have one.</summary>
    public readonly ArrayView<int> CandidateCount;

    /// <summary>Where each act's candidates start, which is also the act definition's own index.</summary>
    public readonly ArrayView<int> CandidateOffset;

    /// <summary>Per act definition, <see cref="RunFilter.Stride"/> integers. See the Fld constants.</summary>
    public readonly ArrayView<int> Info;

    /// <summary>
    /// Which entries of a pool the repeat-avoidance predicate rules out, given the encounter
    /// just drawn. Indexed <c>lastEncounter * 3 + poolKind</c>, one bit per entry of that pool.
    ///
    /// This is the tag comparison, precomputed. It is a property of the act's static encounter
    /// data, so evaluating it per draw meant re-reading a tag mask per bag entry per draw —
    /// several hundred loads a seed to re-derive an answer that never changes. As a mask it
    /// makes "can anything satisfy the predicate" a single AND, which is what the reference has
    /// to know before it decides whether to draw at all.
    /// </summary>
    public readonly ArrayView<ulong> Conflict;

    /// <summary>Boss identities, flattened. Indexed by the act definition's boss start.</summary>
    public readonly ArrayView<int> BossId;

    /// <summary>Each act definition's own Ancients after epoch gating, flattened.</summary>
    public readonly ArrayView<int> AncientId;

    /// <summary>Three integers per boss criterion: act (1-based), boss id, 1 when excluding.</summary>
    public readonly ArrayView<int> BossCriteria;

    /// <summary>Two integers per event criterion: act (1-based), how far into the order counts.</summary>
    public readonly ArrayView<int> EventCriteria;

    /// <summary>
    /// Where each event criterion's event sits in each act definition's filtered pool, before
    /// the shuffle. Indexed <c>criterion * actDefCount + actDef</c>; -1 when that map has no
    /// such event, which the act pre-filter should already have rejected.
    /// </summary>
    public readonly ArrayView<int> EventStartIndex;

    /// <summary>One Ancient identity per criterion.</summary>
    public readonly ArrayView<int> AncientCriteria;

    /// <summary>
    /// Shop-relic probes, <see cref="RunFilter.ShopStride"/> integers each: which criterion this
    /// probe would satisfy, how far into the stream that player's Shop shuffle begins, how long
    /// the deque is, where the wanted relic starts in it, and the final position it has to reach.
    ///
    /// A probe rather than a criterion because "any player" means one criterion can be satisfied
    /// by any of several players' deques, and each of those is a different shuffle in a different
    /// part of the stream.
    /// </summary>
    public readonly ArrayView<int> ShopProbes;

    public RunFilterView(
        ArrayView<int> candidateCount, ArrayView<int> candidateOffset, ArrayView<int> info,
        ArrayView<ulong> conflict, ArrayView<int> bossId, ArrayView<int> ancientId,
        ArrayView<int> bossCriteria, ArrayView<int> eventCriteria,
        ArrayView<int> eventStartIndex, ArrayView<int> ancientCriteria,
        ArrayView<int> shopProbes)
    {
        ShopProbes = shopProbes;
        CandidateCount = candidateCount;
        CandidateOffset = candidateOffset;
        Info = info;
        Conflict = conflict;
        BossId = bossId;
        AncientId = ancientId;
        BossCriteria = bossCriteria;
        EventCriteria = eventCriteria;
        EventStartIndex = eventStartIndex;
        AncientCriteria = ancientCriteria;
    }
}

/// <summary>Scalars for the run stage.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RunFilterParams
{
    /// <summary>GameHash.Deterministic(SnakeCase("UpFront")) — the stream all of this rides on.</summary>
    public ulong UpFrontNameHash;

    /// <summary>GameHash.Deterministic("act_selection"), rolled alongside it.</summary>
    public ulong ActNameHash;

    /// <summary>
    /// <c>RunGenerator.RelicBagDraws</c>: the whole of the relic-bag shuffling as one number.
    /// The kernel only has to arrive at the right stream position, and a shuffle costs the same
    /// draws whether or not anyone reads the result.
    /// </summary>
    public int BagBurnDraws;

    /// <summary>Shared Ancients still revealed. Currently one (Darv), and zero if its epoch is not.</summary>
    public int SharedAncientCount;

    /// <summary>Identity of the shared Ancient. Only meaningful while there is at most one.</summary>
    public int SharedAncientId;

    /// <summary>1 when a shared Ancient handed to an act can still be identified. See the guard in the factory.</summary>
    public int SharedAncientKnown;

    public int ActCount;

    /// <summary>Act definitions in total, which is the stride of <see cref="RunFilterView.EventStartIndex"/>.</summary>
    public int ActDefCount;

    /// <summary>Only <c>AscensionLevels.DoubleBoss</c> matters, and only for the final act.</summary>
    public int Ascension;

    public int BossCriterionCount;
    public int EventCriterionCount;
    public int AncientCriterionCount;
    public int ShopProbeCount;

    /// <summary>All shop criterion bits set. Probes are a disjunction, so they settle as a mask.</summary>
    public int ShopAllMask;

    /// <summary>
    /// 1 when something downstream of the relic bags is being asked about.
    ///
    /// A shop-relic search is settled entirely within the bag shuffles, which are the FIRST thing
    /// the stream does. Generating three acts afterwards to answer nothing is most of the work,
    /// so the stage stops at the bags when nothing needs them.
    /// </summary>
    public int NeedsActs;

    /// <summary>All Ancient criterion bits set. They are a disjunction over acts, so they settle last.</summary>
    public int AncientAllMask;

    /// <summary>1 when this stage runs at all.</summary>
    public int Active;
}

/// <summary>
/// The GPU form of <c>RunGenerator.GenerateRun</c>, as far as the boss, event and Ancient
/// criteria need it.
///
/// The whole of upfront generation is ONE sequential stream, so there is no way to sample it:
/// the Act 3 boss is roughly four hundred draws in, and every one of those draws has to be taken
/// in the right order with the right bound. What this does not do is materialise anything. Three
/// observations make that possible, and between them they are why the run stage fits in registers:
///
/// <list type="number">
/// <item>A bounded draw costs one step of the stream whatever its bound, so a shuffle of n costs
/// n-1 steps whatever it produces. The relic bags — by far the largest thing generation touches —
/// therefore collapse to a single burn count computed once on the host.</item>
/// <item><c>UnstableShuffle</c> is a DESCENDING Fisher-Yates, so it is a sequence of position
/// swaps that never depends on the values being shuffled. Where one particular entry ends up can
/// be followed with a single integer, re-running the same draws per criterion, instead of
/// permuting an array nobody will read.</item>
/// <item>The encounter bag draws with uniform weights, so its running-weight scan is exactly a
/// truncation, and the bag itself is a set of pool indices. A 64-bit mask holds any act's pool
/// with room to spare, and removal is one AND.</item>
/// </list>
///
/// Everything else is the draw order, which is the part that has to be exactly right and is
/// therefore held against <c>Core</c> by <see cref="GpuVerifyRun"/> on every seed it tests.
/// </summary>
public static class RunFilter
{
    /// <summary>Integers per act definition in <see cref="RunFilterView.Info"/>.</summary>
    public const int Stride = 14;

    public const int FldEventCount = 0;
    public const int FldWeakStart = 1, FldWeakCount = 2, FldWeakDraws = 3;
    public const int FldRegularStart = 4, FldRegularCount = 5, FldRegularDraws = 6;
    public const int FldEliteStart = 7, FldEliteCount = 8, FldEliteDraws = 9;
    public const int FldBossStart = 10, FldBossCount = 11;
    public const int FldAncientStart = 12, FldAncientCount = 13;

    /// <summary>
    /// The largest encounter pool a bag mask can hold. Every act is under twenty, and the
    /// factory refuses to build a stage that would exceed this rather than truncating one.
    /// </summary>
    public const int MaxPoolSize = 64;

    /// <summary>
    /// Acts that can receive a share of the shared Ancients, given how many bits each act's
    /// count is packed into. Three acts against eight slots, so this is headroom, not a limit
    /// anyone is near.
    /// </summary>
    public const int MaxPackedActs = 8;

    /// <summary>Largest share of shared Ancients one act can take, being four bits' worth.</summary>
    public const int MaxPackedTake = 15;

    /// <summary>
    /// Pool kinds, which are the second index of <see cref="RunFilterView.Conflict"/>. An act
    /// draws from exactly these three, and a pick from one can rule out entries in another —
    /// weak and regular encounters share a list, so the first regular draw sees the last weak.
    /// </summary>
    public const int PoolWeak = 0, PoolRegular = 1, PoolElite = 2;

    /// <summary>How many pool kinds each encounter carries a conflict mask for.</summary>
    public const int PoolKinds = 3;

    /// <summary>Integers per shop probe in <see cref="RunFilterView.ShopProbes"/>.</summary>
    public const int ShopStride = 5;

    public const int ShopCriterion = 0, ShopDrawsBefore = 1, ShopDequeSize = 2,
                     ShopFromIndex = 3, ShopWantPosition = 4;

    /// <summary>
    /// The index of the <paramref name="nth"/> entry still in the bag, counting from 0.
    /// Bounded by the pool rather than the mask width, since the difference is real work: this
    /// runs once per draw and once more per retry.
    /// </summary>
    private static int SelectNth(ulong bag, int nth, int count)
    {
        for (int b = 0; b < count; b++)
        {
            if ((bag & (1UL << b)) == 0) continue;
            if (nth-- == 0) return b;
        }
        return -1;
    }

    /// <summary>
    /// One draw from a uniform-weight <c>GrabBag</c>.
    ///
    /// The reference walks its entries accumulating weights until the roll is passed. With every
    /// weight 1.0 that running total is exactly the integers, and the roll is in [0, count), so
    /// the entry it lands on is the truncation. Same answer, same single draw, no walk.
    /// </summary>
    private static int Grab(ref GpuRandom rng, ulong bag, int bagCount, int count) =>
        SelectNth(bag, (int)(rng.NextDouble() * bagCount), count);

    /// <summary>
    /// <c>RunGenerator.DrawEncounters</c> — refill when empty, prefer an entry sharing no tag
    /// with the previous pick, fall back to any entry.
    ///
    /// <paramref name="lastId"/> carries across calls because the reference accumulates weak and
    /// regular encounters into the SAME list, so the first regular draw sees the last weak one.
    /// Elites start a new list, and the caller resets it for them.
    /// </summary>
    private static void DrawEncounters(
        ref GpuRandom rng, RunFilterView v, int poolKind, int start, int count, int draws,
        ref int lastId)
    {
        if (count <= 0) return;

        ulong full = count >= MaxPoolSize ? ulong.MaxValue : (1UL << count) - 1;
        ulong bag = 0;
        int bagCount = 0;

        for (int i = 0; i < draws; i++)
        {
            if (bagCount == 0)
            {
                bag = full;
                bagCount = count;
            }

            // Entries the previous pick does not rule out. A null `last` rules out nothing,
            // which is what the reference's SharesTagsWith(null) amounts to.
            ulong allowed = lastId < 0 ? bag : bag & ~v.Conflict[lastId * PoolKinds + poolKind];

            // The reference bails out before drawing at all when nothing can satisfy the
            // predicate. That early return is load-bearing: without it the fallback draw would
            // be a second draw rather than the only one, and every act after this would shift.
            int idx = -1;
            if (allowed != 0)
            {
                // Retry by drawing again, exactly as the reference does. The number of draws
                // consumed here is variable and depends on the bag, which is the whole reason
                // the stream cannot be skipped forward.
                do
                {
                    idx = Grab(ref rng, bag, bagCount, count);
                }
                while (idx >= 0 && (allowed & (1UL << idx)) == 0);
            }

            if (idx < 0) idx = Grab(ref rng, bag, bagCount, count);
            if (idx < 0) continue;

            bag &= ~(1UL << idx);
            bagCount--;
            lastId = start + idx;
        }
    }

    /// <summary>
    /// Where one entry of a list ends up after <c>UnstableShuffle</c>, without shuffling it.
    ///
    /// The shuffle swaps positions, never consulting the values, so following a single index
    /// through the same swaps gives its final position exactly. The rng is taken by value: each
    /// criterion re-runs the same draws from the same state, and the caller advances the real
    /// stream once afterwards.
    /// </summary>
    private static int FinalPosition(GpuRandom rng, int length, int startIndex)
    {
        int pos = startIndex;
        for (int n = length - 1; n >= 1; n--)
        {
            int k = rng.NextInt(n + 1);
            if (pos == k) pos = n;
            else if (pos == n) pos = k;
        }
        return pos;
    }

    /// <summary>Does this seed satisfy every boss, event and Ancient-identity criterion?</summary>
    public static bool Matches(ulong runSeed, RunFilterParams p, RunFilterView v)
    {
        var rng = GpuRandom.Named(runSeed, p.UpFrontNameHash);

        // Shop relics are decided by the bag shuffles, which are the first thing this stream
        // does. Each probe re-runs from here rather than being tracked in a single pass: the
        // deques it cares about are scattered through a dozen shuffles, and following several at
        // once would need an array where a kernel wants registers. Re-running costs a few hundred
        // burned draws per probe against the several hundred that act generation would cost, and
        // it settles the seed BEFORE any of that.
        if (p.ShopProbeCount > 0)
        {
            int satisfied = 0;
            for (int i = 0; i < p.ShopProbeCount; i++)
            {
                int at = i * ShopStride;
                int criterion = v.ShopProbes[at + ShopCriterion];
                if ((satisfied & (1 << criterion)) != 0) continue;

                var probe = rng;
                probe.Burn(v.ShopProbes[at + ShopDrawsBefore]);

                int landed = FinalPosition(
                    probe, v.ShopProbes[at + ShopDequeSize], v.ShopProbes[at + ShopFromIndex]);
                if (landed == v.ShopProbes[at + ShopWantPosition]) satisfied |= 1 << criterion;
            }
            if (satisfied != p.ShopAllMask) return false;
        }

        rng.Burn(p.BagBurnDraws);
        if (p.NeedsActs == 0) return true;

        // Act selection advances alongside the run stream: it decides which map act i uses and
        // is read at the top of that act's turn, so neither needs storing ahead of the other.
        var actRng = GpuRandom.Named(runSeed, p.ActNameHash);

        // Shared Ancients are shuffled, then acts 2+ take a prefix each. The shuffle is burned:
        // with one entry it costs nothing, and the factory turns identity off above one.
        if (p.SharedAncientCount > 1) rng.Burn(p.SharedAncientCount - 1);

        // Each act's share, four bits apiece. Packed rather than kept in an array because the
        // shares are drawn for every act before generation begins, and a kernel that allocates
        // is a kernel that spills.
        int packedTakes = 0;
        int handedOut = 0;
        for (int i = 1; i < p.ActCount; i++)
        {
            int take = rng.NextInt(p.SharedAncientCount - handedOut + 1);
            handedOut += take;
            packedTakes |= (take & 0xF) << (4 * i);
        }

        int ancientMask = 0;
        int lastActDef = -1, lastBoss = -1;

        for (int i = 0; i < p.ActCount; i++)
        {
            int actDef = v.CandidateOffset[i] + actRng.NextInt(0, v.CandidateCount[i]);
            int at = actDef * Stride;

            // 1. Events. Each criterion follows its own event through the same swaps from the
            //    same state, then the stream is advanced once for all of them.
            int events = v.Info[at + FldEventCount];
            for (int c = 0; c < p.EventCriterionCount; c++)
            {
                if (v.EventCriteria[c * 2] != i + 1) continue;

                int from = v.EventStartIndex[c * p.ActDefCount + actDef];
                if (from < 0) return false;
                if (FinalPosition(rng, events, from) >= v.EventCriteria[c * 2 + 1]) return false;
            }
            if (events > 1) rng.Burn(events - 1);

            // 2. Weak then regular encounters, sharing the tag-repeat state; elites separately.
            int lastId = -1;
            DrawEncounters(ref rng, v, PoolWeak, v.Info[at + FldWeakStart],
                           v.Info[at + FldWeakCount], v.Info[at + FldWeakDraws], ref lastId);
            DrawEncounters(ref rng, v, PoolRegular, v.Info[at + FldRegularStart],
                           v.Info[at + FldRegularCount], v.Info[at + FldRegularDraws], ref lastId);

            lastId = -1;
            DrawEncounters(ref rng, v, PoolElite, v.Info[at + FldEliteStart],
                           v.Info[at + FldEliteCount], v.Info[at + FldEliteDraws], ref lastId);

            // 3. Boss.
            int bossStart = v.Info[at + FldBossStart];
            int bossCount = v.Info[at + FldBossCount];
            int boss = bossCount > 0 ? v.BossId[bossStart + rng.NextInt(0, bossCount)] : -1;

            // The final act's bosses cannot be settled yet: Ascension 10 adds a second one, and
            // it is drawn after every act is generated. A criterion tests the act's whole SET.
            if (i < p.ActCount - 1)
            {
                for (int c = 0; c < p.BossCriterionCount; c++)
                {
                    if (v.BossCriteria[c * 3] != i + 1) continue;
                    bool present = v.BossCriteria[c * 3 + 1] == boss;
                    if (present == (v.BossCriteria[c * 3 + 2] != 0)) return false;
                }
            }

            // 4. Ancient, last in the act and drawn from the act's own plus its share of shared.
            int own = v.Info[at + FldAncientCount];
            int total = own + ((packedTakes >> (4 * i)) & 0xF);
            if (total > 0)
            {
                int pick = rng.NextInt(0, total);
                int ancient = pick < own
                    ? v.AncientId[v.Info[at + FldAncientStart] + pick]
                    : (p.SharedAncientKnown != 0 ? p.SharedAncientId : -1);

                for (int c = 0; c < p.AncientCriterionCount; c++)
                    if (v.AncientCriteria[c] == ancient) ancientMask |= 1 << c;
            }

            lastActDef = actDef;
            lastBoss = boss;
        }

        // Ascension 10's second boss: one draw over the final act's other bosses, after
        // everything else, so it shifts nothing before it.
        int secondBoss = -1;
        if (p.Ascension >= 10 && lastActDef >= 0)
        {
            int bossStart = v.Info[lastActDef * Stride + FldBossStart];
            int bossCount = v.Info[lastActDef * Stride + FldBossCount];

            int others = 0;
            for (int b = 0; b < bossCount; b++)
                if (v.BossId[bossStart + b] != lastBoss) others++;

            if (others > 0)
            {
                int nth = rng.NextInt(0, others);
                for (int b = 0; b < bossCount; b++)
                {
                    if (v.BossId[bossStart + b] == lastBoss) continue;
                    if (nth-- == 0) { secondBoss = v.BossId[bossStart + b]; break; }
                }
            }
        }

        for (int c = 0; c < p.BossCriterionCount; c++)
        {
            if (v.BossCriteria[c * 3] != p.ActCount) continue;
            int want = v.BossCriteria[c * 3 + 1];
            bool present = want == lastBoss || want == secondBoss;
            if (present == (v.BossCriteria[c * 3 + 2] != 0)) return false;
        }

        // Ancients are a disjunction over acts, so they can only be settled once every act has
        // been generated. Identity only: whether the Ancient goes on to OFFER a given relic runs
        // a fresh chain per player off variable-length branch sets, which stays on the CPU.
        return ancientMask == p.AncientAllMask;
    }

    /// <summary>
    /// One seed index per thread, answering only "did it match". Used by the verifier, so the
    /// comparison against <c>Core</c> is about generation alone with no tiling or atomics in it.
    /// </summary>
    public static void ProbeKernel(
        Index1D i,
        ArrayView<ulong> indices,
        RunFilterParams p,
        RunFilterView v,
        ArrayView<int> matched)
    {
        matched[i] = Matches(GpuSeedString.RunSeed(indices[i]), p, v) ? 1 : 0;
    }
}
