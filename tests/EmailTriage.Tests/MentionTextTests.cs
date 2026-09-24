using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class MentionTextTests
{
    private static readonly ContactEntry Jane = new("Jane Smith", "jane@x.com", 5);
    private static readonly ContactEntry Bob = new("Jones, Bob", "bob@y.com", 3);

    [Theory]
    [InlineData("@", 0, "")]
    [InlineData("@ja", 0, "ja")]
    [InlineData("thanks @ja", 7, "ja")]
    [InlineData("thanks (@ja", 8, "ja")]
    [InlineData("hi\n@jane sm", 3, "jane sm")]
    public void Finds_the_mention_being_typed(string text, int start, string query)
    {
        var found = MentionText.Find(text, text.Length);

        Assert.NotNull(found);
        Assert.Equal(start, found.Value.Start);
        Assert.Equal(query, found.Value.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("mail bob@y.com")]
    [InlineData("@ ja")]
    [InlineData("@ja\nnext line")]
    [InlineData("@one two three four")]
    public void Ignores_text_that_is_not_a_mention(string text)
    {
        Assert.Null(MentionText.Find(text, text.Length));
    }

    [Fact]
    public void Looks_at_the_caret_not_the_end()
    {
        var text = "hi @ja, see below";
        Assert.Equal("ja", MentionText.Find(text, 6)!.Value.Text);
        Assert.Null(MentionText.Find(text, text.Length));
    }

    [Fact]
    public void A_finished_mention_is_not_searched_again()
    {
        var text = "@Jane Smith ";
        Assert.Null(MentionText.Find(text, text.Length, new[] { Jane }));
        Assert.NotNull(MentionText.Find(text, text.Length));
    }

    [Fact]
    public void Insert_replaces_the_query_and_adds_a_space()
    {
        var text = "thanks @ja";
        var (result, caret) = MentionText.Insert(text, MentionText.Find(text, text.Length)!.Value, text.Length, Jane);

        Assert.Equal("thanks @Jane Smith ", result);
        Assert.Equal(result.Length, caret);
    }

    [Fact]
    public void Insert_mid_sentence_keeps_the_rest()
    {
        var text = "thanks @ja for this";
        var (result, caret) = MentionText.Insert(text, MentionText.Find(text, 10)!.Value, 10, Jane);

        Assert.Equal("thanks @Jane Smith for this", result);
        Assert.Equal("thanks @Jane Smith ".Length, caret);
    }

    [Fact]
    public void Directory_names_are_turned_round()
    {
        Assert.Equal("Bob Jones", MentionText.NameOf(Bob));
        Assert.Equal("Smith, Jane (Contractor)", MentionText.NameOf(new ContactEntry("Smith, Jane (Contractor)", "j@x.com", 0)));
        Assert.Equal("jane", MentionText.NameOf(new ContactEntry("", "jane@x.com", 0)));
    }

    [Fact]
    public void Split_finds_full_and_first_name_mentions()
    {
        var segments = MentionText.Split("@Jane Smith and @Bob, over to you", new[] { Jane, Bob });

        Assert.Collection(segments,
            s => { Assert.Equal("@Jane Smith", s.Text); Assert.Equal(Jane, s.Contact); },
            s => { Assert.Equal(" and ", s.Text); Assert.Null(s.Contact); },
            s => { Assert.Equal("@Bob", s.Text); Assert.Equal(Bob, s.Contact); },
            s => { Assert.Equal(", over to you", s.Text); Assert.Null(s.Contact); });
    }

    [Fact]
    public void Split_leaves_addresses_and_partial_words_alone()
    {
        var segments = MentionText.Split("mail jane@Jane Smith or @Janet", new[] { Jane });

        Assert.Equal("mail jane@Jane Smith or @Janet", Assert.Single(segments).Text);
    }

    [Fact]
    public void Recipient_line_append_and_contains()
    {
        Assert.Equal("Jane Smith <jane@x.com>; ", RecipientLine.Append("", Jane));
        Assert.Equal("bob@y.com; Jane Smith <jane@x.com>; ", RecipientLine.Append("bob@y.com; ", Jane));
        Assert.True(RecipientLine.Contains("Jane Smith <JANE@x.com>; ", Jane));
        Assert.True(RecipientLine.Contains("Jane Smith; ", Jane));
        Assert.False(RecipientLine.Contains("bob@y.com; ", Jane));
    }
}
