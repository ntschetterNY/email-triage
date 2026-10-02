using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EmailTriage.Companion;

/// <summary>
/// What a paired phone needs to trust this PC and be trusted by it: a
/// self-signed TLS certificate the phone pins by fingerprint, and a secret
/// token it sends with every request. Both are made on first use and kept in
/// one file, encrypted to the Windows user with DPAPI where that exists.
/// Resetting makes new ones, which unpairs every phone.
/// </summary>
public sealed class CompanionIdentity
{
    public X509Certificate2 Certificate { get; }

    /// <summary>The bearer token, base64url, 256 bits.</summary>
    public string Token { get; }

    /// <summary>SHA-256 of the certificate's DER bytes, lower-case hex: what the phone pins.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

    private CompanionIdentity(X509Certificate2 certificate, string token)
    {
        Certificate = certificate;
        Token = token;
    }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage", "companion.key");

    /// <summary>Loads the saved identity, or makes and saves a new one.</summary>
    public static CompanionIdentity LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<Saved>(Unprotect(File.ReadAllBytes(path)));
                if (saved is { Token.Length: > 0, Pfx.Length: > 0 })
                    return new CompanionIdentity(LoadPfx(Convert.FromBase64String(saved.Pfx)), saved.Token);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            // Unreadable (another Windows user, or damaged): start over, which
            // just means pairing the phone again.
        }

        return Create(path);
    }

    /// <summary>Replaces the identity with a new one; every paired phone has to pair again.</summary>
    public static CompanionIdentity Create(string? path = null)
    {
        path ??= DefaultPath;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Email Triage companion", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        // Apple refuses server certificates valid for more than 825 days.
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(820));

        // Round-trip through PFX: on Windows, SChannel cannot use the
        // ephemeral key CreateSelfSigned returns.
        var pfx = created.Export(X509ContentType.Pfx);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Protect(JsonSerializer.SerializeToUtf8Bytes(
            new Saved { Token = token, Pfx = Convert.ToBase64String(pfx) })));

        return new CompanionIdentity(LoadPfx(pfx), token);
    }

    /// <summary>Compares in constant time, so the token cannot be guessed a byte at a time.</summary>
    public bool Accepts(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(Token));
    }

    private static X509Certificate2 LoadPfx(byte[] pfx) =>
        new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private static byte[] Unprotect(byte[] data) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private sealed class Saved
    {
        public string Token { get; set; } = "";
        public string Pfx { get; set; } = "";
    }
}
