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
    /// </summary>
    public bool AllCharactersUnlocked { get; init; } = true;

    /// <summary>
    /// Scroll Boxes requires >= 4 commons and >= 2 uncommons in your character's unlocked
    /// pool. True for any character with a normal unlock state.
    /// </summary>
    public bool ScrollBoxesAvailable { get; init; } = true;

    public bool IsMultiplayer => PlayerCount > 1;

    public bool IsAllowed(NeowRelic relic) => relic.Availability switch
    {
        RelicAvailability.SingleplayerOnly => !IsMultiplayer,
        RelicAvailability.MultiplayerOnly => IsMultiplayer,
        RelicAvailability.RequiresAllCharactersUnlocked => AllCharactersUnlocked,
        RelicAvailability.RequiresBundleableCardPool => ScrollBoxesAvailable,
        _ => true,
    };
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
