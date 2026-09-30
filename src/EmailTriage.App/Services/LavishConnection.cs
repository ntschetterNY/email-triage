using System.Diagnostics;
using System.IO;
using System.Net.Http;
using EmailTriage.Core.Services;

namespace EmailTriage.App.Services;

/// <summary>
/// Where Lavish comments go and whose name they go under. The app holds no
/// GitHub secret of its own: it borrows a token from the environment or the
/// `gh` CLI the user is already signed in to, and only when a comment is sent.
/// </summary>
public sealed class LavishConnection
{
    private const string FallbackRepo = "ntschetterNY/email-triage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly AppSettings _settings;
    private Task<string?>? _token;

    public LavishConnection(AppSettings settings) => _settings = settings;

    public string Repo =>
        _settings.LavishRepo is { Length: > 0 } configured ? configured.Trim()
        : AppUpdater.Repo is { Length: > 0 } stamped ? stamped
        : FallbackRepo;

    public static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage", "lavish.json");

    /// <summary>A client for the repo, with a token when one could be found. Looked up once per run.</summary>
    public async Task<GitHubIssues> ConnectAsync()
    {
        _token ??= Task.Run(FindToken);
        return new GitHubIssues(Http, Repo, await _token.ConfigureAwait(true));
    }

    /// <summary>After GitHub turns a token down, look again next time rather than retrying it.</summary>
    public void ForgetToken() => _token = null;

    private static string? FindToken()
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnv) return fromEnv.Trim();

        try
        {
            using var gh = Process.Start(new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (gh is null) return null;

            var output = gh.StandardOutput.ReadToEndAsync();
            if (!gh.WaitForExit(5000)) { gh.Kill(); return null; }
            return gh.ExitCode == 0 && output.Result.Trim() is { Length: > 0 } token ? token : null;
        }
        catch
        {
            return null; // no gh on this machine
        }
    }
}
