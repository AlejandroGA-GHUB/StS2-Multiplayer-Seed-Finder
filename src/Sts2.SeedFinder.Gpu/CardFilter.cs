using System.Runtime.InteropServices;
using ILGPU;

namespace Sts2.SeedFinder.Gpu;

/// <summary>Card criteria as three parallel arrays, which is all a kernel can hold.</summary>
public readonly struct CardCriteriaView
{
    /// <summary>Player slot the criterion names, or -1 for "any player in the lobby".</summary>
    public readonly ArrayView<int> Slot;

    /// <summary>1-based fight, matching <c>CardCriterion.Fight</c>.</summary>
    public readonly ArrayView<int> Fight;

    /// <summary>Global card type id, from <see cref="GpuCardPools.TypeIdOf"/>.</summary>
    public readonly ArrayView<int> TypeId;

    /// <summary>Which uploaded pool each player slot draws from.</summary>
    public readonly ArrayView<int> PoolOfSlot;

    public CardCriteriaView(ArrayView<int> slot, ArrayView<int> fight, ArrayView<int> typeId, ArrayView<int> poolOfSlot)
    {
        Slot = slot;
        Fight = fight;
        TypeId = typeId;
        PoolOfSlot = poolOfSlot;
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

    /// <summary>Whether a candidate shares a card TYPE with one already offered this reward.</summary>
    private static bool Blacklisted(CardPoolView pools, int poolBase, int candidate, int t0, int t1, int takenCount)
    {
        int id = pools.TypeId[poolBase + candidate];
        if (takenCount > 0 && pools.TypeId[poolBase + t0] == id) return true;
        if (takenCount > 1 && pools.TypeId[poolBase + t1] == id) return true;
        return false;
    }

    /// <summary>
    /// How many cards of a rarity remain. The group total is precomputed, so this only has to
    /// subtract the at most two entries already taken, exactly as the CPU path now does.
    /// </summary>
    private static int CountAvailable(
        CardPoolView pools, int pool, int poolBase, int rarity, int t0, int t1, int takenCount)
    {
        if (rarity <= None || rarity >= GpuCardPools.RarityCount) return 0;
        int n = pools.GroupCount[pool * GpuCardPools.RarityCount + rarity];
        if (takenCount > 0 && pools.Rarity[poolBase + t0] == rarity) n--;
        if (takenCount > 1 && pools.Rarity[poolBase + t1] == rarity) n--;
        return n;
    }

    /// <summary>The nth still-available entry of a rarity, in pool order, or -1 if it ran out.</summary>
    private static int NthAvailable(
        CardPoolView pools, int pool, int poolBase, int rarity, int nth, int t0, int t1, int takenCount)
    {
        int start = pools.GroupStart[pool * GpuCardPools.RarityCount + rarity];
        int count = pools.GroupCount[pool * GpuCardPools.RarityCount + rarity];
        for (int i = 0; i < count; i++)
        {
            int entry = pools.Group[start + i];
            if (Blacklisted(pools, poolBase, entry, t0, t1, takenCount)) continue;
            if (nth-- == 0) return entry;
        }
        return -1;
    }

    /// <summary>CardFactory.GetNextAllowedRarity — climb the ladder until the pool can satisfy one.</summary>
    private static int NextAllowedRarity(
        CardPoolView pools, int pool, int poolBase, int rarity, int t0, int t1, int takenCount)
    {
        int seen = 1 << rarity;
        while (rarity != None && CountAvailable(pools, pool, poolBase, rarity, t0, t1, takenCount) == 0)
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
        CardPoolView pools, int pool, int poolBase, int t0, int t1, int takenCount)
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

        return NextAllowedRarity(pools, pool, poolBase, rarity, t0, t1, takenCount);
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
            var rng = GpuRandom.Named(unchecked(runSeed + (ulong)slot), p.RewardsNameHash);

            float rarityOffset = BaseRarityOffset;
            float potionOdds = BasePotionRewardOdds;

            for (int fight = 1; fight <= p.DeepestFight; fight++)
            {
                bool hasPotion = rng.NextFloat() < potionOdds;
                potionOdds += hasPotion ? -0.1f : 0.1f;

                rng.Burn(1);                  // GoldReward.Populate
                if (hasPotion) rng.Burn(2);   // PotionReward.Populate: rarity, then pick

                int t0 = -1, t1 = -1, takenCount = 0;

                for (int i = 0; i < 3; i++)
                {
                    int rarity = RollRarity(ref rng, ref rarityOffset, p, pools, pool, poolBase, t0, t1, takenCount);
                    int count = CountAvailable(pools, pool, poolBase, rarity, t0, t1, takenCount);

                    // Unreachable for a real pool: only a pool with no Common, Uncommon or Rare
                    // could drain every rung of the ladder. The CPU path throws here; a kernel
                    // cannot, so it declines the seed instead, and the verifier would show it.
                    if (count <= 0) return false;

                    int picked = NthAvailable(pools, pool, poolBase, rarity, rng.NextInt(0, count), t0, t1, takenCount);
                    if (picked < 0) return false;

                    rng.NextFloat();          // upgrade roll, taken whether or not it can land

                    int type = pools.TypeId[poolBase + picked];
                    for (int c = 0; c < p.CriterionCount; c++)
                    {
                        if (p.AnyOrder == 0 && criteria.Fight[c] != fight) continue;
                        int want = criteria.Slot[c];
                        if (want >= 0 && want != slot) continue;
                        if (criteria.TypeId[c] == type) satisfied |= 1 << c;
                    }

                    if (takenCount == 0) t0 = picked; else if (takenCount == 1) t1 = picked;
                    takenCount++;
                }
            }
        }

        return satisfied == p.AllMask;
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
