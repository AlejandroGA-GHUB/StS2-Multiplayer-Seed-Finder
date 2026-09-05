namespace Sts2.SeedFinder.Core.Modifiers;

/// <summary>
/// The sixteen run modifiers a Custom run can tick on, in the game's own list order.
///
/// That order is not cosmetic. The custom run screen builds its tickboxes from
/// <c>GoodModifiers.Concat(BadModifiers)</c> and collects them back in the same order, which
/// becomes <c>RunState.Modifiers</c>. Neow then walks that list and offers one forced option per
/// modifier that wants one, in sequence, so the position of a modifier decides how much of a
/// player's Rewards stream is already spent by the time its own option is taken. Reordering this
/// enum would silently move every prediction that depends on it.
///
/// <c>DeprecatedModifier</c> exists in the game and is deliberately absent here: it is a
/// placeholder for modifiers that have been retired, and it is in neither list.
/// </summary>
public enum RunModifier
{
    // GoodModifiers, in order.
    Draft,
    SealedDeck,
    Hoarder,
    Specialized,
    Insanity,
    AllStar,
    Flight,
    Vintage,
    CharacterCards,

    // BadModifiers, in order.
    DeadlyEvents,
    CursedRun,
    BigGameHunter,
    Midas,
    Murderous,
    NightTerrors,
    Terminal,
}

/// <summary>
/// What each modifier costs a player's <c>Rewards</c> stream at Neow, and what that means for
/// predicting the ones that draw from it.
///
/// Five modifiers add a forced Neow option, and three of those cost a known, fixed number of
/// draws because each is a plain uniform pick with no rarity roll and no upgrade roll:
/// Specialized takes one, AllStar five and Insanity thirty. The other two cannot be counted from
/// the seed alone. SealedDeck rolls rarities and accumulates a blacklist across thirty draws, and
/// Draft builds ten three-card rewards the player picks through, so neither has a draw count that
/// is fixed before the run happens.
/// </summary>
public static class RunModifiers
{
    /// <summary>Every modifier, in the game's list order.</summary>
    public static readonly RunModifier[] All = Enum.GetValues<RunModifier>();

    /// <summary>
    /// Draws this modifier's forced Neow option takes off the acting player's Rewards stream.
    /// Zero for the eleven that add no Neow option at all, and null for the two whose cost is
    /// not knowable in advance.
    /// </summary>
    public static int? NeowRewardDraws(RunModifier modifier) => modifier switch
    {
        RunModifier.Specialized => 1,
        RunModifier.AllStar => 5,
        RunModifier.Insanity => 30,

        // Rarity rolls and a running blacklist, and ten player-driven picks respectively.
        RunModifier.SealedDeck => null,
        RunModifier.Draft => null,

        _ => 0,
    };

    /// <summary>Whether this modifier makes Neow offer a forced option instead of its own.</summary>
    public static bool HasNeowOption(RunModifier modifier) =>
        NeowRewardDraws(modifier) != 0;

    /// <summary>
    /// Whether this modifier's payload is one the finder can predict. Only Specialized, for now:
    /// AllStar and Insanity have countable draws but their contents are five and thirty cards
    /// deep, and AllStar reads the colourless pool, which nothing here models.
    /// </summary>
    public static bool IsPredictable(RunModifier modifier) =>
        modifier == RunModifier.Specialized;

    /// <summary>
    /// How many Rewards draws are already spent by the time <paramref name="modifier"/> takes its
    /// own Neow option, given everything ticked on. Null means some modifier ahead of it has an
    /// uncountable cost, so nothing after it on that stream can be predicted.
    ///
    /// Only modifiers EARLIER in the list count, which is why the enum order is load-bearing.
    /// For Specialized that leaves exactly two spoilers, Draft and SealedDeck, and both of them
    /// only ever appear ahead of it.
    /// </summary>
    public static int? PriorRewardDraws(RunModifier modifier, IEnumerable<RunModifier> enabled)
    {
        int total = 0;
        foreach (var other in enabled)
        {
            if (other >= modifier) continue;

            int? cost = NeowRewardDraws(other);
            if (cost is null) return null;
            total += cost.Value;
        }
        return total;
    }

    /// <summary>
    /// Draws every enabled modifier's forced Neow option takes off ONE player's Rewards stream,
    /// before that player's first fight. Null when any of them has no fixed cost.
    ///
    /// The same for every slot: each player is walked through the same forced options on their
    /// own stream, so a Custom run shifts the whole lobby's card rewards by the same amount.
    /// </summary>
    public static int? TotalNeowRewardDraws(IEnumerable<RunModifier> enabled)
    {
        int total = 0;
        foreach (var m in enabled)
        {
            int? cost = NeowRewardDraws(m);
            if (cost is null) return null;
            total += cost.Value;
        }
        return total;
    }

    /// <summary>"SealedDeck" -> "sealed_deck". Matches the slug form used everywhere else.</summary>
    public static string Slug(RunModifier modifier) => GameHash.SnakeCase(modifier.ToString());

    /// <summary>"SealedDeck" -> "Sealed Deck".</summary>
    public static string Display(RunModifier modifier)
    {
        var name = modifier.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>Resolve a user-typed name, slug, or the save's own id form.</summary>
    public static RunModifier? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Run saves write "MODIFIER.SPECIALIZED"; take the entry off the end.
        var value = text.Trim();
        int dot = value.LastIndexOf('.');
        if (dot >= 0) value = value[(dot + 1)..];

        var wanted = Normalize(value);
        foreach (var m in All)
            if (Normalize(m.ToString()) == wanted || Normalize(Slug(m)) == wanted)
                return m;

        return null;
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
