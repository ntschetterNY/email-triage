using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>One folder the organizer found, ticked to be moved.</summary>
public sealed partial class FolderPlanRow : ObservableObject
{
    public required FolderMovePlan Plan { get; init; }

    [ObservableProperty] private bool _isChecked = true;

    /// <summary>"" while waiting, then "Done" or why it failed.</summary>
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private bool _failed;

    public string From => Plan.From;
    public string To => Plan.To.Replace("\\", " › ");
    public bool Merges => Plan.MergesIntoExisting;
}

/// <summary>
/// The Settings page: how folders are named, how those names nest, and
/// reorganizing the folders already in Outlook to match. Edits apply to the
/// shared <see cref="AppSettings"/> only on Save.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IMailStore _store;
    private readonly FolderSearchService _folders;

    [ObservableProperty] private string _namePattern;
    [ObservableProperty] private string _layout;
    [ObservableProperty] private string _home;
    [ObservableProperty] private bool _nestNewFolders;

    /// <summary>A name to try the scheme on, shown nested underneath.</summary>
    [ObservableProperty] private string _example = "Elara - Field Reports - Rimkus";

    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _schemeError = "";
    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    /// <summary>True once Find has run, so an empty list reads as "nothing to do", not "not looked".</summary>
    [ObservableProperty] private bool _hasSearched;

    public ObservableCollection<FolderPlanRow> Plan { get; } = new();

    public SettingsViewModel(AppSettings settings, IMailStore store, FolderSearchService folders)
    {
        _settings = settings;
        _store = store;
        _folders = folders;

        _namePattern = settings.FolderNamePattern;
        _layout = settings.FolderLayout;
        _home = settings.FolderHome;
        _nestNewFolders = settings.NestNewFolders;

        UpdatePreview();
    }

    public FolderScheme Scheme => new(NamePattern, Layout);

    public int CheckedCount => Plan.Count(p => p.IsChecked && p.Outcome.Length == 0);

    public bool HasPlan => Plan.Count > 0;

    public string OrganizeLabel => CheckedCount switch
    {
        0 => "Organize",
        1 => "Organize 1 folder",
        var n => $"Organize {n} folders",
    };

    partial void OnNamePatternChanged(string value) => Changed();
    partial void OnLayoutChanged(string value) => Changed();
    partial void OnHomeChanged(string value) => Changed();
    partial void OnNestNewFoldersChanged(bool value) => IsDirty = true;
    partial void OnExampleChanged(string value) => UpdatePreview();

    private void Changed()
    {
        IsDirty = true;
        UpdatePreview();

        // What was found no longer matches what is typed.
        ClearPlan();
    }

    private void UpdatePreview()
    {
        var scheme = Scheme;
        SchemeError = scheme.Error ?? "";
        if (!scheme.IsValid) { Preview = ""; return; }

        var levels = FolderOrganizer.NewFolderLevels(Example, scheme, Home);
        Preview = levels is null
            ? "Not in this naming scheme - it would stay as it is."
            : string.Join("  ›  ", levels);
    }

    /// <summary>Keeps the edits; false with the reason on the status line when they cannot be used.</summary>
    public bool Save()
    {
        if (Scheme.Error is { } error)
        {
            Status = error;
            return false;
        }

        _settings.FolderNamePattern = NamePattern.Trim();
        _settings.FolderLayout = FolderOrganizer.NormalisePath(Layout);
        _settings.FolderHome = FolderOrganizer.NormalisePath(Home);
        _settings.NestNewFolders = NestNewFolders;

        try
        {
            _settings.Save();
            IsDirty = false;
            Status = "Saved.";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Could not save settings: {ex.Message}";
            return false;
        }
    }

    public void ResetToDefaults()
    {
        NamePattern = FolderScheme.DefaultNamePattern;
        Layout = FolderScheme.DefaultLayout;
        Home = "";
        NestNewFolders = true;
    }

    /// <summary>Reads every folder afresh and lists the ones not yet nested by the scheme.</summary>
    public async Task FindAsync()
    {
        if (IsBusy) return;
        if (Scheme.Error is { } error) { Status = error; return; }

        IsBusy = true;
        ClearPlan();
        Status = "Reading your folders...";
        try
        {
            await _folders.EnsureIndexedAsync(force: true).ConfigureAwait(true);
            var index = await _store.GetFolderIndexAsync().ConfigureAwait(true);

            foreach (var plan in FolderOrganizer.Plan(index, Scheme, Home))
            {
                var row = new FolderPlanRow { Plan = plan };
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderPlanRow.IsChecked)) CountsChanged();
                };
                Plan.Add(row);
            }

            HasSearched = true;
            Status = Plan.Count == 0
                ? $"All {index.Count} folders already follow the scheme - nothing to move."
                : $"{Plan.Count} folder{(Plan.Count == 1 ? "" : "s")} to nest. Untick any to leave alone.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read your folders: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            PlanChanged();
        }
    }

    /// <summary>Moves each ticked folder in turn, recording how each went.</summary>
    public async Task OrganizeAsync()
    {
        if (IsBusy) return;

        var rows = Plan.Where(p => p.IsChecked && p.Outcome.Length == 0).ToList();
        if (rows.Count == 0) { Status = "Tick at least one folder to organize."; return; }

        IsBusy = true;
        int done = 0, failed = 0;
        try
        {
            foreach (var row in rows)
            {
                Status = $"Organizing {done + failed + 1} of {rows.Count}: {row.From}";
                try
                {
                    await _store.MoveFolderAsync(row.Plan.Folder.Ref, row.Plan.To).ConfigureAwait(true);
                    row.Outcome = row.Merges ? "Merged" : "Moved";
                    done++;
                }
                catch (Exception ex)
                {
                    row.Outcome = ex.Message;
                    row.Failed = true;
                    failed++;
                }
            }

            // The move palette searches the new tree from here on.
            try { await _folders.EnsureIndexedAsync(force: true).ConfigureAwait(true); }
            catch { /* the palette rereads on its own later */ }

            Status = failed == 0
                ? $"Organized {done} folder{(done == 1 ? "" : "s")}."
                : $"Organized {done}; {failed} could not be moved - see each one for why.";
        }
        finally
        {
            IsBusy = false;
            CountsChanged();
        }
    }

    public void SetAllChecked(bool on)
    {
        foreach (var row in Plan.Where(p => p.Outcome.Length == 0)) row.IsChecked = on;
    }

    private void ClearPlan()
    {
        if (Plan.Count == 0 && !HasSearched) return;
        Plan.Clear();
        HasSearched = false;
        PlanChanged();
    }

    private void PlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        CountsChanged();
    }

    private void CountsChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(OrganizeLabel));
    }
}
