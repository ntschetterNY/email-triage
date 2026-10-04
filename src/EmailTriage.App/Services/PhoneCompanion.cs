using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Companion;

namespace EmailTriage.App.Services;

/// <summary>
/// The iPhone companion as Settings shows it: on or off, what it is doing,
/// and the pairing code. Off by default - turning it on is the user's choice
/// to let mail leave this PC for their phone over the local network, or
/// sealed through the relay when one is set up.
/// </summary>
public sealed partial class PhoneCompanion : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly CompanionService _service;
    private CompanionIdentity? _identity;
    private CompanionServer? _server;
    private RelayHost? _relay;
    private DateTimeOffset? _lastRequest;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _status = "Off";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _relayUrl;
    [ObservableProperty] private string _relayKey;

    public PhoneCompanion(AppSettings settings, CompanionService service)
    {
        _settings = settings;
        _service = service;
        _isEnabled = settings.PhoneCompanionEnabled;
        _relayUrl = settings.PhoneRelayUrl;
        _relayKey = settings.PhoneRelayKey;
    }

    /// <summary>The relay, when both boxes hold something usable.</summary>
    public RelaySettings? Relay => RelaySettings.From(RelayUrl, RelayKey);

    public int Port => _settings.PhoneCompanionPort is > 0 and < 65536 ? _settings.PhoneCompanionPort : CompanionServer.DefaultPort;

    private CompanionIdentity Identity => _identity ??= CompanionIdentity.LoadOrCreate();

    /// <summary>Starts the server at launch when it was left on.</summary>
    public Task StartIfEnabledAsync() => IsEnabled ? StartAsync() : Task.CompletedTask;

    partial void OnIsEnabledChanged(bool value)
    {
        _settings.PhoneCompanionEnabled = value;
        try { _settings.Save(); } catch (IOException) { /* still applies for this session */ }
        _ = value ? StartAsync() : StopAsync();
    }

    partial void OnRelayUrlChanged(string value) => RelayChanged();

    partial void OnRelayKeyChanged(string value) => RelayChanged();

    private async void RelayChanged()
    {
        _settings.PhoneRelayUrl = RelayUrl.Trim();
        _settings.PhoneRelayKey = RelayKey.Trim();
        try { _settings.Save(); } catch (IOException) { /* still applies for this session */ }

        await StopRelayAsync();
        StartRelay();
        UpdateStatus();
    }

    private async Task StartAsync()
    {
        if (_server is not null) return;

        try
        {
            var server = new CompanionServer(_service, Identity, Port);
            server.Served += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _lastRequest = DateTimeOffset.Now;
                UpdateStatus();
            });
            await server.StartAsync();
            _server = server;
            IsRunning = true;
            StartRelay();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            // Kestrel says "Failed to bind to address ...: address already in use."
            Status = ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase)
                ?$"Port {Port} is in use by another program. Set PhoneCompanionPort in settings.json to another number."
                : $"Could not start: {ex.Message}";
        }
    }

    /// <summary>Dials out to the relay, alongside the server it forwards to on loopback.</summary>
    private void StartRelay()
    {
        if (_server is null || _relay is not null || Relay is not { } relay) return;

        var channel = RelaySeal.Channel(Identity.RelayKey);
        var host = new RelayHost(() => new RealtimeChannel(relay.ProjectUrl, relay.ApiKey, channel), Identity, Port);
        host.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(UpdateStatus);
        _relay = host;
        host.Start();
    }

    private async Task StopRelayAsync()
    {
        var relay = _relay;
        _relay = null;
        if (relay is not null) await relay.DisposeAsync();
    }

    public async Task StopAsync()
    {
        await StopRelayAsync();
        var server = _server;
        _server = null;
        IsRunning = false;
        if (server is not null) await server.DisposeAsync();
        Status = "Off";
    }

    private void UpdateStatus()
    {
        if (_server is null) { Status = "Off"; return; }

        var hosts = Pairing.LocalAddresses();
        var where = hosts.Count == 0
            ? _relay is null ? "but this PC has no private network address - is it on Wi-Fi or office Ethernet?" : "through the relay only"
            : $"on {hosts[0]}:{Port}";
        var relay = _relay?.State switch
        {
            RelayState.Connected => " · relay connected",
            RelayState.Connecting => " · connecting to the relay",
            RelayState.Retrying => $" · relay unreachable, retrying ({_relay.Problem})",
            _ => Relay is null && (RelayUrl.Length > 0 || RelayKey.Length > 0) ? " · relay needs an https:// URL and a key" : "",
        };
        var seen = _lastRequest is { } at ? $" · device last connected {at:HH:mm}" : "";
        Status = $"On, listening {where}{relay}{seen}";
    }

    /// <summary>The link the phone scans, with this PC's current addresses.</summary>
    public string PairingLink() =>
        Pairing.Link(Pairing.LocalAddresses(), Port, Identity, Environment.MachineName, Relay);

    public BitmapImage PairingQr(string link)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(Pairing.QrPng(link, 10));
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>New token and certificate: every paired phone stops working until it is paired again.</summary>
    public async Task UnpairAllAsync()
    {
        var wasRunning = _server is not null;
        await StopAsync();
        _identity = CompanionIdentity.Create();
        if (wasRunning) await StartAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
