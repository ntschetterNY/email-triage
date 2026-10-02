using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using Xunit;

namespace EmailTriage.Tests;

public class FolderUsageRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");

    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly FolderUsageRepository _repo;

    public FolderUsageRepositoryTests()
    {
        _db = new Database(_dbPath);
        _db.Migrate();
        _repo = new FolderUsageRepository(_db, _clock);
    }

    [Fact]
    public async Task Evidence_accumulates_across_recordings_and_reads_back()
    {
        var acme = @"Mailbox\Clients\Acme";
        var first = new[]
        {
            new FilingFeature(FilingFeatureKind.Sender, "pat@acme.com"),
            new FilingFeature(FilingFeatureKind.SubjectWord, "invoice"),
            new FilingFeature(FilingFeatureKind.SubjectWord, "invoice"),
        };

        await _repo.RecordEvidenceAsync(acme, first);
        await _repo.RecordEvidenceAsync(acme, new[] { new FilingFeature(FilingFeatureKind.Sender, "pat@acme.com") });
        await _repo.RecordEvidenceAsync(acme, Array.Empty<FilingFeature>());

        var evidence = await _repo.GetEvidenceAsync();

        Assert.Equal(2, evidence.Count);
        Assert.Equal(2, evidence.Single(e => e.Kind == FilingFeatureKind.Sender).Count);
        Assert.Equal(2, evidence.Single(e => e.Kind == FilingFeatureKind.SubjectWord && e.Token == "invoice").Count);
        Assert.All(evidence, e => Assert.Equal(acme, e.FolderPath));
    }

    [Fact]
    public async Task Study_stamps_are_kept_per_folder_and_overwritten()
    {
        await _repo.MarkStudiedAsync(@"Mailbox\A");
        _clock.Advance(TimeSpan.FromDays(3));
        await _repo.MarkStudiedAsync(@"Mailbox\B");
        await _repo.MarkStudiedAsync(@"Mailbox\A");

        var studied = await _repo.GetStudiedAsync();

        Assert.Equal(2, studied.Count);
        Assert.Equal(_clock.UtcNow, studied[@"Mailbox\A"]);
        Assert.Equal(_clock.UtcNow, studied[@"Mailbox\B"]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }
}
