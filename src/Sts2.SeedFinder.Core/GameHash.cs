using System.IO.Hashing;
using System.Text;

namespace Sts2.SeedFinder.Core;

/// <summary>
/// Port of the hashing half of MegaCrit.Sts2.Core.Helpers.StringHelper (StS2 v0.109.0).
/// </summary>
public static class GameHash
{
    /// <summary>
    /// StringHelper.GetDeterministicHashCode — plain XXH64 over the UTF-8 bytes, seed 0.
    /// This is what turns a seed string into the number every RNG in the game is derived from.
    /// </summary>
    public static ulong Deterministic(string str)
    {
        Span<byte> buf = str.Length <= 128 ? stackalloc byte[Encoding.UTF8.GetByteCount(str)] : new byte[Encoding.UTF8.GetByteCount(str)];
        int written = Encoding.UTF8.GetBytes(str, buf);
        return XxHash64.HashToUInt64(buf[..written], 0L);
    }

    /// <summary>
    /// StringHelper.GetDeterministicHashCodeOld — legacy 32-bit hash, still reachable for
    /// seeds entered with an "old" prefix. Kept for parity with RunRngSet's constructor.
    /// </summary>
    public static int DeterministicOld(string str)
    {
        int num = 352654597;
        int num2 = num;
        for (int i = 0; i < str.Length; i += 2)
        {
            num = ((num << 5) + num) ^ str[i];
            if (i == str.Length - 1) break;
            num2 = ((num2 << 5) + num2) ^ str[i + 1];
        }
        return num + num2 * 1566083941;
    }

    /// <summary>
    /// StringHelper.Slugify — CamelCase boundaries to underscores, uppercased, specials stripped.
    /// Model IDs use this, so e.g. type "Neow" becomes the entry "NEOW" that gets hashed.
    /// </summary>
    public static string Slugify(string txt)
    {
        var withUnderscores = InsertCamelBoundaries(txt.Trim());
        var upper = withUnderscores.ToUpperInvariant();
        var sb = new StringBuilder(upper.Length);
        foreach (var c in upper)
        {
            if (char.IsWhiteSpace(c)) sb.Append('_');
            else if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>StringHelper.SnakeCase — used for RunRngType / PlayerRngType generator names.</summary>
    public static string SnakeCase(string txt) => InsertCamelBoundaries(txt.Trim()).ToLowerInvariant();

    // The game breaks before *every* uppercase letter that follows a letter or digit, which
    // means a run of capitals is split apart rather than kept together as an acronym:
    //
    //   IAmInvincible  -> I_AM_INVINCIBLE     ExpectAFight -> EXPECT_A_FIGHT
    //   ENetClient     -> E_NET_CLIENT        NVSyncPaginator -> N_V_SYNC_PAGINATOR
    //
    // The obvious "split only at a lower-to-upper boundary" rule gets "iam_invincible", which is
    // not what the game calls that card and so matches neither its art nor its localization key.
    // The acronym-preserving variant gets everything except NVSyncPaginator, so it is wrong too.
    // Checked against the game's own Slugify over all 5,771 named types in the assembly.
    private static string InsertCamelBoundaries(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLetterOrDigit(s[i - 1]))
                sb.Append('_');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
