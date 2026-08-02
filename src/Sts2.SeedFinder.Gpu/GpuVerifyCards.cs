using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Holds the card kernel to <c>CardRewardGenerator</c>, the same way <see cref="GpuVerify"/>
/// holds the Neow kernel to <c>NeowGenerator</c>.
///
/// Compares the answer on every seed in a range rather than sampling matches, so a kernel that
/// wrongly rejects a seed fails here. That is the direction that cannot be caught downstream:
/// the CPU re-check throws out false positives, but a seed the pre-filter never yields is one
/// nobody will ever notice was missing.
/// </summary>
public static class GpuVerifyCards
{
    /// <summary>
    /// Run one criterion per player slot, taken from that slot's own pool, over a contiguous
    /// range, and compare the kernel's verdict with the generator's on every seed.
    /// </summary>
    public static GpuCheck Run(
        GpuEngine engine,
        IReadOnlyList<Character> characters,
        int ascension,
        UnlockState? unlocks,
        ulong start,
        int samples,
        int fight)
    {
        using var pools = new GpuCardPools(engine.Accelerator, characters, unlocks);

        // A card each player can actually be offered: the first Common in that slot's pool.
        // Commons are the likeliest rarity, so the criterion is dense enough that a range this
        // size produces matches to disagree about.
        var wanted = new string[characters.Count];
        for (int slot = 0; slot < characters.Count; slot++)
        {
            var pool = CardRewardGenerator.PoolFor(characters[slot], unlocks);
            wanted[slot] = pool.First(e => e.Rarity == CardRarity.Common).TypeName;
        }

        var critSlot = new int[characters.Count];
        var critFight = new int[characters.Count];
        var critType = new int[characters.Count];
        for (int slot = 0; slot < characters.Count; slot++)
        {
            critSlot[slot] = slot;
            critFight[slot] = fight;
            critType[slot] = pools.TypeIdOf(wanted[slot]);
            if (critType[slot] < 0)
                return new GpuCheck("cards", false, $"'{wanted[slot]}' is not in the uploaded pools");
        }

        var p = new CardFilterParams
        {
            RewardsNameHash = GameHash.Deterministic(GameHash.SnakeCase("Rewards")),
            RareOdds = ascension >= CardRewardGenerator.Scarcity ? 0.0149f : 0.03f,
            RarityGrowth = ascension >= CardRewardGenerator.Scarcity ? 0.005f : 0.01f,
            PlayerCount = characters.Count,
            CriterionCount = characters.Count,
            DeepestFight = fight,
            AllMask = (1 << characters.Count) - 1,
        };

        var indices = new ulong[samples];
        for (int i = 0; i < samples; i++) indices[i] = start + (ulong)i;

        var acc = engine.Accelerator;
        using var dIndices = acc.Allocate1D(indices);
        using var dSlot = acc.Allocate1D(critSlot);
        using var dFight = acc.Allocate1D(critFight);
        using var dType = acc.Allocate1D(critType);
        using var dPoolOfSlot = acc.Allocate1D(pools.PoolOfSlot);
        using var dMatched = acc.Allocate1D<int>(samples);

        var kernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<ulong>, CardFilterParams, CardPoolView, CardCriteriaView, ArrayView<int>>(
            CardFilter.ProbeKernel);

        var criteriaView = new CardCriteriaView(dSlot.View, dFight.View, dType.View, dPoolOfSlot.View);
        kernel(samples, dIndices.View, p, pools.View, criteriaView, dMatched.View);
        acc.Synchronize();

        var got = dMatched.GetAsArray1D();

        int matches = 0, disagreements = 0;
        string firstDetail = "";
        for (int i = 0; i < samples; i++)
        {
            ulong runSeed = SeedCodec.RunSeed(SeedCodec.FromIndex(indices[i], GpuSeedString.Length));

            bool want = true;
            for (int slot = 0; slot < characters.Count && want; slot++)
            {
                var rewards = CardRewardGenerator.Hallway(
                    runSeed, slot, characters[slot], fight, ascension, unlocks);
                want = rewards.Fight(fight)?.Cards.Any(c => c.TypeName == wanted[slot]) == true;
            }

            if (want) matches++;
            if (want != (got[i] != 0))
            {
                disagreements++;
                if (firstDetail.Length == 0)
                    firstDetail = $"index {indices[i]}: kernel {(got[i] != 0 ? "matched" : "rejected")}, generator {(want ? "matched" : "rejected")}";
            }
        }

        return disagreements == 0
            ? new GpuCheck($"cards fight {fight}", true,
                $"{samples:N0} seeds, {matches:N0} matches, every verdict agrees")
            : new GpuCheck($"cards fight {fight}", false,
                $"{disagreements:N0} of {samples:N0} disagree; first: {firstDetail}");
    }
}
