using System.Diagnostics;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class ContactDirectoryTests
{
    private static ContactDirectory Build(params ContactEntry[] entries)
    {
        var dir = new ContactDirectory(null, null, new FakeClock());
        foreach (var e in entries) dir.Merge(e);
        dir.Publish();
        return dir;
    }

    [Fact]
    public void Finds_by_first_name_last_name_or_address_prefix()
    {
        var dir = Build(
            new ContactEntry("Jane Smith", "jane.smith@vorea.com", 0),
            new ContactEntry("Bob Jones", "bjones@example.com", 0));

        Assert.Equal("Jane Smith", Assert.Single(dir.Search("ja")).Name);
        Assert.Equal("Jane Smith", Assert.Single(dir.Search("smi")).Name);
        Assert.Equal("Bob Jones", Assert.Single(dir.Search("bjon")).Name);
        Assert.Equal("Jane Smith", Assert.Single(dir.Search("ja sm")).Name);
    }

    [Fact]
    public void Handles_last_comma_first_directory_names()
    {
        var dir = Build(new ContactEntry("Smith, Jane", "jsmith@vorea.com", 0));

        Assert.Single(dir.Search("jane"));
        Assert.Single(dir.Search("smith jane"));
    }

    [Fact]
    public void Every_typed_word_must_match()
    {
        var dir = Build(new ContactEntry("Jane Smith", "jane@x.com", 0));

        Assert.Empty(dir.Search("jane doe"));
    }

    [Fact]
    public void People_you_correspond_with_rank_first()
    {
        var dir = Build(
            new ContactEntry("Jan Directory", "jan.d@vorea.com", 0),
            new ContactEntry("Janet Frequent", "janet@vorea.com", 40));

        Assert.Equal("Janet Frequent", dir.Search("jan")[0].Name);
    }

    [Fact]
    public void Sent_items_names_boost_the_matching_directory_entry()
    {
        var dir = Build(
            new ContactEntry("Alex Adams", "alex.a@vorea.com", 0),
            new ContactEntry("Alex Baker", "alex.b@vorea.com", 0),
            new ContactEntry("Alex Baker", "", 30));

        Assert.Equal("Alex Baker", dir.Search("alex")[0].Name);
    }

    [Fact]
    public void Duplicate_addresses_merge_and_keep_a_real_name()
    {
        var dir = Build(
            new ContactEntry("jane@x.com", "jane@x.com", 5),
            new ContactEntry("Jane Smith", "JANE@x.com", 1));

        var hit = Assert.Single(dir.Search("jane"));
        Assert.Equal("Jane Smith", hit.Name);
        Assert.Equal(5, hit.Weight);
    }

    [Fact]
    public void Entries_without_an_address_are_not_suggested()
    {
        var dir = Build(new ContactEntry("No Address", "", 10), new ContactEntry("Legacy", "/o=Exchange/cn=legacy", 0));

        Assert.Empty(dir.Search("no"));
        Assert.Empty(dir.Search("leg"));
    }

    [Fact]
    public void Searching_a_large_directory_is_fast()
    {
        var entries = Enumerable.Range(0, 60_000)
            .Select(i => new ContactEntry($"Person{i} Surname{i % 997}", $"person{i}@vorea.com", i % 13))
            .ToArray();
        var dir = Build(entries);

        dir.Search("pe"); // warm up
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) dir.Search("person1 sur");
        watch.Stop();

        // Well under a keystroke's worth of time per search, even on a slow CI box.
        Assert.True(watch.ElapsedMilliseconds / 10 < 60, $"{watch.ElapsedMilliseconds / 10} ms per search");
    }

    [Fact]
    public async Task Loads_from_the_store_and_caches_to_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"contacts-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new FakeClock();
            var store = new FakeMailStore();
            store.FrequentContacts.Add(new ContactEntry("Jane Smith", "jane@x.com", 3));
            store.Directory.AddRange(Enumerable.Range(0, 250).Select(i => new ContactEntry($"Dir {i}", $"d{i}@x.com", 0)));

            var first = new ContactDirectory(store, path, clock);
            await first.StartLoading();
            Assert.Equal(251, first.Count);
            Assert.Equal(3, store.DirectoryReads); // 100 + 100 + 50

            // A second start reads the cache, and skips the directory while it is fresh.
            var second = new ContactDirectory(store, path, clock);
            await second.StartLoading();
            Assert.Equal(251, second.Count);
            Assert.Equal(3, store.DirectoryReads);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class RecipientLineTests
{
    private static readonly ContactEntry Jane = new("Jane Smith", "jane@x.com", 0);

    [Theory]
    [InlineData("", "")]
    [InlineData("ja", "ja")]
    [InlineData("bob@y.com; ja", "ja")]
    [InlineData("Jane Smith <jane@x.com>; ", "")]
    [InlineData("Jane Smith <jane@x.com>", "")]
    public void Current_token_is_what_is_being_typed(string line, string expected)
    {
        Assert.Equal(expected, RecipientLine.CurrentToken(line));
    }

    [Theory]
    [InlineData("ja", "Jane Smith <jane@x.com>; ")]
    [InlineData("bob@y.com; ja", "bob@y.com; Jane Smith <jane@x.com>; ")]
    [InlineData("bob@y.com;ja", "bob@y.com; Jane Smith <jane@x.com>; ")]
    public void Accept_replaces_the_partial_entry(string line, string expected)
    {
        Assert.Equal(expected, RecipientLine.Accept(line, Jane));
    }

    [Fact]
    public void Parse_extracts_addresses_and_keeps_bare_names()
    {
        var parsed = RecipientLine.Parse("Smith, Jane <jane@x.com>; bob@y.com; Al Green");
        Assert.Equal(new[] { "jane@x.com", "bob@y.com", "Al Green" }, parsed);
    }

    [Fact]
    public void Parse_splits_comma_lists_of_addresses_only()
    {
        Assert.Equal(new[] { "a@x.com", "b@y.com" }, RecipientLine.Parse("a@x.com, b@y.com"));
        Assert.Equal(new[] { "Smith, Jane" }, RecipientLine.Parse("Smith, Jane"));
    }

    [Fact]
    public void Format_round_trips_through_parse()
    {
        var line = RecipientLine.Format(new[] { new Recipient("Jane Smith", "jane@x.com"), new Recipient("", "bob@y.com") });

        Assert.Equal("Jane Smith <jane@x.com>; bob@y.com; ", line);
        Assert.Equal(new[] { "jane@x.com", "bob@y.com" }, RecipientLine.Parse(line));
    }
}
