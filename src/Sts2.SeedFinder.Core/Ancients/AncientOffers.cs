namespace Sts2.SeedFinder.Core.Ancients;

/// <summary>The act-opening NPCs. Darv is shared and can land in either act 2 or act 3.</summary>
public enum Ancient { Darv, Nonupeipe, Orobas, Pael, Tanx, Tezcatara, Vakuu }

/// <summary>
/// Inputs an Ancient's offer depends on that the seed does not determine. Most are deck
/// state at the moment you meet them, which no seed finder can know ahead of time — so
/// rather than guess, we enumerate the branches (see <see cref="AncientOffers.Branches"/>).
/// </summary>
public sealed record AncientContext
{
    /// <summary>Tezcatara adds Nutritious Soup when your deck still holds a basic Strike.</summary>
    public bool DeckHasBasicStrike { get; init; } = true;

    /// <summary>Tanx adds Tri-Boomerang at 3+ Instinct-enchantable cards.</summary>
    public bool DeckHasThreeInstinctCards { get; init; } = true;

    /// <summary>Nonupeipe adds Beautiful Bracelet at 4+ Swift-enchantable cards.</summary>
    public bool DeckHasFourSwiftCards { get; init; } = true;

    /// <summary>Pael adds Pael's Claw at 3+ Goopy-enchantable cards.</summary>
    public bool DeckHasThreeGoopyCards { get; init; } = true;

    /// <summary>Pael adds Pael's Tooth at 5+ removable cards. Effectively always true.</summary>
    public bool DeckHasFiveRemovableCards { get; init; } = true;

    /// <summary>Pael offers Pael's Legion only when you do NOT already have an event pet.</summary>
    public bool HasEventPet { get; init; }

    /// <summary>
    /// Orobas offers Archaic Tooth only while the transcendence starter card is still in
    /// your deck; Touch of Orobas needs a starter relic, which every character has.
    /// </summary>
    public bool ArchaicToothAvailable { get; init; } = true;

    /// <summary>Darv drops Pandora's Box under a run modifier that clears your deck.</summary>
    public bool DeckClearedByModifier { get; init; }

    /// <summary>Orobas picks another character to theme Sea Glass after. All 5 by default.</summary>
    public int UnlockedCharacterCount { get; init; } = 5;

    /// <summary>Zero-based act. Only Darv reads it, and only for two relic gates.</summary>
    public int ActIndex { get; init; } = 1;
}

/// <summary>The three options an Ancient presents, in the order the game returns them.</summary>
public sealed record AncientOffer(Ancient Ancient, IReadOnlyList<string> Options)
{
    public override string ToString() => string.Join(" | ", Options.Select(AncientOffers.Display));
}

/// <summary>
/// Predicts what each Ancient offers a given player slot.
///
/// Every Ancient inherits <c>EventModel.IsShared => false</c> — none of them override it —
/// so each player rolls their own offer off the same per-player event RNG that Neow uses:
/// <c>runSeed + slotIndex + XXH64(Slugify(typeName))</c>. That stream is independent of the
/// UpFront stream driving act generation, so these predictions stand on their own.
///
/// The draw ORDER below is transcribed from each Ancient's GenerateInitialOptions and is the
/// load-bearing part: a pool that is conditionally larger does not merely add an option, it
/// shifts every later draw.
/// </summary>
public static class AncientOffers
{
    public static ulong RngSeed(ulong runSeed, int playerSlotIndex, Ancient ancient) =>
        unchecked((ulong)((long)runSeed + playerSlotIndex)
                  + GameHash.Deterministic(GameHash.Slugify(ancient.ToString())));

    public static AncientOffer Predict(Ancient ancient, ulong runSeed, int playerSlotIndex, AncientContext ctx)
    {
        var rng = new Rng(RngSeed(runSeed, playerSlotIndex, ancient));
        var options = ancient switch
        {
            Ancient.Vakuu => Vakuu(rng),
            Ancient.Tanx => Tanx(rng, ctx),
            Ancient.Nonupeipe => Nonupeipe(rng, ctx),
            Ancient.Tezcatara => Tezcatara(rng, ctx),
            Ancient.Pael => Pael(rng, ctx),
            Ancient.Orobas => Orobas(rng, ctx),
            Ancient.Darv => Darv(rng, ctx),
            _ => throw new ArgumentOutOfRangeException(nameof(ancient)),
        };
        return new AncientOffer(ancient, options);
    }

    /// <summary>
    /// Every distinct offer this Ancient can produce on this seed, labelled with the
    /// condition that produces it. One entry means the seed pins it down completely.
    /// </summary>
    public static IReadOnlyList<(string Condition, AncientOffer Offer)> Branches(
        Ancient ancient, ulong runSeed, int playerSlotIndex, AncientContext baseCtx)
    {
        // Several deck states usually collapse onto the same offer. Group by outcome and say
        // how many states reach it, so a single printed condition is not read as the only one.
        var grouped = new List<(List<string> Labels, AncientOffer Offer)>();
        int total = 0;
        foreach (var (label, ctx) in ContextVariants(ancient, baseCtx))
        {
            total++;
            var offer = Predict(ancient, runSeed, playerSlotIndex, ctx);
            var existing = grouped.FirstOrDefault(g => g.Offer.Options.SequenceEqual(offer.Options));
            if (existing.Labels is not null) existing.Labels.Add(label);
            else grouped.Add((new List<string> { label }, offer));
        }

        return grouped
            .Select(g => (
                Condition: g.Labels.Count == 1 || total == 1
                    ? g.Labels[0]
                    : $"{g.Labels[0]}, +{g.Labels.Count - 1} more of {total} deck states",
                g.Offer))
            .ToList();
    }

    /// <summary>The deck/run conditions that actually change this Ancient's pools.</summary>
    private static IEnumerable<(string Label, AncientContext Ctx)> ContextVariants(
        Ancient ancient, AncientContext b)
    {
        switch (ancient)
        {
            case Ancient.Vakuu:
                yield return ("always", b);
                break;

            case Ancient.Tezcatara:
                yield return ("deck has a basic Strike", b with { DeckHasBasicStrike = true });
                yield return ("no basic Strike left", b with { DeckHasBasicStrike = false });
                break;

            case Ancient.Tanx:
                yield return ("3+ Instinct-enchantable cards", b with { DeckHasThreeInstinctCards = true });
                yield return ("fewer than 3", b with { DeckHasThreeInstinctCards = false });
                break;

            case Ancient.Nonupeipe:
                yield return ("4+ Swift-enchantable cards", b with { DeckHasFourSwiftCards = true });
                yield return ("fewer than 4", b with { DeckHasFourSwiftCards = false });
                break;

            case Ancient.Orobas:
                yield return ("Archaic Tooth available", b with { ArchaicToothAvailable = true });
                yield return ("starter card removed", b with { ArchaicToothAvailable = false });
                break;

            case Ancient.Pael:
                foreach (var goopy in new[] { true, false })
                foreach (var tooth in new[] { true, false })
                foreach (var pet in new[] { false, true })
                    yield return (
                        $"{(goopy ? "3+ Goopy" : "<3 Goopy")}, {(tooth ? "5+ removable" : "<5 removable")}, {(pet ? "has pet" : "no pet")}",
                        b with { DeckHasThreeGoopyCards = goopy, DeckHasFiveRemovableCards = tooth, HasEventPet = pet });
                break;

            case Ancient.Darv:
                yield return ("standard run", b);
                break;
        }
    }

    // ---- The per-Ancient draw sequences -------------------------------------------------

    /// <summary>Three independent pools, each shuffled, first entry of each taken.</summary>
    private static List<string> Vakuu(Rng rng)
    {
        var p1 = AncientData.VakuuPool1.ToList();
        var p2 = AncientData.VakuuPool2.ToList();
        var p3 = AncientData.VakuuPool3.ToList();
        Shuffle(p1, rng);
        Shuffle(p2, rng);
        Shuffle(p3, rng);
        return new List<string> { p1[0], p2[0], p3[0] };
    }

    /// <summary>One pool, conditionally grown, shuffled, first three taken.</summary>
    private static List<string> Tanx(Rng rng, AncientContext ctx)
    {
        var pool = AncientData.TanxBaseOptionPool.ToList();
        if (ctx.DeckHasThreeInstinctCards) pool.Add(AncientData.TanxTriBoomerangOption[0]);
        Shuffle(pool, rng);
        return pool.Take(3).ToList();
    }

    private static List<string> Nonupeipe(Rng rng, AncientContext ctx)
    {
        var pool = AncientData.NonupeipeOptionPool.ToList();
        if (ctx.DeckHasFourSwiftCards) pool.Add(AncientData.NonupeipeBeautifulBraceletEventOption[0]);
        Shuffle(pool, rng);
        return pool.Take(3).ToList();
    }

    /// <summary>Three pools, one draw from each. Only the first pool is conditional.</summary>
    private static List<string> Tezcatara(Rng rng, AncientContext ctx)
    {
        var p1 = AncientData.TezcataraOptionPool1.ToList();
        if (ctx.DeckHasBasicStrike) p1.Add(AncientData.TezcataraNutritiousSoupOption[0]);
        return new List<string>
        {
            rng.NextItem(p1)!,
            rng.NextItem(AncientData.TezcataraOptionPool2)!,
            rng.NextItem(AncientData.TezcataraOptionPool3)!,
        };
    }

    /// <summary>
    /// Pael's second pool is doubled before Pael's Growth is appended, so Growth is offered
    /// at half the weight of everything else in that slot. That AddRange-onto-itself is not
    /// a decompiler artefact — it is how the weighting is expressed.
    /// </summary>
    private static List<string> Pael(Rng rng, AncientContext ctx)
    {
        var first = rng.NextItem(AncientData.PaelOptionPool1)!;

        var pool2 = AncientData.PaelOptionPool2.ToList();
        if (ctx.DeckHasThreeGoopyCards) pool2.Add(AncientData.PaelPaelsClawOption[0]);
        if (ctx.DeckHasFiveRemovableCards) pool2.Add(AncientData.PaelPaelsToothOption[0]);
        pool2.AddRange(pool2.ToList());
        pool2.Add(AncientData.PaelPaelsGrowthOption[0]);
        var second = rng.NextItem(pool2)!;

        var pool3 = AncientData.PaelOptionPool3.ToList();
        if (!ctx.HasEventPet) pool3.Add(AncientData.PaelPaelsLegionOption[0]);
        var third = rng.NextItem(pool3)!;

        return new List<string> { first, second, third };
    }

    /// <summary>
    /// Orobas draws a second character first (to theme Sea Glass), then a coin-ish float,
    /// before any option is picked. Both consume draws whether or not they change the result.
    /// </summary>
    private static List<string> Orobas(Rng rng, AncientContext ctx)
    {
        // NextItem over the unlocked characters excluding your own.
        var otherCharacters = Math.Max(0, ctx.UnlockedCharacterCount - 1);
        if (otherCharacters > 0) rng.NextInt(0, otherCharacters);

        var pool1 = AncientData.OrobasOptionPool1.ToList();
        pool1.Add(rng.NextFloat() < 0.3333333f ? AncientData.OrobasPrismaticGemOption[0] : "SeaGlass");

        var pool3 = new List<string> { "TouchOfOrobas" };
        if (ctx.ArchaicToothAvailable) pool3.Add("ArchaicTooth");

        return new List<string>
        {
            rng.NextItem(pool1)!,
            rng.NextItem(AncientData.OrobasOptionPool2)!,
            rng.NextItem(pool3)!,
        };
    }

    /// <summary>
    /// Darv draws once per eligible relic set — a one-relic set still costs a draw — then
    /// shuffles the picks, then flips for whether Dusty Tome replaces the third.
    /// </summary>
    private static List<string> Darv(Rng rng, AncientContext ctx)
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
            if (allowed) picks.Add(rng.NextItem(relics)!);
        }
        Shuffle(picks, rng);

        if (rng.NextBool())
        {
            var withTome = picks.Take(2).ToList();
            withTome.Add(AncientData.DarvBonusRelic);
            return withTome;
        }
        return picks.Take(3).ToList();
    }

    /// <summary>ListExtensions.UnstableShuffle — reverse Fisher-Yates, n-1 draws.</summary>
    private static void Shuffle<T>(List<T> list, Rng rng)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }

    // ---- Naming ------------------------------------------------------------------------

    /// <summary>Which Ancients open which act. Darv is shared and can appear in 2 or 3.</summary>
    public static IReadOnlyList<Ancient> ForAct(int actIndex) => actIndex switch
    {
        1 => new[] { Ancient.Orobas, Ancient.Pael, Ancient.Tezcatara, Ancient.Darv },
        2 => new[] { Ancient.Nonupeipe, Ancient.Tanx, Ancient.Vakuu, Ancient.Darv },
        _ => Array.Empty<Ancient>(),
    };

    public static bool TryParse(string name, out Ancient ancient) =>
        Enum.TryParse(name.Replace("_", "").Replace("-", ""), ignoreCase: true, out ancient);

    /// <summary>"BloodSoakedRose" -> "Blood Soaked Rose". Derived from type names, not loc data.</summary>
    public static string Display(string typeName)
    {
        var sb = new System.Text.StringBuilder(typeName.Length + 8);
        for (int i = 0; i < typeName.Length; i++)
        {
            if (i > 0 && char.IsUpper(typeName[i]) && !char.IsUpper(typeName[i - 1])) sb.Append(' ');
            sb.Append(typeName[i]);
        }
        return sb.ToString();
    }

    public static string Slug(string typeName) => GameHash.SnakeCase(typeName);

    /// <summary>Every relic any Ancient can offer, for --list and for validating criteria.</summary>
    public static IReadOnlyList<string> AllRelics(Ancient ancient) => ancient switch
    {
        Ancient.Vakuu => Concat(AncientData.VakuuPool1, AncientData.VakuuPool2, AncientData.VakuuPool3),
        Ancient.Tanx => Concat(AncientData.TanxBaseOptionPool, AncientData.TanxTriBoomerangOption),
        Ancient.Nonupeipe => Concat(AncientData.NonupeipeOptionPool, AncientData.NonupeipeBeautifulBraceletEventOption),
        Ancient.Tezcatara => Concat(AncientData.TezcataraOptionPool1, AncientData.TezcataraOptionPool2,
            AncientData.TezcataraOptionPool3, AncientData.TezcataraNutritiousSoupOption),
        Ancient.Pael => Concat(AncientData.PaelOptionPool1, AncientData.PaelOptionPool2, AncientData.PaelOptionPool3,
            AncientData.PaelPaelsClawOption, AncientData.PaelPaelsToothOption,
            AncientData.PaelPaelsGrowthOption, AncientData.PaelPaelsLegionOption),
        Ancient.Orobas => Concat(AncientData.OrobasOptionPool1, AncientData.OrobasOptionPool2,
            AncientData.OrobasPrismaticGemOption, new[] { "SeaGlass", "TouchOfOrobas", "ArchaicTooth" }),
        Ancient.Darv => AncientData.DarvRelicSets.SelectMany(s => s.Relics)
            .Append(AncientData.DarvBonusRelic).Distinct().ToList(),
        _ => Array.Empty<string>(),
    };

    private static List<string> Concat(params string[][] pools) =>
        pools.SelectMany(p => p).Distinct().ToList();
}
