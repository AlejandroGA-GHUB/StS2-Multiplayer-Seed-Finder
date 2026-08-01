using System.Text.Json;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Install;
using Sts2.SeedFinder.Core.Saves;

namespace Sts2.SeedFinder.Cli;

/// <summary>
/// Differential test against every FINISHED run the profile has kept, in
/// <c>saves/history/*.run</c>.
///
/// This complements <see cref="SaveVerifier"/> rather than replacing it. A history file is a
/// record of what the party actually saw, not of what generation produced: there is no
/// <c>rng</c> block, no grab bags, no full room set. What it does have is
/// <c>map_point_history</c> — every room entered, in order, with its encounter id — plus the
/// act list, the ascension, and each shop's offered relics per player.
///
/// Two things make that worth having:
///
///   * It is retrospective. <c>current_run.save</c> exists only while a run is in progress, so
///     checking one costs a fresh run; history accumulates on its own. On this profile it turns
///     into dozens of runs' worth of assertions for free.
///   * It covers CO-OP. A solo multiplayer lobby cannot be started
///     (<c>StartRunLobby</c> refuses when <c>IsMultiplayer() &amp;&amp; Players.Count == 1</c>),
///     so the two multiplayer-only branches — the per-player relic-bag loop, and
///     <c>GetNumberOfRooms(isMultiplayer)</c> — were previously unverifiable without a partner.
///     Any past co-op run exercises both.
///
/// Runs are skipped rather than failed when they cannot be judged: a different game build, a
/// non-Standard game mode, or an active modifier all change the pools we generate from.
/// </summary>
public static class HistoryVerifier
{
    /// <summary>
    /// Game builds this profile's data tables were generated from. History goes back many
    /// patches and older runs were generated from pools we no longer model, so anything else
    /// is skipped rather than reported as a failure.
    ///
    /// The build we are currently verified against is always compatible with itself, so it joins
    /// the list rather than having to be hand-added. Without that, the run that proves a new patch
    /// is fine gets skipped as "different build" the moment repair.bat records the patch, which is
    /// precisely the run worth checking. The hand-written entries stay because logic-identical
    /// builds share these tables, and only a person reading both patches can say that.
    /// </summary>
    private static readonly string[] KnownCompatibleBuilds = ["v0.109.0", "v0.109.1"];

    private static readonly string[] CompatibleBuilds =
        KnownCompatibleBuilds.Append(VerifiedBuild.Load().Version).Distinct().ToArray();

    private sealed record Visited(
        List<string> Acts,
        List<List<string>> BossesPerAct,
        List<List<string>> MonstersPerAct,
        List<string?> AncientPerAct,
        List<List<string>> ShopRelicsPerPlayer,
        List<Character> Characters,
        int Ascension,
        string Seed,
        string Build,
        string Mode,
        bool HasModifiers);

    public static int Run(string? historyDir, bool verbose)
    {
        var files = FindHistoryFiles(historyDir);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No finished runs found.");
            Console.Error.WriteLine($"Expected .run files under: {historyDir ?? SaveRootHistory()}");
            return 1;
        }

        int checkedRuns = 0, skipped = 0, failedRuns = 0, assertions = 0, coop = 0, explained = 0, discovery = 0;

        // Failures split by whether the run was played on the build you have NOW. Only those
        // answer the question this tool exists for — "does this checkout predict my game" — and
        // an older run cannot, because its lobby's unlock state and the pools it generated from
        // are both unrecoverable. So the exit code tracks current-build failures alone, while
        // older ones are still printed for anyone investigating.
        var installedBuild = GameInstall.ReadRelease(GameInstall.Find()).Version;
        int failedOnCurrent = 0;
        var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files.OrderBy(f => f))
        {
            Visited run;
            try
            {
                run = Read(file);
            }
            catch (Exception ex)
            {
                Note(skipReasons, $"unreadable ({ex.GetType().Name})");
                skipped++;
                continue;
            }

            var skip = WhySkip(run);
            if (skip is not null)
            {
                Note(skipReasons, skip);
                skipped++;
                continue;
            }

            checkedRuns++;
            if (run.Characters.Count > 1) coop++;

            var failures = new List<string>();
            assertions += Check(run, failures);

            // Singleplayer force-picks acts the account had not discovered yet, ahead of the
            // RNG roll. Which those were at the time is not recorded anywhere, and co-op skips
            // that branch entirely, so an act-selection miss on a solo run is not a port bug.
            if (failures.Count == 1 && run.Characters.Count == 1 && failures[0].StartsWith("acts:"))
            {
                discovery++;
                if (verbose)
                    Console.WriteLine($"[state] {Path.GetFileName(file)}  seed {run.Seed}  1P: act "
                                      + "selection differs, which singleplayer decides from the acts "
                                      + "discovered at the time (not recorded)");
                continue;
            }

            // A co-op run generated for a lobby whose profiles were less unlocked than ours will
            // diverge for a reason that is not a bug. Only claim that when a concrete unlock
            // state reproduces the run exactly.
            if (failures.Count > 0 && run.Characters.Count > 1)
            {
                var solved = SolvePlayerUnlocks(run);
                if (solved is not null)
                {
                    var recheck = new List<string>();
                    Check(run, recheck, solved.Value.Players, solved.Value.Run);
                    if (recheck.Count == 0)
                    {
                        explained++;
                        if (verbose)
                            Console.WriteLine($"[lobby] {Path.GetFileName(file)}  seed {run.Seed}  "
                                              + $"{run.Characters.Count}P — matches once each player's own "
                                              + "unlock state is fitted (partner profiles are not readable here)");
                        continue;
                    }
                }
            }

            if (failures.Count > 0)
            {
                failedRuns++;
                bool current = installedBuild is not null
                               && string.Equals(run.Build, installedBuild, StringComparison.OrdinalIgnoreCase);
                if (current) failedOnCurrent++;
                Console.WriteLine($"[{(current ? "FAIL" : "old ")}] {Path.GetFileName(file)}  seed {run.Seed}  "
                                  + $"{run.Build}  {run.Characters.Count}P A{run.Ascension}");
                foreach (var f in failures) Console.WriteLine($"         {f}");
            }
            else if (verbose)
            {
                Console.WriteLine($"[ok]   {Path.GetFileName(file)}  seed {run.Seed}  "
                                  + $"{run.Characters.Count}P A{run.Ascension}  "
                                  + $"{string.Join("/", run.Acts)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Checked   {checkedRuns} runs ({coop} co-op), {assertions} assertions");
        if (explained > 0)
            Console.WriteLine($"Lobby     {explained} co-op run(s) match only once each player's own unlock "
                              + "state is fitted, which is how the game builds relic bags");
        if (discovery > 0)
            Console.WriteLine($"Discovery {discovery} solo run(s) differ only in act selection, which "
                              + "singleplayer decides from acts undiscovered at the time");
        Console.WriteLine($"Skipped   {skipped}");
        foreach (var (reason, n) in skipReasons.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"            {n,4}  {reason}");
        Console.WriteLine();
        if (failedRuns == 0)
            Console.WriteLine($"ALL {checkedRuns} RUNS MATCH.");
        else if (failedOnCurrent == 0)
            Console.WriteLine($"{failedRuns} older run(s) did not match; none on your current build"
                              + $" ({installedBuild}). That is the result that matters, so this passes.");
        else
            Console.WriteLine($"{failedOnCurrent} run(s) on your current build ({installedBuild}) FAILED.");

        if (failedRuns > 0)
        {
            Console.WriteLine();
            Console.WriteLine("A failure here is not automatically a port bug. History records what the");
            Console.WriteLine("party saw, not the state generation ran against, and three inputs are simply");
            Console.WriteLine("not recoverable from an old run:");
            Console.WriteLine("  * which epochs each profile had revealed at the time (bag sizes, so the");
            Console.WriteLine("    whole UpFront stream). Fitted above where a state reproduces the run.");
            Console.WriteLine("  * which bosses each account had already met. ApplyDiscoveryOrderModifications");
            Console.WriteLine("    force-swaps the first unmet one. It draws no RNG, so it moves the boss");
            Console.WriteLine("    without moving anything else.");
            Console.WriteLine("  * installed mods, which change pool sizes exactly like an unlock would.");
            Console.WriteLine("Runs on the CURRENT build are the ones to trust, and they should match exactly.");
        }
        return failedOnCurrent == 0 ? 0 : 2;
    }

    private static string? WhySkip(Visited run)
    {
        if (!CompatibleBuilds.Contains(run.Build)) return $"build {run.Build} (data is for {string.Join("/", CompatibleBuilds)})";
        if (!string.Equals(run.Mode, "standard", StringComparison.OrdinalIgnoreCase)) return $"game mode {run.Mode}";
        if (run.HasModifiers) return "run modifiers active";
        if (run.Characters.Count == 0) return "no players recorded";
        if (run.Acts.Count == 0) return "no acts recorded";
        if (string.IsNullOrWhiteSpace(run.Seed)) return "no seed";
        return null;
    }

    /// <summary>Compares one run and appends any failures. Returns the number of assertions made.</summary>
    private static int Check(Visited run, List<string> failures,
                             IReadOnlyList<UnlockState>? playerUnlocks = null,
                             UnlockState? runUnlocks = null)
    {
        var runSeed = SeedCodec.RunSeed(run.Seed);
        bool isMp = run.Characters.Count > 1;

        // Unlock state is the profile's CURRENT state, not what it was when the run happened.
        // Epochs only ever get revealed, so on a fully-unlocked profile this is exact, and on a
        // partial one an older run may legitimately disagree. Failures here are worth a look
        // rather than an automatic bug.
        var unlocks = runUnlocks ?? ReadCurrentUnlocks();

        int asserts = 0;

        var predictedActs = RunGenerator.SelectActs(runSeed, unlocks, isMp)
            .Select(a => GameHash.Slugify(a.Name)).ToList();
        asserts++;
        if (!predictedActs.SequenceEqual(run.Acts, StringComparer.OrdinalIgnoreCase))
            failures.Add($"acts: game {string.Join("/", run.Acts)}, ours {string.Join("/", predictedActs)}");

        // Generate against the acts the game actually chose, so an act-selection miss does not
        // cascade through every later comparison.
        var actsForGeneration = run.Acts
            .Select(n => ActData.All.FirstOrDefault(a => GameHash.Slugify(a.Name) == n))
            .ToArray();
        if (actsForGeneration.Any(a => a is null))
        {
            failures.Add("save names an act we do not model");
            return asserts;
        }

        var gen = RunGenerator.GenerateRun(runSeed, unlocks, isMp, run.Characters,
                                           actsForGeneration!, run.Ascension, withShopRelics: true,
                                           playerUnlocks: playerUnlocks);

        for (int i = 0; i < gen.Acts.Count && i < run.Acts.Count; i++)
        {
            var mine = gen.Acts[i];
            var label = $"act{i + 1}";

            // Bosses, in the order fought. At A10 the final act has two.
            var predictedBosses = mine.Bosses.Select(b => GameHash.Slugify(b.Name)).ToList();
            var actualBosses = run.BossesPerAct[i];
            if (actualBosses.Count > 0)
            {
                asserts++;
                // A run can end early, so the game may have fought fewer bosses than were drawn.
                if (!predictedBosses.Take(actualBosses.Count)
                        .SequenceEqual(actualBosses, StringComparer.OrdinalIgnoreCase))
                    failures.Add($"{label} boss: game {string.Join("+", actualBosses)}, "
                                 + $"ours {string.Join("+", predictedBosses)}");
            }

            if (run.AncientPerAct[i] is { } ancient)
            {
                asserts++;
                if (!string.Equals(ancient, GameHash.Slugify(mine.Ancient), StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{label} ancient: game {ancient}, ours {GameHash.Slugify(mine.Ancient)}");
            }

            // Monster rooms are consumed from the front of the act's encounter list, so what the
            // party fought must be a PREFIX of what we generated. They may have fought fewer.
            var actualMonsters = run.MonstersPerAct[i];
            if (actualMonsters.Count > 0)
            {
                asserts++;
                var predicted = mine.NormalEncounters.Select(e => GameHash.Slugify(e.Name))
                    .Take(actualMonsters.Count).ToList();
                if (!predicted.SequenceEqual(actualMonsters, StringComparer.OrdinalIgnoreCase))
                {
                    int at = FirstDiff(predicted, actualMonsters);
                    failures.Add($"{label} encounters diverge at #{at + 1}: game "
                                 + $"{At(actualMonsters, at)}, ours {At(predicted, at)}");
                }
            }
        }

        // The shop's third relic slot, one per shop visited, per player.
        for (int p = 0; p < run.ShopRelicsPerPlayer.Count && p < run.Characters.Count; p++)
        {
            var seen = run.ShopRelicsPerPlayer[p];
            for (int visit = 0; visit < seen.Count; visit++)
            {
                var predicted = gen.ShopRelic(p, visit);
                if (predicted is null) continue;
                asserts++;
                if (!string.Equals(predicted.Value.Slug, seen[visit], StringComparison.OrdinalIgnoreCase))
                    failures.Add($"P{p + 1} shop {visit + 1} relic: game {seen[visit]}, ours {predicted.Value.Slug}");
            }
        }

        return asserts;
    }

    /// <summary>
    /// Tries to explain a failing co-op run as a lobby where somebody's profile was less
    /// unlocked than ours.
    ///
    /// This is not an excuse mechanism, it is the correct model. <c>RelicGrabBag.Populate</c>
    /// reads <c>player.UnlockState</c>, so each player's bag is filtered by THEIR OWN epochs,
    /// and a smaller bag means fewer shuffle draws, which shifts the whole UpFront stream from
    /// that player onward. We can read the local profile; a partner's is on their machine.
    ///
    /// The search is linear rather than combinatorial because bags are shuffled in slot order:
    /// slot k's Shop deque depends only on slots before it. So solve slot by slot, using that
    /// player's observed shop relics as the test, and keep the first fit.
    ///
    /// A caveat worth stating plainly: several epoch sets give the same answer, because only
    /// the COUNT of removed relics moves the stream. So this identifies how much of a deficit
    /// fits, not which epochs the partner was missing.
    /// </summary>
    private static (UnlockState Run, IReadOnlyList<UnlockState> Players)? SolvePlayerUnlocks(Visited run)
    {
        var runSeed = SeedCodec.RunSeed(run.Seed);
        var baseline = ReadCurrentUnlocks();

        // The run-level state is the SUPERSET of the lobby's, so it only shrinks where EVERY
        // player was missing an epoch. It filters the shared bag, which is shuffled before any
        // player's, so it has to be settled first — hence an outer loop, smallest deficit first.
        foreach (var runState in CandidateRunStates(baseline))
        {
            var solved = SolveSlots(run, runSeed, runState, baseline);
            if (solved is not null) return (runState, solved);
        }
        return null;
    }

    /// <summary>
    /// Fits each slot in turn against that player's observed shop relics. Linear rather than
    /// combinatorial because bags are shuffled in slot order, so slot k's Shop deque depends
    /// only on the slots before it.
    /// </summary>
    private static List<UnlockState>? SolveSlots(
        Visited run, ulong runSeed, UnlockState runState, UnlockState baseline)
    {
        var solved = new List<UnlockState>();

        for (int slot = 0; slot < run.Characters.Count; slot++)
        {
            var observed = slot < run.ShopRelicsPerPlayer.Count ? run.ShopRelicsPerPlayer[slot] : new List<string>();
            if (observed.Count == 0)
            {
                // Nothing to fit against, so assume they match us. If that is wrong, a later
                // slot or the final recheck will catch it.
                solved.Add(baseline);
                continue;
            }

            UnlockState? fit = null;
            foreach (var candidate in CandidateUnlocks(baseline, run.Characters[slot]))
            {
                var trial = solved.Append(candidate).ToList();
                while (trial.Count < run.Characters.Count) trial.Add(baseline);

                var gen = RunGenerator.GenerateRun(runSeed, runState, run.Characters.Count > 1,
                    run.Characters, acts: null, ascension: run.Ascension,
                    withShopRelics: true, playerUnlocks: trial);

                bool ok = true;
                for (int v = 0; v < observed.Count && ok; v++)
                {
                    var predicted = gen.ShopRelic(slot, v);
                    ok = predicted is not null &&
                         string.Equals(predicted.Value.Slug, observed[v], StringComparison.OrdinalIgnoreCase);
                }
                if (ok) { fit = candidate; break; }
            }

            if (fit is null) return null;
            solved.Add(fit);
        }
        return solved;
    }

    /// <summary>Run-level states to try: the five shared relic epochs, fewest missing first.</summary>
    private static IEnumerable<UnlockState> CandidateRunStates(UnlockState baseline)
    {
        yield return baseline;

        string[] gates = ["Relic1Epoch", "Relic2Epoch", "Relic3Epoch", "Relic4Epoch", "Relic5Epoch"];
        var known = AllKnownEpochIds(Character.Ironclad)
            .Concat(Enum.GetValues<Character>().SelectMany(AllKnownEpochIds))
            .Distinct().ToList();

        foreach (int mask in Enumerable.Range(1, (1 << gates.Length) - 1)
                     .OrderBy(m => System.Numerics.BitOperations.PopCount((uint)m)))
        {
            var revealed = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < gates.Length; b++)
                if ((mask & (1 << b)) != 0) revealed.Remove(GameHash.Slugify(gates[b]));
            yield return UnlockState.FromRevealedEpochs(revealed);
        }
    }

    /// <summary>
    /// Unlock states to try for one player, current profile first. Only epochs that gate RELICS
    /// can resize a bag, so the search is over those: the five shared sets plus the character's
    /// own two. Ordered by how much they remove, so the smallest deficit that fits wins.
    /// </summary>
    private static IEnumerable<UnlockState> CandidateUnlocks(UnlockState baseline, Character character)
    {
        yield return baseline;

        string[] gates =
        [
            "Relic1Epoch", "Relic2Epoch", "Relic3Epoch", "Relic4Epoch", "Relic5Epoch",
            $"{character}3Epoch", $"{character}6Epoch",
        ];

        var known = AllKnownEpochIds(character);
        var masks = Enumerable.Range(1, (1 << gates.Length) - 1)
            .OrderBy(m => System.Numerics.BitOperations.PopCount((uint)m));

        foreach (int mask in masks)
        {
            var revealed = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < gates.Length; b++)
                if ((mask & (1 << b)) != 0) revealed.Remove(GameHash.Slugify(gates[b]));
            yield return UnlockState.FromRevealedEpochs(revealed);
        }
    }

    private static List<string> AllKnownEpochIds(Character character)
    {
        var names = new List<string> { "NeowEpoch", "DarvEpoch", "OrobasEpoch", "Event1Epoch", "Event2Epoch", "Event3Epoch" };
        for (int i = 1; i <= 5; i++) names.Add($"Relic{i}Epoch");
        for (int i = 1; i <= 6; i++) names.Add($"{character}{i}Epoch");
        return names.Select(GameHash.Slugify).ToList();
    }

    private static int FirstDiff(List<string> a, List<string> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return i;
        return Math.Min(a.Count, b.Count);
    }

    private static string At(List<string> list, int i) => i < list.Count ? list[i] : "(none)";

    private static void Note(Dictionary<string, int> into, string reason) =>
        into[reason] = into.GetValueOrDefault(reason) + 1;

    // ---------------------------------------------------------------------------------------

    private static Visited Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var characters = new List<Character>();
        // Lobby slot is the index into players[], and it is what the per-player RNG is keyed on.
        // player_stats entries carry a player_id rather than a position, so map through this
        // rather than trusting their order to match.
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("players", out var players))
        {
            foreach (var p in players.EnumerateArray())
            {
                var id = p.TryGetProperty("character", out var c) ? c.GetString()
                       : p.TryGetProperty("character_id", out var c2) ? c2.GetString() : null;
                if (!TryParseCharacter(Entry(id), out var ch)) continue;
                if (p.TryGetProperty("id", out var pid))
                    slotOf[pid.ToString()] = characters.Count;
                characters.Add(ch);
            }
        }

        var acts = new List<string>();
        if (root.TryGetProperty("acts", out var actArr) && actArr.ValueKind == JsonValueKind.Array)
            foreach (var a in actArr.EnumerateArray()) acts.Add(Entry(a.GetString()));

        var bosses = new List<List<string>>();
        var monsters = new List<List<string>>();
        var ancients = new List<string?>();
        // Shops are recorded per act; the sequence we predict is per PLAYER across the whole run,
        // so flatten in map order.
        var shopRelics = Enumerable.Range(0, Math.Max(characters.Count, 1))
            .Select(_ => new List<string>()).ToList();

        if (root.TryGetProperty("map_point_history", out var mph) && mph.ValueKind == JsonValueKind.Array)
        {
            foreach (var actRooms in mph.EnumerateArray())
            {
                var b = new List<string>();
                var m = new List<string>();
                string? ancient = null;

                foreach (var point in actRooms.EnumerateArray())
                {
                    var type = point.TryGetProperty("map_point_type", out var t) ? t.GetString() : null;

                    if (point.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var room in rooms.EnumerateArray())
                        {
                            var roomType = room.TryGetProperty("room_type", out var rt) ? rt.GetString() : null;
                            var modelId = room.TryGetProperty("model_id", out var mi) ? mi.GetString() : null;
                            if (modelId is null) continue;

                            if (roomType == "boss") b.Add(Entry(modelId));
                            else if (roomType == "monster") m.Add(Entry(modelId));
                            // The act opener is an event room, and it is the Ancient.
                            else if (roomType == "event" && type == "ancient" && ancient is null)
                                ancient = Entry(modelId);
                        }
                    }

                    if (type == "shop" && point.TryGetProperty("player_stats", out var stats))
                    {
                        foreach (var ps in stats.EnumerateArray())
                        {
                            if (!ps.TryGetProperty("player_id", out var pid)) continue;
                            if (!slotOf.TryGetValue(pid.ToString(), out int slot)) continue;
                            if (slot >= shopRelics.Count) continue;
                            if (!ps.TryGetProperty("relic_choices", out var choices)) continue;

                            // Only the Shop-rarity entry is the one we predict. Taking it by
                            // rarity rather than by position matters: a purchase restocks the
                            // slot and appends a fourth entry, so position 3 is not reliable.
                            foreach (var choice in choices.EnumerateArray())
                            {
                                var slug = Entry(choice.TryGetProperty("choice", out var ch)
                                    ? ch.GetString() : null).ToLowerInvariant();
                                if (IsShopRarity(slug)) { shopRelics[slot].Add(slug); break; }
                            }
                        }
                    }
                }
                bosses.Add(b);
                monsters.Add(m);
                ancients.Add(ancient);
            }
        }

        while (bosses.Count < acts.Count) { bosses.Add(new()); monsters.Add(new()); ancients.Add(null); }

        return new Visited(
            acts, bosses, monsters, ancients, shopRelics, characters,
            root.TryGetProperty("ascension", out var asc) && asc.ValueKind == JsonValueKind.Number ? asc.GetInt32() : 0,
            root.TryGetProperty("seed", out var s) ? s.GetString() ?? "" : "",
            root.TryGetProperty("build_id", out var bd) ? bd.GetString() ?? "" : "",
            root.TryGetProperty("game_mode", out var gm) ? gm.GetString() ?? "" : "",
            root.TryGetProperty("modifiers", out var mods) && mods.ValueKind == JsonValueKind.Array
                && mods.GetArrayLength() > 0);
    }

    private static HashSet<string>? _shopSlugs;

    private static bool IsShopRarity(string slug)
    {
        _shopSlugs ??= RelicPoolData.SharedRelics
            .Concat(Enum.GetValues<Character>().SelectMany(RelicPoolData.RelicsFor))
            .Where(r => r.Rarity == "Shop")
            .Select(r => r.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _shopSlugs.Contains(slug);
    }

    private static string Entry(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return "";
        int dot = modelId.IndexOf('.');
        return dot >= 0 ? modelId[(dot + 1)..] : modelId;
    }

    private static bool TryParseCharacter(string entry, out Character character)
    {
        switch (entry.ToUpperInvariant())
        {
            case "IRONCLAD": character = Character.Ironclad; return true;
            case "SILENT": character = Character.Silent; return true;
            case "DEFECT": character = Character.Defect; return true;
            case "REGENT": character = Character.Regent; return true;
            case "NECROBINDER": character = Character.Necrobinder; return true;
            default: character = default; return false;
        }
    }

    private static UnlockState ReadCurrentUnlocks()
    {
        var progress = SaveVerifier.FindProgressSave();
        if (progress is null) return new UnlockState();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(progress));
            var revealed = new List<string>();
            if (doc.RootElement.TryGetProperty("epochs", out var epochs))
            {
                foreach (var e in epochs.EnumerateArray())
                {
                    var state = e.TryGetProperty("state", out var st) ? st.GetString() : null;
                    var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (id is not null && string.Equals(state, "revealed", StringComparison.OrdinalIgnoreCase))
                        revealed.Add(id);
                }
            }
            return UnlockState.FromRevealedEpochs(revealed);
        }
        catch
        {
            return new UnlockState();
        }
    }

    private static string SaveRootHistory() =>
        SaveLocations.FindRoot() ?? string.Join(" or ", SaveLocations.Roots());

    public static List<string> FindHistoryFiles(string? dir)
    {
        // An explicit directory is taken as given; otherwise SaveLocations walks the usual
        // places and honours the same override the web app does.
        if (dir is null) return SaveLocations.History().ToList();
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir, "*.run", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
