using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class MoveProposalsTests
{
    private static FolderGuess Guess(string path, double score) => new(new FolderNode
    {
        Ref = new FolderRef(path, "store", path),
        Name = path.Split('\\')[^1],
        Path = path,
        Depth = 1,
        StoreName = "Mailbox",
    }, score, "test");

    [Fact]
    public void Only_confident_guesses_are_proposed_in_list_order()
    {
        var guesses = new Dictionary<string, IReadOnlyList<FolderGuess>>
        {
            ["a"] = new[] { Guess(@"Mailbox\Acme", 5), Guess(@"Mailbox\Beta", 4) },
            ["b"] = new[] { Guess(@"Mailbox\Beta", MoveProposals.ProposeScore - 0.1) },
            ["c"] = Array.Empty<FolderGuess>(),
            ["d"] = new[] { Guess(@"Mailbox\Receipts", MoveProposals.ProposeScore) },
        };

        var picked = MoveProposals.Pick(new[] { "a", "b", "c", "d" }, k => guesses[k]);

        Assert.Equal(new[] { "a", "d" }, picked.Select(p => p.Item));
        Assert.Equal(@"Mailbox\Acme", picked[0].Guess.Folder.Path);
    }

    [Fact]
    public void A_batch_stops_at_the_limit()
    {
        var items = Enumerable.Range(0, 50).ToList();
        var picked = MoveProposals.Pick(items, _ => new[] { Guess(@"Mailbox\Acme", 9) });

        Assert.Equal(MoveProposals.BatchSize, picked.Count);
        Assert.Equal(Enumerable.Range(0, MoveProposals.BatchSize), picked.Select(p => p.Item));
    }

    [Fact]
    public void A_guess_must_beat_what_the_palette_merely_suggests() =>
        Assert.True(MoveProposals.ProposeScore > FolderGuesser.MinimumScore);
}
