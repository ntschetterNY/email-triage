using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class InboxQueryTests
{
    // One conversation: from Jane at Acme, to Bob at Initech, Carol copied.
    private static IEnumerable<string> Values(QueryField field) => field switch
    {
        QueryField.Text => new[] { "Invoice 42 for March", "Jane Doe" },
        QueryField.Subject => new[] { "Invoice 42 for March" },
        QueryField.From => new[] { "Jane Doe", "jane@acme.com", "Carol King", "carol@globex.com" },
        QueryField.To => new[] { "Bob Smith", "bob@initech.com" },
        _ => Array.Empty<string>(),
    };

    private static bool Hit(string query) => InboxQuery.Parse(query).Matches(Values);

    [Theory]
    [InlineData("2:*@initech")]
    [InlineData("to:*@initech")]
    [InlineData("2:@INITECH")]
    [InlineData("to:bob")]
    [InlineData("from:*@acme")]
    [InlineData("from:*@globex")] // CC counts as from
    [InlineData("from:jane subject:*")]
    [InlineData("from:acme subject:*march")]
    [InlineData("subject:invoice*march")]
    [InlineData("subject:42 for")]
    [InlineData("subject:\"42 for\"")]
    [InlineData("inv from:acme")]
    public void Matches(string query) => Assert.True(Hit(query));

    [Theory]
    [InlineData("2:*@acme")]          // Acme is the sender, not the To line
    [InlineData("to:*@globex")]       // Carol was only copied
    [InlineData("from:*@initech")]
    [InlineData("subject:april")]
    [InlineData("from:acme subject:april")]
    [InlineData("zzz from:acme")]
    public void DoesNotMatch(string query) => Assert.False(Hit(query));

    [Theory]
    [InlineData("2:*@")]
    [InlineData("from: subject:*")]
    [InlineData("subject:")]
    [InlineData("   ")]
    public void UnfilledTemplatesMatchEverything(string query)
    {
        Assert.True(InboxQuery.Parse(query).IsEmpty);
        Assert.True(Hit(query));
    }

    [Fact]
    public void FieldsNeedAColonAtAWordStart()
    {
        // "Re: 10:30" is free text, not a field.
        var q = InboxQuery.Parse("Re: 10:30 standup");
        Assert.Equal("Re: 10:30 standup", q.FreeText);
        Assert.Single(q.Terms);
    }

    [Fact]
    public void AddressPatternsAskForRecipientAddresses()
    {
        Assert.True(InboxQuery.Parse("2:*@acme").NeedsAddresses);
        Assert.True(InboxQuery.Parse("from:@acme").NeedsAddresses);
        Assert.False(InboxQuery.Parse("to:bob").NeedsAddresses);
        Assert.False(InboxQuery.Parse("subject:a@b").NeedsAddresses);
    }
}
