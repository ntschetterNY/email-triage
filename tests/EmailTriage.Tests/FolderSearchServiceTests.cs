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
    public void Breadcrumb_runs_from_the_top_folder_down_without_the_mailbox()
    {
        Assert.Equal("Clients -> Acme -> Invoices", Node(@"Mailbox\Clients\Acme\Invoices", 3).Breadcrumb);
        Assert.Equal("Archive", Node(@"Mailbox\Archive").Breadcrumb);
        Assert.Equal("Mailbox", Node("Mailbox", 0).Breadcrumb);
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
    public async Task Naming_a_parent_lists_its_subfolders_beneath_it()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        var results = service.Search("acme");
        var paths = results.Select(m => m.Folder.Path).ToList();
        var at = paths.IndexOf(@"Mailbox\Clients\Acme");

        Assert.Equal(@"Mailbox\Clients\Acme\Contracts", paths[at + 1]);
        Assert.Equal(@"Mailbox\Clients\Acme\Invoices", paths[at + 2]);
        Assert.Equal(1, results[at + 1].Indent);
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public async Task Parent_names_with_spaces_expand_on_a_partial_query()
    {
        var (service, store, _) = Build();
        store.Folders.AddRange(new[]
        {
            Node(@"Mailbox\Projects\1940 Jerome", 2),
            Node(@"Mailbox\Projects\1940 Jerome\Permits", 3),
            Node(@"Mailbox\Projects\1940 Jerome\Permits\Electrical", 4),
            Node(@"Mailbox\Projects\1940 Jerome\RFIs", 3),
        });
        await service.EnsureIndexedAsync();

        var results = service.Search("1940 jer");
        var at = results.ToList().FindIndex(m => m.Folder.Name == "1940 Jerome");

        Assert.Equal(0, results[at].Indent);
        Assert.Equal(("Permits", 1), (results[at + 1].Folder.Name, results[at + 1].Indent));
        Assert.Equal(("Electrical", 2), (results[at + 2].Folder.Name, results[at + 2].Indent));
        Assert.Equal(("RFIs", 1), (results[at + 3].Folder.Name, results[at + 3].Indent));
    }

    [Fact]
    public async Task Abbreviated_queries_do_not_expand_subfolders()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        Assert.All(service.Search("acmeinv"), m => Assert.Equal(0, m.Indent));
    }

    [Fact]
    public async Task Returns_nothing_when_there_is_no_match()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        Assert.Empty(service.Search("zzzzqqq"));
    }

    [Fact]
    public async Task The_store_name_does_not_make_every_folder_match()
    {
        var (service, _, _) = Build();
        await service.EnsureIndexedAsync();

        // Every letter of "mbx" is in the "Mailbox" root, and in no folder below it.
        Assert.Empty(service.Search("mbx"));
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
