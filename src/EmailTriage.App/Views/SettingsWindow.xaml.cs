using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EmailTriage.App.Input;
using EmailTriage.App.Services;
using EmailTriage.App.ViewModels;

namespace EmailTriage.App.Views;

/// <summary>The Settings page, opened over the main window with Ctrl+, or the Settings button.</summary>
public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    private readonly PhoneCompanion _phone;

    public SettingsWindow(SettingsViewModel viewModel, LavishViewModel lavish, KeyMap keys, PhoneCompanion phone)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        _phone = phone;
        InitializeComponent();

        // The phone switch applies at once, like Outlook's own toggles, rather than on Save.
        PhoneSection.DataContext = phone;

        LavishLayer.Attach(lavish, keys, WindowRoot, LavishButton, area: () => "Settings", panel: false);

        PreviewKeyDown += (_, e) =>
        {
            // Lavish first, so Esc leaves comment mode rather than closing Settings.
            if (LavishLayer.HandleKey(e)) return;
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void OnLavishButtonClick(object sender, RoutedEventArgs e) => LavishLayer.Toggle();

    private void OnSaveClick(object sender, RoutedEventArgs e) => ViewModel.Save();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPairPhoneClick(object sender, RoutedEventArgs e) =>
        new PairPhoneWindow(_phone) { Owner = this }.ShowDialog();

    private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.ResetToDefaults();

    private async void OnFindClick(object sender, RoutedEventArgs e) => await ViewModel.FindAsync();

    private async void OnOrganizeClick(object sender, RoutedEventArgs e)
    {
        // Organizing works from the scheme on screen; keep it, so new folders
        // made from the move palette nest the same way.
        if (ViewModel.IsDirty && !ViewModel.Save()) return;

        var count = ViewModel.CheckedCount;
        if (count == 0) { await ViewModel.OrganizeAsync(); return; }

        var answer = MessageBox.Show(this,
            $"Move {count} folder{(count == 1 ? "" : "s")} in Outlook into the nested structure?\n\n" +
            "Their mail and subfolders go with them. Where a folder is already in place, the contents are " +
            "merged into it and the emptied original goes to Deleted Items.",
            "Organize folders", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.OK) await ViewModel.OrganizeAsync();
    }

    private void OnTickAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAllChecked(true);

    private void OnUntickAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAllChecked(false);

    protected override void OnClosing(CancelEventArgs e)
    {
        // Moving folders is under way in Outlook; leaving now would hide how it went.
        if (ViewModel.IsBusy)
        {
            e.Cancel = true;
            return;
        }

        if (ViewModel.IsDirty)
        {
            var answer = MessageBox.Show(this, "Save your changes to settings?", "Settings",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !ViewModel.Save()))
                e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
