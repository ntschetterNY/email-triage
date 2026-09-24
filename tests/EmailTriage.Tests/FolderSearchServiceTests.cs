using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class FolderSearchServiceTests
{
    private static FolderNode Node(string path, int depth = 1) => new()
    {
        Ref = new FolderRef(path, "store", path),
        Name = path.Split('\\')[^1],
        Path = path,
        Depth = depth,
        StoreName = "Mailbox",
    };

    private static (FolderSearchService Service, FakeMailStore Store, FakeFolderUsage Usage) Build()
    {
        var store = new FakeMailStore();
        store.Folders.AddRange(new[]
        {
            Node(@"Mailbox\Archive"),
            Node(@"Mailbox\Clients"),
            Node(@"Mailbox\Clients\Acme", 2),
            Node(@"Mailbox\Clients\Acme\Invoices", 3),
            Node(@"Mailbox\Clients\Acme\Contracts", 3),
            Node(@"Mailbox\Clients\Beta", 2),
            Node(@"Mailbox\Internal\HR"),
            Node(@"Mailbox\Receipts"),
        });

        var usage = new FakeFolderUsage();
        return (new FolderSearchService(store, usage), store, usage);
    }

    [Fact]
    public async Task Empty_query_lists_folders_without_filtering()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        Assert.Equal(8, service.Search("").Count);
    }

    [Fact]
    public async Task Finds_a_folder_by_its_leaf_name()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        var top = service.Search("invoices").First();
        Assert.Equal("Invoices", top.Folder.Name);
    }

    [Fact]
    public async Task Finds_a_nested_folder_from_an_abbreviated_path()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        var top = service.Search("acmeinv").First();
        Assert.Equal(@"Mailbox\Clients\Acme\Invoices", top.Folder.Path);
    }

    [Fact]
    public async Task Frequently_used_folders_rank_above_equally_good_matches()
    {
        var (service, _, usage) = Build();
        usage.Seed(@"Mailbox\Receipts", 50);
        await service.EnsureIndexedAsync();

        // "Re" matches Receipts and Mailbox\Archive equally on text alone.
        var top = service.Search("re").First();
        Assert.Equal("Receipts", top.Folder.Name);
    }

    [Fact]
    public async Task Returns_nothing_when_there_is_no_match()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        Assert.Empty(service.Search("zzzzqqq"));
    }

    [Fact]
    public async Task Newly_created_folders_are_searchable_without_a_reindex()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        service.AddToIndex(Node(@"Mailbox\Quarterly"));

        Assert.Contains(service.Search("quarterly"), m => m.Folder.Name == "Quarterly");
    }

    [Theory]
    [InlineData("Newthing", null, "Newthing")]
    [InlineData(@"Clients\Acme\Q3", @"Mailbox\Clients\Acme", "Q3")]
    public async Task Resolves_where_a_new_folder_should_be_created(
        string typed, string? expectedParentPath, string expectedName)
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        var (parent, name) = service.ResolveCreationTarget(typed);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedParentPath, parent?.Path);
    }

    [Fact]
    public async Task Records_folder_use_so_ranking_can_learn()
    {
        var (service, _, usage) = Build();
        await service.EnsureIndexedAsync();

        var receipts = service.Search("receipts").First().Folder;
        await service.RecordUseAsync(receipts);

        var scores = await usage.GetScoresAsync();
        Assert.Equal(1, scores[receipts.Path]);
    }
}
