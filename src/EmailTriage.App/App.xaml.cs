using System.IO;
using System.Windows;
using System.Windows.Threading;
using EmailTriage.App.Input;
using EmailTriage.App.Services;
using EmailTriage.App.ViewModels;
using EmailTriage.App.Views;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Data;
using EmailTriage.Core.Services;
using EmailTriage.Outlook;
using Microsoft.Extensions.DependencyInjection;

namespace EmailTriage.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private IMailStore? _store;
    private SnoozeScheduler? _scheduler;
    private ScheduledSender? _sender;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception);

        var settings = AppSettings.Load();

        if (settings.CheckForUpdates)
        {
            // Closing the progress window must not end the app before
            // MainWindow exists.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (await AppUpdater.TryUpdateAsync(e.Args))
            {
                // The updater relaunches the new version once this exits.
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services, settings);
            _services = services.BuildServiceProvider();

            // Give the user something to edit the first time they run it.
            KeyMap.WriteDefaultConfig();

            _services.GetRequiredService<Database>().Migrate();

            _store = _services.GetRequiredService<IMailStore>();
            _scheduler = _services.GetRequiredService<SnoozeScheduler>();
            _sender = _services.GetRequiredService<ScheduledSender>();

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            await window.ViewModel.InitialiseAsync();
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            MessageBox.Show(
                $"Email Triage could not start.\n\n{ex.Message}",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton(new Database(Database.DefaultPath));
        services.AddSingleton(KeyMap.Load());

        services.AddSingleton<IActionItemRepository, ActionItemRepository>();
        services.AddSingleton<ISnoozeRepository, SnoozeRepository>();
        services.AddSingleton<IScheduledSendRepository, ScheduledSendRepository>();
        services.AddSingleton<ScheduledSender>();
        services.AddSingleton<IFolderUsageRepository, FolderUsageRepository>();

        // AI commands run through the Claude Code CLI, so they use the user's
        // own Claude sign-in - this app never holds an API key.
        services.AddSingleton<IAiAssistant>(new ClaudeCodeCli(
            settings.ClaudeCliPath,
            settings.AiModel,
            TimeSpan.FromSeconds(Math.Max(30, settings.AiTimeoutSeconds))));
        services.AddSingleton<AiDraftService>();
        services.AddSingleton<AiSearchService>();

        // The learned writing-style guide lives beside settings.json, as a
        // plain text file the user can open and edit to tune their drafts.
        services.AddSingleton(sp => new WritingStyleService(
            sp.GetRequiredService<IAiAssistant>(),
            sp.GetRequiredService<IMailStore>(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EmailTriage", "writing-style.md")));

        services.AddSingleton<OutlookMailStore>();
        services.AddSingleton<IMailStore>(sp => sp.GetRequiredService<OutlookMailStore>());

        // The same instance: one connection to Outlook, one COM thread.
        services.AddSingleton<ICalendarStore>(sp => sp.GetRequiredService<OutlookMailStore>());
        services.AddSingleton<FolderSearchService>();
        services.AddSingleton(sp => new ContactDirectory(
            sp.GetRequiredService<IMailStore>(),
            ContactDirectory.DefaultCachePath,
            sp.GetRequiredService<IClock>()));

        services.AddSingleton(sp => new SnoozeScheduler(
            sp.GetRequiredService<IMailStore>(),
            sp.GetRequiredService<ISnoozeRepository>(),
            sp.GetRequiredService<IClock>())
        {
            SnoozeFolderPath = settings.SnoozeFolder,
        });

        services.AddSingleton<TriageViewModel>();
        services.AddSingleton<ActionItemsViewModel>();
        services.AddSingleton<CalendarViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Outlook keeps running invisibly if its COM references are not released.
        if (_scheduler is not null) await _scheduler.DisposeAsync();
        if (_sender is not null) await _sender.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();

        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);

        // A single failed Outlook call should not take the whole app down
        // mid-triage; report it and carry on.
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}",
            "Email Triage", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    private static void LogFatal(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmailTriage");
            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never itself throw */ }
    }
}
