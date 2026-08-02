using Sts2.SeedFinder.Core;
using Sts2.SeedFinder.Core.Neow;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Turns the Core-side description of a Neow search into the flat struct the kernel takes.
///
/// One place, deliberately: the CLI, the web app and the verifier all need this mapping, and a
/// second copy of "which index is this relic in the filtered candidate list" is a silent
/// wrong-answer waiting to happen. The candidate list depends on the lobby (player count and
/// unlock state both filter it), so the index is a property of the search, not of the relic.
/// </summary>
public static class NeowPrefilterFactory
{
    /// <summary>Slugify("Neow"), hashed once here because kernels have no strings.</summary>
    private static readonly ulong NeowIdHash = GameHash.Deterministic(NeowGenerator.NeowModelEntry);

    /// <summary>
    /// Build kernel parameters for a curse-branch Neow search, or return false when the GPU
    /// pre-filter does not apply.
    ///
    /// It does not apply whenever the relic is not one of the curse candidates: the positive
    /// options need the whole offer built, which is a shuffle and roughly twenty draws, and
    /// that belongs in a later kernel rather than this one. Returning false is not a failure,
    /// it just means the CPU path handles this search.
    /// </summary>
    public static bool TryBuild(
        NeowContext ctx,
        NeowRelic relic,
        bool anySlot,
        IReadOnlyCollection<int> requiredSlots,
        out NeowPrefilterParams parameters)
    {
        var candidates = NeowGenerator.CurseCandidates(ctx);
        int index = -1;
        for (int i = 0; i < candidates.Count; i++)
            if (ReferenceEquals(candidates[i], relic) || candidates[i].Slug == relic.Slug) { index = i; break; }

        if (index < 0)
        {
            parameters = default;
            return false;
        }

        int mask = 0;
        foreach (var slot in requiredSlots)
            if (slot >= 0 && slot < ctx.PlayerCount) mask |= 1 << slot;

        parameters = new NeowPrefilterParams
        {
            NeowHash = NeowIdHash,
            CandidateCount = candidates.Count,
            WantIndex = index,
            PlayerCount = ctx.PlayerCount,
            RequiredMask = anySlot ? 0 : mask,
            Any = anySlot ? 1 : 0,
            Active = 1,
        };
        return true;
    }
}
