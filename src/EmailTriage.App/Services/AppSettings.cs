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

    /// <summary>
    /// How folders are named, with each part in {braces}. Edited on the
    /// Settings page (Ctrl+,); see <see cref="FolderScheme"/>.
    /// </summary>
    public string FolderNamePattern { get; set; } = FolderScheme.DefaultNamePattern;

    /// <summary>How those parts nest, one folder level per backslash.</summary>
    public string FolderLayout { get; set; } = FolderScheme.DefaultLayout;

    /// <summary>
    /// Folder, from the top of the mailbox, the nested tree is built under
    /// ("Inbox", "Projects"). Empty keeps each folder where it already is,
    /// and puts new ones at the top of the mailbox.
    /// </summary>
    public string FolderHome { get; set; } = "";

    /// <summary>A folder created from the move palette with a name in the scheme is made nested.</summary>
    public bool NestNewFolders { get; set; } = true;

    /// <summary>A method, not a property, so it stays out of settings.json.</summary>
    public FolderScheme GetFolderScheme() => new(FolderNamePattern, FolderLayout);

    /// <summary>How many messages to pull into the triage list.</summary>
    public int InboxPageSize { get; set; } = 250;

    /// <summary>Most messages a from:, to: or subject: search pulls in from beyond the Inbox page.</summary>
    public int SearchResultLimit { get; set; } = 500;

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

    /// <summary>Whether a reply with a meeting (Shift+S) starts with Teams switched on.</summary>
    public bool TeamsByDefault { get; set; } = true;

    /// <summary>How soon before a meeting the join key picks it over the one you are in.</summary>
    public int JoinLeadMinutes { get; set; } = 10;

    /// <summary>
    /// The view the Calendar tab opens on until you pick another (Day,
    /// WorkWeek, Week, Month or Agenda); after that it remembers your last one.
    /// </summary>
    public string CalendarView { get; set; } = "WorkWeek";

    /// <summary>
    /// The hours AI drafts may offer people a meeting in. They offer time
    /// right up to your existing meetings, with no gap either side.
    /// </summary>
    public int WorkdayStartHour { get; set; } = 7;
    public int WorkdayEndHour { get; set; } = 16;

    /// <summary>How many times a draft offers when the email is about meeting.</summary>
    public int ProposedSlotCount { get; set; } = 3;

    /// <summary>How many working days ahead, from tomorrow, drafts look for free time.</summary>
    public int AvailabilityWorkingDays { get; set; } = 10;

    /// <summary>Lunch, which drafts only offer when nothing else fits. Equal hours turn it off.</summary>
    public int LunchStartHour { get; set; } = 12;
    public int LunchEndHour { get; set; } = 13;

    /// <summary>
    /// Default model for every AI command. Anything the Claude Code CLI
    /// accepts works here; "sonnet" answers faster than the default.
    /// </summary>
    public string AiModel { get; set; } = "claude-opus-5";

    /// <summary>
    /// Per-command overrides, so e.g. drafts can run on "sonnet" while
    /// replies stay on "claude-opus-5". Empty means use AiModel. Follow-up
    /// chases are short and formulaic, so they start on "sonnet"; the
    /// Settings page (Ctrl+,) picks another.
    /// </summary>
    public string AiDraftModel { get; set; } = "";
    public string AiFollowUpModel { get; set; } = "sonnet";
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
    /// Let AI commands use an API key (ANTHROPIC_API_KEY, an apiKeyHelper, or
    /// Bedrock/Vertex) when one is set. Off by default, so they always run on
    /// your Claude login rather than on API credits.
    /// </summary>
    public bool AiAllowApiKey { get; set; }

    /// <summary>
    /// Days a blocker or hand-off may sit unchanged before the board flags it
    /// for a follow-up chase. 0 turns the flagging off.
    /// </summary>
    public int FollowUpAfterDays { get; set; } = 5;

    /// <summary>
    /// Flagging a mail (a) asks what has to happen, who has the ball and
    /// when, in one popup. Off, `a` flags at once as it always did.
    /// </summary>
    public bool AskDetailsOnFlag { get; set; } = true;

    /// <summary>
    /// When a hand-off has a due date but no follow-up day, chase this many
    /// days before it is due. 0 chases on the due day itself.
    /// </summary>
    public int FollowUpBeforeDueDays { get; set; } = 2;

    /// <summary>
    /// On launch, install the latest GitHub release if it is newer. Only
    /// release builds update themselves; a local build never does.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

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

    /// <summary>The rules AI drafts follow when offering times you are free.</summary>
    public AvailabilityRules Availability
    {
        get
        {
            var start = Math.Clamp(WorkdayStartHour, 0, 23);
            return new()
            {
                DayStart = TimeSpan.FromHours(start),
                DayEnd = TimeSpan.FromHours(Math.Clamp(WorkdayEndHour, start + 1, 24)),
                WorkingDays = Math.Clamp(AvailabilityWorkingDays, 1, 30),
                LunchStart = LunchEndHour > LunchStartHour ? TimeSpan.FromHours(LunchStartHour) : null,
                LunchEnd = LunchEndHour > LunchStartHour ? TimeSpan.FromHours(LunchEndHour) : null,
                SlotCount = Math.Clamp(ProposedSlotCount, 1, 6),
            };
        }
    }

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
