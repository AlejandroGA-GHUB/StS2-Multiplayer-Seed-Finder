using System.Runtime.InteropServices;
using ILGPU;

namespace Sts2.SeedFinder.Gpu;

/// <summary>Card criteria as three parallel arrays, which is all a kernel can hold.</summary>
public readonly struct CardCriteriaView
{
    /// <summary>Player slot the criterion names, or -1 for "any player in the lobby".</summary>
    public readonly ArrayView<int> Slot;

    /// <summary>
    /// What this criterion is about, and where in the stream to look.
    ///
    /// A positive number is a 1-based fight, matching <c>CardCriterion.Fight</c>. Zero and the
    /// negatives are the other things that come off the same per-player Rewards stream, each
    /// drawn before any fight:
    ///
    /// <list type="bullet">
    /// <item><b>0</b> — a Neow payload, the rares an Arcane Scroll or Hefty Tablet hands over.</item>
    /// <item><b>-1</b> — Specialized, the card a player starts five copies of.</item>
    /// <item><b>-2</b> — All Star, one of the five colourless cards.</item>
    /// <item><b>-3</b> — Draft, a card the player must be able to take from the ten rewards.</item>
    /// </list>
    ///
    /// One discriminator rather than one array per kind, because every kind answers the same
    /// question about the same stream for the same slot, and the satisfied-bitmask below does
    /// not care which produced a match.
    /// </summary>
    public readonly ArrayView<int> Fight;

    /// <summary>Global card type id, from <see cref="GpuCardPools.TypeIdOf"/>.</summary>
    public readonly ArrayView<int> TypeId;

    /// <summary>Which uploaded pool each player slot draws from.</summary>
    public readonly ArrayView<int> PoolOfSlot;

    /// <summary>
    /// Which uploaded pool holds the colourless cards for each slot. All Star reads this
    /// instead of the character pool, and it is per slot because the colourless pool carries
    /// epoch gates of its own.
    /// </summary>
    public readonly ArrayView<int> ColorlessOfSlot;

    /// <summary>
    /// Draws this slot's assumed Neow pick takes off the Rewards stream before fight 1, indexed
    /// by slot. Mirrors <c>SearchCriteria.RewardPriorDraws</c>, and getting it wrong here would
    /// reject seeds the CPU accepts.
    /// </summary>
    public readonly ArrayView<int> PriorDraws;

    /// <summary>
    /// How many payload cards to MODEL for this slot, indexed by slot: 1 for Arcane Scroll, 3
    /// for Hefty Tablet, 0 when nothing asks.
    ///
    /// Separate from <see cref="PriorDraws"/> on purpose. A criterion aimed at "any player"
    /// names no slot, so every slot's payload has to be looked at, while nobody's fight stream
    /// is known to have moved. The payload walk therefore runs on its own copy of the stream —
    /// which is exact, since a payload is drawn before anything else touches it.
    /// </summary>
    public readonly ArrayView<int> PayloadPicks;

    public CardCriteriaView(
        ArrayView<int> slot, ArrayView<int> fight, ArrayView<int> typeId, ArrayView<int> poolOfSlot,
        ArrayView<int> priorDraws, ArrayView<int> payloadPicks,
        ArrayView<int> colorlessOfSlot)
    {
        Slot = slot;
        Fight = fight;
        TypeId = typeId;
        PoolOfSlot = poolOfSlot;
        PriorDraws = priorDraws;
        PayloadPicks = payloadPicks;
        ColorlessOfSlot = colorlessOfSlot;
    }
}

/// <summary>Scalars for the card stage.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CardFilterParams
{
    /// <summary>GameHash.Deterministic(SnakeCase("Rewards")), hashed host-side.</summary>
    public ulong RewardsNameHash;

    /// <summary>CardRarityOdds' rare base, which Scarcity (A7+) halves. Resolved host-side.</summary>
    public float RareOdds;

    /// <summary>How much the rare pity offset grows per draw, also ascension-dependent.</summary>
    public float RarityGrowth;

    public int PlayerCount;
    public int CriterionCount;

    /// <summary>Deepest fight any criterion asks about, so the stream is walked exactly that far.</summary>
    public int DeepestFight;

    /// <summary>All criterion bits set; a seed matches when every one is satisfied.</summary>
    public int AllMask;

    /// <summary>1 when this stage runs at all. A relic-only search leaves it off.</summary>
    public int Active;

    /// <summary>
    /// 1 when the criteria are not pinned to a fight.
    ///
    /// The kernel deliberately tests something LOOSER than the real criterion here: it asks
    /// whether the card appears in any fight, not whether the picks can be laid one per fight.
    /// A pre-filter is only obliged never to reject a valid seed; passing a few extra is free,
    /// because SeedSearcher re-checks every candidate with the exact permutation test. Doing
    /// the matching here would cost a per-criterion fight mask in registers for no gain.
    /// </summary>
    public int AnyOrder;

    // ---- Custom run modifier payloads ------------------------------------------------------
    //
    // Each is drawn at Neow, before any fight, off the same per-player Rewards stream. They sit
    // on their own copy of that stream rather than sharing the fight walk's, because a
    // criterion aimed at "any player" names no slot: every slot's payload has to be looked at
    // while nobody's fight stream is known to have moved. That is exact, since a payload is
    // drawn before anything else touches the stream.
    //
    // The prior counts differ from PriorDraws and from each other, because the game walks the
    // forced options in list order: Draft is first, then Specialized, then All Star, so each
    // starts after everything earlier has spent its draws.

    /// <summary>Draws spent before Specialized's single pick, or -1 when it is not asked about.</summary>
    public int SpecializedPrior;

    /// <summary>Draws spent before All Star's five, or -1 when it is not asked about.</summary>
    public int AllStarPrior;

    /// <summary>How many cards All Star hands out. Five, mirrored from Core rather than hardcoded.</summary>
    public int AllStarCards;

    /// <summary>Draws spent before Draft's ten rewards, or -1 when it is not asked about.</summary>
    public int DraftPrior;

    /// <summary>How many rewards Draft offers.</summary>
    public int DraftRewards;

    /// <summary>How many cards each Draft reward offers, of which the player keeps one.</summary>
    public int DraftOptions;
}

/// <summary>
/// The GPU form of the card-reward half of <c>SeedSearcher.CardsMatch</c>.
///
/// Reproduces <c>CardRewardGenerator.Hallway</c> draw for draw. The draw ORDER is the whole
/// trick and is documented on that class: the potion odds roll happens while the reward list is
/// being built, before anything is populated, then gold, then the potion's own two draws if the
/// roll hit, then three cards of rarity / pick / upgrade.
///
/// Rather than build the rewards and search them afterwards, each card is tested against every
/// criterion as it is produced and the result accumulated into a bitmask. That avoids storing
/// rewards per thread, which on a GPU means spilling to global memory, and it lets a seed be
/// abandoned the moment it cannot satisfy everything.
/// </summary>
public static class CardFilter
{
    // CardRarity ordinals. The enum is None, Basic, Common, Uncommon, Rare, Ancient, ...
    private const int None = 0, Basic = 1, Common = 2, Uncommon = 3, Rare = 4;

    private const float RegularUncommonOdds = 0.37f;
    private const float BaseRarityOffset = -0.05f;
    private const float MaxRarityOffset = 0.4f;
    private const float BasePotionRewardOdds = 0.4f;

    /// <summary>CardRarityExtensions.GetNextHighestRarityWithWrapping — Rare wraps to Common.</summary>
    private static int NextHighest(int r) =>
        r == Basic ? Common
        : r == Common ? Uncommon
        : r == Uncommon ? Rare
        : r == Rare ? Common
        : None;

    /// <summary>
    /// Pool positions already drawn, which later draws must skip.
    ///
    /// Six fields rather than a loop over an array because a kernel has no heap: ILGPU will not
    /// take a `stackalloc` or a `new int[n]` here, so the set has to live in registers. Six is
    /// what the deepest user needs — Scroll Boxes shares one used-card set across two bundles of
    /// three — and a fight reward simply never fills more than two of them.
    /// </summary>
    private struct TakenSet
    {
        public int T0, T1, T2, T3, T4, T5;
        public int Count;

        public int At(int i)
        {
            if (i == 0) return T0;
            if (i == 1) return T1;
            if (i == 2) return T2;
            if (i == 3) return T3;
            if (i == 4) return T4;
            return T5;
        }

        public void Add(int at)
        {
            if (Count == 0) T0 = at;
            else if (Count == 1) T1 = at;
            else if (Count == 2) T2 = at;
            else if (Count == 3) T3 = at;
            else if (Count == 4) T4 = at;
            else T5 = at;
            Count++;
        }
    }

    /// <summary>Whether a candidate shares a card TYPE with one already offered this reward.</summary>
    private static bool Blacklisted(CardPoolView pools, int poolBase, int candidate, in TakenSet taken)
    {
        int id = pools.TypeId[poolBase + candidate];
        for (int i = 0; i < taken.Count; i++)
            if (pools.TypeId[poolBase + taken.At(i)] == id) return true;
        return false;
    }

    /// <summary>
    /// How many cards of a rarity remain. The group total is precomputed, so this only has to
    /// subtract the at most two entries already taken, exactly as the CPU path now does.
    /// </summary>
    private static int CountAvailable(
        CardPoolView pools, int pool, int poolBase, int rarity, in TakenSet taken)
    {
        if (rarity <= None || rarity >= GpuCardPools.RarityCount) return 0;
        int n = pools.GroupCount[pool * GpuCardPools.RarityCount + rarity];
        for (int i = 0; i < taken.Count; i++)
            if (pools.Rarity[poolBase + taken.At(i)] == rarity) n--;
        return n;
    }

    /// <summary>The nth still-available entry of a rarity, in pool order, or -1 if it ran out.</summary>
    private static int NthAvailable(
        CardPoolView pools, int pool, int poolBase, int rarity, int nth, in TakenSet taken)
    {
        int start = pools.GroupStart[pool * GpuCardPools.RarityCount + rarity];
        int count = pools.GroupCount[pool * GpuCardPools.RarityCount + rarity];
        for (int i = 0; i < count; i++)
        {
            int entry = pools.Group[start + i];
            if (Blacklisted(pools, poolBase, entry, in taken)) continue;
            if (nth-- == 0) return entry;
        }
        return -1;
    }

    /// <summary>CardFactory.GetNextAllowedRarity — climb the ladder until the pool can satisfy one.</summary>
    private static int NextAllowedRarity(
        CardPoolView pools, int pool, int poolBase, int rarity, in TakenSet taken)
    {
        int seen = 1 << rarity;
        while (rarity != None && CountAvailable(pools, pool, poolBase, rarity, in taken) == 0)
        {
            rarity = NextHighest(rarity);
            int bit = 1 << rarity;
            if ((seen & bit) != 0) return None;
            seen |= bit;
        }
        return rarity;
    }

    /// <summary>CardRarityOdds.Roll, including the pity offset it carries between draws.</summary>
    private static int RollRarity(
        ref GpuRandom rng, ref float offset, CardFilterParams p,
        CardPoolView pools, int pool, int poolBase, in TakenSet taken)
    {
        float roll = rng.NextFloat();
        float rareThreshold = p.RareOdds + offset;

        int rarity = roll < rareThreshold ? Rare
                   : roll < RegularUncommonOdds + rareThreshold ? Uncommon
                   : Common;

        // Written as a conditional rather than Math.Min so the kernel needs no math intrinsic
        // and no ILGPU.Algorithms dependency. Same result, including at the clamp.
        float grown = offset + p.RarityGrowth;
        offset = rarity == Rare ? BaseRarityOffset : (grown < MaxRarityOffset ? grown : MaxRarityOffset);

        return NextAllowedRarity(pools, pool, poolBase, rarity, in taken);
    }

    /// <summary>
    /// Does this seed satisfy every card criterion?
    ///
    /// Walks each player's Rewards stream once, as deep as the deepest fight asked about,
    /// because fight 2 is a continuation of fight 1's stream rather than a fresh one.
    /// </summary>
    public static bool Matches(
        ulong runSeed, CardFilterParams p, CardPoolView pools, CardCriteriaView criteria)
    {
        int satisfied = 0;

        for (int slot = 0; slot < p.PlayerCount; slot++)
        {
            int pool = criteria.PoolOfSlot[slot];
            int poolBase = pools.PoolBase[pool];

            // Player.cs seeds the whole PlayerRngSet with hash(seed) + slotIndex, then names
            // each generator within it.
            // The Neow payload, when anything asks about this slot. One NextItem per card off
            // the front of the stream: Uniform odds skip the rarity roll entirely and
            // NoUpgradeRoll removes the other, so a pick IS a draw. See Core's NeowCardPayload.
            int payloadPicks = criteria.PayloadPicks[slot];
            if (payloadPicks > 0)
            {
                var prng = GpuRandom.Named(unchecked(runSeed + (ulong)slot), p.RewardsNameHash);
                var taken = default(TakenSet);

                for (int i = 0; i < payloadPicks; i++)
                {
                    int avail = CountAvailable(pools, pool, poolBase, Rare, in taken);
                    if (avail <= 0) break;

                    int got = NthAvailable(pools, pool, poolBase, Rare, prng.NextInt(0, avail), in taken);
                    if (got < 0) break;

                    int gotType = pools.TypeId[poolBase + got];
                    for (int c = 0; c < p.CriterionCount; c++)
                    {
                        if (criteria.Fight[c] != 0) continue;
                        int want = criteria.Slot[c];
                        if (want >= 0 && want != slot) continue;
                        if (criteria.TypeId[c] == gotType) satisfied |= 1 << c;
                    }

                    taken.Add(got);
                }
            }

            // Specialized: one uniform pick, off this player's OWN pool.
            //
            // Satisfies EVERY matching criterion rather than the first unsatisfied one, unlike
            // the two below. There is a single card, so two criteria naming it are the same
            // question asked twice and the CPU answers yes to both; satisfying only one here
            // would reject a seed the CPU accepts, which is the one direction a pre-filter may
            // never be wrong in.
            if (p.SpecializedPrior >= 0 && Asks(slot, Specialized, p, criteria))
            {
                int n = pools.OfferableOwnCount[pool];
                if (n > 0)
                {
                    var prng = GpuRandom.Named(unchecked(runSeed + (ulong)slot), p.RewardsNameHash);
                    prng.Burn(p.SpecializedPrior);

                    int got = pools.Offerable[pools.OfferableStart[pool] + prng.NextInt(0, n)];
                    int gotType = pools.TypeId[poolBase + got];

                    for (int k = 0; k < p.CriterionCount; k++)
                    {
                        if (criteria.Fight[k] != Specialized) continue;
                        int want = criteria.Slot[k];
                        if (want >= 0 && want != slot) continue;
                        if (criteria.TypeId[k] == gotType) satisfied |= 1 << k;
                    }
                }
            }

            // All Star: five uniform picks off the COLOURLESS pool, each with a fresh blacklist,
            // so the five can repeat.
            //
            // Each draw satisfies at most ONE still-unsatisfied criterion, which is what makes
            // asking for the same card twice mean drawn twice. Criteria naming the same card
            // are interchangeable, so taking the first unsatisfied one can never strand a
            // later one: this is exact rather than merely permissive.
            if (p.AllStarPrior >= 0 && Asks(slot, AllStar, p, criteria))
            {
                int cpool = criteria.ColorlessOfSlot[slot];
                int cbase = pools.PoolBase[cpool];
                int n = pools.OfferableCount[cpool];
                if (n > 0)
                {
                    var prng = GpuRandom.Named(unchecked(runSeed + (ulong)slot), p.RewardsNameHash);
                    prng.Burn(p.AllStarPrior);

                    for (int i = 0; i < p.AllStarCards; i++)
                    {
                        int got = pools.Offerable[pools.OfferableStart[cpool] + prng.NextInt(0, n)];
                        satisfied = Claim(pools.TypeId[cbase + got], slot, AllStar, satisfied, p, criteria);
                    }
                }
            }

            // Draft: ten rewards of three, each card a base-odds rarity roll then a pick, with
            // a blacklist that resets per reward. Sixty draws, the most expensive thing in this
            // stage, which is why it runs last of the three.
            //
            // Deliberately LOOSER than the real criterion. The game takes one card from each
            // reward, so two wanted cards sharing a reward cannot both be had; this only asks
            // whether enough distinct rewards offer them. Per card that is exact, because a
            // reward blacklists by type and so offers a given card at most once. Across
            // different cards it over-accepts, which is free: SeedSearcher re-checks every
            // candidate with the real matching.
            if (p.DraftPrior >= 0 && Asks(slot, Draft, p, criteria))
            {
                var prng = GpuRandom.Named(unchecked(runSeed + (ulong)slot), p.RewardsNameHash);
                prng.Burn(p.DraftPrior);

                for (int r = 0; r < p.DraftRewards; r++)
                {
                    var taken = default(TakenSet);
                    for (int i = 0; i < p.DraftOptions; i++)
                    {
                        int rarity = RollBaseRarity(ref prng, p, pools, pool, poolBase, in taken);
                        int avail = CountAvailable(pools, pool, poolBase, rarity, in taken);
                        if (avail <= 0) break;

                        int got = NthAvailable(
                            pools, pool, poolBase, rarity, prng.NextInt(0, avail), in taken);
                        if (got < 0) break;

                        satisfied = Claim(pools.TypeId[poolBase + got], slot, Draft, satisfied, p, criteria);

                        taken.Add(got);
                    }
                }
            }

            // The fight walk is the whole cost of this stage, and it is only worth paying for a
            // player some fight criterion can be answered by: one pinned to them, or one asking
            // about anybody. A criterion aimed at P1 used to walk every player in the lobby.
            if (p.DeepestFight > 0 && WalksFights(slot, p, criteria)
                && !WalkFights(runSeed, slot, p, pools, criteria, pool, poolBase, ref satisfied))
                return false;

            // Everything pinned to this player is settled once their stream has been walked, so a
            // miss here is final and the players after them need not be walked at all.
            if (!PinnedSatisfied(slot, AnyFight, satisfied, p, criteria)) return false;
        }

        return satisfied == p.AllMask;
    }

    /// <summary>For <see cref="PinnedSatisfied"/>: every criterion on the slot, whatever its fight.</summary>
    private const int AnyFight = -99;

    /// <summary>
    /// The <c>Fight</c> values that mean "not a fight". Each names something drawn at Neow, off
    /// the front of the same per-player Rewards stream, before any room is entered.
    /// </summary>
    private const int Specialized = -1;
    private const int AllStar = -2;
    private const int Draft = -3;

    /// <summary>Whether any criterion of this kind can be answered by this slot.</summary>
    private static bool Asks(int slot, int kind, CardFilterParams p, CardCriteriaView criteria)
    {
        for (int c = 0; c < p.CriterionCount; c++)
            if (criteria.Fight[c] == kind && (criteria.Slot[c] < 0 || criteria.Slot[c] == slot))
                return true;
        return false;
    }

    /// <summary>
    /// Marks ONE still-unsatisfied criterion of this kind as met by a drawn card, and returns
    /// the new mask.
    ///
    /// One rather than all, because asking for the same card twice asks to be handed it twice,
    /// and letting a single copy light both bits would accept seeds that give only one.
    /// Criteria naming the same card are interchangeable, so claiming the first unsatisfied one
    /// never strands a later one.
    /// </summary>
    private static int Claim(
        int gotType, int slot, int kind, int satisfied, CardFilterParams p, CardCriteriaView criteria)
    {
        for (int c = 0; c < p.CriterionCount; c++)
        {
            if (criteria.Fight[c] != kind) continue;
            if ((satisfied & (1 << c)) != 0) continue;
            int want = criteria.Slot[c];
            if (want >= 0 && want != slot) continue;
            if (criteria.TypeId[c] != gotType) continue;
            return satisfied | (1 << c);
        }
        return satisfied;
    }

    /// <summary>
    /// <c>CardRarityOdds.RollWithBaseOdds</c>: the base thresholds with no pity offset applied
    /// and none written back, which is what Draft's options select.
    ///
    /// Separate from <see cref="RollRarity"/> rather than a flag on it, matching Core, because
    /// the two differ in both the threshold and the side effect.
    /// </summary>
    private static int RollBaseRarity(
        ref GpuRandom rng, CardFilterParams p, CardPoolView pools, int pool, int poolBase,
        in TakenSet taken)
    {
        float roll = rng.NextFloat();
        int rarity = roll < p.RareOdds ? Rare
                   : roll < RegularUncommonOdds + p.RareOdds ? Uncommon
                   : Common;
        return NextAllowedRarity(pools, pool, poolBase, rarity, in taken);
    }

    /// <summary>Whether any fight criterion can be satisfied by this slot's rewards.</summary>
    private static bool WalksFights(int slot, CardFilterParams p, CardCriteriaView criteria)
    {
        for (int c = 0; c < p.CriterionCount; c++)
            if (criteria.Fight[c] > 0 && (criteria.Slot[c] < 0 || criteria.Slot[c] == slot)) return true;
        return false;
    }

    /// <summary>
    /// Whether every criterion pinned to <paramref name="slot"/> has been met, limited to one
    /// fight unless <paramref name="fight"/> is <see cref="AnyFight"/>. Only criteria pinned to
    /// the slot are asked: an "any player" one can still be met by someone walked later.
    /// </summary>
    private static bool PinnedSatisfied(
        int slot, int fight, int satisfied, CardFilterParams p, CardCriteriaView criteria)
    {
        for (int c = 0; c < p.CriterionCount; c++)
        {
            if (criteria.Slot[c] != slot) continue;
            if (fight != AnyFight && criteria.Fight[c] != fight) continue;
            if ((satisfied & (1 << c)) == 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Walk one player's fights, marking every criterion a card satisfies. False when the seed is
    /// already known to fail, which lets the caller stop without walking anyone else.
    /// </summary>
    private static bool WalkFights(
        ulong runSeed, int slot, CardFilterParams p, CardPoolView pools, CardCriteriaView criteria,
        int pool, int poolBase, ref int satisfied)
    {
        var rng = GpuRandom.Named(unchecked(runSeed + (ulong)slot), p.RewardsNameHash);

        // Whatever that player's Neow pick already spent. Zero for the great majority of
        // picks, and the whole reason the ten stream-drawing options need naming at all.
        rng.Burn(criteria.PriorDraws[slot]);

        float rarityOffset = BaseRarityOffset;
        float potionOdds = BasePotionRewardOdds;

        for (int fight = 1; fight <= p.DeepestFight; fight++)
        {
            bool hasPotion = rng.NextFloat() < potionOdds;
            potionOdds += hasPotion ? -0.1f : 0.1f;

            rng.Burn(1);                  // GoldReward.Populate
            if (hasPotion) rng.Burn(2);   // PotionReward.Populate: rarity, then pick

            var taken = default(TakenSet);

            for (int i = 0; i < 3; i++)
            {
                int rarity = RollRarity(ref rng, ref rarityOffset, p, pools, pool, poolBase, in taken);
                int count = CountAvailable(pools, pool, poolBase, rarity, in taken);

                // Unreachable for a real pool: only a pool with no Common, Uncommon or Rare
                // could drain every rung of the ladder. The CPU path throws here; a kernel
                // cannot, so it declines the seed instead, and the verifier would show it.
                if (count <= 0) return false;

                int picked = NthAvailable(pools, pool, poolBase, rarity, rng.NextInt(0, count), in taken);
                if (picked < 0) return false;

                rng.NextFloat();          // upgrade roll, taken whether or not it can land

                int type = pools.TypeId[poolBase + picked];
                for (int c = 0; c < p.CriterionCount; c++)
                {
                    // A payload criterion is settled above and never by a fight, even under
                    // any-order: its card is a Rare, and a later fight can offer one.
                    if (criteria.Fight[c] == 0) continue;
                    if (p.AnyOrder == 0 && criteria.Fight[c] != fight) continue;
                    int want = criteria.Slot[c];
                    if (want >= 0 && want != slot) continue;
                    if (criteria.TypeId[c] == type) satisfied |= 1 << c;
                }

                taken.Add(picked);
            }

            // In pick order a criterion names its fight, so once that fight is walked it is
            // settled, and most seeds miss fight 1 — no need to walk fights 2 and 3 for them.
            // Any order waits for the whole walk: a card can still turn up in a later fight.
            if (p.AnyOrder == 0 && !PinnedSatisfied(slot, fight, satisfied, p, criteria)) return false;
        }
        return true;
    }

    /// <summary>
    /// One seed index per thread, answering only "did it match". Used by the verifier: it keeps
    /// the comparison against <c>Core</c> about the card logic alone, with none of the tiling,
    /// atomics or overflow retry of the search runner in the way.
    /// </summary>
    public static void ProbeKernel(
        Index1D i,
        ArrayView<ulong> indices,
        CardFilterParams p,
        CardPoolView pools,
        CardCriteriaView criteria,
        ArrayView<int> matched)
    {
        matched[i] = Matches(GpuSeedString.RunSeed(indices[i]), p, pools, criteria) ? 1 : 0;
    }
}
