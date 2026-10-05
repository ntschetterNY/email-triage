using System.Text.Json;
using System.Windows;
using EmailTriage.App.ViewModels;
using EmailTriage.Companion;
using EmailTriage.Core.Services;

namespace EmailTriage.App.Services;

/// <summary>
/// Lavish notes from the iPhone or iPad. The PC files them with its own
/// GitHub sign-in and keeps them in the same Lavish panel as the desktop's,
/// so the device never needs a token. Runs on the UI thread, where the panel lives.
/// </summary>
public sealed class PhoneFeedback : ICompanionFeedback
{
    private readonly LavishViewModel _lavish;

    public PhoneFeedback(LavishViewModel lavish) => _lavish = lavish;

    public Task<FeedbackResultDto> SendAsync(FeedbackRequest request, CancellationToken ct) =>
        OnUiThread(async () =>
        {
            var device = string.IsNullOrWhiteSpace(request.Device) ? "iPhone" : request.Device.Trim();
            var app = device.StartsWith("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad app" : "iPhone app";
            var target = new LavishTarget(
                Kind: Clip(request.Screen, 40) is { Length: > 0 } screen ? screen : "Screen",
                Label: Clip(request.Detail, 80),
                Path: $"{app} › {Clip(request.Screen, 40)}",
                Area: app);
            var version = $"{app} {Clip(request.AppVersion, 20)} · PC {AppUpdater.DisplayVersion}";

            var (note, form, problem) = await _lavish.SendFromDeviceAsync(request.Comment, target, version, Clip(device, 60));
            if (note.IssueNumber is { } n)
                return new FeedbackResultDto(true, n, note.IssueUrl ?? "", $"Filed #{n} on GitHub.");

            var message = problem is null
                ? "The PC isn't signed in to GitHub, so the note opens in GitHub's form. Press Submit there."
                : $"{problem.TrimEnd('.')}. The note opens in GitHub's form instead. Press Submit there.";
            return new FeedbackResultDto(false, null, form ?? "", message);
        });

    public Task<IReadOnlyList<FeedbackNoteDto>> ListAsync(CancellationToken ct) =>
        OnUiThread(async () =>
        {
            await _lavish.RefreshIfStaleAsync();
            return (IReadOnlyList<FeedbackNoteDto>)_lavish.All
                .Take(50)
                .Select(n => new FeedbackNoteDto(
                    n.Comment,
                    n.Target.Path.Length > 0 ? n.Target.Path : n.Target.Describe(),
                    n.IssueNumber,
                    n.IssueUrl,
                    JsonNamingPolicy.CamelCase.ConvertName(n.Stage.ToString()),
                    n.CreatedAt))
                .ToList();
        });

    private static async Task<T> OnUiThread<T>(Func<Task<T>> work)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new CompanionException(503, "Email Triage is closing on the PC.");
        return await await dispatcher.InvokeAsync(work);
    }

    private static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim().Replace('\n', ' ').Replace('\r', ' ');
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
