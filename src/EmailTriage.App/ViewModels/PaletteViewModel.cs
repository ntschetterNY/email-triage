using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EmailTriage.App.ViewModels;

/// <summary>One selectable row in a palette.</summary>
public sealed record PaletteEntry(
    string Primary,
    string Secondary,
    object Payload,
    int[] Highlights)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(Secondary);
}

public enum PaletteMode { Folder, Snooze, Attachment }

/// <summary>
/// The type-and-pick overlay shared by the move (`k`) and snooze (`g`)
/// commands. Holds only presentation state; the owning view model supplies
/// results and decides what confirming an entry means.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private PaletteMode _mode;
    [ObservableProperty] private string _contextLine = "";

    /// <summary>
    /// Shown when nothing matches, offering to create the typed folder. Null
    /// when creation does not apply.
    /// </summary>
    [ObservableProperty] private string? _createPrompt;

    public ObservableCollection<PaletteEntry> Entries { get; } = new();

    public event EventHandler? QueryChanged;

    public PaletteEntry? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Entries.Count ? Entries[SelectedIndex] : null;

    public bool HasEntries => Entries.Count > 0;

    partial void OnQueryChanged(string value) => QueryChanged?.Invoke(this, EventArgs.Empty);

    public void Open(PaletteMode mode, string title, string hint, string contextLine)
    {
        Mode = mode;
        Title = title;
        Hint = hint;
        ContextLine = contextLine;
        Query = "";
        SelectedIndex = 0;
        CreatePrompt = null;
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Query = "";
        Entries.Clear();
        CreatePrompt = null;
    }

    public void SetEntries(IEnumerable<PaletteEntry> entries)
    {
        Entries.Clear();
        foreach (var e in entries) Entries.Add(e);

        SelectedIndex = Entries.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(Selected));
    }

    public void MoveSelection(int delta)
    {
        if (Entries.Count == 0) { SelectedIndex = -1; return; }

        // Wrap, so holding the key cycles rather than sticking at an end.
        var next = (SelectedIndex + delta) % Entries.Count;
        if (next < 0) next += Entries.Count;

        SelectedIndex = next;
        OnPropertyChanged(nameof(Selected));
    }

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(Selected));
}
