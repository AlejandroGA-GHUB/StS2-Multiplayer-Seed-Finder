using System.Runtime.CompilerServices;

namespace Sts2.SeedFinder.Core.Cards;

/// <summary>
/// A card pool, pre-grouped by rarity and with type names replaced by integer ids.
///
/// The pool is fixed for a character and unlock state, but a reward draw asks two questions of
/// it nine times a fight: how many cards of this rarity are still available, and which is the
/// n'th of them. Both used to walk all ninety entries and compare strings; the search spends
/// most of a card criterion inside those two loops, which is why a card search runs at roughly
/// a seventieth the rate of a Neow one.
///
/// Nothing here changes what is drawn. The entries within a rarity keep their POOL order, which
/// is the order the game indexes into, and the same draws are taken in the same sequence. It is
/// the same walk over a shorter list with cheaper comparisons.
///
/// This is also the shape a GPU kernel needs, since neither a string nor a dictionary survives
/// compilation to a kernel: flat int arrays and an index, uploadable as-is.
/// </summary>
public sealed class PoolIndex
{
    /// <summary>
    /// Built once per pool instance. <c>PoolFor</c> hands back the same array for the same
    /// character and unlock state, so keying on the array itself gets one index per distinct
    /// pool without needing to know how that pool was identified. The table holds weak keys, so
    /// an index costs nothing once its pool is gone.
    /// </summary>
    private static readonly ConditionalWeakTable<CardEntry[], PoolIndex> Cache = new();

    private readonly int[][] _byRarity;

    /// <summary>
    /// Position in the pool to an id shared by every entry with the same TypeName.
    ///
    /// The game's blacklist excludes by card type, not by pool position, so comparing positions
    /// would be wrong for a pool that listed one type twice. Interning to ints keeps the exact
    /// semantics and reduces the comparison to an integer one.
    /// </summary>
    public int[] TypeIds { get; }

    private PoolIndex(CardEntry[] pool)
    {
        int rarityCount = 0;
        foreach (var entry in pool) rarityCount = Math.Max(rarityCount, (int)entry.Rarity + 1);

        var buckets = new List<int>[rarityCount];
        for (int r = 0; r < rarityCount; r++) buckets[r] = new List<int>();

        var ids = new Dictionary<string, int>(pool.Length, StringComparer.Ordinal);
        TypeIds = new int[pool.Length];

        for (int i = 0; i < pool.Length; i++)
        {
            buckets[(int)pool[i].Rarity].Add(i);

            if (!ids.TryGetValue(pool[i].TypeName, out int id))
            {
                id = ids.Count;
                ids[pool[i].TypeName] = id;
            }
            TypeIds[i] = id;
        }

        _byRarity = new int[rarityCount][];
        for (int r = 0; r < rarityCount; r++) _byRarity[r] = buckets[r].ToArray();
    }

    public static PoolIndex For(CardEntry[] pool) => Cache.GetValue(pool, static p => new PoolIndex(p));

    /// <summary>
    /// Pool positions of one rarity, in pool order. Empty for a rarity the pool never contains,
    /// which is the normal answer for Rare on a fight the pity counter cannot reach.
    /// </summary>
    public ReadOnlySpan<int> Of(CardRarity rarity)
    {
        int r = (int)rarity;
        return r >= 0 && r < _byRarity.Length ? _byRarity[r] : ReadOnlySpan<int>.Empty;
    }

    /// <summary>How many entries the pool holds at a rarity, before any blacklist.</summary>
    public int CountOf(CardRarity rarity)
    {
        int r = (int)rarity;
        return r >= 0 && r < _byRarity.Length ? _byRarity[r].Length : 0;
    }
}
