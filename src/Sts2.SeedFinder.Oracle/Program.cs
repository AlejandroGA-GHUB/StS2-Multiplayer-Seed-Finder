using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Ancients;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Oracle;

/// <summary>
/// Differential test: loads the real sts2.dll and checks our port produces identical
/// results to the game's own code. This is the quality bar that lets the fast port be
/// trusted, and it will catch drift when the game patches.
///
/// Reflection is used throughout so this project never needs a compile-time reference
/// to the game — it degrades to a clear error if the game isn't installed.
/// </summary>
internal static class Program
{
    private static readonly string[] DefaultGameDirs =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64",
        @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64",
    };

    private static Assembly _sts2 = null!;

    private static int Main(string[] args)
    {
        var gameDir = args.FirstOrDefault(a => !a.StartsWith("--"))
                      ?? DefaultGameDirs.FirstOrDefault(Directory.Exists);

        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "sts2.dll")))
        {
            Console.Error.WriteLine("Could not find sts2.dll. Pass the game's data dir as an argument:");
            Console.Error.WriteLine(@"  dotnet run --project src\Sts2.SeedFinder.Oracle -- ""<path>\data_sts2_windows_x86_64""");
            return 1;
        }

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(gameDir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        _sts2 = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDir, "sts2.dll"));

        var versionFile = Path.Combine(gameDir, "..", "release_info.json");
        if (File.Exists(versionFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(versionFile));
            Console.WriteLine($"Game build: {doc.RootElement.GetProperty("version").GetString()} " +
                              $"({doc.RootElement.GetProperty("date").GetString()})");
        }
        Console.WriteLine();

        int failures = 0;
        failures += CheckHash();
        failures += CheckRngStream();
        failures += CheckNeowSeedAndPick();
        failures += CheckNextBool();
        failures += CheckUnstableShuffle();
        failures += CheckNeowFullOffer();
        failures += CheckGrabBag();
        failures += CheckAncientOffers();
        failures += CheckDoubleBoss();
        failures += CheckCardPools();
        failures += CheckRelicPools();
        failures += CheckShopSlotIsUndrained();
        failures += CheckFirstFightReward();
        failures += CheckNeowCardPayload();
        failures += CheckCurseFastPath();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CHECKS PASSED — the port matches the game."
            : $"{failures} CHECK(S) FAILED — the port has drifted from the game.");
        return failures == 0 ? 0 : 1;
    }

    // ---- game accessors -------------------------------------------------

    private static ulong GameHashOf(string s)
    {
        var t = _sts2.GetType("MegaCrit.Sts2.Core.Helpers.StringHelper", throwOnError: true)!;
        var m = t.GetMethod("GetDeterministicHashCode", BindingFlags.Public | BindingFlags.Static)!;
        return (ulong)m.Invoke(null, new object[] { s })!;
    }

    private static object NewGameRng(ulong seed)
    {
        var t = _sts2.GetType("MegaCrit.Sts2.Core.Random.Rng", throwOnError: true)!;
        return Activator.CreateInstance(t, new object[] { seed })!;
    }

    private static int GameNextInt(object rng, int min, int max)
    {
        var m = rng.GetType().GetMethod("NextInt", new[] { typeof(int), typeof(int) })!;
        return (int)m.Invoke(rng, new object[] { min, max })!;
    }

    private static double GameNextDouble(object rng)
    {
        var m = rng.GetType().GetMethod("NextDouble", Type.EmptyTypes)
                ?? throw new MissingMethodException("Rng.NextDouble");
        return Convert.ToDouble(m.Invoke(rng, null));
    }

    /// <summary>The game's own named-derivation constructor, <c>new Rng(seed, name)</c>.</summary>
    private static object NewGameRng(ulong seed, string name)
    {
        var t = _sts2.GetType("MegaCrit.Sts2.Core.Random.Rng", throwOnError: true)!;
        return Activator.CreateInstance(t, new object[] { seed, name })!;
    }

    /// <summary>Reads a private const or static field off a game type.</summary>
    private static float GameFloatField(string typeName, string field)
    {
        var t = _sts2.GetTypes().FirstOrDefault(x => x.Name == typeName)
                ?? throw new MissingMemberException(typeName);
        var f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new MissingFieldException(typeName, field);
        return Convert.ToSingle(f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null));
    }

    /// <summary>
    /// The <c>float</c> literals a property getter compiles to, in IL order.
    ///
    /// Needed because the ascension-gated odds are PROPERTIES, not fields:
    /// <c>RegularRareOdds => AscensionHelper.GetValueIfAscension(Scarcity, 0.0149f, 0.03f)</c>.
    /// Invoking the getter needs a live AscensionManager, so it cannot be read headless — but the
    /// two values are <c>ldc.r4</c> operands sitting in the getter's body, and those we can read.
    ///
    /// This is deliberately shallow. It asserts the NUMBERS the getter carries, not which one it
    /// returns for a given ascension; that ordering still comes from reading the source. It is
    /// enough to catch the failure that actually matters — a patch retuning the odds, which would
    /// otherwise show up only as the tool quietly getting worse.
    /// </summary>
    private static float[] GamePropertyFloatLiterals(string typeName, string property)
    {
        var t = _sts2.GetTypes().FirstOrDefault(x => x.Name == typeName)
                ?? throw new MissingMemberException(typeName);
        var p = t.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new MissingMemberException(typeName, property);

        var il = p.GetGetMethod(nonPublic: true)?.GetMethodBody()?.GetILAsByteArray()
                 ?? throw new InvalidOperationException($"{typeName}.{property} has no IL body");

        // ldc.r4 is 0x22 followed by a little-endian 4-byte float. Scanning for the opcode byte
        // is safe here only because these getters are one call with constant arguments and carry
        // no other operands that could contain a stray 0x22 — checked by the count assertion at
        // the call site, which fails loudly if a patch makes the body more complicated.
        var found = new List<float>();
        for (int i = 0; i + 4 < il.Length; i++)
            if (il[i] == 0x22) found.Add(BitConverter.ToSingle(il, i + 1));

        return found.ToArray();
    }

    /// <summary>
    /// Int literals in a property getter's IL, the sibling of
    /// <see cref="GamePropertyFloatLiterals"/>. Used for counts a relic declares as decimal
    /// vars, which reflection cannot read without constructing the model.
    ///
    /// Only the short forms are read: ldc.i4.0 through ldc.i4.8 (0x16-0x1E) and ldc.i4.s
    /// (0x1F). A count outside that range would show up as a literal we do not find, which the
    /// call site reports rather than silently accepting.
    /// </summary>
    private static int[] GamePropertyIntLiterals(string typeName, string property)
    {
        var t = _sts2.GetTypes().FirstOrDefault(x => x.Name == typeName)
                ?? throw new MissingMemberException(typeName);
        var p = t.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new MissingMemberException(typeName, property);

        var il = p.GetGetMethod(nonPublic: true)?.GetMethodBody()?.GetILAsByteArray()
                 ?? throw new InvalidOperationException($"{typeName}.{property} has no IL body");

        var found = new List<int>();
        for (int i = 0; i < il.Length; i++)
        {
            if (il[i] >= 0x16 && il[i] <= 0x1E) found.Add(il[i] - 0x16);
            else if (il[i] == 0x1F && i + 1 < il.Length) { found.Add(il[i + 1]); i++; }
        }
        return found.ToArray();
    }

    // ---- checks ---------------------------------------------------------

    private static int Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
        if (!ok || Environment.GetEnvironmentVariable("ORACLE_VERBOSE") == "1")
            Console.WriteLine($"       {detail}");
        return ok ? 0 : 1;
    }

    private static int CheckHash()
    {
        string[] samples = { "NEOW", "0P2ENNHM", "act_selection", "up_front", "rewards", "ABCDEFGHJKLM", "" };
        var bad = new List<string>();
        foreach (var s in samples)
        {
            var mine = Sts2.SeedFinder.Core.GameHash.Deterministic(s);
            var theirs = GameHashOf(s);
            if (mine != theirs) bad.Add($"\"{s}\": ours={mine} game={theirs}");
        }
        return Check("GetDeterministicHashCode (XXH64)", bad.Count == 0,
            bad.Count == 0 ? $"{samples.Length} strings matched" : string.Join("; ", bad));
    }

    private static int CheckRngStream()
    {
        // Compare bounded draws across many independent seeds — this exercises
        // splitmix64 seeding, xoshiro256** stepping, and the double-based bounding.
        var bad = new List<string>();
        for (ulong seed = 0; seed < 500; seed++)
        {
            var mineRng = new Sts2.SeedFinder.Core.Rng(seed * 2654435761uL);
            var gameRng = NewGameRng(seed * 2654435761uL);
            for (int i = 0; i < 8; i++)
            {
                int mine = mineRng.NextInt(0, 9);
                int theirs = GameNextInt(gameRng, 0, 9);
                if (mine != theirs)
                {
                    bad.Add($"seed#{seed} draw#{i}: ours={mine} game={theirs}");
                    break;
                }
            }
            if (bad.Count > 3) break;
        }
        return Check("Rng.NextInt stream (xoshiro256** + splitmix64)", bad.Count == 0,
            bad.Count == 0 ? "500 seeds x 8 draws matched" : string.Join("; ", bad));
    }

    private static int CheckNeowSeedAndPick()
    {
        // The full chain: seed string -> run seed -> Neow event seed -> curse index.
        // Rebuilds the game's own EventModel arithmetic from game primitives only.
        ulong neowIdHash = GameHashOf("NEOW");
        var bad = new List<string>();

        for (ulong i = 0; i < 2000; i++)
        {
            var seedStr = SeedCodec.FromIndex(i * 7919);
            ulong runSeed = GameHashOf(seedStr);

            for (int slot = 0; slot < 3; slot++)
            {
                ulong expectedSeed = unchecked((ulong)((long)runSeed + slot) + neowIdHash);
                ulong actualSeed = Sts2.SeedFinder.Core.Neow.NeowGenerator.RngSeed(runSeed, slot);
                if (expectedSeed != actualSeed)
                {
                    bad.Add($"{seedStr} slot{slot}: seed ours={actualSeed} expected={expectedSeed}");
                    break;
                }

                // 9 curse relics in co-op (Silver Crucible is singleplayer-only).
                var ctx = new Sts2.SeedFinder.Core.Neow.NeowContext { PlayerCount = 2 };
                var candidates = Sts2.SeedFinder.Core.Neow.NeowGenerator.CurseCandidates(ctx);
                int theirs = GameNextInt(NewGameRng(expectedSeed), 0, candidates.Count);
                var mine = Sts2.SeedFinder.Core.Neow.NeowGenerator.PredictCurse(runSeed, slot, ctx);
                if (mine != candidates[theirs])
                {
                    bad.Add($"{seedStr} slot{slot}: ours={mine} game={candidates[theirs]}");
                    break;
                }
            }
            if (bad.Count > 3) break;
        }

        return Check("Neow curse pick, full chain (2000 seeds x 3 slots)", bad.Count == 0,
            bad.Count == 0 ? "all matched" : string.Join("; ", bad));
    }

    /// <summary>
    /// GrabBag is a public generic, so we can drive the game's own instance and compare.
    /// This is the primitive act generation leans on hardest: the predicate overload RETRIES
    /// on failure, so the number of draws consumed varies with the predicate — the single
    /// easiest place to desync from the game.
    /// </summary>
    private static int CheckGrabBag()
    {
        var bagType = _sts2.GetType("MegaCrit.Sts2.Core.Helpers.GrabBag`1", throwOnError: true)!
                           .MakeGenericType(typeof(int));
        var addM = bagType.GetMethod("Add")!;
        var grabM = bagType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .First(m => m.Name == "GrabAndRemove");

        var bad = new List<string>();
        for (ulong seed = 0; seed < 150 && bad.Count < 3; seed++)
        {
            int size = 4 + (int)(seed % 9);
            var gameBag = Activator.CreateInstance(bagType)!;
            var myBag = new Sts2.SeedFinder.Core.Acts.GrabBag<int>();
            for (int i = 0; i < size; i++)
            {
                addM.Invoke(gameBag, new object[] { i, 1.0 });
                myBag.Add(i, 1.0);
            }

            var gameRng = NewGameRng(seed * 2246822519uL + 11);
            var myRng = new Sts2.SeedFinder.Core.Rng(seed * 2246822519uL + 11);

            // Drain with an "avoid the previous value" predicate — the same shape act
            // generation uses for tag avoidance, so the retry loop is exercised.
            int gameLast = -1, myLast = -1;
            for (int step = 0; step < size && bad.Count < 3; step++)
            {
                int capturedGame = gameLast;
                Func<int, bool> gamePred = v => v != capturedGame;
                var g = grabM.Invoke(gameBag, new object[] { gameRng, gamePred });

                int capturedMine = myLast;
                var m = myBag.GrabAndRemove(myRng, v => v != capturedMine);

                if (!Equals(g, m))
                {
                    bad.Add($"size={size} seed#{seed} step{step}: ours={m} game={g}");
                    break;
                }
                if (g is int gi) { gameLast = gi; myLast = gi; }
            }
        }
        return Check("GrabBag weighted draw + predicate retry", bad.Count == 0,
            bad.Count == 0 ? "150 bags drained, draw-for-draw" : string.Join(" | ", bad));
    }

    private static bool GameNextBool(object rng) =>
        (bool)rng.GetType().GetMethod("NextBool", Type.EmptyTypes)!.Invoke(rng, null)!;

    private static int CheckNextBool()
    {
        // Neow's three coin flips use Rng.NextBool, which is Next(2)==0 rather than a
        // top-bit test — an easy thing to get subtly wrong.
        var bad = new List<string>();
        for (ulong seed = 0; seed < 400 && bad.Count < 3; seed++)
        {
            var mine = new Sts2.SeedFinder.Core.Rng(seed * 40503uL + 7);
            var game = NewGameRng(seed * 40503uL + 7);
            for (int i = 0; i < 6; i++)
            {
                bool a = mine.NextBool(), b = GameNextBool(game);
                if (a != b) { bad.Add($"seed#{seed} flip#{i}: ours={a} game={b}"); break; }
            }
        }
        return Check("Rng.NextBool (Neow coin flips)", bad.Count == 0,
            bad.Count == 0 ? "400 seeds x 6 flips matched" : string.Join("; ", bad));
    }

    /// <summary>
    /// ListExtensions.UnstableShuffle is a public static generic, so we can call the game's
    /// own implementation directly — this verifies our shuffle exactly, not just by inference.
    /// </summary>
    private static List<int> GameShuffle(List<int> items, object rng)
    {
        var ext = _sts2.GetType("MegaCrit.Sts2.Core.Extensions.ListExtensions", throwOnError: true)!;
        var m = ext.GetMethods(BindingFlags.Public | BindingFlags.Static)
                   .First(x => x.Name == "UnstableShuffle" && x.GetParameters().Length == 2)
                   .MakeGenericMethod(typeof(int));
        return (List<int>)m.Invoke(null, new object[] { items, rng })!;
    }

    private static void MyShuffle<T>(List<T> list, Sts2.SeedFinder.Core.Rng rng)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }

    /// <summary>
    /// The Ascension 10 second-boss draw, against the game's own <c>Rng.NextItem</c>.
    ///
    /// <c>RunManager.cs:731</c> is
    /// <c>UpFront.NextItem(act.AllBossEncounters.Where(e => e.Id != act.BossEncounter.Id))</c>,
    /// so what matters is that we filter first and draw from the survivors in their original
    /// order. Feeding NextItem a filtered LINQ sequence rather than a list is the part worth
    /// pinning: it re-materialises internally, and a different order here would silently pick
    /// the other boss every time.
    /// </summary>
    /// <summary>
    /// Every card in our generated pools, checked against the class the game actually ships.
    ///
    /// This is the check that matters most for card rewards, because `CardPoolData` is scraped
    /// from source by a Python script and a misread rarity would be invisible everywhere else:
    /// the reward would still be produced, just with the wrong card in it. Constructing the
    /// model is enough to ask it — `CardModel`, like `RelicModel`, needs no ModelDb and no
    /// engine.
    /// </summary>
    /// <summary>
    /// The search's one-draw curse shortcut, checked against building the whole offer.
    ///
    /// This is a check on an OPTIMISATION rather than on the game: `SeedSearcher` settles a
    /// curse-branch relic from Neow's first draw alone instead of generating the full offer,
    /// which is only sound because Neow's curse and positive pools share no relic. Both halves
    /// are asserted here — the disjointness, and then that the shortcut and the full offer
    /// agree for every curse relic across every slot.
    ///
    /// Cheap to run and easy to get wrong later: adding a relic to both pools, or reordering
    /// CurseOptions, would break the shortcut while every game-facing check still passed.
    /// </summary>
    private static int CheckCurseFastPath()
    {
        var bad = new List<string>();

        // 1. The premise: no curse relic is reachable as a positive.
        var positives = NeowRelics.Positives.Concat(NeowRelics.CoinFlips).ToList();
        foreach (var curse in NeowRelics.Curses)
            if (positives.Any(p => p.Slug == curse.Slug))
                bad.Add($"{curse.Name} is in both the curse and positive pools");

        // 2. The shortcut itself, for every curse relic and player count that can occur.
        foreach (int players in (int[])[2, 3, 4])
        {
            var ctx = new NeowContext { PlayerCount = players };
            var candidates = NeowGenerator.CurseCandidates(ctx).ToList();

            for (int index = 0; index < candidates.Count && bad.Count < 5; index++)
            {
                var relic = candidates[index];
                for (ulong s = 0; s < 400 && bad.Count < 5; s++)
                {
                    ulong runSeed = s * 6364136223846793005uL + 1442695040888963407uL;
                    for (int slot = 0; slot < players; slot++)
                    {
                        bool fast = new Sts2.SeedFinder.Core.Rng(NeowGenerator.RngSeed(runSeed, slot))
                            .NextInt(0, candidates.Count) == index;

                        var offer = NeowGenerator.PredictOffer(runSeed, slot, ctx);
                        bool full = offer.Curse == relic || offer.Positive1 == relic || offer.Positive2 == relic;

                        if (fast != full)
                            bad.Add($"{relic.Name} {players}p slot{slot} seed#{s}: fast={fast} full={full}");
                    }
                }
            }
        }

        return Check("Curse fast path == full offer (search optimisation)", bad.Count == 0,
            bad.Count == 0
                ? "9 curse relics x 3 lobby sizes x 400 seeds x every slot agreed, pools disjoint"
                : string.Join(" | ", bad));
    }

    private static int CheckCardPools()
    {
        var cardModel = _sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel", throwOnError: true)!;
        var byName = _sts2.GetTypes()
            .Where(t => !t.IsAbstract && cardModel.IsAssignableFrom(t))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());

        var bad = new List<string>();
        int checkedCount = 0;

        // The five pools overlap heavily, so check each distinct card once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in Sts2.SeedFinder.Core.Cards.CardCatalog.Characters)
        foreach (var entry in Sts2.SeedFinder.Core.Cards.CardPoolData.For(character))
        {
            if (!seen.Add(entry.TypeName)) continue;

            if (!byName.TryGetValue(entry.TypeName, out var type))
            {
                bad.Add($"{entry.TypeName}: no such class in the assembly");
                continue;
            }

            object model;
            try { model = Activator.CreateInstance(type)!; }
            catch (Exception ex) { bad.Add($"{entry.TypeName}: {(ex.InnerException ?? ex).GetType().Name}"); continue; }

            checkedCount++;

            var rarity = type.GetProperty("Rarity")?.GetValue(model)?.ToString();
            if (rarity != entry.Rarity.ToString())
                bad.Add($"{entry.TypeName}: rarity ours={entry.Rarity} game={rarity}");

            // Ours calls it Mode; the game calls it MultiplayerConstraint. Same three values.
            var mode = type.GetProperty("MultiplayerConstraint")?.GetValue(model)?.ToString();
            if (mode != entry.Mode.ToString())
                bad.Add($"{entry.TypeName}: multiplayer ours={entry.Mode} game={mode}");

            if (bad.Count >= 5) break;
        }

        return Check("Card pool rarities + MP constraints vs assembly", bad.Count == 0,
            bad.Count == 0
                ? $"{checkedCount} distinct cards across 5 characters matched"
                : string.Join(" | ", bad));
    }

    /// <summary>
    /// Checks the relic table the same way as the cards: construct the game's class for every
    /// relic we list and compare the rarity we recorded.
    ///
    /// This matters most for RelicRarity.Shop, because that column decides two different things
    /// — how many draws each bag's shuffle costs, and which relics can reach a shop's third
    /// slot. The table is scraped from source by a Python regex, so a misread rarity would be
    /// quietly wrong rather than obviously broken.
    ///
    /// It also asserts the two facts the shop prediction leans on, both readable headless:
    /// the set of rarities a player's bag keeps, and that every Shop relic passes the
    /// IsAllowedInShops filter the merchant applies (so that filter never reorders the deque).
    ///
    /// What it cannot check headless is pool MEMBERSHIP and ORDER: RelicPoolModel.AllRelics
    /// calls ModelDb.Relic&lt;T&gt;(), which needs a populated model db. Bag sizes are covered by
    /// --verify against a run save, and the resulting order by --verify-history, which compares
    /// each shop's third relic against what the game actually offered.
    /// </summary>
    private static int CheckRelicPools()
    {
        var relicModel = _sts2.GetType("MegaCrit.Sts2.Core.Models.RelicModel", throwOnError: true)!;
        var byName = _sts2.GetTypes()
            .Where(t => !t.IsAbstract && relicModel.IsAssignableFrom(t))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());

        var bad = new List<string>();
        int checkedCount = 0;

        var all = Sts2.SeedFinder.Core.Acts.RelicPoolData.SharedRelics
            .Concat(Enum.GetValues<Sts2.SeedFinder.Core.Acts.Character>()
                .SelectMany(Sts2.SeedFinder.Core.Acts.RelicPoolData.RelicsFor));

        foreach (var entry in all)
        {
            if (!byName.TryGetValue(entry.Name, out var type))
            {
                bad.Add($"{entry.Name}: no such class in the assembly");
                if (bad.Count >= 5) break;
                continue;
            }

            object model;
            try { model = Activator.CreateInstance(type)!; }
            catch (Exception ex) { bad.Add($"{entry.Name}: {(ex.InnerException ?? ex).GetType().Name}"); continue; }

            checkedCount++;

            var rarity = type.GetProperty("Rarity")?.GetValue(model)?.ToString();
            if (rarity != entry.Rarity)
                bad.Add($"{entry.Name}: rarity ours={entry.Rarity} game={rarity}");

            // The merchant fills its third slot with `r => r.IsAllowedInShops`. If any Shop
            // relic failed that, PullFromBack would skip past it and our "back of the deque"
            // answer would be off by one.
            if (entry.Rarity == "Shop"
                && type.GetProperty("IsAllowedInShops")?.GetValue(model) is bool allowed && !allowed)
                bad.Add($"{entry.Name}: Shop rarity but IsAllowedInShops is false");

            if (bad.Count >= 5) break;
        }

        // RelicGrabBag._rarities — which deques a player's bag keeps at all.
        var grabBag = _sts2.GetTypes().FirstOrDefault(t => t.Name == "RelicGrabBag");
        var raritiesField = grabBag?.GetField("_rarities",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        if (raritiesField?.GetValue(null) is System.Collections.IEnumerable set)
        {
            var gameRarities = set.Cast<object>().Select(o => o.ToString()!).ToHashSet(StringComparer.Ordinal);
            var ours = Sts2.SeedFinder.Core.Acts.RelicPoolData.GrabBagRarities.ToHashSet(StringComparer.Ordinal);
            if (!gameRarities.SetEquals(ours))
                bad.Add($"grab-bag rarities ours=[{string.Join(",", ours)}] game=[{string.Join(",", gameRarities)}]");
        }
        else
        {
            bad.Add("could not read RelicGrabBag._rarities");
        }

        return Check("Relic pool rarities + shop filter vs assembly", bad.Count == 0,
            bad.Count == 0
                ? $"{checkedCount} relics across 6 pools matched, grab-bag rarities agree"
                : string.Join(" | ", bad));
    }

    /// <summary>
    /// The load-bearing claim behind searching a shop's third slot: nothing except a shop ever
    /// takes a Shop-rarity relic, so that deque is untouched from generation until the first
    /// merchant, and the Nth shop simply gets the Nth entry from the back.
    ///
    /// Every other relic source in the game routes through <c>RelicFactory.RollRarity</c>, so
    /// this runs the GAME's own RollRarity over a wide spread of streams and asserts Shop never
    /// comes out of it. That is the whole argument, tested rather than read.
    /// </summary>
    private static int CheckShopSlotIsUndrained()
    {
        var factory = _sts2.GetTypes().FirstOrDefault(t => t.Name == "RelicFactory");
        var rngType = _sts2.GetType("MegaCrit.Sts2.Core.Random.Rng", throwOnError: true)!;
        var roll = factory?.GetMethod("RollRarity", BindingFlags.Public | BindingFlags.Static,
                                      new[] { rngType });
        if (roll is null)
            return Check("Shop rarity is never rolled (RelicFactory.RollRarity)", false,
                "could not find RelicFactory.RollRarity(Rng)");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 0; seed < 200; seed++)
        {
            var rng = NewGameRng(seed, "rewards");
            for (int i = 0; i < 250; i++)
                seen.Add(roll.Invoke(null, new[] { rng })!.ToString()!);
        }

        bool ok = !seen.Contains("Shop") && seen.Count > 0;
        return Check("Shop rarity is never rolled (RelicFactory.RollRarity)", ok,
            ok ? $"50,000 rolls produced only {string.Join(", ", seen.OrderBy(s => s))}"
               : $"rolled {string.Join(", ", seen.OrderBy(s => s))} — a non-shop source can drain the Shop deque");
    }

    /// <summary>
    /// Replays the first fight's reward using the GAME's Rng object and its own odds constants,
    /// and checks our predictor lands on the same three cards.
    ///
    /// What this proves: the per-player stream derivation (`Rng(runSeed + slot, "rewards")`
    /// built by the game's own named-derivation constructor), the draw ORDER and COUNT — the
    /// potion roll happening before anything is populated, the gold draw, the two conditional
    /// potion draws, and three card draws of exactly three each — and the pity arithmetic.
    ///
    /// What it does not prove: that this sequence is the one `RewardsSet` performs. That came
    /// from reading the game's source, and no headless check can reach it, because building a
    /// real reward needs a live RunState. A wrong reading would agree with itself here. The
    /// end-to-end check is a played run.
    /// </summary>
    private static int CheckFirstFightReward()
    {
        var bad = new List<string>();

        // The constants first: a patch retuning any of these changes every prediction, and it
        // would otherwise show up only as "the tool got worse".
        var constants = new (string Type, string Field, float Ours)[]
        {
            ("CardRarityOdds", "regularUncommonOdds", 0.37f),
            ("CardRarityOdds", "_baseRarityOffset", -0.05f),
            ("CardRarityOdds", "_maxRarityOffset", 0.4f),
            ("PotionRewardOdds", "_basePotionRewardOdds", 0.4f),
        };
        foreach (var (type, field, ours) in constants)
        {
            var theirs = GameFloatField(type, field);
            if (Math.Abs(theirs - ours) > 1e-6f) bad.Add($"{type}.{field}: ours={ours} game={theirs}");
        }

        // The two ascension-gated ones, read out of the getter's IL because the property itself
        // needs a live AscensionManager. These were left unchecked while the only card feature
        // was the first fight, which can never roll a Rare so never consulted them. Fight 2 can,
        // so they are load-bearing now.
        //
        // Each getter is GetValueIfAscension(Scarcity, <at A7+>, <below>), so the literals come
        // out in that order.
        var gated = new (string Property, float AtScarcity, float Below)[]
        {
            ("RegularRareOdds", 0.0149f, 0.03f),
            ("RarityGrowth", 0.005f, 0.01f),
        };
        foreach (var (property, atScarcity, below) in gated)
        {
            var literals = GamePropertyFloatLiterals("CardRarityOdds", property);
            if (literals.Length != 2)
            {
                bad.Add($"CardRarityOdds.{property}: expected 2 float literals in the getter, "
                        + $"found {literals.Length} — the property is no longer a plain "
                        + "GetValueIfAscension call and needs re-reading");
                continue;
            }
            if (Math.Abs(literals[0] - atScarcity) > 1e-6f || Math.Abs(literals[1] - below) > 1e-6f)
                bad.Add($"CardRarityOdds.{property}: ours=({atScarcity}, {below}) "
                        + $"game=({literals[0]}, {literals[1]})");
        }

        const float RareOdds = 0.03f;      // CardRarityOdds.RegularRareOdds below Ascension 7
        const float Growth = 0.01f;        // CardRarityOdds.RarityGrowth, same gate

        foreach (var character in Sts2.SeedFinder.Core.Cards.CardCatalog.Characters)
        {
            var pool = Sts2.SeedFinder.Core.Cards.CardRewardGenerator.PoolFor(character);

            for (ulong seed = 0; seed < 120 && bad.Count < 5; seed++)
            {
                ulong runSeed = seed * 6364136223846793005uL + 1442695040888963407uL;

                for (int slot = 0; slot < 2; slot++)
                {
                    // The game's own two-argument constructor, so the derivation is under test
                    // rather than assumed.
                    var rng = NewGameRng(unchecked(runSeed + (ulong)slot), "rewards");

                    bool hasPotion = GameNextFloat(rng) < 0.4f;
                    GameNextFloat(rng);                                    // GoldReward
                    if (hasPotion) { GameNextFloat(rng); GameNextFloat(rng); }

                    var taken = new List<string>(3);
                    var theirs = new List<string>(3);
                    float offset = -0.05f;

                    for (int i = 0; i < 3; i++)
                    {
                        var available = pool.Where(c => !taken.Contains(c.TypeName)).ToList();

                        float roll = GameNextFloat(rng);
                        float rareAt = RareOdds + offset;
                        var rarity = roll < rareAt ? Sts2.SeedFinder.Core.Cards.CardRarity.Rare
                                   : roll < 0.37f + rareAt ? Sts2.SeedFinder.Core.Cards.CardRarity.Uncommon
                                   : Sts2.SeedFinder.Core.Cards.CardRarity.Common;
                        offset = rarity == Sts2.SeedFinder.Core.Cards.CardRarity.Rare
                            ? -0.05f
                            : Math.Min(offset + Growth, 0.4f);

                        var candidates = available.Where(c => c.Rarity == rarity).ToList();
                        var picked = (Sts2.SeedFinder.Core.Cards.CardEntry)GameNextItem(rng, candidates)!;
                        GameNextFloat(rng);                                // upgrade roll

                        theirs.Add(picked.TypeName);
                        taken.Add(picked.TypeName);
                    }

                    var mine = Sts2.SeedFinder.Core.Cards.CardRewardGenerator
                        .FirstFight(runSeed, slot, character);

                    if (mine.HasPotion != hasPotion)
                        bad.Add($"{character} seed#{seed} P{slot + 1}: potion ours={mine.HasPotion} game={hasPotion}");
                    else if (!mine.Cards.Select(c => c.TypeName).SequenceEqual(theirs))
                        bad.Add($"{character} seed#{seed} P{slot + 1}: " +
                                $"ours=[{string.Join(",", mine.Cards.Select(c => c.TypeName))}] " +
                                $"game=[{string.Join(",", theirs)}]");
                }
            }
        }

        return Check("First fight card reward, draw-for-draw vs game Rng", bad.Count == 0,
            bad.Count == 0
                ? "5 characters x 120 seeds x 2 slots matched, and 4 odds constants agree "
                  + "(RegularRareOdds / RarityGrowth are ascension-gated properties, not readable headless)"
                : string.Join(" | ", bad));
    }

    /// <summary>
    /// Holds <c>NeowCardPayload</c> to the game, on the two things that can make it wrong.
    ///
    /// FIRST, the draw sequence, replayed with the game's own <c>Rng</c>. The whole claim of a
    /// payload is what it does NOT draw: <c>CreateForReward</c> branches on Uniform odds and
    /// never reaches <c>RollForRarity</c>, and <c>NoUpgradeRoll</c> removes the other roll, so
    /// each card is one <c>NextItem</c> and nothing else. If that reading were wrong this check
    /// diverges on the first card of the first seed, because our picks would be reading the
    /// wrong position of the stream.
    ///
    /// SECOND, how many cards each relic hands out, read off the game's own
    /// <c>CanonicalVars</c>. That number is a balance lever rather than a mechanism, so it is
    /// exactly the kind of thing a patch retunes quietly. It reaches the IL rather than
    /// constructing the relic because a <c>CardsVar</c> is built from a decimal literal, which
    /// is the one shape reflection cannot read without a live model.
    ///
    /// What no headless check can reach is the branch itself: the game's factory needs a live
    /// Player. So this proves our chain consumes the stream exactly as the game's Rng hands it
    /// over, not that <c>CreateForReward</c> still takes the Uniform path. A played run is the
    /// final authority there, as it is for Neow's two positive options.
    /// </summary>
    private static int CheckNeowCardPayload()
    {
        var bad = new List<string>();

        // CanonicalVars is `new CardsVar(N)` for both relics, and a decimal literal small
        // enough to fit an int compiles to `ldc.i4 N; newobj decimal(int)`. Anything else in
        // that getter would break the count assertion below rather than pass quietly.
        foreach (var slug in Sts2.SeedFinder.Core.Cards.NeowCardPayload.Predictable)
        {
            var relic = Sts2.SeedFinder.Core.Neow.NeowRelics.Find(slug)!;
            string typeName = relic.Name.Replace("'", "").Replace(" ", "");
            int ours = Sts2.SeedFinder.Core.Cards.NeowCardPayload.CardCount(slug);

            var literals = GamePropertyIntLiterals(typeName, "CanonicalVars");
            if (literals.Length != 1)
            {
                bad.Add($"{typeName}.CanonicalVars: expected one int literal (the card count), "
                        + $"found {literals.Length}. The relic's vars are no longer a single "
                        + "CardsVar and need re-reading.");
                continue;
            }
            if (literals[0] != ours)
                bad.Add($"{typeName} hands out {literals[0]} cards, we predict {ours}");
        }

        foreach (var character in Sts2.SeedFinder.Core.Cards.CardCatalog.Characters)
        {
            var pool = Sts2.SeedFinder.Core.Cards.CardRewardGenerator.PoolFor(character);
            var rares = pool.Where(c => c.Rarity == Sts2.SeedFinder.Core.Cards.CardRarity.Rare).ToList();

            foreach (var slug in Sts2.SeedFinder.Core.Cards.NeowCardPayload.Predictable)
            {
                int count = Sts2.SeedFinder.Core.Cards.NeowCardPayload.CardCount(slug);

                for (ulong seed = 0; seed < 80 && bad.Count < 5; seed++)
                {
                    ulong runSeed = seed * 6364136223846793005uL + 1442695040888963407uL;

                    for (int slot = 0; slot < 2; slot++)
                    {
                        // The payload is the FIRST thing on this stream, so the game's Rng is
                        // used from its very first draw. No potion roll, no gold, no rarity and
                        // no upgrade: that absence is the thing under test.
                        var rng = NewGameRng(unchecked(runSeed + (ulong)slot), "rewards");

                        var theirs = new List<string>(count);
                        for (int i = 0; i < count; i++)
                        {
                            var candidates = rares.Where(c => !theirs.Contains(c.TypeName)).ToList();
                            var picked = (Sts2.SeedFinder.Core.Cards.CardEntry)GameNextItem(rng, candidates)!;
                            theirs.Add(picked.TypeName);
                        }

                        var mine = Sts2.SeedFinder.Core.Cards.NeowCardPayload
                            .Generate(runSeed, slot, character, slug)
                            .Select(c => c.TypeName)
                            .ToList();

                        if (!mine.SequenceEqual(theirs))
                            bad.Add($"{character} {slug} seed#{seed} P{slot + 1}: "
                                    + $"ours=[{string.Join(",", mine)}] game=[{string.Join(",", theirs)}]");
                    }
                }
            }
        }

        return Check("Neow card payloads, draw-for-draw vs game Rng", bad.Count == 0,
            bad.Count == 0
                ? "Arcane Scroll and Hefty Tablet: 5 characters x 80 seeds x 2 slots matched, "
                  + "and both card counts agree with the game's own CanonicalVars"
                : string.Join(" | ", bad));
    }

    private static int CheckDoubleBoss()
    {
        var bad = new List<string>();

        foreach (var act in Sts2.SeedFinder.Core.Acts.ActData.All)
        {
            var bosses = act.Bosses.Select(b => b.Name).ToList();
            for (ulong seed = 0; seed < 60 && bad.Count < 3; seed++)
            {
                ulong s = seed * 2654435761uL + (ulong)act.Name.Length;

                // The already-drawn boss rotates through the act's list, so the filter is
                // exercised at every position rather than only the first.
                var first = bosses[(int)(seed % (ulong)bosses.Count)];
                var others = bosses.Where(b => b != first).ToList();

                var mine = others[new Sts2.SeedFinder.Core.Rng(s).NextInt(0, others.Count)];
                var theirs = (string)GameNextItem(NewGameRng(s), bosses.Where(b => b != first))!;

                if (mine != theirs)
                    bad.Add($"{act.Name} after {first} seed#{seed}: ours={mine} game={theirs}");
            }
        }

        return Check("Double Boss second draw vs game Rng.NextItem", bad.Count == 0,
            bad.Count == 0
                ? $"{Sts2.SeedFinder.Core.Acts.ActData.All.Length} acts x 60 seeds x every first boss matched"
                : string.Join(" | ", bad));
    }

    private static object? GameNextItem(object rng, IEnumerable<string> items) =>
        GameNextItem<string>(rng, items);

    private static object? GameNextItem<T>(object rng, IEnumerable<T> items)
    {
        var m = rng.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                   .First(x => x.Name == "NextItem" && x.GetParameters().Length == 1)
                   .MakeGenericMethod(typeof(T));
        return m.Invoke(rng, new object[] { items });
    }

    private static int CheckUnstableShuffle()
    {
        var bad = new List<string>();
        // Cover the pool sizes Neow actually produces (roughly 12-17 positives).
        foreach (int size in new[] { 2, 3, 9, 12, 13, 14, 15, 16, 17, 30 })
        {
            for (ulong seed = 0; seed < 40 && bad.Count < 3; seed++)
            {
                var mine = Enumerable.Range(0, size).ToList();
                var theirs = Enumerable.Range(0, size).ToList();
                MyShuffle(mine, new Sts2.SeedFinder.Core.Rng(seed * 6364136223846793005uL + (ulong)size));
                GameShuffle(theirs, NewGameRng(seed * 6364136223846793005uL + (ulong)size));
                if (!mine.SequenceEqual(theirs))
                    bad.Add($"size={size} seed#{seed}: ours=[{string.Join(",", mine)}] game=[{string.Join(",", theirs)}]");
            }
        }
        return Check("ListExtensions.UnstableShuffle", bad.Count == 0,
            bad.Count == 0 ? "10 sizes x 40 seeds matched" : string.Join(" | ", bad));
    }

    /// <summary>
    /// Replays Neow.GenerateInitialOptions' exact draw sequence using the GAME's Rng object,
    /// and checks our predictor lands on the same offer.
    ///
    /// Caveat worth stating plainly: this proves our RNG consumption and ordering match, but
    /// the algorithm itself is transcribed from decompiled source rather than executed — the
    /// game's own Neow needs a live Player/RunState we cannot build headless. A live co-op run
    /// is still the final authority on the two positive options.
    /// </summary>
    private static int CheckNeowFullOffer()
    {
        var ctx = new Sts2.SeedFinder.Core.Neow.NeowContext { PlayerCount = 2 };
        var curses = Sts2.SeedFinder.Core.Neow.NeowGenerator.CurseCandidates(ctx);
        var bad = new List<string>();

        for (ulong i = 0; i < 1500 && bad.Count < 3; i++)
        {
            var seedStr = SeedCodec.FromIndex(i * 104729);
            ulong runSeed = GameHashOf(seedStr);

            for (int slot = 0; slot < 2 && bad.Count < 3; slot++)
            {
                var gameRng = NewGameRng(Sts2.SeedFinder.Core.Neow.NeowGenerator.RngSeed(runSeed, slot));

                // Replay the sequence with the game's RNG.
                var curse = curses[GameNextInt(gameRng, 0, curses.Count)];
                var positives = Sts2.SeedFinder.Core.Neow.NeowRelics.Positives.ToList();
                switch (curse.Slug)
                {
                    case "cursed_pearl":      positives.RemoveAll(r => r.Slug == "golden_pearl"); break;
                    case "hefty_tablet":      positives.RemoveAll(r => r.Slug == "arcane_scroll"); break;
                    case "leafy_poultice":    positives.RemoveAll(r => r.Slug == "new_leaf"); break;
                    case "precarious_shears": positives.RemoveAll(r => r.Slug == "precise_scissors"); break;
                    case "neows_sacrifice":
                        positives.RemoveAll(r => r.Slug is "phial_holster" or "lost_coffer");
                        break;
                }
                if (curse.Slug != "large_capsule")
                    positives.Add(GameNextBool(gameRng)
                        ? Sts2.SeedFinder.Core.Neow.NeowRelics.LavaRock
                        : Sts2.SeedFinder.Core.Neow.NeowRelics.SmallCapsule);
                positives.Add(GameNextBool(gameRng)
                    ? Sts2.SeedFinder.Core.Neow.NeowRelics.NutritiousOyster
                    : Sts2.SeedFinder.Core.Neow.NeowRelics.StoneHumidifier);
                positives.Add(GameNextBool(gameRng)
                    ? Sts2.SeedFinder.Core.Neow.NeowRelics.NeowsTalisman
                    : Sts2.SeedFinder.Core.Neow.NeowRelics.Pomander);
                positives.RemoveAll(r => !ctx.IsAllowed(r));

                var idx = Enumerable.Range(0, positives.Count).ToList();
                GameShuffle(idx, gameRng);
                var expected = new[] { positives[idx[0]], positives[idx[1]] };

                var mine = Sts2.SeedFinder.Core.Neow.NeowGenerator.PredictOffer(runSeed, slot, ctx);
                if (mine.Curse != curse || mine.Positive1 != expected[0] || mine.Positive2 != expected[1])
                    bad.Add($"{seedStr} slot{slot}: ours=[{mine}] expected=[{expected[0]} | {expected[1]} | {curse}]");
            }
        }

        return Check("Neow full offer, draw-for-draw vs game Rng (1500 seeds x 2 slots)", bad.Count == 0,
            bad.Count == 0 ? "all matched" : string.Join(" | ", bad));
    }

    // NextFloat is declared as NextFloat(float max = 1f) — there is no zero-arg overload.
    private static float GameNextFloat(object rng) =>
        (float)rng.GetType().GetMethod("NextFloat", new[] { typeof(float) })!.Invoke(rng, new object[] { 1f })!;

    private static List<string> GameShuffleStrings(List<string> items, object rng)
    {
        var ext = _sts2.GetType("MegaCrit.Sts2.Core.Extensions.ListExtensions", throwOnError: true)!;
        var m = ext.GetMethods(BindingFlags.Public | BindingFlags.Static)
                   .First(x => x.Name == "UnstableShuffle" && x.GetParameters().Length == 2)
                   .MakeGenericMethod(typeof(string));
        return (List<string>)m.Invoke(null, new object[] { items, rng })!;
    }

    /// <summary>
    /// Replays each Ancient's GenerateInitialOptions using the GAME's Rng object — its
    /// NextInt, NextBool, NextFloat and UnstableShuffle — and checks our predictor agrees.
    ///
    /// Same caveat as the Neow check: this proves the RNG arithmetic, draw counts and draw
    /// ORDER are right, which is where ports actually break. It does not independently prove
    /// the pool contents, since both sides read the same extracted tables. Meeting an Ancient
    /// in a real run remains the authority on those.
    /// </summary>
    private static int CheckAncientOffers()
    {
        var bad = new List<string>();
        var ctx = new AncientContext();

        for (ulong i = 0; i < 400 && bad.Count < 4; i++)
        {
            var seedStr = SeedCodec.FromIndex(i * 92821);
            ulong runSeed = GameHashOf(seedStr);

            foreach (Ancient ancient in Enum.GetValues<Ancient>())
            {
                for (int slot = 0; slot < 2 && bad.Count < 4; slot++)
                {
                    var g = NewGameRng(AncientOffers.RngSeed(runSeed, slot, ancient));
                    var expected = ReplayWithGameRng(ancient, g, ctx);
                    var mine = AncientOffers.Predict(ancient, runSeed, slot, ctx).Options;

                    if (!mine.SequenceEqual(expected))
                        bad.Add($"{ancient} {seedStr} slot{slot}: ours=[{string.Join(",", mine)}] game=[{string.Join(",", expected)}]");
                }
            }
        }

        return Check($"Ancient offers, draw-for-draw vs game Rng (400 seeds x {Enum.GetValues<Ancient>().Length} ancients x 2 slots)",
            bad.Count == 0, bad.Count == 0 ? "all matched" : string.Join(" | ", bad));
    }

    /// <summary>The same draw sequences as AncientOffers, but driven by the game's own Rng.</summary>
    private static List<string> ReplayWithGameRng(Ancient ancient, object g, AncientContext ctx)
    {
        switch (ancient)
        {
            case Ancient.Vakuu:
            {
                var p1 = GameShuffleStrings(AncientData.VakuuPool1.ToList(), g);
                var p2 = GameShuffleStrings(AncientData.VakuuPool2.ToList(), g);
                var p3 = GameShuffleStrings(AncientData.VakuuPool3.ToList(), g);
                return new List<string> { p1[0], p2[0], p3[0] };
            }
            case Ancient.Tanx:
            {
                var pool = AncientData.TanxBaseOptionPool.ToList();
                if (ctx.DeckHasThreeInstinctCards) pool.Add(AncientData.TanxTriBoomerangOption[0]);
                return GameShuffleStrings(pool, g).Take(3).ToList();
            }
            case Ancient.Nonupeipe:
            {
                var pool = AncientData.NonupeipeOptionPool.ToList();
                if (ctx.DeckHasFourSwiftCards) pool.Add(AncientData.NonupeipeBeautifulBraceletEventOption[0]);
                return GameShuffleStrings(pool, g).Take(3).ToList();
            }
            case Ancient.Tezcatara:
            {
                var p1 = AncientData.TezcataraOptionPool1.ToList();
                if (ctx.DeckHasBasicStrike) p1.Add(AncientData.TezcataraNutritiousSoupOption[0]);
                return new List<string>
                {
                    p1[GameNextInt(g, 0, p1.Count)],
                    AncientData.TezcataraOptionPool2[GameNextInt(g, 0, AncientData.TezcataraOptionPool2.Length)],
                    AncientData.TezcataraOptionPool3[GameNextInt(g, 0, AncientData.TezcataraOptionPool3.Length)],
                };
            }
            case Ancient.Pael:
            {
                var first = AncientData.PaelOptionPool1[GameNextInt(g, 0, AncientData.PaelOptionPool1.Length)];
                var pool2 = AncientData.PaelOptionPool2.ToList();
                if (ctx.DeckHasThreeGoopyCards) pool2.Add(AncientData.PaelPaelsClawOption[0]);
                if (ctx.DeckHasFiveRemovableCards) pool2.Add(AncientData.PaelPaelsToothOption[0]);
                pool2.AddRange(pool2.ToList());
                pool2.Add(AncientData.PaelPaelsGrowthOption[0]);
                var second = pool2[GameNextInt(g, 0, pool2.Count)];
                var pool3 = AncientData.PaelOptionPool3.ToList();
                if (!ctx.HasEventPet) pool3.Add(AncientData.PaelPaelsLegionOption[0]);
                var third = pool3[GameNextInt(g, 0, pool3.Count)];
                return new List<string> { first, second, third };
            }
            case Ancient.Orobas:
            {
                int others = Math.Max(0, ctx.UnlockedCharacterCount - 1);
                if (others > 0) GameNextInt(g, 0, others);
                var pool1 = AncientData.OrobasOptionPool1.ToList();
                pool1.Add(GameNextFloat(g) < 0.3333333f ? AncientData.OrobasPrismaticGemOption[0] : "SeaGlass");
                var pool3 = new List<string> { "TouchOfOrobas" };
                if (ctx.ArchaicToothAvailable) pool3.Add("ArchaicTooth");
                return new List<string>
                {
                    pool1[GameNextInt(g, 0, pool1.Count)],
                    AncientData.OrobasOptionPool2[GameNextInt(g, 0, AncientData.OrobasOptionPool2.Length)],
                    pool3[GameNextInt(g, 0, pool3.Count)],
                };
            }
            case Ancient.Darv:
            {
                var picks = new List<string>();
                foreach (var (gate, relics) in AncientData.DarvRelicSets)
                {
                    bool allowed = gate switch
                    {
                        DarvGate.Always => true,
                        DarvGate.DeckNotCleared => !ctx.DeckClearedByModifier,
                        DarvGate.Act2Only => ctx.ActIndex == 1,
                        DarvGate.Act2OrLater => ctx.ActIndex >= 1,
                        _ => true,
                    };
                    if (allowed) picks.Add(relics[GameNextInt(g, 0, relics.Length)]);
                }
                picks = GameShuffleStrings(picks, g);
                if (GameNextBool(g))
                {
                    var withTome = picks.Take(2).ToList();
                    withTome.Add(AncientData.DarvBonusRelic);
                    return withTome;
                }
                return picks.Take(3).ToList();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ancient));
        }
    }
}
