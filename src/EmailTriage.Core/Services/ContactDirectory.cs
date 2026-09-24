using System.Text.Json;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// The address book behind recipient autocomplete. Searches run in memory on
/// every keystroke, so they never wait on Outlook; loading happens in the
/// background, from a disk cache first and then from Outlook itself.
///
/// Searching and loading run on different threads. The loader builds a new
/// immutable snapshot and swaps it in whole, so a search never sees a
/// half-built index.
/// </summary>
public sealed class ContactDirectory
{
    private const int GalBatchSize = 100;
    private const int GalCap = 60_000;

    private static readonly char[] WordBreaks = { ' ', ',', '.', '-', '(', ')', '\'', '"', '/', '_' };
    private static readonly char[] LocalBreaks = { '.', '_', '-', '+' };

    private readonly IMailStore? _store;
    private readonly string? _cachePath;
    private readonly IClock _clock;

    private volatile Indexed[] _snapshot = Array.Empty<Indexed>();

    // Loader-only state.
    private readonly Dictionary<string, ContactEntry> _byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _nameBoosts = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loading;

    /// <summary>How long a cached copy of the company directory is trusted before re-reading it.</summary>
    public TimeSpan DirectoryRefreshAge { get; init; } = TimeSpan.FromHours(24);

    public ContactDirectory(IMailStore? store, string? cachePath, IClock clock)
    {
        _store = store;
        _cachePath = cachePath;
        _clock = clock;
    }

    public int Count => _snapshot.Length;

    public static string DefaultCachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage",
            "contacts.json");

    // ---- search -----------------------------------------------------------

    /// <summary>
    /// Best matches for a partly typed name or address. Every word typed must
    /// start a word of the name or of the address, so "ja sm" finds
    /// "Jane Smith" and "jsmith@" finds her by address.
    /// </summary>
    public IReadOnlyList<ContactEntry> Search(string query, int limit = 8)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length == 0) return Array.Empty<ContactEntry>();

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var snapshot = _snapshot;
        var hits = new List<(Indexed Entry, int Score)>();

        foreach (var e in snapshot)
        {
            var score = Score(e, query, tokens);
            if (score > int.MinValue) hits.Add((e, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Contact.Display, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(h => h.Entry.Contact)
            .ToList();
    }

    /// <summary>The people corresponded with most, for a bare "@" with nothing typed yet.</summary>
    public IReadOnlyList<ContactEntry> Frequent(int limit = 8) =>
        _snapshot
            .Where(e => e.Contact.Weight > 0)
            .OrderByDescending(e => e.Contact.Weight)
            .ThenBy(e => e.Contact.Display, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(e => e.Contact)
            .ToList();

    private static int Score(Indexed e, string query, string[] tokens)
    {
        foreach (var t in tokens)
        {
            if (!StartsAny(e.Words, t) && !StartsAny(e.LocalParts, t) && !e.Address.StartsWith(t, StringComparison.Ordinal))
            {
                // Last resort: part of an address typed from the middle.
                if (query.Length >= 3 && e.Address.Contains(query, StringComparison.Ordinal))
                    return Rank(e) - 200;
                return int.MinValue;
            }
        }

        var score = Rank(e);
        if (e.Name.StartsWith(query, StringComparison.Ordinal)) score += 300;
        else if (e.Words.Length > 0 && e.Words[0].StartsWith(tokens[0], StringComparison.Ordinal)) score += 150;
        if (e.Address.StartsWith(query, StringComparison.Ordinal)) score += 200;
        return score;
    }

    /// <summary>Correspondence counts matter, but with diminishing returns.</summary>
    private static int Rank(Indexed e) => (int)(Math.Log2(1 + Math.Max(0, e.Contact.Weight)) * 60);

    private static bool StartsAny(string[] words, string token)
    {
        foreach (var w in words)
            if (w.StartsWith(token, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- loading ----------------------------------------------------------

    /// <summary>Starts loading in the background; safe to call more than once.</summary>
    public Task StartLoading(CancellationToken ct = default) => _loading ??= Task.Run(() => LoadAsync(ct), ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        var cache = ReadCache();
        if (cache is not null)
        {
            foreach (var c in cache.Contacts) Merge(c);
            Publish();
        }

        if (_store is null) return;

        try
        {
            foreach (var c in await _store.GetFrequentContactsAsync(ct).ConfigureAwait(false)) Merge(c);
            Publish();
        }
        catch when (!ct.IsCancellationRequested) { /* the directory still helps */ }

        var directoryFresh = cache is not null
            && _clock.UtcNow - cache.DirectoryReadUtc < DirectoryRefreshAge;

        var directoryRead = false;
        if (!directoryFresh)
        {
            try
            {
                // Small batches, each its own trip to Outlook, so a move or send
                // typed meanwhile is never stuck behind the whole directory.
                for (var start = 0; start < GalCap; start += GalBatchSize)
                {
                    var batch = await _store.GetAddressBookBatchAsync(start, GalBatchSize, ct).ConfigureAwait(false);
                    foreach (var c in batch.Entries) Merge(c);

                    if ((start / GalBatchSize) % 10 == 0) Publish();
                    if (start + GalBatchSize >= batch.Total || batch.Entries.Count == 0) break;
                }
                directoryRead = true;
            }
            catch when (!ct.IsCancellationRequested) { /* no Exchange directory, or Outlook busy */ }
        }

        Publish();
        WriteCache(directoryRead ? _clock.UtcNow : cache?.DirectoryReadUtc ?? DateTimeOffset.MinValue);
    }

    /// <summary>Adds or strengthens an entry. Loader thread only.</summary>
    public void Merge(ContactEntry c)
    {
        if (string.IsNullOrWhiteSpace(c.Address))
        {
            // A name seen in Sent Items: remember the boost for whoever owns it.
            if (!string.IsNullOrWhiteSpace(c.Name))
                _nameBoosts[c.Name.Trim()] = _nameBoosts.GetValueOrDefault(c.Name.Trim()) + c.Weight;
            return;
        }

        var address = c.Address.Trim();
        if (!address.Contains('@')) return;

        if (_byAddress.TryGetValue(address, out var existing))
        {
            // Keep the better name (a real one over a bare address) and the
            // stronger signal, rather than adding: the cache already holds
            // counts from last time.
            var name = string.IsNullOrWhiteSpace(existing.Name) || existing.Name == existing.Address ? c.Name : existing.Name;
            _byAddress[address] = existing with { Name = name, Weight = Math.Max(existing.Weight, c.Weight) };
        }
        else
        {
            _byAddress[address] = c with { Address = address, Name = c.Name.Trim() };
        }
    }

    /// <summary>Builds a fresh search snapshot from everything merged so far.</summary>
    public void Publish()
    {
        _snapshot = _byAddress.Values
            .Select(c => _nameBoosts.TryGetValue(c.Name, out var boost) ? c with { Weight = c.Weight + boost } : c)
            .Select(Indexed.From)
            .ToArray();
    }

    // ---- cache ------------------------------------------------------------

    private sealed record CacheFile(DateTimeOffset DirectoryReadUtc, List<ContactEntry> Contacts);

    private CacheFile? ReadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return null;
        try { return JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath)); }
        catch { return null; } // a bad cache is simply rebuilt
    }

    private void WriteCache(DateTimeOffset directoryReadUtc)
    {
        if (_cachePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new CacheFile(directoryReadUtc, _byAddress.Values.ToList());
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch { /* next start just reloads from Outlook */ }
    }

    /// <summary>A contact with its search keys worked out once, not per keystroke.</summary>
    private sealed record Indexed(ContactEntry Contact, string Name, string Address, string[] Words, string[] LocalParts)
    {
        public static Indexed From(ContactEntry c)
        {
            var name = c.Name.ToLowerInvariant();
            var address = c.Address.ToLowerInvariant();
            var at = address.IndexOf('@');
            var local = at < 0 ? address : address[..at];

            return new Indexed(
                c,
                name,
                address,
                name.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries),
                local.Split(LocalBreaks, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
