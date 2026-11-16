using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>One line of the proposed moves panel: a conversation, where it would go, and whether it will.</summary>
public sealed partial class MoveProposalViewModel : ObservableObject
{
    [ObservableProperty] private FolderNode _folder;
    [ObservableProperty] private string _reason;
    [ObservableProperty] private bool _isIncluded = true;
    [ObservableProperty] private bool _isCurrent;

    public MoveProposalViewModel(MailRowViewModel row, FolderGuess guess)
    {
        Row = row;
        _folder = guess.Folder;
        _reason = guess.Reason;
    }

    public MailRowViewModel Row { get; }
    public string Subject => Row.Subject;
    public string Sender => Row.Sender;
}

/// <summary>
/// Proposed moves (`m`): the inbox conversations the folder guesser is
/// confident about, twenty at a time, each already ticked. Space or x
/// unticks one, v picks a different folder for it, and Enter files every
/// ticked one in a single step that z undoes. The next twenty follow.
/// </summary>
public sealed partial class TriageViewModel
{
    public ObservableCollection<MoveProposalViewModel> Proposals { get; } = new();

    [ObservableProperty] private bool _isProposing;
    [ObservableProperty] private int _proposalIndex;

    /// <summary>Conversations unticked when a batch was accepted: not proposed again until the panel reopens.</summary>
    private readonly HashSet<string> _proposalsDeclined = new(StringComparer.Ordinal);

    /// <summary>The proposal whose folder the palette is picking (v in the panel).</summary>
    private MoveProposalViewModel? _retargeting;

    public int IncludedProposalCount => Proposals.Count(p => p.IsIncluded);

    public string AcceptProposalsLabel => IncludedProposalCount switch
    {
        0 => "Nothing ticked",
        1 => "Move 1 conversation",
        var n => $"Move {n} conversations",
    };

    public string ProposalsHint =>
        "j/k move · Space tick · v change folder · Ctrl+A tick all/none · Enter move ticked · Esc close";

    /// <summary>`m`: proposes moves for the inbox as it stands.</summary>
    public async Task OpenProposalsAsync()
    {
        if (!_folders.IsIndexed)
        {
            Status = "Reading folder list...";
            await _folders.EnsureIndexedAsync().ConfigureAwait(true);
        }

        _proposalsDeclined.Clear();
        FillProposals();

        if (Proposals.Count == 0)
        {
            Status = "No confident folder guesses for the inbox yet · file some with v and they will start showing up here";
            return;
        }

        Status = "";
        IsProposing = true;
    }

    public void CloseProposals()
    {
        IsProposing = false;
        _retargeting = null;
        SetProposals(Array.Empty<MoveProposalViewModel>());
    }

    /// <summary>The next batch: in list order, leaving out flagged mail, mail just back from a snooze, and what was declined.</summary>
    private void FillProposals()
    {
        var candidates = Rows.Where(r =>
            !r.IsActionRequired && !r.IsBackFromSnooze && !r.IsLeaving && !_proposalsDeclined.Contains(r.Key));

        var picked = MoveProposals.Pick(
            candidates,
            r => _folders.Suggest(r.Summary.Subject, r.Summary.SenderAddress, limit: 1));

        SetProposals(picked.Select(p => new MoveProposalViewModel(p.Item, p.Guess)).ToList());
    }

    private void SetProposals(IReadOnlyList<MoveProposalViewModel> proposals)
    {
        foreach (var p in Proposals) p.PropertyChanged -= OnProposalChanged;
        Proposals.Clear();

        foreach (var p in proposals)
        {
            p.PropertyChanged += OnProposalChanged;
            Proposals.Add(p);
        }

        ProposalIndex = 0;
        MarkCurrentProposal();
        OnProposalCountsChanged();
    }

    private void OnProposalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MoveProposalViewModel.IsIncluded)) OnProposalCountsChanged();
    }

    private void OnProposalCountsChanged()
    {
        OnPropertyChanged(nameof(IncludedProposalCount));
        OnPropertyChanged(nameof(AcceptProposalsLabel));
    }

    partial void OnProposalIndexChanged(int value) => MarkCurrentProposal();

    private MoveProposalViewModel? CurrentProposal =>
        ProposalIndex >= 0 && ProposalIndex < Proposals.Count ? Proposals[ProposalIndex] : null;

    private void MarkCurrentProposal()
    {
        for (var i = 0; i < Proposals.Count; i++) Proposals[i].IsCurrent = i == ProposalIndex;
    }

    public void MoveProposal(int delta)
    {
        if (Proposals.Count == 0) return;
        ProposalIndex = Math.Clamp(ProposalIndex + delta, 0, Proposals.Count - 1);
        MarkCurrentProposal();
    }

    public void SelectProposal(MoveProposalViewModel proposal)
    {
        var index = Proposals.IndexOf(proposal);
        if (index < 0) return;
        ProposalIndex = index;
        MarkCurrentProposal();
    }

    public void ToggleProposal()
    {
        if (CurrentProposal is { } p) p.IsIncluded = !p.IsIncluded;
    }

    /// <summary>Ticks every line, or unticks them all when all are ticked already.</summary>
    public void ToggleAllProposals()
    {
        var tick = Proposals.Any(p => !p.IsIncluded);
        foreach (var p in Proposals) p.IsIncluded = tick;
    }

    /// <summary>`v` in the panel, or a click on a folder: picks another folder for that line.</summary>
    public async Task ChangeProposalFolderAsync(MoveProposalViewModel? proposal = null)
    {
        if ((proposal ?? CurrentProposal) is not { } p) return;
        SelectProposal(p);
        _retargeting = p;

        Palette.Open(
            PaletteMode.Retarget,
            "Move to which folder?",
            "Enter use this folder · Ctrl+Enter create it · Esc keep the suggestion",
            p.Subject);

        await _folders.EnsureIndexedAsync().ConfigureAwait(true);
        RefreshPalette();
    }

    private async Task ConfirmRetargetAsync(bool forceCreate)
    {
        try
        {
            var target = await PaletteFolderAsync(forceCreate).ConfigureAwait(true);
            Palette.Close();

            if (target is null || _retargeting is not { } p) return;
            p.Folder = target;
            p.Reason = "your pick";
            p.IsIncluded = true;
        }
        catch (Exception ex)
        {
            Palette.Close();
            Status = $"Could not create that folder: {ex.Message}";
        }
        finally
        {
            _retargeting = null;
        }
    }

    /// <summary>
    /// Enter: files every ticked line where it says, as one undo step, and
    /// brings up the next batch. The unticked ones stay in the inbox and
    /// are not proposed again while the panel is open.
    /// </summary>
    public Task AcceptProposalsAsync()
    {
        var chosen = Proposals.Where(p => p.IsIncluded).ToList();
        if (chosen.Count == 0)
        {
            Status = "Nothing ticked · Space ticks a line, Esc closes";
            return Task.CompletedTask;
        }

        foreach (var p in Proposals.Where(p => !p.IsIncluded)) _proposalsDeclined.Add(p.Row.Key);

        var routes = chosen.Select(p => (p.Row, p.Folder)).ToList();
        var folders = routes.Select(r => r.Folder.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var label = chosen.Count == 1 ? "1 conversation" : $"{chosen.Count} conversations";
        var where = folders == 1 ? routes[0].Folder.Name : $"{folders} folders";

        var work = MoveInBackground(
            chosen.Select(p => p.Row).ToList(),
            $"Moved {label} to {where}",
            $"move of {label}",
            () => Task.FromResult(routes));

        FillProposals();
        if (Proposals.Count == 0)
        {
            IsProposing = false;
            Status = $"Moved {label} to {where} · no more confident suggestions";
        }
        else
        {
            Status = $"Moved {label} to {where} · {Proposals.Count} more proposed";
        }

        return work;
    }
}
