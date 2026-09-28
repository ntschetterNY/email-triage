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
/// The CLI prefers ANTHROPIC_API_KEY (and friends) over that login whenever
/// they are set, which quietly moves every call onto API credits. So unless
/// the user opts in with "AiAllowApiKey", those variables are removed from
/// the child's environment and its user settings (where an apiKeyHelper or
/// an "env" block could set one) are not loaded.
///
/// Each call also runs lean: no tools, MCP servers, skills or Claude Code
/// system prompt, which otherwise add ~30k input tokens to every question.
///
/// The prompt goes in over stdin (no command-line length or quoting limits)
/// and the answer comes back as one JSON object on stdout.
/// </summary>
public sealed class ClaudeCodeCli : IAiAssistant
{
    private readonly string? _configuredPath;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly bool _allowApiKey;
    private readonly AiUsageLog? _usage;
    private string? _resolvedPath;

    /// <summary>What the CLI answers through; a key here beats the Claude login.</summary>
    private static readonly string[] ApiAuthVariables =
    {
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
    };

    /// <summary>
    /// Replaces the Claude Code agent prompt. Each feature's own prompt says
    /// what to write and in what shape; this only keeps the answer bare.
    /// Plain words only: it travels on a command line, possibly through cmd.exe.
    /// </summary>
    private const string SystemPrompt =
        "You are the writing and search assistant inside a desktop email app. " +
        "Do exactly what the request asks and reply with only the requested output.";

    public ClaudeCodeCli(
        string? cliPath = null,
        string model = "claude-opus-5",
        TimeSpan? timeout = null,
        bool allowApiKey = false,
        AiUsageLog? usage = null)
    {
        _configuredPath = string.IsNullOrWhiteSpace(cliPath) ? null : cliPath;
        _model = SanitizeToken(model, fallback: "claude-opus-5");
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
        _allowApiKey = allowApiKey;
        _usage = usage;
    }

    public async Task<string> AskAsync(string prompt, string? model = null, string? feature = null, CancellationToken ct = default)
    {
        var cli = _resolvedPath ??= FindCli()
            ?? throw new AiUnavailableException(
                "Claude Code was not found. Install it from claude.com/claude-code, run `claude` once to sign in, " +
                "or set \"ClaudeCliPath\" in settings.json.");

        var chosenModel = string.IsNullOrWhiteSpace(model) ? _model : SanitizeToken(model, _model);
        var startedAt = DateTimeOffset.Now;
        var psi = BuildStartInfo(cli, $"-p --output-format json --model {chosenModel} {LeanArgs(_allowApiKey)}");
        ScrubAuth(psi);

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
            {
                var failure = DescribeFailure(process.ExitCode, output, errors);
                Log(output, feature, chosenModel, startedAt, failure);
                throw new AiUnavailableException(failure);
            }

            try
            {
                var result = ParseResult(output);
                Log(output, feature, chosenModel, startedAt, error: null);
                return result;
            }
            catch (AiUnavailableException ex)
            {
                Log(output, feature, chosenModel, startedAt, ex.Message);
                throw;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            Log("", feature, chosenModel, startedAt, "timed out");
            throw new AiUnavailableException(
                $"Claude took longer than {(int)_timeout.TotalSeconds}s - try again, or set a faster \"AiModel\" in settings.json.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>
    /// Asks the CLI which account it would answer through, with the same
    /// environment the AI commands get. Null when the CLI can't be found or run.
    /// </summary>
    public async Task<AiAuthInfo?> GetAuthAsync(CancellationToken ct = default)
    {
        var cli = _resolvedPath ??= FindCli();
        if (cli is null) return null;

        var psi = BuildStartInfo(cli, "auth status");
        ScrubAuth(psi);

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!process.Start()) return null;
            process.StandardInput.Close();
            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return ParseAuth(stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
    }

    /// <summary>Reads `claude auth status` JSON. Public and pure for tests.</summary>
    public static AiAuthInfo? ParseAuth(string stdout)
    {
        if (ExtractJsonObject(stdout) is not { } json) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new AiAuthInfo(
                root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True,
                Str(root, "authMethod") ?? "unknown",
                Str(root, "email"),
                Str(root, "subscriptionType"),
                Str(root, "apiKeySource"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Reads the token counts and cost from the CLI's JSON envelope. Public and
    /// pure for tests; an envelope without usage (a crash, a timeout) logs zeros.
    /// </summary>
    public static AiCall ParseUsage(string stdout, string feature, string model, DateTimeOffset at, string? error)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0;
        var duration = (int)Math.Max(0, (DateTimeOffset.Now - at).TotalMilliseconds);

        if (ExtractJsonObject(stdout) is { } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    input = Num(u, "input_tokens");
                    output = Num(u, "output_tokens");
                    cacheRead = Num(u, "cache_read_input_tokens");
                    cacheWrite = Num(u, "cache_creation_input_tokens");
                }
                if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number)
                    cost = c.GetDecimal();
                if (root.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number)
                    duration = d.GetInt32();
            }
            catch (JsonException) { /* counts stay zero */ }
        }

        return new AiCall(at, feature, model, input, output, cacheRead, cacheWrite, cost, duration, error is null, error);
    }

    private void Log(string stdout, string? feature, string model, DateTimeOffset at, string? error) =>
        _usage?.Record(ParseUsage(stdout, string.IsNullOrWhiteSpace(feature) ? "other" : feature, model, at, error));

    /// <summary>
    /// Flags that strip Claude Code's agent machinery from a one-shot answer.
    /// Without "AiAllowApiKey", user settings are skipped too, so an
    /// apiKeyHelper or "env" block there can't swap the login for a key.
    /// </summary>
    private static string LeanArgs(bool allowApiKey) =>
        "--tools \"\" --strict-mcp-config --disable-slash-commands " +
        (allowApiKey ? "" : "--setting-sources \"\" ") +
        $"--system-prompt \"{SystemPrompt}\"";

    private void ScrubAuth(ProcessStartInfo psi)
    {
        if (_allowApiKey) return;
        foreach (var name in ApiAuthVariables) psi.Environment.Remove(name);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static ProcessStartInfo BuildStartInfo(string cli, string args)
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

        // npm installs land as claude.cmd, which Process can only run through
        // the shell; the args are fixed tokens and plain quoted words, so
        // quoting stays trivial.
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
        var detail = Truncate(FirstNonEmpty(EnvelopeResult(stdout), stderr, stdout), 240);

        // Out of API credits means the call went to a key, not the Claude login.
        if (detail.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude is using an API key whose credits have run out, not your Claude login. " +
                   "Remove ANTHROPIC_API_KEY from your environment (or set \"AiAllowApiKey\": false), then try again.";
        }

        if (detail.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("limit reached", StringComparison.OrdinalIgnoreCase))
        {
            return $"Your Claude plan's usage limit is used up for now: {detail}";
        }

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

    /// <summary>The "result" text of a JSON envelope, which is where the CLI puts its error.</summary>
    private static string EnvelopeResult(string stdout)
    {
        if (ExtractJsonObject(stdout) is not { } json) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "result") ?? "";
        }
        catch (JsonException) { return ""; }
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
