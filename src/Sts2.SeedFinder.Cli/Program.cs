using System.Diagnostics;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Ancients;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Install;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Cli;

internal static class Program
{
    /// <summary>
    /// The game build results are stamped with: the one this checkout was last CONFIRMED against,
    /// not the one that last changed the data tables.
    ///
    /// Those two drift apart routinely. A patch that leaves every pool untouched still moves what
    /// we are verified against, and refresh reports "unchanged" and rewrites nothing, so a
    /// constant here would keep naming an older version indefinitely. Reading the baseline instead
    /// means repair.bat's "record your game version as verified" updates this too, and a stamp can
    /// never claim a build the checkout was never checked against.
    /// </summary>
    private static string GameVersion => VerifiedBuild.Load().Version;

    /// <summary>How much of each act's event order to print. The whole thing is ~28 entries.</summary>
    private const int EventsShown = 8;

    /// <summary>"Queen", or "Queen + Test Subject" when A10 gave the act two.</summary>
    private static string BossesOf(GeneratedAct act) =>
        string.Join(" + ", act.Bosses.Select(b => ActCatalog.Display(b.Name)));

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                PrintUsage();
                return 0;
            }

            // --verify is its own mode: it reads a real run save rather than searching.
            int v = Array.IndexOf(args, "--verify");
            if (v >= 0)
            {
                int p = Array.IndexOf(args, "--progress");
                return SaveVerifier.Run(
                    savePath: OptionalValue(args, v),
                    progressPath: p >= 0 ? OptionalValue(args, p) : null);
            }

            // --verify-history checks every FINISHED run instead, which is the only way to test
            // the co-op path without a partner: a solo multiplayer lobby cannot be started.
            int vh = Array.IndexOf(args, "--verify-history");
            if (vh >= 0)
                return HistoryVerifier.Run(OptionalValue(args, vh), verbose: args.Contains("--verbose"));

            // --refresh rewrites the data tables from the installed game, for a patch that only
            // moved content around. It cannot touch the hand-written draw order; --doctor is
            // what tells you whether that is what broke.
            int rf = Array.IndexOf(args, "--refresh");
            if (rf >= 0)
                return Tools.Refresh.Run(OptionalValue(args, rf),
                    force: args.Contains("--force"), dryRun: args.Contains("--dry-run"));

            // --doctor is the triage front door: which layer broke, what still works, what to
            // type next. repair.bat wraps it for people who do not use a terminal.
            if (args.Contains("--doctor"))
                return Tools.Doctor.Run(verbose: args.Contains("--verbose"));

            // --gpu-verify holds the GPU kernels to Core the way the Oracle holds Core to the
            // game. It is a separate command rather than part of --doctor because it answers a
            // different question: --doctor asks whether this checkout still predicts your GAME,
            // this asks whether the accelerated path still agrees with the checkout.
            if (args.Contains("--gpu-verify") || args.Contains("--gpu-bench"))
                return Tools.GpuDoctor.Run(
                    verbose: args.Contains("--verbose"),
                    bench: args.Contains("--gpu-bench"));

            // --show prints a game method beside the file that mirrors it, which is the whole
            // of the "an algorithm changed" loop that can be automated.
            int sh = Array.IndexOf(args, "--show");
            if (sh >= 0)
                return Tools.Show.Run(OptionalValue(args, sh), gameDirArg: null);

            // --snapshot re-baselines the mirrored methods. Only correct on a build you have
            // actually verified, so it is separate from --refresh rather than part of it.
            if (args.Contains("--snapshot"))
                return Tools.Snapshotting.Run(checkOnly: args.Contains("--check"));

            // --accept records your game as the build this checkout agrees with. Refuses while
            // a layer is failing, so "verified" keeps meaning something.
            if (args.Contains("--accept"))
                return Tools.Accept.Run(runAlreadyVerified: args.Contains("--run-verified"));

            var opts = Options.Parse(args);
            return opts.Explain is not null ? Explain(opts) : Search(opts);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("run with --help for usage");
            return 1;
        }
    }

    /// <summary>The value after a flag, or null when the flag stands alone.</summary>
    private static string? OptionalValue(string[] args, int flagIndex) =>
        flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith("--") ? args[flagIndex + 1] : null;

    private static NeowContext ContextOf(Options o) => new()
    {
        PlayerCount = o.PlayerCount,
        AllCharactersUnlocked = !o.NotAllCharactersUnlocked,
        ScrollBoxesAvailable = !o.NoScrollBoxes,
    };

    private static int Explain(Options opts)
    {
        var seed = SeedCodec.Canonicalize(opts.Explain!);
        if (!SeedCodec.IsValid(seed))
            throw new ArgumentException($"'{opts.Explain}' is not a valid seed (alphabet: {SeedCodec.Alphabet})");

        var ctx = ContextOf(opts);
        var offers = NeowGenerator.PredictAllOffers(SeedCodec.RunSeed(seed), ctx);

        Console.WriteLine();
        Console.WriteLine($"  seed {seed}   ({opts.PlayerCount}-player co-op, StS2 {GameVersion})");
        Console.WriteLine($"  run seed hash: {SeedCodec.RunSeed(seed)}");
        Console.WriteLine();
        Console.WriteLine("  Neow's offer, by lobby slot (Act 1):");
        for (int slot = 0; slot < offers.Length; slot++)
        {
            var o = offers[slot];
            Console.WriteLine($"    P{slot + 1}:  {o.Positive1.Name}");
            Console.WriteLine($"         {o.Positive2.Name}");
            Console.WriteLine($"         {o.Curse.Name}  (curse branch)");
            if (slot < offers.Length - 1) Console.WriteLine();
        }
        if (opts.Characters.Count > 0)
        {
            PrintFirstFight(seed, opts);
            PrintActs(seed, opts);
        }

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// The card reward the first fight offers each player. Row 1 of the map is always a normal
    /// fight, so this happens whatever route the party takes, and it comes off a per-player
    /// stream that act generation never touches.
    /// </summary>
    private static void PrintFirstFight(string seed, Options opts)
    {
        if (opts.Characters.Count != opts.PlayerCount) return;

        Console.WriteLine();
        Console.WriteLine("  Card rewards, by lobby slot:");
        for (int slot = 0; slot < opts.PlayerCount; slot++)
            foreach (var line in FightLines(seed, slot, opts))
                Console.WriteLine("    " + line);

        Console.WriteLine();
        Console.WriteLine($"  Fight 1 is forced by the map. Fights 2 to {CardRewardGenerator.MaxPredictableFight} "
                          + "assume you walk straight into");
        Console.WriteLine("  the next monster room each time, with no shop, elite, event or rest between");
        Console.WriteLine("  them, so they have to be consecutive.");
        Console.WriteLine("  All assume the Neow pick took no cards. Arcane Scroll, Hefty Tablet, Massive");
        Console.WriteLine("  Scroll, Scroll Boxes and Neow's Bones draw off the same stream first.");
    }

    /// <summary>
    /// "P1 fight 1: Anger, Spite, Blood Wall (+ a potion)" — one line per predicted fight.
    ///
    /// Both fights come off ONE walk of the player's stream rather than two calls, because
    /// fight 2 continues where fight 1 stopped and carries both of its pity counters.
    /// </summary>
    private static IEnumerable<string> FightLines(string seed, int slot, Options opts)
    {
        var hallway = CardRewardGenerator.Hallway(
            SeedCodec.RunSeed(seed), slot, opts.Characters[slot],
            CardRewardGenerator.MaxPredictableFight, opts.Ascension,
            new Sts2.SeedFinder.Core.Acts.UnlockState());

        for (int i = 0; i < hallway.Fights.Count; i++)
        {
            var reward = hallway.Fights[i];
            yield return $"P{slot + 1} fight {i + 1}: " +
                   string.Join(", ", reward.Cards.Select(c => CardCatalog.Display(c.TypeName))) +
                   (reward.HasPotion ? "   (+ a potion)" : "");
        }
    }

    /// <summary>
    /// Act order, bosses and Ancients. UNVERIFIED — printed so it can be tested against a
    /// real run, not because it is known correct. See RunGenerator's header.
    /// </summary>
    private static void PrintActs(string seed, Options opts)
    {
        if (opts.Characters.Count != opts.PlayerCount)
            throw new ArgumentException(
                $"--characters needs exactly {opts.PlayerCount} entries (one per player, in lobby order); " +
                $"got {opts.Characters.Count}.");

        var run = Sts2.SeedFinder.Core.Acts.RunGenerator.GenerateRun(
            SeedCodec.RunSeed(seed),
            new Sts2.SeedFinder.Core.Acts.UnlockState(),
            isMultiplayer: true,
            characters: opts.Characters,
            ascension: opts.Ascension,
            withShopRelics: true,
            withChestRelics: true,
            extraChestPicksBefore: opts.ExtraChestPicks);

        Console.WriteLine();
        Console.WriteLine("  Assumes a fully-unlocked account. Act order and bosses depend on player count;");
        Console.WriteLine("  Ancient offers are per player and independent of the party.");
        Console.WriteLine($"  Party: {string.Join(", ", opts.Characters.Select((c, i) => $"P{i + 1}={c}"))}");
        if (opts.Ascension >= AscensionLevels.DoubleBoss)
            Console.WriteLine($"  Ascension {opts.Ascension}: Double Boss, so the final act has two.");
        Console.WriteLine();
        for (int i = 0; i < run.Acts.Count; i++)
        {
            var a = run.Acts[i];
            Console.WriteLine(
                $"    Act {i + 1}: {a.Act.Name,-11} boss={BossesOf(a),-30} ancient={a.Ancient}");
        }
        PrintShops(run, opts);
        PrintChests(run, opts);
        PrintEvents(run);
        PrintRunAncients(run, SeedCodec.RunSeed(seed), opts);
    }

    /// <summary>How many shop visits to show. Runs rarely fit more than five shops.</summary>
    private const int ShopsShown = 5;

    /// <summary>
    /// The third relic slot of each shop, per player. Rarity is hardcoded to Shop there and
    /// filling it draws no RNG, so it is simply the back of that player's Shop deque, one taken
    /// per visit. The other two slots roll against a pity counter your own play has moved, so
    /// they are not predictable and are not shown.
    /// </summary>
    private static void PrintShops(GeneratedRun run, Options opts)
    {
        if (run.ShopRelics is null) return;

        Console.WriteLine();
        Console.WriteLine($"  Shop relics, third slot only (first {ShopsShown} shops each player visits):");
        for (int slot = 0; slot < run.ShopRelics.Count; slot++)
        {
            var seq = run.ShopRelics[slot].Take(ShopsShown).Select(r => ShopRelics.Display(r.Slug));
            Console.WriteLine($"    P{slot + 1} ({opts.Characters[slot]}): {string.Join(" -> ", seq)}");
        }
        Console.WriteLine();
        Console.WriteLine("  Counted by shops VISITED, not by floor, so skipping one shifts the rest along.");
        Console.WriteLine("  The other two relic slots roll off a pity counter your run has already moved.");
    }

    /// <summary>How many alternates to show per chest slot, for when the bag has been drained.</summary>
    private const int ChestAlternatesShown = 4;

    /// <summary>
    /// What each act's treasure chest puts on the table. One relic per player, and the whole
    /// party votes on the set — so this is not a per-player prediction.
    ///
    /// The rarity is exact. The relic is the front of the shared bag, which every relic anyone
    /// picks up earlier drains, so the alternates are printed too: they are what you get instead,
    /// in order, for each relic of that rarity already taken.
    /// </summary>
    private static void PrintChests(GeneratedRun run, Options opts)
    {
        if (run.Chests is null) return;

        Console.WriteLine();
        Console.WriteLine($"  Treasure chests (one per act, at co-op floors "
                          + $"{string.Join("/", ChestRelics.MultiplayerFloors)}, unskippable):");

        for (int act = 1; act <= run.Chests.Slots.Count; act++)
        {
            var slots = run.Chests.Slots[act - 1];
            var offered = slots.Select(s => $"{ChestRelics.Display(s.Expected?.Slug ?? "-")} ({s.Rarity[0]})");
            Console.WriteLine($"    Act {act}: {string.Join(",  ", offered)}");

            foreach (var s in slots)
            {
                var alts = s.Candidates.Skip(1).Take(ChestAlternatesShown)
                            .Select(r => ChestRelics.Display(r.Slug));
                if (alts.Any())
                    Console.WriteLine($"        then ({s.Rarity}): {string.Join(" -> ", alts)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  The RARITIES are exact. The relics assume nobody has taken one out of the");
        Console.WriteLine("  shared bag yet — an elite reward, a merchant's stock or a relic event each");
        Console.WriteLine("  remove one, and every removal ahead of a pick moves it one along the 'then' list.");
        if (opts.ExtraChestPicks == 0)
            Console.WriteLine("  Assumes no ? room became a treasure room; if one did, pass --extra-chests 1.");
    }

    /// <summary>
    /// The head of each act's event queue. Rooms take from the front, but skip any event that
    /// is not currently allowed or has already been seen this run — so this is the order, not
    /// a schedule. Half the events gate themselves on HP, gold, deck or act index.
    /// </summary>
    private static void PrintEvents(GeneratedRun run)
    {
        Console.WriteLine();
        Console.WriteLine($"  Event order (first {EventsShown} of each act; rooms take from the front,");
        Console.WriteLine("  skipping any that your run does not currently qualify for):");
        for (int i = 0; i < run.Acts.Count; i++)
        {
            var names = run.Acts[i].Events.Take(EventsShown).Select(ActCatalog.Display);
            Console.WriteLine($"    Act {i + 1}: {string.Join(", ", names)}");
        }
    }

    /// <summary>
    /// What each Ancient in this run offers, per player slot. Several Ancients gate a pool on
    /// deck state, which no seed can determine — those are printed as separate branches rather
    /// than collapsed into a guess.
    /// </summary>
    private static void PrintRunAncients(Sts2.SeedFinder.Core.Acts.GeneratedRun run, ulong runSeed, Options opts)
    {
        for (int act = 0; act < run.Acts.Count; act++)
        {
            if (!AncientOffers.TryParse(run.Acts[act].Ancient, out var ancient)) continue;

            Console.WriteLine();
            Console.WriteLine($"    Act {act + 1}, {ancient} offers:");
            for (int slot = 0; slot < opts.PlayerCount; slot++)
            {
                var branches = AncientOffers.Branches(
                    ancient, runSeed, slot, new AncientContext { ActIndex = act });

                if (branches.Count == 1)
                {
                    Console.WriteLine($"      P{slot + 1}: {branches[0].Offer}");
                }
                else
                {
                    Console.WriteLine($"      P{slot + 1}: depends on your deck...");
                    foreach (var (condition, offer) in branches)
                        Console.WriteLine($"           {offer}   [{condition}]");
                }
            }
        }
    }

    private static int Search(Options opts)
    {
        // Keep the original default: a bare invocation still hunts Silken Tress. But once any
        // act criterion is given, no Neow requirement is implied.
        bool actCriteria = opts.Ancients.Count > 0 || opts.Bosses.Count > 0 || opts.Events.Count > 0
                           || opts.Cards.Count > 0 || opts.ShopRelicsWanted.Count > 0
                           || opts.ChestRelicsWanted.Count > 0;
        var relicName = opts.Relic ?? (actCriteria ? null : "silken_tress");
        NeowRelic? relic = null;
        if (relicName is not null)
            relic = NeowRelics.Find(relicName)
                ?? throw new ArgumentException($"unknown relic '{relicName}'. Use --list to see them all.");

        var criteria = new SearchCriteria
        {
            Relic = relic,
            CardOrder = opts.AnyOrder ? CardOrder.AnyPermutation : CardOrder.Exact,
            Act1 = opts.Act1,
            Context = ContextOf(opts),
            Requirement = opts.Requirement,
            RequiredSlots = opts.RequiredSlots,
            Where = opts.Where,
            Ancients = opts.Ancients,
            Bosses = opts.Bosses,
            Events = opts.Events,
            Cards = opts.Cards,
            ShopRelicsWanted = opts.ShopRelicsWanted,
            ChestRelicsWanted = opts.ChestRelicsWanted,
            ExtraChestPicks = opts.ExtraChestPicks,
            Ascension = opts.Ascension,
            Characters = opts.Characters,
        };

        Console.WriteLine();
        if (opts.Act1 is not null)
            Console.WriteLine($"  Act 1 map     : {opts.Act1}");
        if (relic is not null)
            Console.WriteLine($"  Neow          : {relic.Name}  ({DescribeWhere(opts.Where)})");
        foreach (var b in opts.Bosses)
            Console.WriteLine($"  Boss          : {b}");
        foreach (var e in opts.Events)
            Console.WriteLine($"  Event         : {e}");
        foreach (var a in opts.Ancients)
            Console.WriteLine($"  Ancient       : {a}");
        foreach (var c in opts.Cards)
            Console.WriteLine($"  Card          : {c}");
        foreach (var sr in opts.ShopRelicsWanted)
            Console.WriteLine($"  Shop relic    : {sr}");
        foreach (var cr in opts.ChestRelicsWanted)
            Console.WriteLine($"  Chest relic   : {cr}");
        if (opts.ExtraChestPicks > 0)
            Console.WriteLine($"  extra chests  : {opts.ExtraChestPicks} before Act 1's "
                              + "(? rooms that became treasure rooms)");
        Console.WriteLine($"  lobby         : {opts.PlayerCount} players" +
                          (opts.Characters.Count > 0 ? $" — {string.Join(", ", opts.Characters)}" : "") +
                          (opts.Ascension > 0 ? $", ascension {opts.Ascension}" : "") +
                          (opts.Ascension >= AscensionLevels.DoubleBoss ? " (Double Boss)" : ""));
        // --require is the Neow relic's slot rule, so saying "at least one player is offered
        // it" on a search that has no relic names nothing. Card and Ancient rows carry their
        // own slot rule and print it themselves.
        if (relic is not null)
        {
            Console.WriteLine($"  requirement   : {DescribeRequirement(opts)}");
            double p = SeedSearcher.MatchProbability(criteria);
            Console.WriteLine($"  Neow rate     : 1 in {1.0 / p:N1} seeds (before act filtering)");
        }
        Console.WriteLine($"  scanning      : {opts.Count:N0} seeds from index {opts.Start:N0}");

        // The GPU only ever narrows which indices get examined; the criteria chain that decides
        // a match is the same either way. Disposed with the search, since a CLI process runs one.
        using var planner = Sts2.SeedFinder.Gpu.GpuSearchPlanner.Create();
        bool accelerated = planner.TryPlan(criteria, opts.Start, opts.Count, CancellationToken.None,
            out var candidates);
        if (accelerated)
            Console.WriteLine($"  engine        : {planner.Status.Backend} ({planner.Status.DeviceName})");
        else if (planner.Available)
            Console.WriteLine("  engine        : cpu (these criteria have no GPU pre-filter yet)");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        int n = 0;
        foreach (var hit in SeedSearcher.Search(criteria, opts.Start, opts.Count, opts.MaxResults,
                     CancellationToken.None, candidates))
        {
            n++;
            var slots = relic is null
                ? ""
                : string.Join(",", hit.MatchingSlots(relic, opts.Where).Select(s => $"P{s + 1}"));
            Console.WriteLine($"  {hit.Seed}   {slots,-10}");
            for (int i = 0; i < hit.OffersBySlot.Length; i++)
                Console.WriteLine($"      P{i + 1} Neow: {hit.OffersBySlot[i]}");

            // Only when asked for. It costs a per-player generation pass, and on a boss or
            // relic search it would be noise.
            // Only as deep as the search actually asked about, so a fight-1 search does not
            // start printing a fight-2 line whose hallway assumption nobody made.
            if (opts.Cards.Count > 0 && opts.Characters.Count == opts.PlayerCount)
            {
                int deepest = opts.Cards.Max(c => c.Fight);
                for (int i = 0; i < opts.PlayerCount; i++)
                    foreach (var line in FightLines(hit.Seed, i, opts).Take(deepest))
                        Console.WriteLine("      " + line);
            }
            if (hit.Run is not null)
            {
                // Same rule as the cards: only shown when the search asked about them.
                if (opts.ShopRelicsWanted.Count > 0 && hit.Run.ShopRelics is not null)
                    for (int i = 0; i < hit.Run.ShopRelics.Count; i++)
                        Console.WriteLine($"      P{i + 1} shops: " + string.Join(" -> ",
                            hit.Run.ShopRelics[i].Take(ShopsShown).Select(r => ShopRelics.Display(r.Slug))));

                for (int i = 0; i < hit.Run.Acts.Count; i++)
                {
                    var a = hit.Run.Acts[i];
                    Console.WriteLine($"      Act {i + 1}: {a.Act.Name,-11} " +
                                      $"boss={BossesOf(a),-30} ancient={a.Ancient}");
                }
                if (opts.Events.Count > 0) PrintEvents(hit.Run);
                PrintRunAncients(hit.Run, SeedCodec.RunSeed(hit.Seed), opts);
            }
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"  {n} seed(s) in {sw.Elapsed.TotalSeconds:N2}s");
        if (n == 0) Console.WriteLine("  try a larger --count, or a different --start");
        Console.WriteLine();
        return n > 0 ? 0 : 2;
    }

    private static string DescribeWhere(OfferSlot w) => w switch
    {
        OfferSlot.CurseOnly => "curse branch only",
        OfferSlot.PositiveOnly => "positive options only",
        _ => "anywhere in the offer",
    };

    private static string DescribeRequirement(Options o) => o.Requirement switch
    {
        SlotRequirement.All => "every player is offered it",
        SlotRequirement.Specific => $"offered to {string.Join(" and ", o.RequiredSlots.Select(s => $"P{s + 1}"))}",
        _ => "at least one player is offered it",
    };

    private static void PrintUsage()
    {
        Console.WriteLine($"""

          sts2seed — Slay the Spire 2 co-op seed finder (game {GameVersion})

          Searches Neow's full Act 1 offer: the curse-branch relic and both positive
          options. In co-op each player rolls their own offer, so you can require
          specific players.

          USAGE
            sts2seed [--relic <name>] [--players N] [options]
            sts2seed --explain <SEED> [--players N]
            sts2seed --verify [<run.save>] [--progress <progress.save>]

          OPTIONS
            --relic <name>     Neow relic to look for. Default: silken_tress, unless an
                               --ancient flag is given, in which case Neow is unconstrained.
            --act1 <map>       overgrowth | underdocks. Default: either. Rolled on its own
                               RNG, so this is close to free and is tested first.
            --players N        Lobby size, 2-4. Default: 2
            --require <who>    any | all | p1,p2,...   Default: any
            --where <slot>     any | curse | positive   Default: any
            --count N          How many seeds to scan. Default: 5000000
            --start N          Index to start from. Default: random (printed, so you
                               can pass it back to reproduce a run)
            --results N        Stop after N matches. Default: 10
            --explain <SEED>   Show the full offer for every player on a seed
            --list             List every relic Neow, the Ancients and shops can offer
            -a, --ascension N  Ascension level, 0-10. Default: 0
            --double-boss      Shorthand for --ascension 10
            -h, --help         This message

          ASCENSION 10 — DOUBLE BOSS
            A10 is the only ascension level that changes generation, and it changes one
            thing: the FINAL act gets a second boss, drawn from that act's other two.
            It is the last draw generation makes, after everything else, so every other
            prediction is byte-identical with the mode on or off — you only need this
            flag if you care about the second boss.

            With it on, two --boss requirements on the final act pin the pair, and one
            negated requirement keeps a boss out of both slots:
              --ascension 10 --boss 3:queen --boss 3:aeonglass    exactly that pair
              --ascension 10 --boss 3:!test_subject               anything but that one


          BOSSES AND EVENTS (repeatable; both need --characters)
            --boss <act>:<boss>           Require an act to end with a specific boss.
                                          e.g. --boss 2:kaiser_crab
            --boss <act>:!<boss>          Require it NOT to. e.g. --boss 3:!queen
            --event <act>:<event>[:<n>]   Require an event within the first n of that
                                          act's event order. n defaults to 3.
                                          e.g. --event 1:trash_heap:5

            The boss is a run-level draw, so it is the same for everybody in the lobby.
            Act 1's two maps have disjoint boss lists, so naming an Act 1 boss also pins
            the map whether or not --act1 was given.

            An act shuffles its whole event pool once and hands them out from the front,
            so the ORDER is fixed by the seed. How far down it you actually get is not:
            a room takes the next event that is currently allowed and not already seen,
            and half the events gate themselves on HP, gold, deck or act. Treat --event
            as "near the front of the queue" rather than as a guarantee.

          CARD REWARDS (repeatable; needs --characters)
            --card <player>:<card>[:<n>]  Require fight n to offer that player a specific
                                          card. n is 1 or 2, default 1. e.g. --card p1:anger
                                          or --card p1:offering:2
            --shop <player>:<relic>[:<n>] Require that player's nth shop to stock a relic in
                                          its third slot. n counts shops ENTERED, not floors,
                                          and defaults to their first. e.g. --shop p1:toolbox

            The third slot is the only part of a shop a seed decides. Its rarity is fixed
            rather than rolled, and filling it draws no RNG: it takes the back of a relic
            bag shuffled before the run began, one per shop. So the relics are a fixed
            sequence. The other two slots roll against a counter your own run has moved.
                                          "any" instead of a player means someone gets it.

            The first room of a run is always a normal fight, and each player rolls their
            own reward for it, so this is per-player like Neow: --card p1:anger --card
            p2:deflect asks for both at once. It needs --characters because the pool
            belongs to the character, not the lobby.

            Fight 1 needs no assumption. Fight 2 assumes you walk straight into a second
            monster room, with no shop, elite, event or rest between them. Fight 1 can
            never offer a Rare and one is refused there; fight 2 can, and --ascension
            matters for it, because A7+ (Scarcity) lowers the rare odds.

          TREASURE CHEST (repeatable)
            --chest <act>:<relic>[:<n>]   Require that act's chest to hold a relic. n
                                          allows for relics taken out of the shared bag
                                          first, default 0. e.g. --chest 1:vajra
            --extra-chests <n>            ? rooms that became treasure rooms before Act 1's
                                          chest. Each shifts every chest by one. Default 0.

            Every act has exactly one chest, at co-op floors 9, 24 and 38, and no route
            skips it. It puts one relic per player on the table and the whole party votes,
            so this is run-level: it asks what is IN the chest, not who takes it. Naming
            two relics for one act asks for both, up to the player count.

            The RARITY is exact. The relic is the front of the shared relic bag, which
            every relic anyone picks up earlier removes an entry from, so raise n to accept
            the next relics of that rarity instead.

            Two limits, both from the game's rarity odds rather than from this tool:
            the first fight can never offer a RARE (the rare odds carry a penalty that
            has not worn off by the third draw), and nothing is upgraded in Act 1.
            --list shows only what is actually reachable.

            One caveat worth knowing: Arcane Scroll, Hefty Tablet, Massive Scroll,
            Scroll Boxes and Neow's Bones draw cards off the same stream at Neow, which
            shifts this reward. The prediction assumes you took none of them. Every other
            Neow option, Silken Tress included, leaves it alone.

          ACT 2/3 ANCIENTS (repeatable; both need --characters)
            --ancient <name>              Require this Ancient to appear, whatever it
                                          offers. e.g. --ancient vakuu
            --ancient-relic <a>:<relic>   Require that Ancient to be offering a specific
                                          relic. e.g. --ancient-relic vakuu:fiddle

            Which Ancient shows up comes from the shared run RNG, so it depends on player
            count; what it offers is rolled per player, like Neow. Only Vakuu is fully
            pinned down by the seed — the others gate a pool on your deck, so a match may
            hold in some deck states and not others. The output says which.

          REPAIRING AFTER A GAME PATCH
            --refresh [dir]    Rewrite the data tables from your installed game. Fixes a patch
                               that added, removed or re-tiered content: relics, cards, acts,
                               encounters, events, bosses. Rebuild afterwards.
            --dry-run          Show what --refresh would change, without writing.
            --force            Write even a table that lost most of its rows.
            --doctor           Check whether this build still predicts your game, by layer, and
                               say what to do about it. Start here after a patch.
            --show [Type.Method]  Print a game method beside the file that mirrors it. With no
                               argument, lists every method this project mirrors.
            --snapshot         Re-record those methods as the baseline future patches are
                               diffed against. Only run this on a build you have verified.
            --accept           Record your game as the build this checkout agrees with, which
                               clears the drift banner. Refuses while anything is failing.

            --refresh reads the game by RUNNING it: it populates the game's own model database
            and asks it, rather than parsing decompiled source. So there is no pattern to
            misread.

            It cannot fix a change to the draw ORDER, which is hand-written code. It does
            DETECT one: every mirrored game method is diffed against a recorded snapshot, and
            any that changed are named along with the file to edit. Use --show to read them.

          VERIFYING ACT GENERATION AGAINST A REAL RUN
            --verify [path]    Check our act/boss/Ancient generation against a run the
                               game actually created. The save records every draw
                               GenerateRooms makes, so this tests the whole chain.
                               Defaults to the newest current_run(_mp).save found under
                               %APPDATA%\SlayTheSpire2\steam.
            --progress [path]  profile save to read real unlock state from. Found
                               automatically; pass a path to override.
            --verify-history [dir]  Check every FINISHED run instead, from saves/history.
                               Covers acts, bosses, Ancients, encounter order and each
                               shop's third relic. Add --verbose to list every run.

            Start a run, quit to the menu, then run --verify. A singleplayer run covers
            everything except the two multiplayer deltas (player count into the relic
            bags, one fewer room per act); current_run_mp.save covers those too.

            --verify-history needs no live run and covers CO-OP, which --verify cannot
            without a partner: a solo multiplayer lobby refuses to start. Any past co-op
            run exercises both multiplayer branches.

            --gpu-verify       Check the GPU search kernels against Core. Answers a
                               different question from --doctor: not "does this predict
                               your game" but "does the accelerated path still agree
                               with the checkout". Needs no GPU (it falls back to
                               ILGPU's CPU backend) and needs no run.
            --gpu-bench        Same checks, then measure throughput on this machine.

            STS2_GPU=off disables the GPU entirely; cuda / opencl / cpu force a backend.

          UNLOCK STATE (affects which relics can appear — set these if they apply)
            --not-all-characters   You have NOT unlocked every character.
                                   Removes Kaleidoscope from the pool.
            --no-scroll-boxes      Your character's pool lacks 4 commons / 2 uncommons.
                                   Removes Scroll Boxes.

          EXAMPLES
            sts2seed --relic silken_tress --players 2 --require all
            sts2seed --relic kaleidoscope --where positive --require p1
            sts2seed --relic hefty_tablet --players 3 --require p1,p3
            sts2seed --explain 0P2ENNHM
            sts2seed --ancient vakuu --characters ironclad,silent
            sts2seed --boss 3:queen --characters ironclad,silent
            sts2seed --boss 3:!queen --characters ironclad,silent
            sts2seed --ascension 10 --boss 3:queen --boss 3:aeonglass --characters ironclad,silent
            sts2seed --boss 1:the_kin --event 1:dense_vegetation:5 --characters ironclad,silent
            sts2seed --card p1:anger --card p2:deflect --characters ironclad,silent
            sts2seed --shop p1:belt_buckle --characters ironclad,silent
            sts2seed --shop p1:toolbox:2 --shop p2:orrery --characters ironclad,silent
            sts2seed --relic silken_tress --require all \
                     --ancient-relic vakuu:fiddle --characters ironclad,silent

          NOTE
            Shop inventories are deliberately absent: they are rolled when you walk in,
            off streams that your play has already moved (the card-rarity pity counter,
            the relic grab bag, the rewards RNG), so no seed determines them. Card
            rewards after the FIRST fight are out for the same reason — on floor 1 none
            of that state exists yet, which is exactly why the first one is predictable.
            Results depend on game version, lobby order and unlock state.

          """);
    }

    private sealed record Options
    {
        /// <summary>Null means "no Neow requirement" — only meaningful alongside --ancient.</summary>
        public string? Relic { get; init; }

        /// <summary>Act 1 map to require, or null for either.</summary>
        public string? Act1 { get; init; }

        /// <summary>Ancient requirements from --ancient / --ancient-relic, in the order given.</summary>
        public IReadOnlyList<AncientCriterion> Ancients { get; init; } = Array.Empty<AncientCriterion>();

        /// <summary>Per-act boss requirements from --boss.</summary>
        public IReadOnlyList<BossCriterion> Bosses { get; init; } = Array.Empty<BossCriterion>();

        /// <summary>Per-act event-order requirements from --event.</summary>
        public IReadOnlyList<EventCriterion> Events { get; init; } = Array.Empty<EventCriterion>();

        /// <summary>
        /// First-fight card requirements from --card. Resolved after parsing rather than
        /// during it, because a card name only means anything against a character's pool and
        /// --characters may come later on the command line.
        /// </summary>
        public IReadOnlyList<string> CardArgs { get; init; } = Array.Empty<string>();

        public IReadOnlyList<CardCriterion> Cards { get; init; } = Array.Empty<CardCriterion>();

        /// <summary>--any-order: card picks may land in any fight order, one per fight.</summary>
        public bool AnyOrder { get; init; }

        /// <summary>
        /// Shop third-slot requirements from --shop. Deferred like the card args, because the
        /// player-count check needs --players and the character check needs --characters.
        /// </summary>
        public IReadOnlyList<string> ShopArgs { get; init; } = Array.Empty<string>();

        public IReadOnlyList<ShopRelicCriterion> ShopRelicsWanted { get; init; } =
            Array.Empty<ShopRelicCriterion>();

        /// <summary>
        /// Treasure chest requirements from --chest. Deferred like the others, because the
        /// "no more relics than there are players" check needs --players.
        /// </summary>
        public IReadOnlyList<string> ChestArgs { get; init; } = Array.Empty<string>();

        public IReadOnlyList<ChestRelicCriterion> ChestRelicsWanted { get; init; } =
            Array.Empty<ChestRelicCriterion>();

        /// <summary>
        /// --extra-chests: ? rooms that turned into treasure rooms before Act 1's chest. Each one
        /// shifts every later chest by a full player count of draws.
        /// </summary>
        public int ExtraChestPicks { get; init; }

        /// <summary>
        /// Ascension level. Only A10 (Double Boss) changes generation, and only by giving the
        /// final act a second boss.
        /// </summary>
        public int Ascension { get; init; }
        public int PlayerCount { get; init; } = 2;
        public SlotRequirement Requirement { get; init; } = SlotRequirement.Any;
        public IReadOnlyList<int> RequiredSlots { get; init; } = Array.Empty<int>();
        public OfferSlot Where { get; init; } = OfferSlot.Anywhere;
        public ulong Start { get; init; } = RandomStart();
        public ulong Count { get; init; } = 5_000_000;
        public int MaxResults { get; init; } = 10;
        public string? Explain { get; init; }
        public bool NotAllCharactersUnlocked { get; init; }
        public bool NoScrollBoxes { get; init; }

        /// <summary>
        /// Each player's character, in lobby order. Required for act prediction because one
        /// relic grab bag is shuffled per player off the same RNG that later picks the
        /// Ancient, so the number of players shifts every draw after it. Which characters
        /// they pick does not, as things stand — all five have identically sized pools.
        /// </summary>
        public IReadOnlyList<Sts2.SeedFinder.Core.Acts.Character> Characters { get; init; }
            = Array.Empty<Sts2.SeedFinder.Core.Acts.Character>();

        public static Options Parse(string[] args)
        {
            if (args.Contains("--list"))
            {
                Console.WriteLine("\n  CURSE BRANCH (exactly one is always offered):\n");
                foreach (var r in NeowRelics.Curses)
                    Console.WriteLine($"    {r.Slug,-20} {r.Name}{Note(r)}");
                Console.WriteLine("\n  POSITIVE POOL (two are offered):\n");
                foreach (var r in NeowRelics.Positives)
                    Console.WriteLine($"    {r.Slug,-20} {r.Name}{Note(r)}");
                Console.WriteLine("\n  COIN-FLIP PAIRS (one of each pair joins the positive pool):\n");
                Console.WriteLine("    lava_rock / small_capsule           (skipped if the curse is Large Capsule)");
                Console.WriteLine("    nutritious_oyster / stone_humidifier");
                Console.WriteLine("    neows_talisman / pomander");
                Console.WriteLine("\n  ANCIENTS (use with --ancient / --ancient-relic <ancient>:<relic>):\n");
                foreach (Ancient a in Enum.GetValues<Ancient>())
                {
                    Console.WriteLine($"    {a.ToString().ToLowerInvariant()}");
                    Console.WriteLine($"      {string.Join(", ", AncientOffers.AllRelics(a).Select(AncientOffers.Slug))}");
                }

                // Only Shop rarity reaches the third slot, so this list IS the set of answerable
                // requests. The other two slots roll a rarity that depends on run state.
                Console.WriteLine("\n  SHOP RELICS, third slot (use with --shop <player>:<relic>[:<visit>]):\n");
                Console.WriteLine("      " + string.Join(", ",
                    RelicPoolData.SharedRelics.Where(r => r.Rarity == "Shop").Select(r => r.Slug)));
                foreach (var c in Enum.GetValues<Sts2.SeedFinder.Core.Acts.Character>())
                    foreach (var r in RelicPoolData.RelicsFor(c).Where(r => r.Rarity == "Shop"))
                        Console.WriteLine($"      {r.Slug,-20} ({c} only)");

                Console.WriteLine("\n  BOSSES (use with --boss <act>:<boss>):\n");
                foreach (var act in ActCatalog.ActNumbers)
                    foreach (var group in ActCatalog.Bosses(act).GroupBy(b => b.Map))
                        Console.WriteLine($"    act {act}, {group.Key,-11} " +
                                          string.Join(", ", group.Select(b => ActCatalog.Slug(b.TypeName))));

                Console.WriteLine("\n  EVENTS (use with --event <act>:<event>[:<within-first>]):\n");
                foreach (var act in ActCatalog.ActNumbers)
                {
                    Console.WriteLine($"    act {act}:");
                    Console.WriteLine("      " + string.Join(", ", ActCatalog.EventNames(act).Select(ActCatalog.Slug)));
                }

                // Rares are omitted deliberately: the first fight's rarity roll cannot reach
                // them, so listing them would only invite a search with no answers.
                Console.WriteLine("\n  FIRST FIGHT CARDS (use with --card <player>:<card>):\n");
                foreach (var c in CardCatalog.Characters)
                {
                    Console.WriteLine($"    {c.ToString().ToLowerInvariant()}:");
                    foreach (var rarity in CardCatalog.FirstFightRarities)
                        Console.WriteLine($"      {rarity.ToString().ToLowerInvariant(),-9} " +
                            string.Join(", ", CardCatalog.FirstFightOfferable(c)
                                .Where(e => e.Rarity == rarity)
                                .Select(e => CardCatalog.Slug(e.TypeName))));
                }
                Console.WriteLine();
                Environment.Exit(0);
            }

            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next(string flag) => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"{flag} needs a value");

                switch (args[i])
                {
                    case "--relic":   o = o with { Relic = Next("--relic") }; break;
                    case "--act1":    o = o with { Act1 = Next("--act1") }; break;
                    case "--ancient": o = o with { Ancients = o.Ancients.Append(ParseAncient(Next("--ancient"))).ToList() }; break;
                    case "--ancient-relic":
                        o = o with { Ancients = o.Ancients.Append(ParseAncientRelic(Next("--ancient-relic"))).ToList() };
                        break;
                    case "--boss":
                        o = o with { Bosses = o.Bosses.Append(ParseBoss(Next("--boss"))).ToList() };
                        break;
                    case "--ascension":
                    case "-a":
                        o = o with { Ascension = ParseAscension(Next("--ascension")) };
                        break;
                    case "--double-boss":
                        o = o with { Ascension = Math.Max(o.Ascension, AscensionLevels.DoubleBoss) };
                        break;
                    case "--event":
                        o = o with { Events = o.Events.Append(ParseEvent(Next("--event"))).ToList() };
                        break;
                    case "--card":
                        o = o with { CardArgs = o.CardArgs.Append(Next("--card")).ToList() };
                        break;
                    case "--chest":
                        o = o with { ChestArgs = o.ChestArgs.Append(Next("--chest")).ToList() };
                        break;

                    case "--extra-chests":
                    {
                        var raw = Next("--extra-chests");
                        if (!int.TryParse(raw, out var extra) || extra < 0)
                            throw new ArgumentException($"--extra-chests wants a count of 0 or more, got '{raw}'");
                        o = o with { ExtraChestPicks = extra };
                        break;
                    }

                    case "--shop":
                        o = o with { ShopArgs = o.ShopArgs.Append(Next("--shop")).ToList() };
                        break;
                    case "--players": o = o with { PlayerCount = ParsePlayers(Next("--players")) }; break;
                    case "--count":   o = o with { Count = ulong.Parse(Next("--count")) }; break;
                    case "--start":   o = o with { Start = ulong.Parse(Next("--start")) }; break;
                    case "--results": o = o with { MaxResults = int.Parse(Next("--results")) }; break;
                    case "--explain": o = o with { Explain = Next("--explain") }; break;
                    case "--characters":
                        o = o with
                        {
                            Characters = Next("--characters")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(s => Enum.TryParse<Sts2.SeedFinder.Core.Acts.Character>(s, ignoreCase: true, out var c)
                                    ? c
                                    : throw new ArgumentException(
                                        $"unknown character '{s}'. Valid: " +
                                        string.Join(", ", Enum.GetNames<Sts2.SeedFinder.Core.Acts.Character>())))
                                .ToArray(),
                        };
                        break;
                    case "--any-order": o = o with { AnyOrder = true }; break;
                    case "--not-all-characters": o = o with { NotAllCharactersUnlocked = true }; break;
                    case "--no-scroll-boxes":    o = o with { NoScrollBoxes = true }; break;
                    case "--where":
                        o = Next("--where").Trim().ToLowerInvariant() switch
                        {
                            "any" => o with { Where = OfferSlot.Anywhere },
                            "curse" => o with { Where = OfferSlot.CurseOnly },
                            "positive" => o with { Where = OfferSlot.PositiveOnly },
                            var v => throw new ArgumentException($"--where must be any|curse|positive, got '{v}'"),
                        };
                        break;
                    case "--require":
                    {
                        var v = Next("--require").Trim().ToLowerInvariant();
                        o = v switch
                        {
                            "any" => o with { Requirement = SlotRequirement.Any },
                            "all" => o with { Requirement = SlotRequirement.All },
                            _ => o with { Requirement = SlotRequirement.Specific, RequiredSlots = ParseSlots(v) },
                        };
                        break;
                    }
                    default:
                        throw new ArgumentException($"unknown argument '{args[i]}'");
                }
            }

            foreach (var s in o.RequiredSlots)
                if (s < 0 || s >= o.PlayerCount)
                    throw new ArgumentException($"P{s + 1} is not in a {o.PlayerCount}-player lobby");

            // Now that --characters is known whatever order it was given in.
            if (o.CardArgs.Count > 0)
                o = o with { Cards = o.CardArgs.Select(s => ParseCard(s, o)).ToList() };

            if (o.ShopArgs.Count > 0)
                o = o with { ShopRelicsWanted = o.ShopArgs.Select(s => ParseShop(s, o)).ToList() };

            if (o.ChestArgs.Count > 0)
                o = o with { ChestRelicsWanted = o.ChestArgs.Select(ParseChest).ToList() };

            return o;
        }

        /// <summary>
        /// "p1:anger", "1:anger" or "any:anger" — which player, and the card the first fight
        /// must offer them. The slot is 1-based to match the P1..P4 the rest of the tool uses.
        /// </summary>
        private static CardCriterion ParseCard(string s, Options o)
        {
            var parts = s.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                throw new ArgumentException(
                    $"--card wants <player>:<card>[:<fight>], e.g. p1:anger, got '{s}'");

            var who = parts[0].ToLowerInvariant();
            int slot;
            if (who is "any" or "*")
            {
                slot = -1;
            }
            else if (int.TryParse(who.TrimStart('p'), out var n) && n >= 1)
            {
                if (n > o.PlayerCount)
                    throw new ArgumentException($"P{n} is not in a {o.PlayerCount}-player lobby");
                slot = n - 1;
            }
            else
            {
                throw new ArgumentException($"bad player '{parts[0]}' — use p1, p2, ... or any");
            }

            // A card pool belongs to a character, so there is nothing to resolve the name
            // against until every slot has one.
            if (o.Characters.Count != o.PlayerCount)
                throw new ArgumentException(
                    "--card needs --characters, because which cards exist depends on who is playing.");

            // For "any", the card only has to be in someone's pool; the search then accepts a
            // match from whichever player can actually be offered it.
            // Which fight, 1-based. Fight 1 is forced by the map; every fight after it assumes
            // the party walks straight into the next Monster room.
            int fight = 1;
            if (parts.Length == 3
                && (!int.TryParse(parts[2], out fight)
                    || fight < 1 || fight > CardRewardGenerator.MaxPredictableFight))
                throw new ArgumentException(
                    $"card fight must be between 1 and {CardRewardGenerator.MaxPredictableFight}, "
                    + $"got '{parts[2]}'");

            var pools = slot < 0 ? Enumerable.Range(0, o.PlayerCount) : [slot];
            foreach (var i in pools)
                if (CardCatalog.Find(o.Characters[i], parts[1]) is { } found)
                    return new CardCriterion(slot, found, fight);

            var who2 = slot < 0 ? "anyone in the party" : $"the {o.Characters[slot]}";
            throw new ArgumentException(
                $"'{parts[1]}' is not a card {who2} can be offered. Use --list to see the pools.");
        }

        /// <summary>
        /// "p1:belt_buckle" or "p1:belt_buckle:2" — which player, which relic, and optionally
        /// which shop visit (1-based; the default is their first shop).
        /// </summary>
        private static ShopRelicCriterion ParseShop(string s, Options o)
        {
            var parts = s.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                throw new ArgumentException(
                    $"--shop wants <player>:<relic>[:<visit>], e.g. p1:belt_buckle, got '{s}'");

            var who = parts[0].ToLowerInvariant();
            int slot;
            if (who is "any" or "*")
            {
                slot = -1;
            }
            else if (int.TryParse(who.TrimStart('p'), out var n) && n >= 1)
            {
                if (n > o.PlayerCount)
                    throw new ArgumentException($"P{n} is not in a {o.PlayerCount}-player lobby");
                slot = n - 1;
            }
            else
            {
                throw new ArgumentException($"bad player '{parts[0]}' — use p1, p2, ... or any");
            }

            var relic = ShopRelics.Find(parts[1])
                ?? throw new ArgumentException(
                    $"'{parts[1]}' is not a shop relic. Use --list to see them all.");

            int visit = 1;
            if (parts.Length == 3 && (!int.TryParse(parts[2], out visit) || visit < 1))
                throw new ArgumentException($"shop visit must be 1 or higher, got '{parts[2]}'");

            return new ShopRelicCriterion(slot, relic.Slug, visit - 1);
        }

        /// <summary>
        /// "2:vajra" or "2:vajra:3" — which act's chest, which relic, and optionally how many
        /// relics may already have been taken out of the shared bag ahead of it.
        ///
        /// No player slot: the chest is a shared pick, so the seed decides what is on the table
        /// and the party decides who takes it.
        /// </summary>
        private static ChestRelicCriterion ParseChest(string s)
        {
            var parts = s.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                throw new ArgumentException(
                    $"--chest wants <act>:<relic>[:<tolerance>], e.g. 1:vajra, got '{s}'");

            if (!int.TryParse(parts[0], out var act) || act is < 1 or > 3)
                throw new ArgumentException($"--chest act must be 1, 2 or 3, got '{parts[0]}'");

            var relic = ChestRelics.Find(parts[1])
                ?? throw new ArgumentException(
                    $"'{parts[1]}' is not a relic a chest can offer. Chests roll Common, Uncommon "
                    + "or Rare from the shared pool. Use --list to see them.");

            int tolerance = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[2], out tolerance) || tolerance < 0))
                throw new ArgumentException($"chest tolerance must be 0 or higher, got '{parts[2]}'");

            return new ChestRelicCriterion(act, relic.Slug, tolerance);
        }

        private static AncientCriterion ParseAncient(string s)
        {
            if (!AncientOffers.TryParse(s.Trim(), out var a))
                throw new ArgumentException(
                    $"unknown Ancient '{s}'. Valid: {string.Join(", ", Enum.GetNames<Ancient>()).ToLowerInvariant()}");
            return new AncientCriterion(a);
        }

        /// <summary>"vakuu:fiddle" — the Ancient and the relic it must be offering.</summary>
        private static AncientCriterion ParseAncientRelic(string s)
        {
            var parts = s.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                throw new ArgumentException($"--ancient-relic wants <ancient>:<relic>, got '{s}'");

            var who = ParseAncient(parts[0]).Ancient;
            var match = AncientOffers.AllRelics(who)
                .FirstOrDefault(r => AncientOffers.Slug(r).Equals(parts[1].Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new ArgumentException(
                    $"{who} never offers '{parts[1]}'. Try: " +
                    string.Join(", ", AncientOffers.AllRelics(who).Select(AncientOffers.Slug)));
            return new AncientCriterion(who, match);
        }

        /// <summary>
        /// "2:kaiser_crab" — the act, 1-based, and a boss it must end with. A leading "!" or
        /// "not " on the boss negates it: "3:!queen" finds runs the Queen stays out of.
        /// </summary>
        private static BossCriterion ParseBoss(string s)
        {
            var (act, name) = SplitAct(s, "--boss", "<act>:[!]<boss>");

            bool exclude = false;
            foreach (var prefix in (string[])["!", "not ", "not-", "no "])
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                exclude = true;
                name = name[prefix.Length..].Trim();
                break;
            }

            return new BossCriterion(act, ActCatalog.FindBoss(act, name) ?? throw new ArgumentException(
                $"Act {act} never ends with '{name}'. Try: " +
                string.Join(", ", ActCatalog.Bosses(act).Select(b => ActCatalog.Slug(b.TypeName)))), exclude);
        }

        private static int ParseAscension(string s)
        {
            if (!int.TryParse(s.TrimStart('a', 'A'), out var n) || n < 0 || n > AscensionLevels.Max)
                throw new ArgumentException(
                    $"--ascension wants 0-{AscensionLevels.Max}, got '{s}'. Only A{AscensionLevels.DoubleBoss} " +
                    "changes generation, by giving the final act a second boss.");
            return n;
        }

        /// <summary>"1:trash_heap" or "1:trash_heap:5" — act, event, and how far into the order.</summary>
        private static EventCriterion ParseEvent(string s)
        {
            var (act, rest) = SplitAct(s, "--event", "<act>:<event>[:<within-first>]");

            int within = 3;
            var colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!int.TryParse(rest[(colon + 1)..], out within) || within < 1)
                    throw new ArgumentException(
                        $"'{rest[(colon + 1)..]}' is not a position; --event wants a count of 1 or more.");
                rest = rest[..colon];
            }

            return new EventCriterion(act, ActCatalog.FindEvent(act, rest) ?? throw new ArgumentException(
                $"Act {act} never offers '{rest}'. Try: " +
                string.Join(", ", ActCatalog.EventNames(act).Select(ActCatalog.Slug))), within);
        }

        private static (int Act, string Name) SplitAct(string s, string flag, string shape)
        {
            var parts = s.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var act))
                throw new ArgumentException($"{flag} wants {shape}, got '{s}'");
            if (!ActCatalog.ActNumbers.Contains(act))
                throw new ArgumentException(
                    $"there is no act {act}; use {string.Join(", ", ActCatalog.ActNumbers)}.");
            return (act, parts[1]);
        }

        private static string Note(NeowRelic r) => r.Availability switch
        {
            RelicAvailability.SingleplayerOnly => "   (singleplayer only)",
            RelicAvailability.MultiplayerOnly => "   (co-op only)",
            RelicAvailability.RequiresAllCharactersUnlocked => "   (needs all characters unlocked)",
            RelicAvailability.RequiresBundleableCardPool => "   (needs a large enough card pool)",
            _ => "",
        };

        /// <summary>
        /// A random index far enough into the space that generated seeds use the full
        /// alphabet across all 12 characters, with headroom so --count cannot overflow.
        /// </summary>
        private static ulong RandomStart()
        {
            var space = SeedCodec.SpaceSize() ?? ulong.MaxValue;
            return (ulong)System.Random.Shared.NextInt64(0, (long)Math.Min(space / 2, long.MaxValue));
        }

        private static int ParsePlayers(string s)
        {
            var n = int.Parse(s);
            if (n is < 2 or > 4)
                throw new ArgumentException("--players must be 2-4 (this tool is co-op only)");
            return n;
        }

        private static int[] ParseSlots(string v) =>
            v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(t =>
             {
                 if (!int.TryParse(t.TrimStart('p'), out var n) || n < 1)
                     throw new ArgumentException($"bad slot '{t}' — use p1, p2, ...");
                 return n - 1;
             })
             .ToArray();
    }
}
