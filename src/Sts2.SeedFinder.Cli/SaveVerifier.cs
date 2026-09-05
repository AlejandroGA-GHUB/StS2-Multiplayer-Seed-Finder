using System.Text.Json;
using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Map;
using Sts2.SeedFinder.Core.Modifiers;
using Sts2.SeedFinder.Core.Saves;

namespace Sts2.SeedFinder.Cli;

/// <summary>
/// Differential test against a real run, using the game's own save file as the oracle.
///
/// The run save is plain JSON and records, for every act, the complete output of
/// RunManager.GenerateRooms: the shuffled event list, every normal and elite encounter in
/// draw order, the boss and the Ancient — plus the relic grab bags and the UpFront RNG's
/// serialized counter. That is every draw our generator makes, so one saved run checks the
/// whole chain rather than just the handful of things a player can see.
///
/// This is what makes Act 2/3 testable without a co-op partner: start a run, quit to the
/// menu, point this at the save. A singleplayer run exercises all the same machinery — the
/// only multiplayer deltas are the player count fed to the relic bags and one fewer room per
/// act — so it validates everything except those two toggles. Point it at current_run_mp.save
/// when a co-op run is available and it covers those too.
/// </summary>
public static class SaveVerifier
{
    private sealed record SaveRoomSet(
        List<string> Events, List<string> Normals, List<string> Elites,
        string? Boss, string? SecondBoss, string? Ancient);

    private sealed record SaveAct(string Act, SaveRoomSet Rooms);

    public static int Run(string? savePath, string? progressPath)
    {
        savePath ??= FindLatestRunSave();
        if (savePath is null)
        {
            Console.Error.WriteLine("No run save found. Start a run in-game, then quit to the menu.");
            Console.Error.WriteLine($"Expected under: {SaveRoot()}");
            return 1;
        }
        if (!File.Exists(savePath))
        {
            Console.Error.WriteLine($"No such file: {savePath}");
            return 1;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(savePath));
        var root = doc.RootElement;

        var seedString = root.GetProperty("rng").TryGetProperty("seed", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(seedString))
        {
            Console.Error.WriteLine("Save has no seed — cannot verify.");
            return 1;
        }

        var characters = new List<Character>();
        foreach (var p in root.GetProperty("players").EnumerateArray())
            characters.Add(ParseCharacter(Entry(p.GetProperty("character_id").GetString())));

        bool isMultiplayer = characters.Count > 1;
        var runSeed = SeedCodec.RunSeed(seedString!);

        // Reading the profile's real epoch state replaces our fully-unlocked assumption.
        var progress = progressPath ?? FindProgressSave();
        var unlocks = progress is not null ? ReadUnlocks(progress) : new UnlockState();

        var savedActs = ReadActs(root);

        // The save records the run's ascension, and A10 adds the final act's second boss —
        // one extra UpFront draw at the very end. Taking it from the save rather than a flag
        // means the counter check stays exact and the second boss is verified for free.
        int ascension = root.TryGetProperty("ascension", out var asc) && asc.ValueKind == JsonValueKind.Number
            ? asc.GetInt32() : 0;

        Console.WriteLine($"Save        {savePath}");
        Console.WriteLine($"Seed        {seedString}   (runSeed {runSeed})");
        Console.WriteLine($"Players     {characters.Count}: {string.Join(", ", characters)}");
        Console.WriteLine($"Mode        {(isMultiplayer ? "multiplayer" : "singleplayer")}");
        Console.WriteLine($"Ascension   {ascension}" +
                          (ascension >= AscensionLevels.DoubleBoss ? "  (Double Boss)" : ""));
        Console.WriteLine($"Acts        {string.Join(" -> ", savedActs.Select(a => a.Act))}");
        Console.WriteLine();

        int failures = 0;

        // --- Check 1: act selection (its own RNG, independent of the UpFront stream) ---
        var predictedActs = RunGenerator.SelectActs(runSeed, unlocks, isMultiplayer);
        var predictedActNames = predictedActs.Select(a => GameHash.Slugify(a.Name)).ToList();
        var savedActNames = savedActs.Select(a => a.Act).ToList();
        failures += Compare("act selection", savedActNames, predictedActNames);

        // Generate against the acts the game actually chose, so an act-selection miss does not
        // cascade into every downstream comparison and hide where the real divergence is.
        var actsForGeneration = savedActNames
            .Select(n => ActData.All.FirstOrDefault(a => GameHash.Slugify(a.Name) == n))
            .ToArray();
        if (actsForGeneration.Any(a => a is null))
        {
            Console.Error.WriteLine("Save references an act we do not know. Regenerate ActData.");
            return 1;
        }

        var run = RunGenerator.GenerateRun(
            runSeed, unlocks, isMultiplayer, characters, actsForGeneration!, ascension);

        // --- Check 2: relic grab bags -----------------------------------------------------
        // Only the sizes are ours to predict (we store counts, not orderings), but the sizes
        // ARE the draw accounting that everything downstream depends on. Relics already taken
        // shrink the saved bag, so a shortfall is only conclusive on a freshly started run.
        failures += CompareRelicBags(root, characters);

        // --- Check 3: per-act generation, draw for draw -----------------------------------
        for (int i = 0; i < savedActs.Count; i++)
        {
            var saved = savedActs[i];
            var mine = run.Acts[i];
            Console.WriteLine($"-- Act {i + 1}: {saved.Act}");

            failures += Compare("  events", saved.Rooms.Events, mine.Events.Select(GameHash.Slugify).ToList());
            failures += Compare("  normal encounters", saved.Rooms.Normals,
                mine.NormalEncounters.Select(e => GameHash.Slugify(e.Name)).ToList());
            failures += Compare("  elite encounters", saved.Rooms.Elites,
                mine.EliteEncounters.Select(e => GameHash.Slugify(e.Name)).ToList());
            failures += Compare("  boss", One(saved.Rooms.Boss), One(GameHash.Slugify(mine.Boss.Name)));
            // Only present at A10+, and only on the final act — but check it whenever EITHER
            // side has one, so drawing a second boss the run never had fails just as loudly
            // as missing one it did. Silent when both agree there is none.
            if (saved.Rooms.SecondBoss is not null || mine.SecondBoss is not null)
                failures += Compare("  second boss", One(saved.Rooms.SecondBoss),
                    One(mine.SecondBoss is null ? null : GameHash.Slugify(mine.SecondBoss.Name)));
            failures += Compare("  ancient", One(saved.Rooms.Ancient), One(GameHash.Slugify(mine.Ancient)));
            Console.WriteLine();
        }

        // --- Check 4: the UpFront counter -------------------------------------------------
        // A single integer covering every draw in generation. It keeps advancing during play,
        // so it can only be equal on a save taken before anything consumed UpFront.
        if (root.GetProperty("rng").TryGetProperty("rngs", out var rngs) &&
            TryGetIgnoreCase(rngs, "up_front", out var upFront) &&
            upFront.TryGetProperty("counter", out var counterEl))
        {
            int saved = counterEl.GetInt32();
            string verdict = saved == run.UpFrontDraws ? "MATCH"
                : saved > run.UpFrontDraws ? $"saved is ahead by {saved - run.UpFrontDraws} (expected if the run has progressed)"
                : $"MISMATCH — saved is BEHIND by {run.UpFrontDraws - saved}, we over-draw";
            Console.WriteLine($"UpFront draws: ours {run.UpFrontDraws}, saved counter {saved}: {verdict}");
            if (saved < run.UpFrontDraws) failures++;
            Console.WriteLine();
        }

        // --- Check 5: the act maps --------------------------------------------------------
        // Maps are built on ENTERING an act, not upfront, so a save only carries one for each act
        // the run has actually reached. Acts without one are skipped silently rather than counted
        // as passes, which is why a fresh run reports a single map here and a finished one three.
        //
        // This draws from its own stream ("act_n_map") and touches nothing above, so a failure
        // here never implicates the checks before it, and vice versa.
        var savedActElements = root.GetProperty("acts").EnumerateArray().ToList();
        for (int i = 0; i < savedActElements.Count && i < run.Acts.Count; i++)
        {
            var map = ActMap.Generate(
                runSeed, i, actsForGeneration[i]!, isMultiplayer, ascension,
                hasSecondBoss: run.Acts[i].SecondBoss is not null);

            failures += MapVerifier.Verify(savedActElements[i], map, $"Act {i + 1}: {savedActs[i].Act}");
        }

        // --- Check 6: the Specialized modifier's starting card -----------------------------
        failures += VerifySpecialized(root, runSeed, characters, unlocks, isMultiplayer);

        Console.WriteLine(failures == 0
            ? "ALL CHECKS PASSED — generation matches the game for this run."
            : $"{failures} check(s) FAILED.");
        return failures == 0 ? 0 : 2;
    }

    /// <summary>
    /// Only runs for a Custom run with Specialized ticked on, and prints nothing otherwise.
    ///
    /// The oracle here is not an RNG counter but the deck itself: Specialized puts five copies of
    /// one card into a player's starting deck, so a deck holding exactly one card five times over
    /// is the answer, and the starting Strikes and Defends are what the count has to see past. A
    /// character starts with four or five Strikes, so five alone would not identify it — this
    /// looks for the non-basic card instead, which is unambiguous.
    /// </summary>
    private static int VerifySpecialized(
        JsonElement root, ulong runSeed, List<Character> characters,
        UnlockState unlocks, bool isMultiplayer)
    {
        var enabled = new List<RunModifier>();
        if (root.TryGetProperty("modifiers", out var mods) && mods.ValueKind == JsonValueKind.Array)
            foreach (var m in mods.EnumerateArray())
                if (m.TryGetProperty("id", out var id)
                    && RunModifiers.TryParse(id.GetString() ?? "") is { } parsed)
                    enabled.Add(parsed);

        if (!enabled.Contains(RunModifier.Specialized)) return 0;

        Console.WriteLine();
        Console.WriteLine($"-- Specialized  (modifiers: {string.Join(", ", enabled.Select(RunModifiers.Display))})");

        if (RunModifiers.PriorRewardDraws(RunModifier.Specialized, enabled) is not { } prior)
        {
            Console.WriteLine("[SKIP] Draft or Sealed Deck is also on, so where Specialized lands "
                              + "in the Rewards stream is not knowable.");
            return 0;
        }

        var players = root.GetProperty("players").EnumerateArray().ToList();
        int failures = 0;

        for (int slot = 0; slot < players.Count && slot < characters.Count; slot++)
        {
            var predicted = SpecializedPayload.Predict(
                runSeed, slot, characters[slot], unlocks, isMultiplayer, prior);

            var actual = FiveOfAKind(players[slot]);
            if (actual is null)
            {
                // Before the player clicks through Neow the cards are simply not there yet.
                Console.WriteLine($"[SKIP] P{slot + 1}: no card appears five times in the deck yet "
                                  + "(the Neow option has not been taken).");
                continue;
            }

            bool ok = predicted is not null
                      && string.Equals(CardCatalog.Slug(predicted.TypeName), actual, StringComparison.OrdinalIgnoreCase);

            Console.WriteLine(ok
                ? $"[PASS] P{slot + 1} ({characters[slot]}): 5x {CardCatalog.Display(predicted!.TypeName)}"
                : $"[FAIL] P{slot + 1} ({characters[slot]}): game {actual}, "
                  + $"ours {(predicted is null ? "nothing" : CardCatalog.Slug(predicted.TypeName))}");

            if (!ok) failures++;
        }

        return failures;
    }

    /// <summary>
    /// The slug of the one non-basic card this player holds five copies of, or null if there
    /// isn't one. Basics are excluded because a starting deck already carries five Strikes.
    /// </summary>
    private static string? FiveOfAKind(JsonElement player)
    {
        if (!player.TryGetProperty("deck", out var deck) || deck.ValueKind != JsonValueKind.Array)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in deck.EnumerateArray())
        {
            var id = card.ValueKind == JsonValueKind.Object
                ? (card.TryGetProperty("id", out var v) ? v.GetString() : null)
                : card.GetString();
            if (id is null) continue;

            var slug = Entry(id).ToLowerInvariant();
            counts[slug] = counts.GetValueOrDefault(slug) + 1;
        }

        foreach (var (slug, n) in counts)
        {
            if (n < 5) continue;
            if (slug.StartsWith("strike", StringComparison.Ordinal)
                || slug.StartsWith("defend", StringComparison.Ordinal)) continue;
            return slug;
        }
        return null;
    }

    private static List<string> One(string? v) => v is null ? new List<string>() : new List<string> { v };

    /// <summary>Prints a verdict and, on failure, the first index where the two lists diverge.</summary>
    private static int Compare(string label, List<string> expected, List<string> actual)
    {
        if (expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[PASS] {label} ({expected.Count})");
            return 0;
        }

        Console.WriteLine($"[FAIL] {label}: game {expected.Count} entries, ours {actual.Count}");
        int n = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < n; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"       first divergence at [{i}]: game {expected[i]}, ours {actual[i]}");
                Console.WriteLine($"       game: {string.Join(", ", expected.Skip(Math.Max(0, i - 2)).Take(6))}");
                Console.WriteLine($"       ours: {string.Join(", ", actual.Skip(Math.Max(0, i - 2)).Take(6))}");
                return 1;
            }
        }
        Console.WriteLine($"       lists agree for {n} entries, then differ in length");
        return 1;
    }

    private static int CompareRelicBags(JsonElement root, IReadOnlyList<Character> characters)
    {
        int failures = 0;

        if (root.TryGetProperty("shared_relic_grab_bag", out var sharedBag))
            failures += CompareBagSizes("shared relic bag", sharedBag, Tally(RelicPoolData.SharedRelics, filtered: false));

        var players = root.GetProperty("players").EnumerateArray().ToList();
        for (int i = 0; i < players.Count && i < characters.Count; i++)
        {
            if (!players[i].TryGetProperty("relic_grab_bag", out var bag)) continue;

            var expected = Tally(
                RelicPoolData.SharedRelics.Concat(RelicPoolData.RelicsFor(characters[i])), filtered: true);
            failures += CompareBagSizes($"P{i + 1} ({characters[i]}) relic bag", bag, expected);
        }
        Console.WriteLine();
        return failures;
    }

    /// <summary>Relics per rarity. The shared bag keeps every rarity; a player's bag is filtered.</summary>
    private static Dictionary<string, int> Tally(IEnumerable<PoolRelic> pool, bool filtered)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relic in pool)
        {
            if (filtered && Array.IndexOf(RelicPoolData.GrabBagRarities, relic.Rarity) < 0) continue;
            counts[relic.Rarity] = counts.GetValueOrDefault(relic.Rarity) + 1;
        }
        return counts;
    }

    private static int CompareBagSizes(string label, JsonElement bag, IReadOnlyDictionary<string, int> expected)
    {
        if (!bag.TryGetProperty("relic_id_lists", out var lists)) return 0;

        var actual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in lists.EnumerateObject())
            actual[entry.Name] = entry.Value.GetArrayLength();

        var parts = new List<string>();
        bool ok = true;
        foreach (var (rarity, count) in expected.OrderBy(kv => kv.Key))
        {
            actual.TryGetValue(rarity, out int got);
            // The bag only shrinks as relics are handed out, so more than we predict is a
            // genuine miscount; fewer is expected once the run has progressed.
            if (got > count) ok = false;
            parts.Add($"{rarity} {got}/{count}");
        }
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}: {string.Join(", ", parts)}  (saved/predicted)");
        return ok ? 0 : 1;
    }

    private static List<SaveAct> ReadActs(JsonElement root)
    {
        var acts = new List<SaveAct>();
        foreach (var act in root.GetProperty("acts").EnumerateArray())
        {
            var rooms = act.GetProperty("rooms");
            acts.Add(new SaveAct(
                Entry(act.GetProperty("id").GetString()),
                new SaveRoomSet(
                    Entries(rooms, "event_ids"),
                    Entries(rooms, "normal_encounter_ids"),
                    Entries(rooms, "elite_encounter_ids"),
                    rooms.TryGetProperty("boss_id", out var b) ? Entry(b.GetString()) : null,
                    rooms.TryGetProperty("second_boss_id", out var sb) && sb.ValueKind != JsonValueKind.Null
                        ? Entry(sb.GetString()) : null,
                    rooms.TryGetProperty("ancient_id", out var a) ? Entry(a.GetString()) : null)));
        }
        return acts;
    }

    private static List<string> Entries(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => Entry(e.GetString())).ToList()
            : new List<string>();

    /// <summary>ModelIds serialize as "TYPE.ENTRY"; we only ever compare the entry.</summary>
    private static string Entry(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return "";
        int dot = modelId.IndexOf('.');
        return dot >= 0 ? modelId[(dot + 1)..] : modelId;
    }

    private static Character ParseCharacter(string entry) => entry.ToUpperInvariant() switch
    {
        "IRONCLAD" => Character.Ironclad,
        "SILENT" => Character.Silent,
        "DEFECT" => Character.Defect,
        "REGENT" => Character.Regent,
        "NECROBINDER" => Character.Necrobinder,
        _ => throw new InvalidOperationException($"Unknown character in save: {entry}"),
    };

    private static UnlockState ReadUnlocks(string progressPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(progressPath));
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

    private static bool TryGetIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name.Replace("_", ""), name.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Where saves are looked for, for the "I could not find one" message. Discovery itself
    /// lives in <see cref="SaveLocations"/> so the CLI and the web app agree on the answer and
    /// honour the same override.
    /// </summary>
    private static string SaveRoot() =>
        SaveLocations.FindRoot() ?? string.Join(" or ", SaveLocations.Roots());

    /// <summary>Newest current_run.save / current_run_mp.save across every account and profile.</summary>
    public static string? FindLatestRunSave() => SaveLocations.CurrentRun();

    public static string? FindProgressSave() => SaveLocations.ProgressSave();
}
