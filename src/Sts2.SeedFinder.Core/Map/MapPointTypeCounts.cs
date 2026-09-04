namespace Sts2.SeedFinder.Core.Map;

/// <summary>
/// How many of each "interesting" room an act's map should contain, from
/// MegaCrit.Sts2.Core.Map.MapPointTypeCounts and each act model's GetMapPointTypes override.
///
/// These are TARGETS, not results. The generator queues this many and assigns what it can place
/// legally; the pruning repair pass then tops any type back up if pruning removed nodes. Anything
/// still unassigned at the end becomes a Monster, which is why monsters have no count of their own.
///
/// This is also the act map's FIRST rng consumer, before a single node exists, so its draw count
/// has to be exact or everything downstream shifts. NextGaussianInt resamples until it is in range,
/// so that count is not fixed: budget two draws per attempt, not two draws total.
/// </summary>
/// <param name="Unknowns">`?` rooms.</param>
/// <param name="Rests">Campfires, ON TOP of the last row, which is forced to rest sites for free.</param>
/// <param name="Elites">
/// Not seed-dependent and not per-act: 5, or 8 once Swarming Elites is on. That ascension is
/// level 1, so every run above A0 gets the higher count.
/// </param>
/// <param name="Shops">Always 3.</param>
public readonly record struct MapPointTypeCounts(int Unknowns, int Rests, int Elites, int Shops)
{
    /// <summary>MapPointTypeCounts.NumOfElites — Math.Round(5 * 1.6) is 8.</summary>
    public static int ElitesFor(int ascension) => ascension >= (int)AscensionLevel.SwarmingElites ? 8 : 5;

    /// <summary>MapPointTypeCounts.StandardRandomUnknownCount, shared by all four acts.</summary>
    public static int StandardRandomUnknownCount(Rng rng) => rng.NextGaussianInt(12, 1, 10, 14);

    /// <summary>
    /// The act's own GetMapPointTypes override, keyed by name so ActData does not have to carry a
    /// delegate. Rest count first, then unknowns: that is the order the draws happen in, and
    /// swapping them would still typecheck while quietly generating a different map.
    /// </summary>
    public static MapPointTypeCounts For(string actName, Rng rng, int ascension)
    {
        int rests, unknowns;
        switch (actName)
        {
            case "Overgrowth":
            case "Underdocks":
                rests = rng.NextGaussianInt(7, 1, 6, 7);
                unknowns = StandardRandomUnknownCount(rng);
                break;
            case "Hive":
                rests = rng.NextGaussianInt(6, 1, 6, 7);
                unknowns = StandardRandomUnknownCount(rng) - 1;
                break;
            case "Glory":
                // The only act that does not use a gaussian here, so it is also the only one
                // whose rest count costs a single draw rather than two per attempt.
                rests = rng.NextInt(5, 7);
                unknowns = StandardRandomUnknownCount(rng) - 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(actName), actName, "No map point counts for that act.");
        }

        return new MapPointTypeCounts(unknowns, rests, ElitesFor(ascension), Shops: 3);
    }
}

/// <summary>
/// MegaCrit.Sts2.Core.Entities.Ascension.AscensionLevel. Ordered, and HasAscension is a
/// greater-or-equal test, so the enum value IS the ascension number the modifier switches on.
/// Only the ones we actually read are named here.
/// </summary>
public enum AscensionLevel
{
    None = 0,
    SwarmingElites = 1,
    Scarcity = 7,
    DoubleBoss = 10,
}
