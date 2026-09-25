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

    public Task<string> AskAsync(string prompt, string? model = null, CancellationToken ct = default)
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
