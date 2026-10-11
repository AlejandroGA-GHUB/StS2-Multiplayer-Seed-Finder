using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Modifiers;

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

    /// <summary>
    /// Pool-local positions of every entry a UNIFORM pick can land on, in pool order: the
    /// game's "not Basic and not Ancient" filter, which the Uniform branch of
    /// <c>CardFactory.CreateForReward</c> applies instead of rolling a rarity.
    ///
    /// A second index rather than a reuse of <see cref="Group"/>, because the two answer
    /// different questions. A rarity roll indexes within ONE rarity; a uniform pick indexes
    /// across three of them in pool order, which is not any one group and is not the
    /// concatenation of the groups either, since those are ordered by rarity first.
    /// </summary>
    public readonly ArrayView<int> Offerable;

    /// <summary>Start of a pool's run within <see cref="Offerable"/>.</summary>
    public readonly ArrayView<int> OfferableStart;

    /// <summary>How many entries that run holds.</summary>
    public readonly ArrayView<int> OfferableCount;

    /// <summary>
    /// How many of them belong to the player's OWN character, which is a prefix of the run.
    ///
    /// Specialized reads this rather than <see cref="OfferableCount"/>. Its options never set
    /// the IsCardReward flag, so the CharacterCards hook declines it and its pool stays the
    /// character's own, even in a lobby where every reward pool has been widened. The added
    /// pools are APPENDED, so the own pool is a prefix of the widened one, in the same order,
    /// and a count is all it takes to stop at the boundary.
    /// </summary>
    public readonly ArrayView<int> OfferableOwnCount;

    public CardPoolView(
        ArrayView<byte> rarity, ArrayView<int> typeId, ArrayView<int> group,
        ArrayView<int> groupStart, ArrayView<int> groupCount, ArrayView<int> poolBase,
        ArrayView<int> offerable, ArrayView<int> offerableStart, ArrayView<int> offerableCount,
        ArrayView<int> offerableOwnCount)
    {
        Rarity = rarity;
        TypeId = typeId;
        Group = group;
        GroupStart = groupStart;
        GroupCount = groupCount;
        PoolBase = poolBase;
        Offerable = offerable;
        OfferableStart = offerableStart;
        OfferableCount = offerableCount;
        OfferableOwnCount = offerableOwnCount;
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
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _offerable;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _offerableStart;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _offerableCount;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _offerableOwnCount;

    /// <summary>
    /// Which uploaded pool each player slot uses. Slots with the same character AND the same
    /// unlock state share one.
    /// </summary>
    public int[] PoolOfSlot { get; }

    /// <summary>Type name to global id, for turning a card criterion into an int.</summary>
    private readonly Dictionary<string, int> _typeIds;

    public CardPoolView View => new(
        _rarity.View, _typeId.View, _group.View, _groupStart.View, _groupCount.View, _poolBase.View,
        _offerable.View, _offerableStart.View, _offerableCount.View, _offerableOwnCount.View);

    /// <summary>
    /// Which uploaded pool holds the COLOURLESS cards for each slot, which is what All Star
    /// draws from. Per slot rather than one for the lobby, because the pool is epoch-gated by
    /// each player's own unlocks exactly as a character pool is.
    /// </summary>
    public int[] ColorlessPoolOfSlot { get; }

    /// <param name="unlocksBySlot">
    /// Each slot's OWN unlock state, in lobby order — what <c>SearchCriteria.UnlocksBySlot</c>
    /// returns. Not the run's union: a card pool is gated by its player's epochs, and the CPU
    /// reads it that way through <c>UnlocksFor(slot)</c>. A single shared state here once made
    /// the kernel draw from a partner's union-sized pool while the CPU drew from their own, so an
    /// imported friend epoch that differed in card epochs silently dropped real seeds.
    /// </param>
    /// <param name="characterCardPools">
    /// The lobby's CharacterCards tickboxes. Lobby-wide, so two slots on the same character and
    /// unlocks still share one pool, which is why this does not widen the cache key. What it
    /// adds is per character, since a player playing a ticked character gains nothing.
    /// </param>
    public GpuCardPools(
        Accelerator accelerator, IReadOnlyList<Character> characters,
        IReadOnlyList<UnlockState?> unlocksBySlot, bool isMultiplayer = true,
        IReadOnlyList<Character>? characterCardPools = null)
    {
        if (unlocksBySlot.Count != characters.Count)
            throw new ArgumentException(
                $"one unlock state per slot: {characters.Count} characters, {unlocksBySlot.Count} states.",
                nameof(unlocksBySlot));

        // Distinct pools only. A co-op lobby of two Ironclads on the same unlocks is one pool,
        // not two. Keyed on the inputs rather than on PoolFor's array: its cache holds one entry
        // per character, so alternating unlock states for the same character rebuild the array
        // and would defeat an identity match, uploading duplicates.
        var pools = new List<CardEntry[]>();
        var keys = new List<(Character Character, UnlockState? Unlocks)>();
        PoolOfSlot = new int[characters.Count];
        for (int slot = 0; slot < characters.Count; slot++)
        {
            var character = characters[slot];
            var unlocks = unlocksBySlot[slot];
            int existing = keys.FindIndex(k => k.Character == character && ReferenceEquals(k.Unlocks, unlocks));
            if (existing < 0)
            {
                existing = pools.Count;
                keys.Add((character, unlocks));
                pools.Add(CardRewardGenerator.PoolFor(
                    character, unlocks, isMultiplayer,
                    CharacterCardsModifier.AddedFor(character, characterCardPools)));
            }
            PoolOfSlot[slot] = existing;
        }

        // How much of each pool is the player's OWN character, before CharacterCards appended
        // anything. Specialized stops here; everything else reads the whole pool.
        var ownLength = new List<int>();
        for (int i = 0; i < pools.Count; i++)
        {
            var (character, unlocks) = keys[i];
            ownLength.Add(CardRewardGenerator.PoolFor(character, unlocks, isMultiplayer).Length);
        }

        // The colourless pool, which All Star draws from. One per distinct unlock state rather
        // than one for the lobby: it carries five epoch gates of its own, so two players whose
        // accounts differ in those epochs see different pools, exactly as with a character pool.
        ColorlessPoolOfSlot = new int[characters.Count];
        var colorlessKeys = new List<(UnlockState? Unlocks, int Pool)>();
        for (int slot = 0; slot < characters.Count; slot++)
        {
            var unlocks = unlocksBySlot[slot];
            int at = colorlessKeys.FindIndex(k => ReferenceEquals(k.Unlocks, unlocks));
            if (at < 0)
            {
                var colorless = CardRewardGenerator.ColorlessPool(unlocks, isMultiplayer);
                colorlessKeys.Add((unlocks, pools.Count));
                at = colorlessKeys.Count - 1;
                pools.Add(colorless);
                ownLength.Add(colorless.Length);
            }
            ColorlessPoolOfSlot[slot] = colorlessKeys[at].Pool;
        }

        _typeIds = new Dictionary<string, int>(StringComparer.Ordinal);

        var rarity = new List<byte>();
        var typeId = new List<int>();
        var group = new List<int>();
        var groupStart = new int[pools.Count * RarityCount];
        var groupCount = new int[pools.Count * RarityCount];
        var poolBase = new int[pools.Count];
        var offerable = new List<int>();
        var offerableStart = new int[pools.Count];
        var offerableCount = new int[pools.Count];
        var offerableOwnCount = new int[pools.Count];

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

            // Pool order again, across the three reward rarities at once. Mirrors the game's
            // own filter, written as the exclusion it is rather than as "the reward rarities",
            // so a pool that one day grows a fourth reachable rarity keeps matching.
            offerableStart[p] = offerable.Count;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i].Rarity is CardRarity.Basic or CardRarity.Ancient) continue;
                offerable.Add(i);
                if (i < ownLength[p]) offerableOwnCount[p]++;
            }
            offerableCount[p] = offerable.Count - offerableStart[p];
        }

        _rarity = accelerator.Allocate1D(rarity.ToArray());
        _typeId = accelerator.Allocate1D(typeId.ToArray());
        _group = accelerator.Allocate1D(group.ToArray());
        _groupStart = accelerator.Allocate1D(groupStart);
        _groupCount = accelerator.Allocate1D(groupCount);
        _poolBase = accelerator.Allocate1D(poolBase);
        _offerable = accelerator.Allocate1D(offerable.ToArray());
        _offerableStart = accelerator.Allocate1D(offerableStart);
        _offerableCount = accelerator.Allocate1D(offerableCount);
        _offerableOwnCount = accelerator.Allocate1D(offerableOwnCount);
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
        _offerable.Dispose();
        _offerableStart.Dispose();
        _offerableCount.Dispose();
        _offerableOwnCount.Dispose();
    }
}
