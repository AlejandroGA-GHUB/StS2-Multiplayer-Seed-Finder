namespace Sts2.SeedFinder.Core.Acts;

/// <summary>
/// Port of MegaCrit.Sts2.Core.Helpers.GrabBag (StS2 v0.109.0).
///
/// A weighted bag drawn with <c>rng.NextDouble()</c>. The predicate overload retries by
/// drawing again — so the number of RNG draws consumed is VARIABLE and depends on which
/// entries currently satisfy the predicate. Reproducing that retry loop exactly is what
/// keeps the downstream stream (boss, Ancient) aligned with the game.
/// </summary>
public sealed class GrabBag<T>
{
    private readonly List<(T Item, double Weight)> _entries = new();
    private double _totalWeight;

    public int Count => _entries.Count;
    public bool Any() => _entries.Count > 0;

    public void Add(T element, double weight)
    {
        _entries.Add((element, weight));
        _totalWeight += weight;
    }

    public T? GrabAndRemove(Rng rng, Func<T, bool>? predicate = null)
    {
        int i = GrabIndex(rng, predicate);
        if (i < 0) return default;
        var item = _entries[i].Item;
        RemoveAt(i);
        return item;
    }

    private int GrabIndex(Rng rng, Func<T, bool>? predicate)
    {
        // The game bails out before drawing at all when nothing can satisfy the predicate.
        // That early return is load-bearing: without it we would consume an extra draw.
        if (predicate is not null && !_entries.Any(e => predicate(e.Item)))
            return -1;

        int index;
        do
        {
            index = GrabIndex(rng);
        }
        while (predicate is not null && index >= 0 && !predicate(_entries[index].Item));
        return index;
    }

    private int GrabIndex(Rng rng)
    {
        double roll = rng.NextDouble() * _totalWeight;
        double running = 0.0;
        for (int i = 0; i < _entries.Count; i++)
        {
            running += _entries[i].Weight;
            if (roll < running) return i;
        }
        return -1;
    }

    private void RemoveAt(int index)
    {
        _totalWeight -= _entries[index].Weight;
        _entries.RemoveAt(index);
    }
}
