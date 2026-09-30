using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

/// <summary>A canned assistant that also records what it was asked, and with which model.</summary>
file sealed class FakeAssistant : IAiAssistant
{
    public string Answer { get; set; } = "";
    public string? LastPrompt { get; private set; }
    public string? LastModel { get; private set; }

    public Task<string> AskAsync(string prompt, string? model = null, string? feature = null, CancellationToken ct = default)
    {
        LastPrompt = prompt;
        LastModel = model;
        return Task.FromResult(Answer);
    }
}

public class ClaudeCodeCliTests
{
    [Fact]
    public void ParseResult_ReadsTheAnswer()
    {
        var json = """{"type":"result","subtype":"success","is_error":false,"result":"Hello there","session_id":"abc"}""";
        Assert.Equal("Hello there", ClaudeCodeCli.ParseResult(json));
    }

    [Fact]
    public void ParseResult_ToleratesLogLinesAroundTheJson()
    {
        var output = "some warning\n" +
                     """{"is_error":false,"result":"The answer"}""" +
                     "\ntrailing";
        Assert.Equal("The answer", ClaudeCodeCli.ParseResult(output));
    }

    [Fact]
    public void ParseResult_ErrorEnvelopeThrowsWithItsMessage()
    {
        var json = """{"is_error":true,"result":"Credit balance too low"}""";
        var ex = Assert.Throws<AiUnavailableException>(() => ClaudeCodeCli.ParseResult(json));
        Assert.Contains("Credit balance too low", ex.Message);
    }

    [Fact]
    public void ParseResult_GarbageThrowsRatherThanReturningGarbage()
    {
        Assert.Throws<AiUnavailableException>(() => ClaudeCodeCli.ParseResult("not json at all"));
        Assert.Throws<AiUnavailableException>(() => ClaudeCodeCli.ParseResult("{broken"));
        Assert.Throws<AiUnavailableException>(() => ClaudeCodeCli.ParseResult("""{"result":""}"""));
    }

    [Fact]
    public void ParseUsage_ReadsTokensCostAndDuration()
    {
        var json = """{"type":"result","is_error":false,"result":"ok","duration_ms":1532,"total_cost_usd":0.217847,"usage":{"input_tokens":2,"cache_creation_input_tokens":21262,"cache_read_input_tokens":10234,"output_tokens":4}}""";
        var at = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

        var call = ClaudeCodeCli.ParseUsage(json, "draft", "claude-opus-5", at, error: null);

        Assert.Equal("draft", call.Feature);
        Assert.Equal(2, call.InputTokens);
        Assert.Equal(4, call.OutputTokens);
        Assert.Equal(10234, call.CacheReadTokens);
        Assert.Equal(21262, call.CacheWriteTokens);
        Assert.Equal(31502, call.TotalTokens);
        Assert.Equal(0.217847m, call.CostUsd);
        Assert.Equal(1532, call.DurationMs);
        Assert.True(call.Succeeded);
    }

    [Fact]
    public void ParseUsage_NoEnvelopeLogsAFailureWithZeroes()
    {
        var call = ClaudeCodeCli.ParseUsage("", "search", "sonnet", DateTimeOffset.Now, "timed out");

        Assert.False(call.Succeeded);
        Assert.Equal("timed out", call.Error);
        Assert.Equal(0, call.TotalTokens);
        Assert.Equal(0m, call.CostUsd);
    }

    [Fact]
    public void ParseAuth_TellsTheClaudeLoginFromAnApiKey()
    {
        var login = ClaudeCodeCli.ParseAuth("""{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com","subscriptionType":"max"}""");
        var key = ClaudeCodeCli.ParseAuth("""{"loggedIn":true,"authMethod":"api_key","apiKeySource":"ANTHROPIC_API_KEY"}""");

        Assert.NotNull(login);
        Assert.True(login!.IsSubscription);
        Assert.Contains("max plan", login.Describe());
        Assert.NotNull(key);
        Assert.False(key!.IsSubscription);
        Assert.Contains("ANTHROPIC_API_KEY", key.Describe());
        Assert.Null(ClaudeCodeCli.ParseAuth("not json"));
    }
}

public class AiUsageLogTests
{
    private static AiCall Call(DateTimeOffset at, string feature, decimal cost, bool ok = true) =>
        new(at, feature, "claude-opus-5", 100, 20, 0, 0, cost, 1000, ok, ok ? null : "failed");

    [Fact]
    public void Summarise_CountsOnlyTheWindowAndGroupsByFeature()
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var log = new AiUsageLog(path: null);
        log.Record(Call(now.AddDays(-2), "draft", 0.50m));
        log.Record(Call(now.AddHours(-1), "draft", 0.10m));
        log.Record(Call(now.AddHours(-1), "search", 0.02m, ok: false));

        var today = log.Summarise(new DateTimeOffset(now.Date, TimeSpan.Zero));

        Assert.Equal(2, today.Calls);
        Assert.Equal(1, today.Failures);
        Assert.Equal(0.12m, today.CostUsd);
        Assert.Equal(240, today.TotalTokens);
        Assert.Equal(new[] { "draft", "search" }, today.ByFeature.Select(f => f.Feature));
    }

    [Fact]
    public void Log_SurvivesARestartAndDropsCallsPastThirtyDays()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-usage-{Guid.NewGuid():N}.jsonl");
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var first = new AiUsageLog(path, now);
            first.Record(Call(now.AddDays(-40), "style", 1m));
            first.Record(Call(now.AddDays(-1), "draft", 0.25m));
            File.AppendAllText(path, "{torn line" + Environment.NewLine);

            var reopened = new AiUsageLog(path, now);

            var call = Assert.Single(reopened.Calls);
            Assert.Equal("draft", call.Feature);
            Assert.Equal(0.25m, call.CostUsd);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class AiDraftTests
{
    private static DraftContext Context(string instructions = "", params DraftMessage[] messages) => new()
    {
        Subject = "RE: Elara East - sleeve layout",
        Kind = "reply to everyone on the conversation below",
        Recipients = "Dana Reyes  ·  cc Sam Ortiz",
        Instructions = instructions,
        Messages = messages,
    };

    private static DraftMessage Message(string text, bool mine = false) =>
        new(mine ? "Me" : "Dana Reyes", "dana@example.com",
            new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero), text, mine);

    [Fact]
    public void BuildPrompt_CarriesTheConversationAndWhoIsWho()
    {
        var prompt = AiDraftService.BuildPrompt(Context(
            "",
            Message("Can you send the revised layout by Friday?"),
            Message("Attached the first cut.", mine: true)));

        Assert.Contains("RE: Elara East - sleeve layout", prompt);
        Assert.Contains("Dana Reyes  ·  cc Sam Ortiz", prompt);
        Assert.Contains("Can you send the revised layout by Friday?", prompt);
        Assert.Contains("Me (you)", prompt);
        Assert.Contains("no subject line", prompt);
    }

    [Fact]
    public void BuildPrompt_UserNotesReplaceTheDefaultBrief()
    {
        var withNotes = AiDraftService.BuildPrompt(Context("say yes, ask for the SOV by Friday"));
        Assert.Contains("say yes, ask for the SOV by Friday", withNotes);
        Assert.DoesNotContain("Write what the conversation calls for", withNotes);

        var without = AiDraftService.BuildPrompt(Context());
        Assert.Contains("Write what the conversation calls for", without);
    }

    [Fact]
    public void BuildPrompt_TrimsAWallOfText()
    {
        var wall = string.Join("\n", Enumerable.Repeat("A line of quoted history that goes on.", 500));
        var prompt = AiDraftService.BuildPrompt(Context("", Message(wall)));

        Assert.Contains("[older text of this message trimmed]", prompt);
        Assert.True(prompt.Length < wall.Length);
    }

    [Fact]
    public void Clean_UnwrapsFencesAndDropsASubjectLine()
    {
        Assert.Equal("Hi Dana,\n\nYes - Friday works.",
            AiDraftService.Clean("```\nHi Dana,\n\nYes - Friday works.\n```"));

        Assert.Equal("Hi Dana,",
            AiDraftService.Clean("Subject: RE: layout\nHi Dana,"));

        Assert.Equal("Plain answer.", AiDraftService.Clean("  Plain answer.  "));
    }

    [Fact]
    public async Task DraftAsync_ReturnsTheCleanedDraft()
    {
        var fake = new FakeAssistant { Answer = "```\nHi Dana,\n\nFriday works.\n```" };
        var service = new AiDraftService(fake);

        var text = await service.DraftAsync(Context());

        Assert.Equal("Hi Dana,\n\nFriday works.", text);
        Assert.Contains("RE: Elara East", fake.LastPrompt);
    }

    [Fact]
    public async Task DraftAsync_EmptyAnswerIsAnError()
    {
        var service = new AiDraftService(new FakeAssistant { Answer = "```\n```" });
        await Assert.ThrowsAsync<AiUnavailableException>(() => service.DraftAsync(Context()));
    }

    [Fact]
    public async Task DraftAsync_PassesThePerTaskModelThrough()
    {
        var fake = new FakeAssistant { Answer = "Hi." };
        await new AiDraftService(fake).DraftAsync(Context(), model: "sonnet");

        Assert.Equal("sonnet", fake.LastModel);
    }

    [Fact]
    public void BuildPrompt_CarriesTheWritingStyleGuide()
    {
        var styled = Context() with { Style = "- opens with the first name, no 'Hi'\n- short sentences" };
        var prompt = AiDraftService.BuildPrompt(styled);

        Assert.Contains("in the user's own voice", prompt);
        Assert.Contains("opens with the first name", prompt);

        Assert.DoesNotContain("own voice", AiDraftService.BuildPrompt(Context()));
    }
}

public class AiSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    private static AiSearchRow Row(string key, string subject = "Subject", string snippet = "") =>
        new(key, subject, "Dana Reyes", "dana@example.com", Now.AddDays(-1), Unread: false, MessageCount: 2, snippet);

    [Fact]
    public void BuildPrompt_CarriesTheQuestionTheDateAndTheRows()
    {
        var prompt = AiSearchService.BuildPrompt(
            "what am I waiting on from the architect?",
            new[] { Row("k1", "Sleeve layout"), Row("k2", "Lunch") },
            Now);

        Assert.Contains("what am I waiting on from the architect?", prompt);
        Assert.Contains("2026", prompt);
        Assert.Contains("Sleeve layout", prompt);
        Assert.Contains("\"k2\"", prompt);
    }

    [Fact]
    public void ParseResponse_ReadsPlainAndFencedJson()
    {
        var plain = AiSearchService.ParseResponse("""{"keys":["a","b"],"answer":"Two threads about sleeves"}""");
        Assert.Equal(new[] { "a", "b" }, plain.Keys);
        Assert.Equal("Two threads about sleeves", plain.Answer);

        var fenced = AiSearchService.ParseResponse("```json\n{\"keys\":[\"a\"],\"answer\":\"One\"}\n```");
        Assert.Equal(new[] { "a" }, fenced.Keys);
    }

    [Fact]
    public void ParseResponse_NoJsonIsAnError()
    {
        Assert.Throws<AiUnavailableException>(() => AiSearchService.ParseResponse("I found two emails."));
    }

    [Fact]
    public async Task SearchAsync_KeepsOnlyRealKeysInTheModelsOrder()
    {
        var fake = new FakeAssistant
        {
            Answer = """{"keys":["k2","made-up","k1","k2"],"answer":"Found two"}""",
        };
        var service = new AiSearchService(fake);

        var result = await service.SearchAsync("sleeves?", new[] { Row("k1"), Row("k2") }, Now);

        Assert.Equal(new[] { "k2", "k1" }, result.Keys);
        Assert.Equal("Found two", result.Answer);
        Assert.Contains("sleeves?", fake.LastPrompt);
    }

    [Fact]
    public async Task SearchAsync_NothingFoundIsAnAnswerNotAnError()
    {
        var service = new AiSearchService(new FakeAssistant
        {
            Answer = """{"keys":[],"answer":"Nothing about sleeves in the inbox"}""",
        });

        var result = await service.SearchAsync("sleeves?", new[] { Row("k1") }, Now);

        Assert.Empty(result.Keys);
        Assert.Equal("Nothing about sleeves in the inbox", result.Answer);
    }
}
