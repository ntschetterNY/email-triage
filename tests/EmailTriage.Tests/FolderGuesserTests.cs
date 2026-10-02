using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class FolderGuesserTests
{
    private static FolderNode Node(string path, int depth = 1) => new()
    {
        Ref = new FolderRef(path, "store", path),
        Name = path.Split('\\')[^1],
        Path = path,
        Depth = depth,
        StoreName = "Mailbox",
    };

    private static readonly IReadOnlyList<FolderNode> Index = new[]
    {
        Node(@"Mailbox\Inbox"),
        Node(@"Mailbox\Archive"),
        Node(@"Mailbox\Clients\Acme", 2),
        Node(@"Mailbox\Clients\Beta", 2),
        Node(@"Mailbox\Projects\1940 Jerome", 2),
        Node(@"Mailbox\Receipts"),
    };

    private static FilingEvidence Ev(string path, FilingFeatureKind kind, string token, long count)
        => new(path, kind, token, count);

    [Fact]
    public void Subject_words_drop_prefixes_tags_short_words_and_stopwords_but_keep_numbers()
    {
        var words = FolderGuesser.SubjectWords("RE: FW: [External] RFI 12 for the 1940 Jerome - re: a HVAC");

        Assert.Equal(new[] { "rfi", "12", "1940", "jerome", "hvac" }, words);
    }

    [Fact]
    public void Subject_words_are_distinct_and_empty_subjects_give_nothing()
    {
        Assert.Equal(new[] { "budget" }, FolderGuesser.SubjectWords("Budget budget BUDGET"));
        Assert.Empty(FolderGuesser.SubjectWords(""));
        Assert.Empty(FolderGuesser.SubjectWords("RE: "));
    }

    [Fact]
    public void Features_hold_the_sender_their_domain_and_the_subject_words()
    {
        var features = FolderGuesser.Features("Invoice 4471", "Pat.Lee@Acme.com");

        Assert.Contains(new FilingFeature(FilingFeatureKind.Sender, "pat.lee@acme.com"), features);
        Assert.Contains(new FilingFeature(FilingFeatureKind.Domain, "acme.com"), features);
        Assert.Contains(new FilingFeature(FilingFeatureKind.SubjectWord, "invoice"), features);
        Assert.Contains(new FilingFeature(FilingFeatureKind.SubjectWord, "4471"), features);
    }

    [Fact]
    public void A_personal_mail_provider_is_not_a_company()
    {
        var features = FolderGuesser.Features("Hi", "someone@gmail.com");

        Assert.DoesNotContain(features, f => f.Kind == FilingFeatureKind.Domain);
        Assert.Contains(new FilingFeature(FilingFeatureKind.Sender, "someone@gmail.com"), features);
    }

    [Fact]
    public void The_same_sender_outweighs_a_shared_subject_word()
    {
        var evidence = new[]
        {
            Ev(@"Mailbox\Clients\Acme", FilingFeatureKind.Sender, "pat@acme.com", 12),
            Ev(@"Mailbox\Clients\Acme", FilingFeatureKind.Domain, "acme.com", 20),
            Ev(@"Mailbox\Receipts", FilingFeatureKind.SubjectWord, "invoice", 9),
        };

        var guesses = FolderGuesser.Guess("Invoice for March", "pat@acme.com", evidence, Index);

        Assert.Equal(@"Mailbox\Clients\Acme", guesses[0].Folder.Path);
        Assert.Equal("12 from this sender", guesses[0].Reason);
        Assert.Equal(@"Mailbox\Receipts", guesses[1].Folder.Path);
        Assert.Equal("subject matches 9 emails here", guesses[1].Reason);
    }

    [Fact]
    public void Subject_words_alone_can_pick_a_folder_and_a_project_number_counts()
    {
        var evidence = new[]
        {
            Ev(@"Mailbox\Projects\1940 Jerome", FilingFeatureKind.SubjectWord, "1940", 6),
            Ev(@"Mailbox\Projects\1940 Jerome", FilingFeatureKind.SubjectWord, "jerome", 6),
            Ev(@"Mailbox\Clients\Beta", FilingFeatureKind.SubjectWord, "schedule", 3),
        };

        var guesses = FolderGuesser.Guess("RE: 1940 Jerome schedule", "new.person@unknown.org", evidence, Index);

        Assert.Equal(@"Mailbox\Projects\1940 Jerome", guesses[0].Folder.Path);
        Assert.StartsWith("subject matches 6 emails", guesses[0].Reason);
    }

    [Fact]
    public void A_word_spread_across_many_folders_counts_for_little()
    {
        // "rfi" sits in six folders; "jerome" in one. The one with the rare word wins
        // even though the common word is seen more often in the other.
        var evidence = new List<FilingEvidence>
        {
            Ev(@"Mailbox\Projects\1940 Jerome", FilingFeatureKind.SubjectWord, "jerome", 4),
            Ev(@"Mailbox\Clients\Acme", FilingFeatureKind.SubjectWord, "rfi", 40),
        };
        foreach (var path in new[] { @"Mailbox\Clients\Beta", @"Mailbox\Receipts", @"Mailbox\Archive", @"Mailbox\Projects\1940 Jerome", @"Mailbox\Inbox" })
            evidence.Add(Ev(path, FilingFeatureKind.SubjectWord, "rfi", 5));

        var guesses = FolderGuesser.Guess("RFI 7 Jerome", "x@y.org", evidence, Index);

        Assert.Equal(@"Mailbox\Projects\1940 Jerome", guesses[0].Folder.Path);
    }

    [Fact]
    public void Thin_evidence_gives_no_guess()
    {
        var evidence = new[]
        {
            Ev(@"Mailbox\Receipts", FilingFeatureKind.SubjectWord, "invoice", 1),
        };

        Assert.Empty(FolderGuesser.Guess("Invoice", "a@b.com", evidence, Index));
        Assert.Empty(FolderGuesser.Guess("Invoice", "a@b.com", Array.Empty<FilingEvidence>(), Index));
    }

    [Fact]
    public void One_earlier_mail_from_the_same_sender_is_enough()
    {
        var evidence = new[] { Ev(@"Mailbox\Clients\Beta", FilingFeatureKind.Sender, "kim@beta.io", 1) };

        var guess = Assert.Single(FolderGuesser.Guess("Anything", "KIM@beta.io", evidence, Index));
        Assert.Equal(@"Mailbox\Clients\Beta", guess.Folder.Path);
        Assert.Equal("1 from this sender", guess.Reason);
    }

    [Fact]
    public void Folders_no_longer_in_the_index_are_not_offered()
    {
        var evidence = new[] { Ev(@"Mailbox\Gone", FilingFeatureKind.Sender, "kim@beta.io", 30) };

        Assert.Empty(FolderGuesser.Guess("Anything", "kim@beta.io", evidence, Index));
    }

    [Fact]
    public void Guesses_are_capped_at_the_limit()
    {
        var evidence = new[]
        {
            Ev(@"Mailbox\Clients\Acme", FilingFeatureKind.Sender, "a@b.com", 9),
            Ev(@"Mailbox\Clients\Beta", FilingFeatureKind.Sender, "a@b.com", 5),
            Ev(@"Mailbox\Receipts", FilingFeatureKind.Sender, "a@b.com", 2),
            Ev(@"Mailbox\Archive", FilingFeatureKind.Sender, "a@b.com", 1),
        };

        var guesses = FolderGuesser.Guess("x", "a@b.com", evidence, Index, limit: 2);

        Assert.Equal(2, guesses.Count);
        Assert.Equal(@"Mailbox\Clients\Acme", guesses[0].Folder.Path);
    }
}

public class FolderStudyTests
{
    private static FolderNode Node(string path, int depth = 1) => new()
    {
        Ref = new FolderRef(path, "store", path),
        Name = path.Split('\\')[^1],
        Path = path,
        Depth = depth,
        StoreName = "Mailbox",
    };

    private static MailSummary Mail(string subject, string sender, bool sent = false) => new()
    {
        Ref = new MailRef(Guid.NewGuid().ToString("N"), "store"),
        InternetMessageId = Guid.NewGuid().ToString("N"),
        Subject = subject,
        SenderName = sender,
        SenderAddress = sender,
        ReceivedUtc = DateTimeOffset.UtcNow,
        IsUnread = false,
        HasAttachments = false,
        IsSent = sent,
    };

    [Fact]
    public void Outlook_folders_the_inbox_and_the_snooze_folder_are_left_alone_but_inbox_subfolders_count()
    {
        Assert.False(FolderStudy.IsWorthStudying(Node("Mailbox", 0)));
        Assert.False(FolderStudy.IsWorthStudying(Node(@"Mailbox\Inbox")));
        Assert.False(FolderStudy.IsWorthStudying(Node(@"Mailbox\Sent Items")));
        Assert.False(FolderStudy.IsWorthStudying(Node(@"Mailbox\Deleted Items\Old", 2)));
        Assert.False(FolderStudy.IsWorthStudying(Node(@"Mailbox\Snoozed")));
        Assert.True(FolderStudy.IsWorthStudying(Node(@"Mailbox\Inbox\Projects", 2)));
        Assert.True(FolderStudy.IsWorthStudying(Node(@"Mailbox\Archive")));
        Assert.True(FolderStudy.IsWorthStudying(Node(@"Mailbox\Clients\Acme", 2)));
    }

    [Fact]
    public async Task Samples_each_due_folder_once_records_what_it_found_and_skips_sent_copies()
    {
        var store = new FakeMailStore();
        var acme = Node(@"Mailbox\Clients\Acme", 2);
        var sent = Node(@"Mailbox\Sent Items");
        store.Folders.AddRange(new[] { acme, sent, Node(@"Mailbox\Inbox") });
        store.MailByFolder[acme.Ref.EntryId] = new()
        {
            Mail("Invoice 4471", "pat@acme.com"),
            Mail("RE: Invoice 4471", "pat@acme.com"),
            Mail("RE: Invoice 4471", "me@mine.com", sent: true),
        };

        var usage = new FakeFolderUsage();
        var clock = new FakeClock { UtcNow = usage.Now };
        var study = new FolderStudy(store, usage, clock) { Pause = TimeSpan.Zero };

        Assert.Equal(1, await study.RunAsync(store.Folders));
        Assert.Equal(new[] { acme.Ref.EntryId }, store.FoldersRead);
        Assert.Equal(2, usage.EvidenceCount(acme.Path, FilingFeatureKind.Sender, "pat@acme.com"));
        Assert.Equal(2, usage.EvidenceCount(acme.Path, FilingFeatureKind.SubjectWord, "invoice"));
        Assert.Equal(0, usage.EvidenceCount(acme.Path, FilingFeatureKind.Sender, "me@mine.com"));
        Assert.True(usage.Studied.ContainsKey(acme.Path));

        // Already sampled: nothing to do until the sample is two weeks old.
        Assert.Equal(0, await study.RunAsync(store.Folders));
        clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(1, await study.RunAsync(store.Folders));
    }

    [Fact]
    public async Task A_run_reads_no_more_folders_than_its_cap()
    {
        var store = new FakeMailStore();
        for (var i = 0; i < 5; i++) store.Folders.Add(Node($@"Mailbox\F{i}"));

        var usage = new FakeFolderUsage();
        var study = new FolderStudy(store, usage, new FakeClock { UtcNow = usage.Now }) { Pause = TimeSpan.Zero, MaxFoldersPerRun = 2 };

        Assert.Equal(2, await study.RunAsync(store.Folders));
        Assert.Equal(2, await study.RunAsync(store.Folders));
        Assert.Equal(1, await study.RunAsync(store.Folders));
    }
}
