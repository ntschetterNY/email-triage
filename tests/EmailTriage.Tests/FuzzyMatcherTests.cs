using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("inv", "Invoices")]
    [InlineData("invoices", "Invoices")]
    [InlineData("acinv", @"Clients\Acme\Invoices")]
    [InlineData("ca", @"Clients\Acme")]
    [InlineData("", "anything")]
    public void Matches_subsequences(string query, string target)
        => Assert.NotNull(FuzzyMatcher.Score(query, target));

    [Theory]
    [InlineData("xyz", "Invoices")]
    [InlineData("ivn", "Invoices")]          // wrong order
    [InlineData("invoicess", "Invoices")]    // longer than target
    public void Rejects_non_subsequences(string query, string target)
        => Assert.Null(FuzzyMatcher.Score(query, target));

    [Fact]
    public void Is_case_insensitive()
        => Assert.NotNull(FuzzyMatcher.Score("INV", "invoices"));

    [Fact]
    public void Prefers_word_boundary_matches()
    {
        // "ac" starting the word "Acme" should beat "ac" buried inside "Fax".
        var boundary = FuzzyMatcher.Score("ac", "Acme Corp");
        var interior = FuzzyMatcher.Score("ac", "Fax Machine");

        Assert.NotNull(boundary);
        Assert.NotNull(interior);
        Assert.True(boundary > interior);
    }

    [Fact]
    public void Prefers_consecutive_matches()
    {
        var consecutive = FuzzyMatcher.Score("inv", "Invoices");
        var scattered = FuzzyMatcher.Score("inv", "Interesting Novel Verse");

        Assert.NotNull(consecutive);
        Assert.NotNull(scattered);
        Assert.True(consecutive > scattered);
    }

    [Fact]
    public void Prefers_shorter_exact_match_over_long_incidental_one()
    {
        var direct = FuzzyMatcher.Score("acme", "Acme");
        var incidental = FuzzyMatcher.Score("acme", "Archived Client Mail Everything");

        Assert.NotNull(direct);
        Assert.True(incidental is null || direct > incidental);
    }

    [Fact]
    public void Reports_match_positions()
    {
        var score = FuzzyMatcher.Score("inv", "Invoices", out var positions);

        Assert.NotNull(score);
        Assert.Equal(3, positions.Length);

        // Positions must be strictly increasing and point at matching characters.
        for (int i = 1; i < positions.Length; i++)
            Assert.True(positions[i] > positions[i - 1]);

        Assert.Equal('I', "Invoices"[positions[0]]);
        Assert.Equal('n', "Invoices"[positions[1]]);
        Assert.Equal('v', "Invoices"[positions[2]]);
    }

    [Fact]
    public void Empty_target_never_matches_a_query()
        => Assert.Null(FuzzyMatcher.Score("a", ""));
}
