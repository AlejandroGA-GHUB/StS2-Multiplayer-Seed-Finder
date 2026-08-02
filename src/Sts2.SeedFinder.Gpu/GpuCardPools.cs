using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// A card pool as a kernel sees it: flat integer arrays, no strings, no dictionaries.
///
/// Every pool in the lobby is concatenated into one set of arrays and addressed by offset, so
/// a four-player lobby of four different characters costs one upload and one binding rather
/// than four of each.
/// </summary>
public readonly struct CardPoolView
{
    /// <summary>Rarity of each entry, indexed globally (pool base + pool-local position).</summary>
    public readonly ArrayView<byte> Rarity;

    /// <summary>
    /// Card type id of each entry, in a GLOBAL id space shared by every pool.
    ///
    /// Global rather than per pool because a criterion can ask for a card from "any player",
    /// and the CPU answers that by comparing type NAMES across the lobby. Two characters that
    /// both list a card must therefore agree on its id.
    /// </summary>
    public readonly ArrayView<int> TypeId;

    /// <summary>Pool-local entry positions grouped by rarity, each group in pool order.</summary>
    public readonly ArrayView<int> Group;

    /// <summary>Start of a group within <see cref="Group"/>, indexed [pool * RarityCount + rarity].</summary>
    public readonly ArrayView<int> GroupStart;

    /// <summary>Length of that group.</summary>
    public readonly ArrayView<int> GroupCount;

    /// <summary>Where each pool's entries begin in <see cref="Rarity"/> and <see cref="TypeId"/>.</summary>
    public readonly ArrayView<int> PoolBase;

    public CardPoolView(
        ArrayView<byte> rarity, ArrayView<int> typeId, ArrayView<int> group,
        ArrayView<int> groupStart, ArrayView<int> groupCount, ArrayView<int> poolBase)
    {
        Rarity = rarity;
        TypeId = typeId;
        Group = group;
        GroupStart = groupStart;
        GroupCount = groupCount;
        PoolBase = poolBase;
    }
}

/// <summary>
/// Builds <see cref="CardPoolView"/> from the same <c>CardPoolData</c> the CPU path uses, and
/// owns the device buffers behind it.
///
/// The flattening deliberately mirrors <c>Core.Cards.PoolIndex</c> rather than inventing a
/// second grouping: same per-rarity buckets, same pool order within a bucket, same interning of
/// type names to ids. If the two ever disagree the kernel silently draws a different card, so
/// the verifier compares whole rewards rather than trusting the shapes to line up.
/// </summary>
public sealed class GpuCardPools : IDisposable
{
    /// <summary>Number of <c>CardRarity</c> members, which is how the group table is strided.</summary>
    public const int RarityCount = 11;

    private readonly MemoryBuffer1D<byte, Stride1D.Dense> _rarity;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _typeId;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _group;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _groupStart;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _groupCount;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _poolBase;

    /// <summary>Which uploaded pool each player slot uses. Identical characters share one.</summary>
    public int[] PoolOfSlot { get; }

    /// <summary>Type name to global id, for turning a card criterion into an int.</summary>
    private readonly Dictionary<string, int> _typeIds;

    public CardPoolView View => new(
        _rarity.View, _typeId.View, _group.View, _groupStart.View, _groupCount.View, _poolBase.View);

    public GpuCardPools(Accelerator accelerator, IReadOnlyList<Character> characters, UnlockState? unlocks)
    {
        // Distinct pools only. A co-op lobby of two Ironclads is one pool, not two.
        var pools = new List<CardEntry[]>();
        PoolOfSlot = new int[characters.Count];
        for (int slot = 0; slot < characters.Count; slot++)
        {
            var pool = CardRewardGenerator.PoolFor(characters[slot], unlocks);
            int existing = pools.FindIndex(p => ReferenceEquals(p, pool));
            if (existing < 0) { existing = pools.Count; pools.Add(pool); }
            PoolOfSlot[slot] = existing;
        }

        _typeIds = new Dictionary<string, int>(StringComparer.Ordinal);

        var rarity = new List<byte>();
        var typeId = new List<int>();
        var group = new List<int>();
        var groupStart = new int[pools.Count * RarityCount];
        var groupCount = new int[pools.Count * RarityCount];
        var poolBase = new int[pools.Count];

        for (int p = 0; p < pools.Count; p++)
        {
            var pool = pools[p];
            poolBase[p] = rarity.Count;

            foreach (var entry in pool)
            {
                rarity.Add((byte)entry.Rarity);
                if (!_typeIds.TryGetValue(entry.TypeName, out int id))
                {
                    id = _typeIds.Count;
                    _typeIds[entry.TypeName] = id;
                }
                typeId.Add(id);
            }

            // Pool order preserved within each rarity, because pool order is what the game
            // indexes into when it picks the n'th available card.
            for (int r = 0; r < RarityCount; r++)
            {
                groupStart[p * RarityCount + r] = group.Count;
                for (int i = 0; i < pool.Length; i++)
                    if ((int)pool[i].Rarity == r) group.Add(i);
                groupCount[p * RarityCount + r] = group.Count - groupStart[p * RarityCount + r];
            }
        }

        _rarity = accelerator.Allocate1D(rarity.ToArray());
        _typeId = accelerator.Allocate1D(typeId.ToArray());
        _group = accelerator.Allocate1D(group.ToArray());
        _groupStart = accelerator.Allocate1D(groupStart);
        _groupCount = accelerator.Allocate1D(groupCount);
        _poolBase = accelerator.Allocate1D(poolBase);
    }

    /// <summary>Global id for a card type, or -1 when no pool in this lobby contains it.</summary>
    public int TypeIdOf(string typeName) => _typeIds.TryGetValue(typeName, out int id) ? id : -1;

    public void Dispose()
    {
        _rarity.Dispose();
        _typeId.Dispose();
        _group.Dispose();
        _groupStart.Dispose();
        _groupCount.Dispose();
        _poolBase.Dispose();
    }
}
