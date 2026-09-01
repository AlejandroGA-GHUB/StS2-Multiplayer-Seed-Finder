using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;

namespace Sts2.SeedFinder.Core.Saves;

/// <summary>
/// One account's revealed epochs as a short string, so a lobby can be predicted without every
/// player handing over their save file.
///
/// This exists because unlock state is PER PLAYER and only the local profile is readable — a
/// partner's lives on their machine. <c>RunGenerator.GenerateRun</c> takes each player's own
/// state for their own relic bag, and those bags are shuffled off the shared UpFront stream in
/// lobby order, so a partner whose pools are a different size moves every draw after their bag:
/// the bosses, the events and the Ancients, for the whole party rather than for them alone.
///
/// The code is a bitmask over <see cref="Epochs"/>, prefixed with how many epochs that build
/// knew about. The prefix is the point: a patch that adds an epoch shifts every bit after it, so
/// a code minted by another build must be refused rather than silently decoded into a state
/// nobody has. Refusing costs a re-import; accepting costs a wrong run.
/// </summary>
public static class UnlockCode
{
    /// <summary>
    /// Every epoch that gates something this tool models, in a fixed order. Sorted by name
    /// rather than by discovery order so the ordering is a property of the set and not of how
    /// the tables happen to be laid out.
    ///
    /// The three sources are the three kinds of gate: the six named flags on
    /// <see cref="UnlockState"/> (Ancients and events), the relic pools, and the card pools.
    /// An epoch outside this list gates nothing we predict, so a code that cannot carry it
    /// loses nothing.
    /// </summary>
    public static IReadOnlyList<string> Epochs => AllEpochs;

    private static readonly string[] AllEpochs = BuildEpochs();

    private static string[] BuildEpochs()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal)
        {
            "NeowEpoch", "DarvEpoch", "OrobasEpoch", "Event1Epoch", "Event2Epoch", "Event3Epoch",
        };

        foreach (var pool in RelicPoolData.EpochGates.Values)
            foreach (var epoch in pool.Keys) names.Add(epoch);
        foreach (var pool in CardPoolData.EpochGates.Values)
            foreach (var epoch in pool.Keys) names.Add(epoch);

        if (names.Count > 64)
            throw new InvalidOperationException(
                $"{names.Count} epochs will not fit a 64-bit code. Widen the encoding.");

        return names.ToArray();
    }

    /// <summary>The account's state as a code, for example "36-fffffffff".</summary>
    public static string Encode(UnlockState state)
    {
        ulong mask = 0;
        for (int i = 0; i < AllEpochs.Length; i++)
            if (state.IsEpochRevealed(AllEpochs[i])) mask |= 1uL << i;

        return $"{AllEpochs.Length}-{mask:x}";
    }

    /// <summary>
    /// The state a code describes, or null when it is malformed or was minted by a build that
    /// counted a different number of epochs.
    /// </summary>
    public static UnlockState? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        int dash = code.IndexOf('-');
        if (dash <= 0 || dash == code.Length - 1) return null;

        if (!int.TryParse(code.AsSpan(0, dash), out int count) || count != AllEpochs.Length)
            return null;
        if (!ulong.TryParse(code.AsSpan(dash + 1), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out ulong mask))
            return null;

        // FromRevealedEpochs matches on the slugified id, which is the form a save file uses.
        var revealed = new List<string>(AllEpochs.Length);
        for (int i = 0; i < AllEpochs.Length; i++)
            if ((mask & (1uL << i)) != 0) revealed.Add(GameHash.Slugify(AllEpochs[i]));

        return UnlockState.FromRevealedEpochs(revealed);
    }

    /// <summary>How many of <see cref="Epochs"/> the state has revealed.</summary>
    public static int RevealedCount(UnlockState state)
    {
        int n = 0;
        foreach (var epoch in AllEpochs) if (state.IsEpochRevealed(epoch)) n++;
        return n;
    }

    /// <summary>
    /// The epochs the state has NOT revealed, named for a reader. This is what a partner's
    /// import is worth showing: "everything" is reassuring and a short list is actionable,
    /// whereas a count on its own says neither.
    /// </summary>
    public static IReadOnlyList<string> Missing(UnlockState state)
    {
        var missing = new List<string>();
        foreach (var epoch in AllEpochs)
            if (!state.IsEpochRevealed(epoch)) missing.Add(Display(epoch));
        return missing;
    }

    /// <summary>"Ironclad3Epoch" to "Ironclad 3", which is how the game presents them.</summary>
    public static string Display(string epoch)
    {
        var bare = epoch.EndsWith("Epoch", StringComparison.Ordinal)
            ? epoch[..^"Epoch".Length]
            : epoch;

        var sb = new System.Text.StringBuilder(bare.Length + 2);
        for (int i = 0; i < bare.Length; i++)
        {
            bool boundary = i > 0 &&
                (char.IsDigit(bare[i]) != char.IsDigit(bare[i - 1]) ||
                 (char.IsUpper(bare[i]) && !char.IsUpper(bare[i - 1])));
            if (boundary) sb.Append(' ');
            sb.Append(bare[i]);
        }
        return sb.ToString();
    }
}
