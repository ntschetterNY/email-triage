using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class FolderSchemeTests
{
    [Fact]
    public void Default_scheme_nests_each_part()
    {
        Assert.Equal(new[] { "Elara", "Field Reports", "Rimkus" },
            FolderScheme.Default.ToLevels("Elara - Field Reports - Rimkus"));
    }

    [Fact]
    public void A_name_that_stops_early_nests_what_it_has()
    {
        Assert.Equal(@"Elara\Field Reports", FolderScheme.Default.ToPath("Elara - Field Reports"));
    }

    [Fact]
    public void A_single_part_is_an_ordinary_folder()
    {
        Assert.Null(FolderScheme.Default.ToLevels("Elara"));
        Assert.Null(FolderScheme.Default.ToLevels("Invoices"));
    }

    [Fact]
    public void Extra_separators_stay_in_the_last_part()
    {
        Assert.Equal(new[] { "Elara", "Field Reports", "Rimkus - 2024" },
            FolderScheme.Default.ToLevels("Elara - Field Reports - Rimkus - 2024"));
    }

    [Fact]
    public void Hyphens_without_the_spaced_separator_are_part_of_a_name()
    {
        Assert.Equal(new[] { "Smith-Jones", "Field Reports", "Rimkus" },
            FolderScheme.Default.ToLevels("Smith-Jones - Field Reports - Rimkus"));
        Assert.Null(FolderScheme.Default.ToLevels("Year-End"));
    }

    [Fact]
    public void Empty_parts_do_not_count()
    {
        Assert.Null(FolderScheme.Default.ToLevels("Elara -  - Rimkus"));
    }

    [Fact]
    public void Layout_can_reorder_drop_and_add_fixed_folders()
    {
        var scheme = new FolderScheme("{Project} - {Type} - {Company}", @"Projects\{Company}\{Project}");
        Assert.Equal(new[] { "Projects", "Rimkus", "Elara" },
            scheme.ToLevels("Elara - Field Reports - Rimkus"));
    }

    [Fact]
    public void Levels_the_name_lacks_are_left_out()
    {
        var scheme = new FolderScheme("{Project} - {Type} - {Company}", @"{Company}\{Project}\{Type}");
        Assert.Equal(new[] { "Elara", "Field Reports" }, scheme.ToLevels("Elara - Field Reports"));
    }

    [Fact]
    public void Other_separators_work()
    {
        var scheme = new FolderScheme("{Client}_{Job}", @"{Client}\{Job}");
        Assert.Equal(new[] { "Acme", "Roof" }, scheme.ToLevels("Acme_Roof"));
    }

    [Fact]
    public void Forward_slashes_in_the_layout_mean_the_same_as_backslashes()
    {
        var scheme = new FolderScheme(FolderScheme.DefaultNamePattern, "{Project}/{Type}/{Company}");
        Assert.True(scheme.IsValid);
        Assert.Equal(@"Elara\Field Reports\Rimkus", scheme.ToPath("Elara - Field Reports - Rimkus"));
    }

    [Theory]
    [InlineData("", @"{A}\{B}")]
    [InlineData("{A}", @"{A}\{B}")]
    [InlineData("{A}{B}", @"{A}\{B}")]
    [InlineData("{A} - {A}", @"{A}\{A}")]
    [InlineData("{A} - {B}", @"{A}\{C}")]
    [InlineData("{A} - {B}", "{A}")]
    [InlineData("{A} - {B}", "")]
    [InlineData("{A} - {B}", @"[x]\{A}")]
    public void Unusable_schemes_say_why(string pattern, string layout)
    {
        var scheme = new FolderScheme(pattern, layout);
        Assert.False(scheme.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(scheme.Error));
        Assert.Null(scheme.ToLevels("a - b"));
    }
}

public class FolderOrganizerTests
{
    private static FolderNode Node(string path, string store = "store") => new()
    {
        Ref = new FolderRef(path, store, path),
        Name = path.Split('\\')[^1],
        Path = path,
        Depth = path.Count(c => c == '\\') - 1,
        StoreName = "Mailbox",
    };

    [Fact]
    public void Nests_a_flat_folder_where_it_already_is()
    {
        var plan = FolderOrganizer.Plan(
            new[] { Node(@"me@x.com\Inbox\Elara - Field Reports - Rimkus") },
            FolderScheme.Default, homeFolder: "");

        var move = Assert.Single(plan);
        Assert.Equal(@"Inbox\Elara - Field Reports - Rimkus", move.From);
        Assert.Equal(@"Inbox\Elara\Field Reports\Rimkus", move.To);
        Assert.False(move.MergesIntoExisting);
    }

    [Fact]
    public void A_home_folder_gathers_every_tree_in_one_place()
    {
        var plan = FolderOrganizer.Plan(
            new[] { Node(@"me@x.com\Elara - Field Reports - Rimkus"), Node(@"me@x.com\Inbox\Elara - Invoices") },
            FolderScheme.Default, homeFolder: "Projects/");

        Assert.Equal(
            new[] { @"Projects\Elara\Field Reports\Rimkus", @"Projects\Elara\Invoices" }.OrderBy(x => x),
            plan.Select(p => p.To).OrderBy(x => x));
    }

    [Fact]
    public void Flags_a_merge_into_a_folder_already_there()
    {
        var plan = FolderOrganizer.Plan(
            new[]
            {
                Node(@"me@x.com\Elara"),
                Node(@"me@x.com\Elara\Field Reports"),
                Node(@"me@x.com\Elara - Field Reports"),
            },
            FolderScheme.Default, homeFolder: "");

        Assert.True(Assert.Single(plan).MergesIntoExisting);
    }

    [Fact]
    public void Merges_are_only_within_the_same_mailbox()
    {
        var plan = FolderOrganizer.Plan(
            new[]
            {
                Node(@"other@x.com\Elara\Field Reports", store: "other"),
                Node(@"me@x.com\Elara - Field Reports"),
            },
            FolderScheme.Default, homeFolder: "");

        Assert.False(Assert.Single(plan).MergesIntoExisting);
    }

    [Fact]
    public void Leaves_ordinary_and_already_nested_folders_alone()
    {
        var plan = FolderOrganizer.Plan(
            new[]
            {
                Node(@"me@x.com\Inbox"),
                Node(@"me@x.com\Elara"),
                Node(@"me@x.com\Elara\Field Reports"),
                Node(@"me@x.com\Elara\Field Reports\Rimkus"),
            },
            FolderScheme.Default, homeFolder: "");

        Assert.Empty(plan);
    }

    [Fact]
    public void Never_touches_Deleted_Items_or_Sent_Items()
    {
        var plan = FolderOrganizer.Plan(
            new[]
            {
                Node(@"me@x.com\Deleted Items\Elara - Field Reports"),
                Node(@"me@x.com\Sent Items\Elara - Field Reports"),
            },
            FolderScheme.Default, homeFolder: "");

        Assert.Empty(plan);
    }

    [Fact]
    public void Deepest_folders_move_first()
    {
        var plan = FolderOrganizer.Plan(
            new[]
            {
                Node(@"me@x.com\Elara - Field Reports"),
                Node(@"me@x.com\Elara - Field Reports\Elara - Field Reports - Rimkus"),
            },
            FolderScheme.Default, homeFolder: "Projects");

        Assert.Equal(@"Projects\Elara\Field Reports\Rimkus", plan[0].To);
        Assert.Equal(@"Projects\Elara\Field Reports", plan[1].To);
    }

    [Fact]
    public void An_invalid_scheme_plans_nothing()
    {
        var plan = FolderOrganizer.Plan(
            new[] { Node(@"me@x.com\Elara - Field Reports") },
            new FolderScheme("{A}", "{A}"), homeFolder: "");

        Assert.Empty(plan);
    }

    [Fact]
    public void New_folders_go_under_the_home_folder()
    {
        Assert.Equal(new[] { "Inbox", "Elara", "Field Reports", "Rimkus" },
            FolderOrganizer.NewFolderLevels("Elara - Field Reports - Rimkus", FolderScheme.Default, @"\Inbox\"));
        Assert.Equal(new[] { "Elara", "Field Reports", "Rimkus" },
            FolderOrganizer.NewFolderLevels("Elara - Field Reports - Rimkus", FolderScheme.Default, ""));
        Assert.Null(FolderOrganizer.NewFolderLevels("Receipts", FolderScheme.Default, ""));
    }

    [Fact]
    public async Task Search_finds_the_nested_folder_a_scheme_name_stands_for()
    {
        var store = new FakeMailStore();
        store.Folders.AddRange(new[]
        {
            Node(@"me@x.com\Elara"),
            Node(@"me@x.com\Elara\Field Reports"),
            Node(@"me@x.com\Elara\Field Reports\Rimkus"),
            Node(@"me@x.com\Other\Field Reports\Rimkus"),
        });
        var service = new FolderSearchService(store, new FakeFolderUsage());
        await service.EnsureIndexedAsync();

        var found = service.FindByLevels(FolderScheme.Default.ToLevels("Elara - Field Reports - Rimkus")!);

        Assert.Equal(@"me@x.com\Elara\Field Reports\Rimkus", found?.Path);
        Assert.Null(service.FindByLevels(new[] { "Nope", "Rimkus" }));
    }
}
