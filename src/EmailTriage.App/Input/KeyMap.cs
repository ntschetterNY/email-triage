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
    /// The shipped bindings, following Superhuman's layout wherever the app has
    /// the same command: j/k to move, e done, h remind me, v move, r reply,
    /// Enter reply all (handled as Confirm in the list), z undo. Commands
    /// Superhuman has no equivalent for keep their own letters.
    /// </summary>
    public static readonly (string Stroke, TriageAction Action)[] DefaultSpec =
    {
        // Movement
        ("j",           TriageAction.NextMail),
        ("down",        TriageAction.NextMail),
        ("k",           TriageAction.PrevMail),
        ("up",          TriageAction.PrevMail),
        ("home",        TriageAction.FirstMail),
        ("ctrl+up",     TriageAction.FirstMail),
        ("end",         TriageAction.LastMail),
        ("ctrl+down",   TriageAction.LastMail),
        ("pagedown",    TriageAction.PageDown),
        ("pageup",      TriageAction.PageUp),

        // Triage decisions
        ("a",           TriageAction.MarkActionRequired),
        ("n",           TriageAction.MarkNoAction),
        ("v",           TriageAction.MoveToFolder),
        ("h",           TriageAction.Snooze),
        ("e",           TriageAction.Archive),
        ("u",           TriageAction.ToggleRead),
        ("shift+3",     TriageAction.Delete),
        ("delete",      TriageAction.Delete),

        // Replying. Reply all is Enter, which is Confirm everywhere else.
        ("r",           TriageAction.ReplySender),
        ("f",           TriageAction.Forward),
        ("ctrl+o",      TriageAction.OpenAttachment),

        // Action list
        ("t",           TriageAction.AddNote),
        ("b",           TriageAction.AddBlocker),
        ("shift+a",     TriageAction.AddAssignment),
        ("x",           TriageAction.ToggleComplete),
        ("shift+p",     TriageAction.CyclePriority),
        ("o",           TriageAction.OpenInOutlook),

        // Action board: arrows between columns, [ ] or Shift+arrows move the card
        ("left",        TriageAction.PrevColumn),
        ("right",       TriageAction.NextColumn),
        ("oem4",        TriageAction.StageBack),
        ("shift+left",  TriageAction.StageBack),
        ("oem6",        TriageAction.StageForward),
        ("shift+right", TriageAction.StageForward),
        ("d",           TriageAction.SetDue),
        ("w",           TriageAction.ClearWait),
        ("c",           TriageAction.Chase),

        // Shell
        ("tab",         TriageAction.SwitchSection),
        ("/",           TriageAction.Search),
        ("f5",          TriageAction.Refresh),
        ("shift+oem2",  TriageAction.ShowHelp),
        ("z",           TriageAction.Undo),
        ("ctrl+z",      TriageAction.Undo),
        ("escape",      TriageAction.Cancel),
        ("enter",       TriageAction.Confirm),
    };

    /// <summary>
    /// The layout shipped before the Superhuman one. Config files from then
    /// hold a full copy of it, which would otherwise pin the old keys.
    /// </summary>
    private static readonly (string Stroke, TriageAction Action)[] LegacySpec =
    {
        ("j", TriageAction.NextMail), ("down", TriageAction.NextMail),
        ("p", TriageAction.PrevMail), ("up", TriageAction.PrevMail),
        ("home", TriageAction.FirstMail), ("end", TriageAction.LastMail),
        ("pagedown", TriageAction.PageDown), ("pageup", TriageAction.PageUp),
        ("a", TriageAction.MarkActionRequired), ("n", TriageAction.MarkNoAction),
        ("k", TriageAction.MoveToFolder), ("g", TriageAction.Snooze),
        ("e", TriageAction.Archive), ("u", TriageAction.ToggleRead),
        ("delete", TriageAction.Delete),
        ("r", TriageAction.ReplyAll), ("shift+r", TriageAction.ReplySender),
        ("f", TriageAction.Forward), ("v", TriageAction.OpenAttachment),
        ("t", TriageAction.AddNote), ("b", TriageAction.AddBlocker),
        ("shift+a", TriageAction.AddAssignment), ("x", TriageAction.ToggleComplete),
        ("shift+p", TriageAction.CyclePriority), ("o", TriageAction.OpenInOutlook),
        ("left", TriageAction.PrevColumn), ("h", TriageAction.PrevColumn),
        ("right", TriageAction.NextColumn), ("l", TriageAction.NextColumn),
        ("oem4", TriageAction.StageBack), ("shift+left", TriageAction.StageBack),
        ("oem6", TriageAction.StageForward), ("shift+right", TriageAction.StageForward),
        ("d", TriageAction.SetDue), ("w", TriageAction.ClearWait), ("c", TriageAction.Chase),
        ("tab", TriageAction.SwitchSection), ("/", TriageAction.Search),
        ("f5", TriageAction.Refresh), ("shift+oem2", TriageAction.ShowHelp),
        ("z", TriageAction.Undo), ("ctrl+z", TriageAction.Undo),
        ("escape", TriageAction.Cancel), ("enter", TriageAction.Confirm),
    };

    /// <summary>Bumped when the defaults change in a way old config files would mask.</summary>
    private const int ConfigVersion = 2;

    private static IEnumerable<(KeyStroke, TriageAction)> Defaults =>
        DefaultSpec
            .Select(d => (KeyStroke.Parse(d.Stroke), d.Action))
            .Where(d => !d.Item1.IsEmpty);

    public void Bind(KeyStroke stroke, TriageAction action)
    {
        if (stroke.IsEmpty || action == TriageAction.None) return;

        // A stroke does one thing: rebinding it takes it off the old action,
        // so help and hints stop showing it there.
        if (_bindings.TryGetValue(stroke, out var previous) && _reverse.TryGetValue(previous, out var old))
            old.Remove(stroke);

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

            var bindings = config.Bindings;
            if (config.Version < ConfigVersion)
            {
                bindings = UserChanges(bindings);
                Upgrade(path, bindings);
            }

            foreach (var (strokeText, actionText) in bindings)
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

    /// <summary>
    /// The entries of an old file the user actually chose: anything that is
    /// not simply a copy of the old defaults.
    /// </summary>
    private static Dictionary<string, string> UserChanges(Dictionary<string, string> bindings) =>
        bindings
            .Where(b =>
            {
                var stroke = KeyStroke.Parse(b.Key);
                return !LegacySpec.Any(l =>
                    KeyStroke.Parse(l.Stroke).Equals(stroke) &&
                    string.Equals(l.Action.ToString(), b.Value, StringComparison.OrdinalIgnoreCase));
            })
            .ToDictionary(b => b.Key, b => b.Value);

    /// <summary>
    /// Rewrites an old file as the current defaults plus the user's own
    /// changes, keeping the original beside it.
    /// </summary>
    private static void Upgrade(string path, Dictionary<string, string> userChanges)
    {
        try
        {
            File.Copy(path, Path.ChangeExtension(path, ".v1.json"), overwrite: true);

            var bindings = DefaultSpec.ToDictionary(d => d.Stroke, d => d.Action.ToString());
            foreach (var (stroke, action) in userChanges) bindings[stroke] = action;

            WriteConfig(path, bindings);
        }
        catch
        {
            // The upgrade is applied in memory either way; it is retried next start.
        }
    }

    /// <summary>Writes the current defaults out as a starting point for editing.</summary>
    public static void WriteDefaultConfig(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (File.Exists(path)) return;

        WriteConfig(path, DefaultSpec.ToDictionary(d => d.Stroke, d => d.Action.ToString()));
    }

    private static void WriteConfig(string path, Dictionary<string, string> bindings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var file = new KeyBindingFile
        {
            Comment = "Edit to rebind. Format: \"chord\": \"ActionName\". "
                    + "Chords look like \"k\", \"shift+r\", \"ctrl+enter\". "
                    + "Action names come from TriageAction.",
            Version = ConfigVersion,
            Bindings = bindings,
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

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("bindings")]
        public Dictionary<string, string>? Bindings { get; set; }
    }
}
