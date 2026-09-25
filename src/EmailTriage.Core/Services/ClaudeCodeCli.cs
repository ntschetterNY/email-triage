using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EmailTriage.Core.Abstractions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Asks Claude by running the Claude Code CLI (`claude -p`) as a child
/// process. That uses whatever sign-in the user already has - a Claude
/// subscription login from `claude` in a terminal - so this app never holds
/// an API key of its own.
///
/// The prompt goes in over stdin (no command-line length or quoting limits)
/// and the answer comes back as one JSON object on stdout.
/// </summary>
public sealed class ClaudeCodeCli : IAiAssistant
{
    private readonly string? _configuredPath;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private string? _resolvedPath;

    public ClaudeCodeCli(string? cliPath = null, string model = "claude-opus-5", TimeSpan? timeout = null)
    {
        _configuredPath = string.IsNullOrWhiteSpace(cliPath) ? null : cliPath;
        _model = SanitizeToken(model, fallback: "claude-opus-5");
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public async Task<string> AskAsync(string prompt, string? model = null, CancellationToken ct = default)
    {
        var cli = _resolvedPath ??= FindCli()
            ?? throw new AiUnavailableException(
                "Claude Code was not found. Install it from claude.com/claude-code, run `claude` once to sign in, " +
                "or set \"ClaudeCliPath\" in settings.json.");

        var psi = BuildStartInfo(cli, string.IsNullOrWhiteSpace(model) ? _model : SanitizeToken(model, _model));

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new AiUnavailableException($"Claude Code did not start ({cli}).");
        }
        catch (Exception ex) when (ex is not AiUnavailableException)
        {
            _resolvedPath = null; // re-probe next time; the install may have moved
            throw new AiUnavailableException($"Claude Code did not start: {ex.Message}", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new AiUnavailableException(DescribeFailure(process.ExitCode, output, errors));

            return ParseResult(output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new AiUnavailableException(
                $"Claude took longer than {(int)_timeout.TotalSeconds}s - try again, or set a faster \"AiModel\" in settings.json.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static ProcessStartInfo BuildStartInfo(string cli, string model)
    {
        // Run from the temp folder so the CLI picks up no project context
        // (CLAUDE.md and the like) from wherever this app happens to live.
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var args = $"-p --output-format json --model {model}";

        // npm installs land as claude.cmd, which Process can only run through
        // the shell; the args are fixed simple tokens, so quoting stays trivial.
        if (cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            cli.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"\"{cli}\" {args}\"";
        }
        else
        {
            psi.FileName = cli;
            psi.Arguments = args;
        }

        return psi;
    }

    /// <summary>
    /// The configured path, or the first `claude` found: the native installer's
    /// location, then anywhere on PATH, then an npm global install.
    /// </summary>
    private string? FindCli()
    {
        if (_configuredPath is not null)
            return File.Exists(_configuredPath) ? _configuredPath : null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var names = OperatingSystem.IsWindows()
            ? new[] { "claude.exe", "claude.cmd" }
            : new[] { "claude" };

        var candidates = new List<string>();
        foreach (var name in names)
            candidates.Add(Path.Combine(home, ".local", "bin", name));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var name in names)
        {
            try { candidates.Add(Path.Combine(dir.Trim(), name)); }
            catch { /* an unparseable PATH entry is somebody else's problem */ }
        }

        if (OperatingSystem.IsWindows())
            candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Reads the CLI's `--output-format json` envelope and returns the answer
    /// text. Public and pure so the parsing is testable without a process.
    /// </summary>
    public static string ParseResult(string stdout)
    {
        var json = ExtractJsonObject(stdout)
            ?? throw new AiUnavailableException(
                $"Claude gave an answer this app could not read: {Truncate(stdout, 200)}");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? ""
                : "";

            if (root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
            {
                throw new AiUnavailableException(
                    $"Claude reported an error: {Truncate(result.Length > 0 ? result : json, 200)}");
            }

            if (result.Length == 0)
                throw new AiUnavailableException("Claude sent back an empty answer - try again.");

            return result;
        }
        catch (JsonException ex)
        {
            throw new AiUnavailableException(
                $"Claude gave an answer this app could not read: {Truncate(stdout, 200)}", ex);
        }
    }

    /// <summary>The outermost {...} in the text; the CLI may log lines around it.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string DescribeFailure(int exitCode, string stdout, string stderr)
    {
        var detail = Truncate(FirstNonEmpty(stderr, stdout), 240);

        // The CLI's own words for "sign in first" - point at the fix directly.
        if (detail.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("authenticat", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude Code is not signed in - run `claude` in a terminal and log in, then try again.";
        }

        return detail.Length > 0
            ? $"Claude Code failed (exit {exitCode}): {detail}"
            : $"Claude Code failed (exit {exitCode}).";
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) ?? "";

    private static string Truncate(string text, int max)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max] + "...";
    }

    /// <summary>Model names travel on a command line; keep them to safe characters.</summary>
    private static string SanitizeToken(string value, string fallback)
    {
        var clean = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or ':' or '_').ToArray());
        return clean.Length > 0 ? clean : fallback;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* it may have exited in the meantime */ }
    }
}
