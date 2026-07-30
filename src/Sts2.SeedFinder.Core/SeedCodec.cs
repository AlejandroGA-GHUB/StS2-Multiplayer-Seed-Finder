using System.Text;

namespace Sts2.SeedFinder.Core;

/// <summary>
/// Port of MegaCrit.Sts2.Core.Helpers.SeedHelper, plus deterministic enumeration of the
/// seed-string space so a search can be split, resumed, and reproduced.
/// </summary>
public static class SeedCodec
{
    /// <summary>The game's seed alphabet. O and I are absent — they canonicalize to 0 and 1.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public const int DefaultLength = 12;

    /// <summary>SeedHelper.CanonicalizeSeed — what the game does to typed input before hashing.</summary>
    public static string Canonicalize(string seed) =>
        seed.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Trim();

    /// <summary>
    /// Map an index onto a seed string in the game's alphabet (base-34, fixed width).
    /// Enumerating indices gives every distinct seed string exactly once, which makes a
    /// search shardable across threads and resumable across runs.
    /// </summary>
    public static string FromIndex(ulong index, int length = DefaultLength)
    {
        Span<char> buf = stackalloc char[length];
        for (int i = length - 1; i >= 0; i--)
        {
            buf[i] = Alphabet[(int)(index % (ulong)Alphabet.Length)];
            index /= (ulong)Alphabet.Length;
        }
        return new string(buf);
    }

    /// <summary>Total distinct seed strings of the given length, or null if it overflows a ulong.</summary>
    public static ulong? SpaceSize(int length = DefaultLength)
    {
        ulong total = 1;
        for (int i = 0; i < length; i++)
        {
            if (total > ulong.MaxValue / (ulong)Alphabet.Length) return null;
            total *= (ulong)Alphabet.Length;
        }
        return total;
    }

    /// <summary>
    /// The run-level seed value, matching RunRngSet's constructor: XXH64 of the seed string,
    /// with the legacy "old"-prefixed path preserved.
    /// </summary>
    public static ulong RunSeed(string seedString)
    {
        if (seedString.StartsWith("old", StringComparison.Ordinal))
            return (uint)GameHash.DeterministicOld(seedString[3..]);
        return GameHash.Deterministic(seedString);
    }

    public static bool IsValid(string seed) =>
        seed.Length > 0 && seed.All(c => Alphabet.Contains(c));
}
