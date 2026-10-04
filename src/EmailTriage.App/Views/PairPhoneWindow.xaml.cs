using System.Windows;
using System.Windows.Input;
using EmailTriage.App.Services;
using EmailTriage.Companion;

namespace EmailTriage.App.Views;

/// <summary>Shows the pairing code for the iPhone app: a QR code, and the same link as text.</summary>
public partial class PairPhoneWindow : Window
{
    private readonly PhoneCompanion _phone;

    public PairPhoneWindow(PhoneCompanion phone)
    {
        _phone = phone;
        InitializeComponent();
        ShowCode();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void ShowCode()
    {
        var link = _phone.PairingLink();
        LinkBox.Text = link;
        QrImage.Source = _phone.PairingQr(link);

        if (Pairing.LocalAddresses().Count == 0 && _phone.Relay is null)
        {
            Warning.Text = "This PC has no address on a private network, so an iPhone or iPad can't reach it. " +
                           "Connect it to Wi-Fi or office Ethernet and open this again.";
            Warning.Visibility = Visibility.Visible;
        }
    }

    private async void OnUnpairClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Make a new pairing code? Every iPhone and iPad paired now stops working until you pair it again.",
            "Unpair all devices", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        await _phone.UnpairAllAsync();
        ShowCode();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();
}
