using System.Reflection;
using Sts2.SeedFinder.Core;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// Spot-checks the handful of game functions everything else stands on, against the game's own
/// code: the seed hash, the slug rule, and the RNG stream itself.
///
/// Shared by <see cref="Doctor"/> and <see cref="Refresh"/>, and the reason it is shared matters.
/// Regenerating the data tables from a game whose hashing or RNG we no longer match produces
/// tables that are individually correct and collectively useless, because nothing downstream can
/// predict that game anyway. Worse, it overwrites tables that were right for the build the user
/// actually plays. So refresh asks this first and refuses.
///
/// Deliberately a triage check rather than the exhaustive one. If these disagree nothing else is
/// worth reading; if they agree, the Oracle is the thing to run for the full differential suite.
/// </summary>
public static class Primitives
{
    /// <param name="Problems">Empty when the primitives agree.</param>
    /// <param name="SignatureChanged">
    /// True when a member we call has a different shape than expected, rather than merely a
    /// different result. That distinction is the diagnosis: a changed return type means the
    /// function was redefined, which is a bigger and more specific finding than "the numbers
    /// differ", and reporting it as an exception name throws that away.
    /// </param>
    public sealed record Result(IReadOnlyList<string> Problems, bool SignatureChanged)
    {
        public bool Ok => Problems.Count == 0;
    }

    public static Result Check(GameModels game)
    {
        var problems = new List<string>();
        bool signature = false;

        try
        {
            var stringHelper = game.TypeNamed("MegaCrit.Sts2.Core.Helpers.StringHelper");

            var hash = stringHelper.GetMethod("GetDeterministicHashCode",
                BindingFlags.Public | BindingFlags.Static);
            if (hash is null)
            {
                problems.Add("StringHelper.GetDeterministicHashCode is gone");
                signature = true;
            }
            else if (hash.ReturnType != typeof(ulong))
            {
                // The v0.107.1 -> v0.109.1 change looked exactly like this: Int32 became UInt64
                // when the 32-bit hash was replaced. The signature IS the answer.
                problems.Add($"StringHelper.GetDeterministicHashCode returns {hash.ReturnType.Name}, "
                             + "we expect UInt64, so seed-to-runSeed derivation has changed");
                signature = true;
            }
            else
            {
                foreach (var seed in new[] { "8NZJ8J63RAKH", "NEOW", "up_front" })
                {
                    var theirs = (ulong)hash.Invoke(null, [seed])!;
                    if (theirs != GameHash.Deterministic(seed))
                        problems.Add($"hash of \"{seed}\" differs");
                }
            }

            var slugify = stringHelper.GetMethod("Slugify", BindingFlags.Public | BindingFlags.Static);
            if (slugify is not null && slugify.ReturnType == typeof(string))
            {
                foreach (var name in new[] { "BeltBuckle", "ChemicalX", "IAmInvincible" })
                {
                    var theirs = (string)slugify.Invoke(null, [name])!;
                    if (theirs != GameHash.Slugify(name))
                        problems.Add($"Slugify(\"{name}\") is \"{theirs}\", we say \"{GameHash.Slugify(name)}\"");
                }
            }
            else if (slugify is not null)
            {
                problems.Add($"StringHelper.Slugify returns {slugify.ReturnType.Name}, we expect String");
                signature = true;
            }

            // The stream itself, which everything else indexes into.
            var rngType = game.TypeNamed("MegaCrit.Sts2.Core.Random.Rng");
            var ctor = rngType.GetConstructor([typeof(ulong)]);
            var nextInt = rngType.GetMethod("NextInt", [typeof(int), typeof(int)]);
            if (ctor is null || nextInt is null || nextInt.ReturnType != typeof(int))
            {
                problems.Add("Rng(ulong) / Rng.NextInt(int, int) no longer has the shape we call");
                signature = true;
            }
            else
            {
                var theirRng = ctor.Invoke([12345UL]);
                var ours = new Rng(12345UL);
                for (int i = 0; i < 64; i++)
                {
                    int t = (int)nextInt.Invoke(theirRng, [0, 1000])!;
                    if (t != ours.NextInt(0, 1000))
                    {
                        problems.Add($"Rng.NextInt diverges at draw {i}");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Anything left is a shape we did not anticipate, which is still a signature story.
            problems.Add($"{(ex.InnerException ?? ex).GetType().Name} reading the game's primitives, "
                         + "which usually means one of them was redefined");
            signature = true;
        }

        return new Result(problems, signature);
    }
}
