namespace Sts2.SeedFinder.Core;

/// <summary>
/// Port of MegaCrit.Sts2.Core.Random.Rng (StS2 v0.109.0) — the thin wrapper the game
/// uses over <see cref="MegaRandom"/>. Only the draws we need are implemented; each one
/// is a faithful copy, including the counter the game keeps for save serialization.
/// </summary>
public sealed class Rng
{
    private readonly MegaRandom _random;

    /// <summary>Number of draws taken. The game serializes this; it does not affect values.</summary>
    public int Counter { get; private set; }

    public Rng(ulong seed) => _random = new MegaRandom(seed);

    /// <summary>
    /// The game's named-generator constructor: <c>new Rng(seed, name)</c> derives a
    /// decorrelated stream as <c>seed + hash(name)</c>. Unchecked — the game relies on wraparound.
    /// </summary>
    public Rng(ulong seed, string name) : this(unchecked(seed + GameHash.Deterministic(name))) { }

    public bool NextBool()
    {
        Counter++;
        return _random.Next(2) == 0;
    }

    public int NextInt(int maxExclusive = int.MaxValue)
    {
        Counter++;
        return _random.Next(maxExclusive);
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(nameof(minInclusive), "Minimum must be lower than maximum.");
        Counter++;
        return _random.Next(minInclusive, maxExclusive);
    }

    /// <summary>
    /// Rng.NextFloat(max = 1f) delegates to NextFloat(0f, max), whose body is
    /// <c>(float)(_random.NextDouble() * (max - min) + min)</c> — so it goes through
    /// NextDouble, NOT MegaRandom.NextFloat. The two consume one draw each but derive
    /// their value from different bits (>> 11 vs >> 40), so this distinction matters.
    /// </summary>
    public float NextFloat()
    {
        Counter++;
        return (float)_random.NextDouble();
    }

    /// <summary>Used by GrabBag's weighted draw.</summary>
    public double NextDouble()
    {
        Counter++;
        return _random.NextDouble();
    }

    /// <summary>Rng.NextItem — one bounded draw, used as an index. Returns default for an empty set.</summary>
    public T? NextItem<T>(IReadOnlyList<T> items)
    {
        int count = items.Count;
        if (count == 0) return default;
        return items[NextInt(0, count)];
    }

    /// <summary>
    /// The index NextItem would select, without needing the list itself. Lets a search
    /// test a candidate seed with a single draw and no allocation.
    /// </summary>
    public int NextItemIndex(int count) => NextInt(0, count);
}
