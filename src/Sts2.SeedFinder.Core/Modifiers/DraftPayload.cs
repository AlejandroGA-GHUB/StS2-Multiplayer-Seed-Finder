using Sts2.SeedFinder.Core.Acts;
using Sts2.SeedFinder.Core.Cards;
using Sts2.SeedFinder.Core.Saves;

namespace Sts2.SeedFinder.Core.Modifiers;

/// <summary>
/// What the Draft modifier offers a player: ten rewards of three cards, one taken from each.
///
/// The draw chain is <see cref="CardRewardGenerator.DraftRewards"/>, which documents why Draft's
/// thirty cards differ from a fight reward's three. This holds the part that is about what a
/// user can ask for.
///
/// Draft is the only payload here where the seed does not settle what the player ENDS UP with.
/// It fixes the thirty cards on offer, and the player then takes one from each reward of three,
/// with no skip (<c>CanSkip = false</c>). So a criterion is a claim about what is REACHABLE, and
/// the game's rule is one card per reward: three named cards sitting in the same reward of three
/// can never all be taken, however often they appear. That is what
/// <see cref="CanTakeAll"/> enforces, and it is the reason this is a matching problem rather than
/// a containment test.
/// </summary>
public static class DraftPayload
{
    /// <summary>Rewards offered, which is also how many cards the player ends up with.</summary>
    public const int Rewards = CardRewardGenerator.DraftRewardCount;

    /// <summary>Cards in each reward, of which exactly one is taken.</summary>
    public const int OptionsPerReward = CardRewardGenerator.DraftOptionCount;

    /// <summary>
    /// The ten rewards, in the order the player is walked through them. Reward order is
    /// meaningful: a card in reward 1 and a card in reward 7 can both be taken, two in reward 3
    /// cannot.
    /// </summary>
    public static RewardCard[][] Predict(
        ulong runSeed,
        int playerSlotIndex,
        Character character,
        UnlockState? unlocks = null,
        bool isMultiplayer = true,
        int ascension = 0,
        int priorRewardDraws = 0,
        IReadOnlyList<Character>? addedCharacters = null) =>
        CardRewardGenerator.DraftRewards(
            runSeed, playerSlotIndex, character, ascension, unlocks, priorRewardDraws,
            isMultiplayer, addedCharacters);

    /// <summary>
    /// Whether every named card can be taken, one per reward.
    ///
    /// A bipartite matching between wanted cards and the rewards that offer them, solved by
    /// Kuhn's augmenting paths. Greedy assignment is not enough and neither is counting: a card
    /// offered only in reward 4 must claim it, which may force a card offered in rewards 4 and 9
    /// onto 9, and that re-routing is exactly what an augmenting path does. With ten rewards it
    /// is also far cheaper than trying the assignments outright.
    ///
    /// Asking for the same card twice asks to take it twice, which needs it in two different
    /// rewards. That falls out: it is simply two wanted entries competing for distinct rewards.
    /// </summary>
    public static bool CanTakeAll(RewardCard[][] rewards, IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0) return true;
        if (wanted.Count > rewards.Length) return false;

        // Which reward each wanted card has claimed, and which card holds each reward.
        var heldBy = new int[rewards.Length];
        Array.Fill(heldBy, -1);

        for (int card = 0; card < wanted.Count; card++)
        {
            var seen = new bool[rewards.Length];
            if (!Claim(card, seen)) return false;
        }
        return true;

        bool Claim(int card, bool[] seen)
        {
            for (int reward = 0; reward < rewards.Length; reward++)
            {
                if (seen[reward]) continue;
                if (!Offers(rewards[reward], wanted[card])) continue;

                seen[reward] = true;

                // Free, or its current holder can be re-routed to another reward.
                if (heldBy[reward] < 0 || Claim(heldBy[reward], seen))
                {
                    heldBy[reward] = card;
                    return true;
                }
            }
            return false;
        }

        static bool Offers(RewardCard[] reward, string card)
        {
            foreach (var option in reward)
                if (option.TypeName == card) return true;
            return false;
        }
    }
}
