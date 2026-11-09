using System.Runtime.InteropServices;
using ILGPU;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Gpu;

/// <summary>The relic payload stage's scalars.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RelicPayloadParams
{
    public int Active;
    public ulong UpFrontNameHash;
    public ulong RewardsNameHash;
    public ulong NicheNameHash;
    public int ProbeCount;

    /// <summary>One bit per thing asked; a seed passes when every bit is set.</summary>
    public int AllMask;
}

/// <summary>The relic payload stage's one array: <see cref="RelicPayloadFilter.Stride"/> ints a probe.</summary>
public readonly struct RelicPayloadView
{
    public readonly ArrayView<int> Probes;

    public RelicPayloadView(ArrayView<int> probes) => Probes = probes;
}

/// <summary>
/// What a Small or Large Capsule pulls, the two relics Neow's Bones deals and the curse it adds,
/// as probes that never build a deque or a list.
///
/// <list type="bullet">
/// <item><b>Capsule.</b> Roll the rarities on the player's Rewards stream. A wanted relic of
/// rarity R is handed over exactly when it ends the UpFront shuffle of that player's R deque
/// somewhere in front of the number of R rolls: the first R pull takes position 0, the second
/// position 1. Where it ends is found by following its index through the shuffle's swaps, as the
/// shop probes do, after burning every shuffle ahead of it.</item>
/// <item><b>Neow's Bones.</b> Follow the wanted relic's index through the Rewards shuffle of
/// that player's Bones list; dealt means it ends at 0 or 1.</item>
/// <item><b>Curse.</b> One draw off Niche. Whether that draw is still first when the player
/// resolves Neow depends on everyone's offer, which this stage does not build, so it can only
/// over-accept: the CPU re-checks every candidate.</item>
/// </list>
///
/// Probes for "any player" set the same bit from several slots, so they are alternatives, and
/// one criterion asking for two relics gets a bit per relic. Two relics satisfied by two
/// different players then passes here and fails on the CPU, which is the direction a
/// pre-filter may be wrong in.
/// </summary>
public static class RelicPayloadFilter
{
    public const int Stride = 8;

    public const int KindCapsule = 1, KindBones = 2, KindCurse = 3;

    public const int FldKind = 0, FldBit = 1, FldSlot = 2;

    // Capsule: how many pulls, the wanted relic's rarity (0 Common, 1 Uncommon, 2 Rare), and
    // where its deque sits in UpFront.
    public const int FldPulls = 3, FldRarity = 4, FldDrawsBefore = 5, FldDequeSize = 6, FldFrom = 7;

    // Bones: list length and the wanted relic's index in it. Curse: the wanted index and the
    // pool length.
    public const int FldLength = 3, FldIndex = 4;

    public static bool Matches(ulong runSeed, RelicPayloadParams p, RelicPayloadView v)
    {
        int satisfied = 0;
        for (int i = 0; i < p.ProbeCount; i++)
        {
            int at = i * Stride;
            int bit = 1 << v.Probes[at + FldBit];
            if ((satisfied & bit) != 0) continue;

            int kind = v.Probes[at + FldKind];
            ulong slotSeed = unchecked(runSeed + (ulong)v.Probes[at + FldSlot]);

            if (kind == KindCurse)
            {
                var niche = GpuRandom.Named(runSeed, p.NicheNameHash);
                if (niche.NextInt(0, v.Probes[at + FldLength]) == v.Probes[at + FldIndex]) satisfied |= bit;
                continue;
            }

            var rewards = GpuRandom.Named(slotSeed, p.RewardsNameHash);

            if (kind == KindBones)
            {
                int length = v.Probes[at + FldLength];
                int pos = v.Probes[at + FldIndex];
                for (int n = length - 1; n >= 1; n--)
                {
                    int k = rewards.NextInt(n + 1);
                    if (pos == k) pos = n;
                    else if (pos == n) pos = k;
                }
                if (pos < 2) satisfied |= bit;
                continue;
            }

            // Capsule. RelicFactory.RollRarity: < 0.5 Common, < 0.83 Uncommon, else Rare.
            int pulls = v.Probes[at + FldPulls];
            int rarity = v.Probes[at + FldRarity];
            int hits = 0;
            for (int r = 0; r < pulls; r++)
            {
                float roll = rewards.NextFloat();
                int rolled = roll < NeowRelicPayload.CommonBelow ? 0 : roll < NeowRelicPayload.UncommonBelow ? 1 : 2;
                if (rolled == rarity) hits++;
            }
            if (hits == 0) continue;

            var upFront = GpuRandom.Named(runSeed, p.UpFrontNameHash);
            upFront.Burn(v.Probes[at + FldDrawsBefore]);
            int landed = v.Probes[at + FldFrom];
            for (int n = v.Probes[at + FldDequeSize] - 1; n >= 1; n--)
            {
                int k = upFront.NextInt(n + 1);
                if (landed == k) landed = n;
                else if (landed == n) landed = k;
            }
            if (landed < hits) satisfied |= bit;
        }
        return satisfied == p.AllMask;
    }
}

/// <summary>Builds the relic payload stage's probes from a search, or declines.</summary>
public static class RelicPayloadFilterFactory
{
    /// <summary>Bits available: one int of satisfied mask, keeping the sign bit clear.</summary>
    private const int MaxBits = 31;

    public static bool TryBuild(SearchCriteria criteria, out RelicPayloadParams p, out int[] probes)
    {
        p = default;
        probes = Array.Empty<int>();
        if (!criteria.NeedsNeowRelicPayloads) return false;
        if (criteria.Characters.Count != criteria.PlayerCount) return false;

        var deques = new Dictionary<string, RunGenerator.ShopDeque?[]>(StringComparer.Ordinal);
        RunGenerator.ShopDeque? DequeOf(string rarity, int slot)
        {
            if (!deques.TryGetValue(rarity, out var all))
                deques[rarity] = all = RunGenerator.PlayerDeques(
                    criteria.Characters, criteria.Unlocks, criteria.PlayerUnlocks, rarity);
            return all[slot];
        }

        var list = new List<int>();
        int bits = 0;

        void Add(int kind, int bit, int slot, int a, int b, int c = 0, int d = 0, int e = 0) =>
            list.AddRange(new[] { kind, bit, slot, a, b, c, d, e });

        foreach (var want in criteria.NeowCriteria)
        {
            if (want.Relics.Count == 0 && want.PayloadCurse is null) continue;

            var shape = NeowRelicPayload.ShapeOf(want.Relic.Slug);
            bool any = want.Requirement == SlotRequirement.Any;
            var slots = any
                ? Enumerable.Range(0, criteria.PlayerCount).ToArray()
                : want.ResolveSlots(criteria.PlayerCount).ToArray();

            // One bit per relic for "any player", one per relic per slot otherwise.
            foreach (var relic in want.Relics)
            {
                int anyBit = any ? bits++ : -1;
                foreach (var slot in slots)
                {
                    int bit = any ? anyBit : bits++;
                    if (shape == RelicPayloadShape.Capsule)
                    {
                        var found = NeowRelicPayload.FindCapsuleRelic(relic);
                        if (found is null) return false;
                        int rarity = found.Value.Rarity switch { "Common" => 0, "Uncommon" => 1, _ => 2 };
                        var deque = DequeOf(found.Value.Rarity, slot);
                        int from = deque is null ? -1 : IndexOf(deque.Relics, relic);
                        // Not in this player's bag: no probe, so the bit stays clear unless
                        // another slot sets it, which is the CPU's answer too.
                        if (from < 0) continue;
                        Add(RelicPayloadFilter.KindCapsule, bit, slot,
                            NeowRelicPayload.RelicCount(want.Relic.Slug), rarity,
                            deque!.DrawsBefore, deque.Relics.Count, from);
                    }
                    else
                    {
                        var bones = NeowRelicPayload.BonesList(slot, criteria.Context);
                        int index = -1;
                        for (int i = 0; i < bones.Count; i++)
                            if (bones[i].Slug == relic) { index = i; break; }
                        if (index < 0) continue;
                        Add(RelicPayloadFilter.KindBones, bit, slot, bones.Count, index);
                    }
                }
            }

            if (want.PayloadCurse is { } curse)
            {
                int index = Array.IndexOf(NeowRelicPayload.BonesCursePool, NeowRelicPayload.FindCurse(curse));
                if (index < 0) return false;
                Add(RelicPayloadFilter.KindCurse, bits++, 0, NeowRelicPayload.BonesCursePool.Length, index);
            }

            if (bits > MaxBits) return false;
        }

        if (bits == 0) return false;

        // Cheapest probes first: the curse is one draw, Bones a shuffle of thirty, and a capsule
        // a burn of a few hundred, so a seed the cheap ones reject never pays for the others.
        var ordered = Enumerable.Range(0, list.Count / RelicPayloadFilter.Stride)
            .OrderBy(i => list[i * RelicPayloadFilter.Stride + RelicPayloadFilter.FldKind] switch
            {
                RelicPayloadFilter.KindCurse => 0,
                RelicPayloadFilter.KindBones => 1,
                _ => 2,
            })
            .SelectMany(i => list.Skip(i * RelicPayloadFilter.Stride).Take(RelicPayloadFilter.Stride))
            .ToArray();

        probes = ordered.Length > 0 ? ordered : new int[RelicPayloadFilter.Stride];
        p = new RelicPayloadParams
        {
            Active = 1,
            UpFrontNameHash = GameHash.Deterministic(GameHash.SnakeCase("UpFront")),
            RewardsNameHash = GameHash.Deterministic(GameHash.SnakeCase("Rewards")),
            NicheNameHash = GameHash.Deterministic(GameHash.SnakeCase("Niche")),
            ProbeCount = ordered.Length / RelicPayloadFilter.Stride,
            AllMask = bits == 31 ? int.MaxValue : (1 << bits) - 1,
        };
        return true;
    }

    private static int IndexOf(IReadOnlyList<PoolRelic> relics, string slug)
    {
        for (int i = 0; i < relics.Count; i++)
            if (relics[i].Slug == slug) return i;
        return -1;
    }
}
