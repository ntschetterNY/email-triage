using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class WaitingReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static ActionItem Item(long id, string subject) => new()
    {
        Id = id,
        InternetMessageId = $"<{id}@x>",
        Subject = subject,
        SenderName = "Sender",
        SenderAddress = "s@x.com",
        ReceivedUtc = Now.AddDays(-10),
    };

    private static IReadOnlyList<ActionItem> Sample()
    {
        var a = Item(1, "Shop drawings, level 3");
        a.Blockers.Add(new BlockingTask { Description = "Stamped set", WaitingOn = "Dina Brown", CreatedUtc = Now.AddDays(-6) });
        a.Assignments.Add(new Assignment { PersonName = "Bobby K", Task = "Price the change", CreatedUtc = Now.AddDays(-2) });

        var b = Item(2, "RFI 14");
        b.Blockers.Add(new BlockingTask { Description = "Answer", WaitingOn = "dina brown", CreatedUtc = Now.AddDays(-1), DueUtc = Now.AddDays(-1) });
        b.Blockers.Add(new BlockingTask { Description = "Old one", WaitingOn = "Tomas", CreatedUtc = Now.AddDays(-30), ResolvedUtc = Now });
        b.Blockers.Add(new BlockingTask { Description = "Unassigned", CreatedUtc = Now.AddDays(-3) });

        var done = Item(3, "Finished");
        done.CompletedUtc = Now;
        done.Blockers.Add(new BlockingTask { Description = "x", WaitingOn = "Ignored", CreatedUtc = Now });

        return new[] { a, b, done };
    }

    [Fact]
    public void Rows_cover_open_waits_on_open_items_only()
    {
        var rows = WaitingReport.Rows(Sample());

        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.Person is "Tomas" or "Ignored");
        Assert.Contains(rows, r => r.Person == WaitingReport.NoOneNamed);
        Assert.Equal(2, rows.Count(r => r.Kind == "Blocked by" && r.Person.Equals("dina brown", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Grouping_matches_names_regardless_of_case()
    {
        var groups = WaitingReport.ByPerson(WaitingReport.Rows(Sample()), WaitingSort.Person);

        Assert.Equal(new[] { "Bobby K", "Dina Brown", WaitingReport.NoOneNamed }, groups.Select(g => g.Person));
        Assert.Equal(2, groups[1].Count);
        Assert.Equal(1, groups[1].OverdueCount);
    }

    [Theory]
    [InlineData(WaitingSort.MostItems, "Dina Brown")]
    [InlineData(WaitingSort.LongestWait, "Dina Brown")]
    [InlineData(WaitingSort.MostOverdue, "Dina Brown")]
    [InlineData(WaitingSort.Person, "Bobby K")]
    public void Sorts_put_the_right_person_first_and_no_one_last(WaitingSort sort, string first)
    {
        var groups = WaitingReport.ByPerson(WaitingReport.Rows(Sample()), sort);

        Assert.Equal(first, groups[0].Person);
        Assert.Equal(WaitingReport.NoOneNamed, groups[^1].Person);
    }

    [Fact]
    public void Csv_quotes_commas_and_counts_days()
    {
        var csv = WaitingReport.ToCsv(WaitingReport.ByPerson(WaitingReport.Rows(Sample()), WaitingSort.Person), Now);
        var lines = csv.TrimEnd().Split(Environment.NewLine);

        Assert.StartsWith("Person,Type,Task", lines[0]);
        Assert.Contains("\"Shop drawings, level 3\"", csv);
        Assert.Contains(lines, l => l.StartsWith("Dina Brown,Blocked by,\"Shop drawings, level 3\",Stamped set,6,"));
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void Html_encodes_and_flags_overdue()
    {
        var item = Item(9, "<Budget> & fees");
        item.Assignments.Add(new Assignment { PersonName = "Al", Task = "x", CreatedUtc = Now, DueUtc = Now.AddDays(-1) });

        var html = WaitingReport.ToHtml(WaitingReport.ByPerson(WaitingReport.Rows(new[] { item }), WaitingSort.Person), Now);

        Assert.Contains("&lt;Budget&gt; &amp; fees", html);
        Assert.Contains("(1 overdue)", html);
    }

    [Fact]
    public void Known_people_are_deduplicated_most_used_first()
    {
        var people = WaitingReport.KnownPeople(Sample());

        Assert.Equal("Dina Brown", people[0]);
        Assert.Single(people, p => p.Equals("dina brown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Tomas", people); // resolved waits still teach the name
    }
}
