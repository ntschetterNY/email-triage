using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace EmailTriage.App.Services;

/// <summary>
/// Brings the app up to the latest GitHub release on launch. Only copies
/// built by the release workflow know their repo (it is stamped in as
/// assembly metadata), so a local build never replaces itself.
/// </summary>
public static class AppUpdater
{
    public const string SkipArgument = "--skip-update";

    private const string ZipAsset = "EmailTriage-win-x64.zip";
    private const string VersionAsset = "version.txt";

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// For the title bar: "v1.0.4" for a release, "local build" for one made
    /// on this machine, which is never stamped with a release number.
    /// </summary>
    public static string DisplayVersion =>
        Repo is { Length: > 0 } ? $"v{CurrentVersion.ToString(3)}" : "local build";

    private static string? Repo =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value;

    /// <summary>
    /// Returns true when an update has been staged and the app should exit
    /// so the swap can happen; a helper relaunches it afterwards. Any failure
    /// - offline, GitHub down, no write access - just carries on with the
    /// installed version.
    /// </summary>
    public static async Task<bool> TryUpdateAsync(string[] args)
    {
        if (args.Contains(SkipArgument) || Repo is not { Length: > 0 } repo) return false;

        var exe = Environment.ProcessPath;
        var installDir = Path.GetDirectoryName(exe);
        if (exe is null || installDir is null || !CanWrite(installDir)) return false;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EmailTriage-Updater");

        // The latest/download redirect is not subject to the API's rate limit,
        // which a whole office behind one address would hit.
        var baseUrl = $"https://github.com/{repo}/releases/latest/download/";

        Version latest;
        try
        {
            var text = await http.GetStringAsync(baseUrl + VersionAsset);
            if (!Version.TryParse(text.Trim().TrimStart('v'), out latest!)) return false;
        }
        catch { return false; }

        if (latest <= CurrentVersion) return false;

        var progress = ShowProgress($"Updating Email Triage to {latest}…");
        try
        {
            var work = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmailTriage", "update");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            var staging = Path.Combine(work, "files");
            Directory.CreateDirectory(staging);

            // The zip is a few MB; allow for a slow connection.
            http.Timeout = TimeSpan.FromMinutes(3);
            var zip = Path.Combine(work, ZipAsset);
            await using (var source = await http.GetStreamAsync(baseUrl + ZipAsset))
            await using (var target = File.Create(zip))
            {
                await source.CopyToAsync(target);
            }
            ZipFile.ExtractToDirectory(zip, staging);

            if (!File.Exists(Path.Combine(staging, Path.GetFileName(exe))))
                throw new InvalidDataException($"{ZipAsset} does not contain {Path.GetFileName(exe)}.");

            StartSwap(work, staging, installDir, exe);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Update to {latest} failed: {ex}");
            return false;
        }
        finally
        {
            progress.Close();
        }
    }

    /// <summary>
    /// The running exe and its native DLLs are locked, so the copy is done by
    /// a hidden PowerShell that waits for this process to exit, then starts
    /// the new version - or the old one again if the copy failed.
    /// </summary>
    private static void StartSwap(string work, string staging, string installDir, string exe)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 } catch { }
            $ok = $false
            for ($i = 0; $i -lt 10 -and -not $ok; $i++) {
                try {
                    Copy-Item -Path (Join-Path {{Quote(staging)}} '*') -Destination {{Quote(installDir)}} -Recurse -Force
                    $ok = $true
                } catch {
                    $err = $_
                    Start-Sleep -Milliseconds 500
                }
            }
            if ($ok) {
                Start-Process -FilePath {{Quote(exe)}}
                Remove-Item -Path {{Quote(staging)}} -Recurse -Force -ErrorAction SilentlyContinue
            } else {
                Add-Content -Path {{Quote(LogPath)}} -Value "[$(Get-Date -Format o)] Update copy failed: $err"
                Start-Process -FilePath {{Quote(exe)}} -ArgumentList '{{SkipArgument}}'
            }
            """;

        var scriptPath = Path.Combine(work, "apply-update.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static Window ShowProgress(string message)
    {
        var window = new Window
        {
            Title = "Email Triage",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 12) },
                    new ProgressBar { IsIndeterminate = true, Height = 6 },
                },
            },
        };
        window.Show();
        return window;
    }

    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage", "error.log");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never itself throw */ }
    }
}
