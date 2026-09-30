using System.Net;
using System.Text;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class LavishTests
{
    private static LavishNote Note(string comment = "The Settings button is hard to find", string? text = "⚙ Settings") => new()
    {
        Comment = comment,
        Target = new LavishTarget("Button", "Settings", "Top bar", "Triage", text),
    };

    [Fact]
    public void Title_is_the_first_line_cut_to_fit()
    {
        Assert.Equal("Lavish: Too dim", LavishIssue.Title("  Too dim\nand more detail"));
        Assert.Equal("Lavish: Feedback", LavishIssue.Title("   "));

        var long_ = LavishIssue.Title(new string('a', 200));
        Assert.Equal("Lavish: ".Length + 72, long_.Length);
        Assert.EndsWith("…", long_);
    }

    [Fact]
    public void Body_quotes_the_comment_and_names_the_element()
    {
        var note = Note("Line one\r\nLine two");
        var body = LavishIssue.Body(note, "v1.0.9", "Windows 11");

        Assert.StartsWith(LavishIssue.Marker(note.Id), body);
        Assert.Contains("> Line one\n> Line two", body.Replace("\r\n", "\n"));
        Assert.Contains("| **Element** | Button · Settings |", body);
        Assert.Contains("| **Says** | ⚙ Settings |", body);
        Assert.Contains("- [ ] Merged into `main`", body);
    }

    [Fact]
    public void Body_leaves_out_text_that_was_withheld()
    {
        var body = LavishIssue.Body(Note(text: null), "v1", "Windows");

        Assert.DoesNotContain("**Says**", body);
    }

    [Fact]
    public void Body_keeps_table_cells_on_one_line()
    {
        var note = Note();
        note.Target = note.Target with { Text = "a | b\nc" };

        Assert.Contains("| **Says** | a \\| b c |", LavishIssue.Body(note, "v1", "Windows"));
    }

    [Fact]
    public void Marker_round_trips()
    {
        var note = Note();

        Assert.Equal(note.Id, LavishIssue.ParseMarker("text\n" + LavishIssue.Marker(note.Id) + "\nmore"));
        Assert.Null(LavishIssue.ParseMarker("no marker here"));
        Assert.Null(LavishIssue.ParseMarker(null));
    }

    [Fact]
    public void New_issue_link_carries_title_body_and_label()
    {
        var url = LavishIssue.NewIssueUrl("me/repo", "Lavish: Hi", "a b&c");

        Assert.Equal("https://github.com/me/repo/issues/new?labels=lavish&title=Lavish%3A%20Hi&body=a%20b%26c", url);
    }

    [Fact]
    public void New_issue_link_is_cut_to_fit_but_keeps_the_marker()
    {
        var note = Note(string.Concat(Enumerable.Repeat("ünïcode words ", 2000)));
        var body = LavishIssue.Body(note, "v1", "Windows");

        var url = LavishIssue.NewIssueUrl("me/repo", "t", body);

        Assert.True(url.Length <= LavishIssue.MaxUrlLength);
        var decoded = Uri.UnescapeDataString(url[(url.IndexOf("&body=") + 6)..]);
        Assert.Equal(note.Id, LavishIssue.ParseMarker(decoded));
        Assert.EndsWith("(cut to fit the link)", decoded);
    }

    [Theory]
    [InlineData(false, null, new string[0], LavishStage.Filed)]
    [InlineData(false, null, new[] { "lavish", "lavish: branch" }, LavishStage.Branch)]
    [InlineData(false, null, new[] { "lavish: branch", "Lavish: In Review" }, LavishStage.InReview)]
    [InlineData(true, "completed", new[] { "lavish: in review" }, LavishStage.Merged)]
    [InlineData(true, "completed", new[] { "lavish: released" }, LavishStage.Released)]
    [InlineData(true, "not_planned", new[] { "lavish: filed" }, LavishStage.Declined)]
    public void Stage_comes_from_state_and_labels(bool closed, string? reason, string[] labels, LavishStage expected)
    {
        Assert.Equal(expected, LavishIssue.StageOf(closed, reason, labels));
    }

    [Fact]
    public void Log_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lavish-{Guid.NewGuid():N}", "lavish.json");
        var log = new LavishLog(path);
        var note = Note();
        note.Stage = LavishStage.InReview;
        note.IssueNumber = 12;

        log.Save([note]);
        var back = Assert.Single(log.Load());

        Assert.Equal(note.Id, back.Id);
        Assert.Equal(LavishStage.InReview, back.Stage);
        Assert.Equal(12, back.IssueNumber);
        Assert.Equal("⚙ Settings", back.Target.Text);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Missing_or_broken_log_loads_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lavish-{Guid.NewGuid():N}.json");
        Assert.Empty(new LavishLog(path).Load());

        File.WriteAllText(path, "{ not json");
        Assert.Empty(new LavishLog(path).Load());
        File.Delete(path);
    }

    [Fact]
    public async Task Creates_an_issue_with_the_lavish_label()
    {
        var handler = new StubHandler(HttpStatusCode.Created, """{ "number": 41, "html_url": "https://github.com/me/repo/issues/41" }""");
        var github = new GitHubIssues(new HttpClient(handler), "me/repo", "tok");

        var (number, url) = await github.CreateAsync("Lavish: x", "body");

        Assert.Equal(41, number);
        Assert.Equal("https://github.com/me/repo/issues/41", url);
        Assert.Equal("https://api.github.com/repos/me/repo/issues", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", handler.Last.Headers.Authorization!.ToString());
        Assert.Contains("\"labels\":[\"lavish\"]", handler.LastBody);
    }

    [Fact]
    public async Task Filing_without_a_token_is_refused()
    {
        var github = new GitHubIssues(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")), "me/repo", "  ");

        Assert.False(github.CanFile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => github.CreateAsync("t", "b"));
    }

    [Fact]
    public async Task A_refused_token_is_explained()
    {
        var github = new GitHubIssues(new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "{}")), "me/repo", "bad");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => github.CreateAsync("t", "b"));
        Assert.Contains("gh auth login", ex.Message);
    }

    [Fact]
    public async Task Lists_issues_and_skips_pull_requests()
    {
        var id = new string('a', 32);
        var json = $$"""
        [
          { "number": 7, "html_url": "u7", "state": "open", "state_reason": null,
            "labels": [ { "name": "lavish" }, { "name": "lavish: branch" } ],
            "body": "<!-- lavish:{{id}} -->\n> hi" },
          { "number": 8, "html_url": "u8", "state": "closed", "state_reason": "completed",
            "labels": [], "body": null },
          { "number": 9, "html_url": "u9", "state": "open", "labels": [], "pull_request": {} }
        ]
        """;
        var github = new GitHubIssues(new HttpClient(new StubHandler(HttpStatusCode.OK, json)), "me/repo", null);

        var list = await github.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal(new LavishIssueState(7, "u7", id, LavishStage.Branch), list[0]);
        Assert.Equal(new LavishIssueState(8, "u8", null, LavishStage.Merged), list[1]);
    }

    [Fact]
    public void Apply_matches_by_number_or_by_marker()
    {
        var filed = new LavishNote { IssueNumber = 3, Stage = LavishStage.Filed };
        var fromBrowser = new LavishNote();
        var unknown = new LavishNote();
        var issues = new[]
        {
            new LavishIssueState(3, "u3", null, LavishStage.InReview),
            new LavishIssueState(4, "u4", fromBrowser.Id, LavishStage.Filed),
        };

        Assert.True(GitHubIssues.Apply([filed, fromBrowser, unknown], issues));

        Assert.Equal(LavishStage.InReview, filed.Stage);
        Assert.Equal(4, fromBrowser.IssueNumber);
        Assert.Equal("u4", fromBrowser.IssueUrl);
        Assert.Equal(LavishStage.Filed, fromBrowser.Stage);
        Assert.Equal(LavishStage.Draft, unknown.Stage);
        Assert.False(GitHubIssues.Apply([filed, fromBrowser], issues));
    }

    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
