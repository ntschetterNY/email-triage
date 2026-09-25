using System.IO;
using System.Text.Json;
using EmailTriage.Core.Services;

namespace EmailTriage.App.Services;

/// <summary>User-adjustable behaviour, persisted next to the database.</summary>
public sealed class AppSettings
{
    /// <summary>Outlook category applied to mail that needs action.</summary>
    public string ActionCategory { get; set; } = "Action Required";

    /// <summary>Folder, relative to the mailbox root, that holds snoozed mail.</summary>
    public string SnoozeFolder { get; set; } = "Snoozed";

    /// <summary>How many messages to pull into the triage list.</summary>
    public int InboxPageSize { get; set; } = 250;

    /// <summary>How many of your recent sent messages to fold into conversations.</summary>
    public int SentPageSize { get; set; } = 200;

    /// <summary>Most messages shown stacked in the reading pane for one conversation.</summary>
    public int ThreadMessageLimit { get; set; } = 12;

    /// <summary>Conversations below the selected one to load ahead, so moving down is instant.</summary>
    public int PrefetchAhead { get; set; } = 5;

    /// <summary>
    /// Remote images are shown by default, since logos and pictures in mail
    /// are content people expect to see. Invisible tracking pixels are
    /// stripped either way; set this to block every remote image as well.
    /// </summary>
    public bool BlockRemoteImages { get; set; }

    /// <summary>Mark a message read once it has been on screen this long.</summary>
    public int MarkReadAfterMs { get; set; } = 1200;

    /// <summary>Times of day the snooze presets anchor to.</summary>
    public int MorningHour { get; set; } = 8;
    public int AfternoonHour { get; set; } = 13;
    public int EveningHour { get; set; } = 18;

    /// <summary>How far ahead the Calendar tab looks.</summary>
    public int CalendarDaysAhead { get; set; } = 14;

    /// <summary>Length of a calendar block when none is typed ("tomorrow 2pm" rather than "tomorrow 2pm 1h").</summary>
    public int DefaultEventMinutes { get; set; } = 30;

    /// <summary>Reminder on blocks made from mail; 0 for none.</summary>
    public int BlockReminderMinutes { get; set; } = 5;

    /// <summary>How soon before a meeting the join key picks it over the one you are in.</summary>
    public int JoinLeadMinutes { get; set; } = 10;

    /// <summary>
    /// Default model for every AI command. Anything the Claude Code CLI
    /// accepts works here; "sonnet" answers faster than the default.
    /// </summary>
    public string AiModel { get; set; } = "claude-opus-5";

    /// <summary>
    /// Per-command overrides, so e.g. drafts can run on "sonnet" while
    /// follow-up chases stay on "claude-opus-5". Empty means use AiModel.
    /// </summary>
    public string AiDraftModel { get; set; } = "";
    public string AiFollowUpModel { get; set; } = "";
    public string AiSearchModel { get; set; } = "";
    public string AiStyleModel { get; set; } = "";

    /// <summary>
    /// Full path to the Claude Code CLI. Empty finds `claude` on PATH and in
    /// the usual install locations.
    /// </summary>
    public string ClaudeCliPath { get; set; } = "";

    /// <summary>How long an AI command may run before it is given up on.</summary>
    public int AiTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Days a blocker or hand-off may sit unchanged before the board flags it
    /// for a follow-up chase. 0 turns the flagging off.
    /// </summary>
    public int FollowUpAfterDays { get; set; } = 5;

    /// <summary>The model each feature runs on: its own setting, or AiModel.</summary>
    public string ResolveDraftModel() => Pick(AiDraftModel);
    public string ResolveFollowUpModel() => Pick(AiFollowUpModel);
    public string ResolveSearchModel() => Pick(AiSearchModel);
    public string ResolveStyleModel() => Pick(AiStyleModel);

    private string Pick(string specific) =>
        string.IsNullOrWhiteSpace(specific) ? AiModel : specific.Trim();

    public SnoozeDayShape DayShape => new()
    {
        Morning = TimeSpan.FromHours(MorningHour),
        Afternoon = TimeSpan.FromHours(AfternoonHour),
        Evening = TimeSpan.FromHours(EveningHour),
    };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EmailTriage",
            "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* fall through to defaults */ }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(
            this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
