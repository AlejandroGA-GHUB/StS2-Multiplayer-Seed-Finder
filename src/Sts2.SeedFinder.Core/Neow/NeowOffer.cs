namespace Sts2.SeedFinder.Core.Neow;

/// <summary>
/// Account/lobby state that changes which relics Neow may offer. These are genuine
/// inputs to generation, not cosmetic settings — get them wrong and predictions are wrong.
/// </summary>
public sealed record NeowContext
{
    public required int PlayerCount { get; init; }

    /// <summary>
    /// Kaleidoscope requires every character's card pool unlocked
    /// (UnlockState.CharacterCardPools.Count() == ModelDb.AllCharacters.Count()).
    ///
    /// A lobby-wide override: false takes it away from everyone, whatever
    /// <see cref="AllCharactersUnlockedBySlot"/> says. It is what <c>--not-all-characters</c>
    /// sets, for a caller that knows the answer but has no unlock states to derive it from.
    /// </summary>
    public bool AllCharactersUnlocked { get; init; } = true;

    /// <summary>
    /// The same fact per player, in lobby order, or null when every player is assumed to have
    /// it. Kaleidoscope's <c>IsAllowedAtNeow</c> reads the player Neow is making the offer to,
    /// not the run, so one lobby can offer it to some players and not to others — and a player
    /// who cannot be offered it has a positive pool one relic shorter, which reshuffles every
    /// positive option they are shown. Build it from each slot's own
    /// <see cref="Acts.UnlockState.AllCharactersUnlocked"/>.
    /// </summary>
    public IReadOnlyList<bool>? AllCharactersUnlockedBySlot { get; init; }

    /// <summary>
    /// Scroll Boxes requires >= 4 commons and >= 2 uncommons in your character's unlocked
    /// pool. True for any character with a normal unlock state.
    /// </summary>
    public bool ScrollBoxesAvailable { get; init; } = true;

    public bool IsMultiplayer => PlayerCount > 1;

    /// <summary>Whether the player in <paramref name="slot"/> has every character unlocked.</summary>
    public bool AllCharactersUnlockedFor(int slot) =>
        AllCharactersUnlocked
        && (AllCharactersUnlockedBySlot is not { } bySlot || (uint)slot >= (uint)bySlot.Count || bySlot[slot]);

    /// <summary>
    /// Whether Neow can offer this relic to the player in <paramref name="slot"/>.
    ///
    /// Named apart from <see cref="IsAllowed(NeowRelic)"/> on purpose: as an overload it made
    /// <c>.Where(ctx.IsAllowed)</c> ambiguous with LINQ's index-taking <c>Where</c>, and had it
    /// resolved the other way it would have read each relic's list position as a slot.
    /// </summary>
    public bool IsAllowedFor(NeowRelic relic, int slot) => relic.Availability switch
    {
        RelicAvailability.SingleplayerOnly => !IsMultiplayer,
        RelicAvailability.MultiplayerOnly => IsMultiplayer,
        RelicAvailability.RequiresAllCharactersUnlocked => AllCharactersUnlockedFor(slot),
        RelicAvailability.RequiresBundleableCardPool => ScrollBoxesAvailable,
        _ => true,
    };

    /// <summary>
    /// Whether Neow can offer this relic to EVERY player in the lobby. The right question for
    /// anything that builds one pool for the whole lobby; a per-player offer wants
    /// <see cref="IsAllowedFor"/>.
    /// </summary>
    public bool IsAllowed(NeowRelic relic)
    {
        for (int slot = 0; slot < Math.Max(PlayerCount, 1); slot++)
            if (!IsAllowedFor(relic, slot)) return false;
        return true;
    }

    /// <summary>Whether Neow can offer this relic to at least one player in the lobby.</summary>
    public bool IsAllowedForAnyone(NeowRelic relic)
    {
        for (int slot = 0; slot < Math.Max(PlayerCount, 1); slot++)
            if (IsAllowedFor(relic, slot)) return true;
        return false;
    }

    /// <summary>
    /// Whether players are offered from different pools. Only per-player availability can make
    /// them differ, and only then does a single lobby-wide pool stop describing everybody.
    /// </summary>
    public bool PoolsDifferBySlot
    {
        get
        {
            for (int slot = 1; slot < PlayerCount; slot++)
                if (AllCharactersUnlockedFor(slot) != AllCharactersUnlockedFor(0)) return true;
            return false;
        }
    }
}

/// <summary>
/// The three options Neow presents to one player: one curse-branch relic and two positives.
/// Presentation order matches the game's (positives first, then the curse).
/// </summary>
public sealed record NeowOffer(NeowRelic Curse, NeowRelic Positive1, NeowRelic Positive2)
{
    public IEnumerable<NeowRelic> All => new[] { Positive1, Positive2, Curse };

    public bool Contains(NeowRelic relic) => All.Contains(relic);

    public override string ToString() => $"{Positive1} | {Positive2} | {Curse} (curse)";
}
