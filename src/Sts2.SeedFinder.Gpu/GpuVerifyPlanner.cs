using Sts2.SeedFinder.Core;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Holds the whole accelerated search to the plain one: the same criteria over the same range,
/// run once through <see cref="GpuSearchPlanner.TryPlan"/> and once without it, and the complete
/// hit sets compared.
///
/// The kernel checks cannot see this layer. Each of them builds the pools and parameters it hands
/// the kernel itself, from the same inputs it gives the CPU side, so a planner that builds them
/// differently from how <c>SeedSearcher</c> reads the criteria passes every one of them and still
/// drops seeds in a real search. That happened: the card pools were once built from the lobby's
/// union unlock state rather than each player's own, which only showed with an imported friend
/// epoch. This goes through <c>TryPlan</c> exactly as the web app and the CLI do.
/// </summary>
public static class GpuVerifyPlanner
{
    public static GpuCheck Run(
        GpuSearchPlanner planner, SearchCriteria criteria, ulong start, ulong count, string label)
    {
        string name = $"planner {label}";

        // Declining is always safe in a search, but here it would mean the case tested nothing.
        if (!planner.TryPlan(criteria, start, count, CancellationToken.None, out var candidates))
            return new GpuCheck(name, false, "the planner declined a search it should accelerate");

        var plain = SeedSearcher.Search(criteria, start, count, int.MaxValue)
            .Select(h => h.Seed).ToHashSet(StringComparer.Ordinal);
        var accelerated = SeedSearcher.Search(criteria, start, count, int.MaxValue, default, candidates)
            .Select(h => h.Seed).ToHashSet(StringComparer.Ordinal);

        // Missed seeds are the failure that matters; extras cannot happen, since every candidate
        // goes through the same CPU chain, and would point at the comparison itself being broken.
        int missed = plain.Count(s => !accelerated.Contains(s));
        int extra = accelerated.Count(s => !plain.Contains(s));

        return missed == 0 && extra == 0
            ? new GpuCheck(name, true, $"{count:N0} seeds, {plain.Count:N0} matches, identical sets")
            : new GpuCheck(name, false,
                $"{count:N0} seeds: CPU {plain.Count:N0}, accelerated {accelerated.Count:N0}, "
                + $"{missed:N0} missed, {extra:N0} extra");
    }
}
