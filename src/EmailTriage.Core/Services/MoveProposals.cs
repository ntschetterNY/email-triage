namespace EmailTriage.Core.Services;

/// <summary>A conversation and the folder it is proposed to go to.</summary>
public sealed record MoveProposal<T>(T Item, FolderGuess Guess);

/// <summary>
/// Picks the inbox mail worth proposing a move for (`m`): the conversations
/// whose best folder guess is confident enough to accept without a second
/// look, a batch at a time. Pure; the guesses come from
/// <see cref="FolderGuesser"/> through the caller.
/// </summary>
public static class MoveProposals
{
    /// <summary>How many are proposed at once - a screenful to check and accept in one go.</summary>
    public const int BatchSize = 20;

    /// <summary>
    /// The best guess must clear this to be proposed: a little above what
    /// the move palette needs to merely suggest a folder, since here the
    /// guess is accepted in bulk.
    /// </summary>
    public const double ProposeScore = FolderGuesser.MinimumScore + 1.0;

    /// <summary>
    /// The first <paramref name="limit"/> items, in the order given, whose top
    /// guess clears <see cref="ProposeScore"/>, each with that guess.
    /// </summary>
    public static IReadOnlyList<MoveProposal<T>> Pick<T>(
        IEnumerable<T> items,
        Func<T, IReadOnlyList<FolderGuess>> guess,
        int limit = BatchSize)
    {
        var picked = new List<MoveProposal<T>>();
        if (limit <= 0) return picked;

        foreach (var item in items)
        {
            var best = guess(item).FirstOrDefault();
            if (best is null || best.Score < ProposeScore) continue;

            picked.Add(new MoveProposal<T>(item, best));
            if (picked.Count >= limit) break;
        }

        return picked;
    }
}
