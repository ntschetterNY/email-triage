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
