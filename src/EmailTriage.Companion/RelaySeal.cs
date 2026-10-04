using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmailTriage.Companion;

// What crosses the relay. The relay is someone else's server, so it only
// ever carries ciphertext: each request and response is compressed, sealed
// with AES-256-GCM under a key that only travels inside the pairing code,
// and cut into chunks small enough for one broadcast message.

/// <summary>A call the phone makes through the relay: what it would have sent over HTTPS.</summary>
/// <param name="At">When the phone sent it, Unix milliseconds; stale requests are dropped.</param>
/// <param name="Path">Path and query, e.g. "/api/folders?q=ela".</param>
/// <param name="Body">The JSON body, or null.</param>
public sealed record RelayRequest(string Id, long At, string Method, string Path, string? Body);

/// <summary>The PC's answer: the status and body the HTTPS server gave.</summary>
public sealed record RelayResponse(string Id, int Status, string Body);

/// <summary>One broadcast message: part <see cref="I"/> of <see cref="N"/> of a sealed request or response.</summary>
public sealed record RelayChunk(string Id, int I, int N, string D);

public static class RelaySeal
{
    /// <summary>Base64 characters per chunk: under the free plan's 256 KB broadcast limit, with room for the envelope.</summary>
    public const int ChunkSize = 160 * 1024;

    /// <summary>Most chunks one message may have (about 30 MB): anything bigger is refused rather than buffered.</summary>
    public const int MaxChunks = 200;

    /// <summary>Most a sealed message may inflate to.</summary>
    private const int MaxPlaintext = 32 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Bound into each seal, so a request can't be replayed as a response or the other way round.
    private static readonly byte[] RequestLabel = "email-triage relay request"u8.ToArray();
    private static readonly byte[] ResponseLabel = "email-triage relay response"u8.ToArray();

    /// <summary>A new relay key: 256 bits.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The channel the PC and its devices meet on, made from the key so that
    /// only someone holding the pairing code can find it. Lower-case hex.
    /// </summary>
    public static string Channel(byte[] key) =>
        "email-triage-" + Convert.ToHexString(
            SHA256.HashData([.. key, .. "email-triage relay channel"u8])).ToLowerInvariant()[..40];

    /// <summary>Raw deflate, then AES-GCM; base64 of nonce, ciphertext and tag (CryptoKit's "combined" layout).</summary>
    public static string Seal(byte[] key, ReadOnlySpan<byte> plaintext, bool request)
    {
        var compressed = Deflate(plaintext);
        var sealedBytes = new byte[12 + compressed.Length + 16];
        var nonce = sealedBytes.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, compressed, sealedBytes.AsSpan(12, compressed.Length),
            sealedBytes.AsSpan(12 + compressed.Length), request ? RequestLabel : ResponseLabel);
        return Convert.ToBase64String(sealedBytes);
    }

    /// <summary>The plaintext, or a <see cref="CryptographicException"/> when it wasn't sealed with this key.</summary>
    public static byte[] Open(byte[] key, string sealedText, bool request)
    {
        byte[] sealedBytes;
        try { sealedBytes = Convert.FromBase64String(sealedText); }
        catch (FormatException ex) { throw new CryptographicException("Not a sealed message.", ex); }
        if (sealedBytes.Length < 12 + 16) throw new CryptographicException("Not a sealed message.");

        var compressed = new byte[sealedBytes.Length - 12 - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(12, compressed.Length),
            sealedBytes.AsSpan(12 + compressed.Length), compressed, request ? RequestLabel : ResponseLabel);
        return Inflate(compressed);
    }

    public static IReadOnlyList<RelayChunk> Chunk(string id, string sealedText)
    {
        var n = Math.Max(1, (sealedText.Length + ChunkSize - 1) / ChunkSize);
        if (n > MaxChunks) throw new CompanionException(413, "That's too big to send to the device.");
        return Enumerable.Range(0, n)
            .Select(i => new RelayChunk(id, i, n,
                sealedText.Substring(i * ChunkSize, Math.Min(ChunkSize, sealedText.Length - i * ChunkSize))))
            .ToList();
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var inflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = inflate.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxPlaintext) throw new CryptographicException("Sealed message is too big.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    public static T? Decode<T>(byte[] utf8) => JsonSerializer.Deserialize<T>(utf8, Json);

    public static string Text(byte[] utf8) => Encoding.UTF8.GetString(utf8);
}

/// <summary>Puts chunks back together; drops messages that never finish.</summary>
public sealed class RelayAssembler
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, (string?[] Parts, int Have, DateTimeOffset Started)> _pending = new();
    private readonly TimeProvider _time;

    public RelayAssembler(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The whole sealed text once the last chunk is in; otherwise null.</summary>
    public string? Add(RelayChunk chunk)
    {
        var now = _time.GetUtcNow();
        foreach (var stale in _pending.Where(p => now - p.Value.Started > GiveUpAfter).Select(p => p.Key).ToList())
            _pending.Remove(stale);

        if (string.IsNullOrEmpty(chunk.Id) || chunk.N is < 1 or > RelaySeal.MaxChunks || chunk.I < 0 || chunk.I >= chunk.N)
            return null;
        if (chunk.N == 1) return chunk.D;

        if (!_pending.TryGetValue(chunk.Id, out var entry))
            entry = (new string?[chunk.N], 0, now);
        if (entry.Parts.Length != chunk.N) return null;

        if (entry.Parts[chunk.I] is null) entry.Have++;
        entry.Parts[chunk.I] = chunk.D;

        if (entry.Have < chunk.N)
        {
            _pending[chunk.Id] = entry;
            return null;
        }

        _pending.Remove(chunk.Id);
        return string.Concat(entry.Parts);
    }
}
