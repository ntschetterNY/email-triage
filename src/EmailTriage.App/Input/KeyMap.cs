using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailTriage.App.Input;

/// <summary>
/// Maps chords to actions, with the defaults from the original brief and a
/// user-editable override file.
///
/// The bindings are configurable on purpose. `k` is taken by "move to folder"
/// here, which displaces the usual vim meaning of "previous"; if that turns out
/// to fight muscle memory it should be changeable without a rebuild.
/// </summary>
public sealed class KeyMap
{
    private readonly Dictionary<KeyStroke, TriageAction> _bindings = new();
    private readonly Dictionary<TriageAction, List<KeyStroke>> _reverse = new();

    public IReadOnlyDictionary<KeyStroke, TriageAction> Bindings => _bindings;

    public static KeyMap CreateDefault()
    {
        var map = new KeyMap();
        foreach (var (stroke, action) in Defaults) map.Bind(stroke, action);
        return map;
    }

    /// <summary>
    /// The shipped bindings. The four the brief pinned down - k, g, r, Shift+R -
    /// are fixed points; the rest follow mutt/gmail convention.
    /// </summary>
    public static readonly (string Stroke, TriageAction Action)[] DefaultSpec =
    {
        // Movement. `k` is unavailable for "previous" because the brief assigns
        // it to the move command, so Up and `p` cover that instead.
        ("j",           TriageAction.NextMail),
        ("down",        TriageAction.NextMail),
        ("p",           TriageAction.PrevMail),
        ("up",          TriageAction.PrevMail),
        ("home",        TriageAction.FirstMail),
        ("end",         TriageAction.LastMail),
        ("pagedown",    TriageAction.PageDown),
        ("pageup",      TriageAction.PageUp),

        // Triage decisions
        ("a",           TriageAction.MarkActionRequired),
        ("n",           TriageAction.MarkNoAction),
        ("k",           TriageAction.MoveToFolder),
        ("g",           TriageAction.Snooze),
        ("e",           TriageAction.Archive),
        ("u",           TriageAction.ToggleRead),
        ("delete",      TriageAction.Delete),

        // Replying
        ("r",           TriageAction.ReplyAll),
        ("shift+r",     TriageAction.ReplySender),

        // Action list
        ("t",           TriageAction.AddNote),
        ("b",           TriageAction.AddBlocker),
        ("shift+a",     TriageAction.AddAssignment),
        ("x",           TriageAction.ToggleComplete),
        ("shift+p",     TriageAction.CyclePriority),
        ("o",           TriageAction.OpenInOutlook),

        // Shell
        ("tab",         TriageAction.SwitchSection),
        ("/",           TriageAction.Search),
        ("f5",          TriageAction.Refresh),
        ("shift+oem2",  TriageAction.ShowHelp),
        ("ctrl+z",      TriageAction.Undo),
        ("escape",      TriageAction.Cancel),
        ("enter",       TriageAction.Confirm),
    };

    private static IEnumerable<(KeyStroke, TriageAction)> Defaults =>
        DefaultSpec
            .Select(d => (KeyStroke.Parse(d.Stroke), d.Action))
            .Where(d => !d.Item1.IsEmpty);

    public void Bind(KeyStroke stroke, TriageAction action)
    {
        if (stroke.IsEmpty || action == TriageAction.None) return;

        _bindings[stroke] = action;

        if (!_reverse.TryGetValue(action, out var list))
            _reverse[action] = list = new List<KeyStroke>();

        if (!list.Contains(stroke)) list.Add(stroke);
    }

    public TriageAction Resolve(KeyStroke stroke) =>
        _bindings.GetValueOrDefault(stroke, TriageAction.None);

    /// <summary>The chord to show in help and hints for a given action.</summary>
    public string Describe(TriageAction action) =>
        _reverse.TryGetValue(action, out var list) && list.Count > 0
            ? list[0].ToString()
            : "";

    public IReadOnlyList<KeyStroke> StrokesFor(TriageAction action) =>
        _reverse.GetValueOrDefault(action) ?? (IReadOnlyList<KeyStroke>)Array.Empty<KeyStroke>();

    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EmailTriage",
            "keybindings.json");

    /// <summary>
    /// Loads defaults, then applies any overrides from disk. A malformed or
    /// missing file degrades to the defaults rather than failing startup.
    /// </summary>
    public static KeyMap Load(string? path = null)
    {
        var map = CreateDefault();
        path ??= DefaultConfigPath;

        if (!File.Exists(path)) return map;

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<KeyBindingFile>(json, JsonOptions);
            if (config?.Bindings is null) return map;

            foreach (var (strokeText, actionText) in config.Bindings)
            {
                var stroke = KeyStroke.Parse(strokeText);
                if (stroke.IsEmpty) continue;

                if (Enum.TryParse<TriageAction>(actionText, ignoreCase: true, out var action))
                    map.Bind(stroke, action);
            }
        }
        catch
        {
            // A broken keymap must never stop the app from opening.
        }

        return map;
    }

    /// <summary>Writes the current defaults out as a starting point for editing.</summary>
    public static void WriteDefaultConfig(string? path = null)
    {
        path ??= DefaultConfigPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(path)) return;

        var file = new KeyBindingFile
        {
            Comment = "Edit to rebind. Format: \"chord\": \"ActionName\". "
                    + "Chords look like \"k\", \"shift+r\", \"ctrl+enter\". "
                    + "Action names come from TriageAction.",
            Bindings = DefaultSpec.ToDictionary(d => d.Stroke, d => d.Action.ToString()),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class KeyBindingFile
    {
        [JsonPropertyName("_comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("bindings")]
        public Dictionary<string, string>? Bindings { get; set; }
    }
}
