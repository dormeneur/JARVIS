using Jarvis.App.Hosting;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Core;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Cursor;
using Jarvis.Core.Dictation;
using Jarvis.Platform.Mac;
using Jarvis.Platform.Mac.Audio;
using Jarvis.Platform.Mac.Capture;
using Jarvis.Platform.Mac.Input;
using Jarvis.Platform.Mac.Notch;
using Jarvis.Platform.Mac.Overlay;
using Jarvis.Platform.Mac.Shell;
using Jarvis.Platform.Mac.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Mac;

/// <summary>
/// The Mac app: what Windows' App does, around the same host. A menu bar item (Pause, Settings,
/// Fill a form, jobs, Quit), the notch's home on hover, the engines warmed up at start, a
/// one-time permissions check, and the JARVIS_DEMO switches for checking it by eye.
/// </summary>
internal sealed partial class MacApp
{
    private readonly IHost _host;
    private readonly ILogger<MacApp> _logger;
    private MacSettings? _settings;
    private StatusMenu? _menu;
    private string? _lastLook;
    private Task _enginesReady = Task.CompletedTask;

    public MacApp(IHost host)
    {
        _host = host;
        _logger = host.Services.GetRequiredService<ILogger<MacApp>>();
        MainThread.Failed += exception => LogUiFailed(exception);
    }

    private IServiceProvider Services => _host.Services;

    /// <summary>On the main thread, once the app loop runs.</summary>
    public void Start()
    {
        var overlay = Services.GetRequiredService<MacOverlay>();
        overlay.ShowInRecordings = Services.GetRequiredService<IOptions<PrivacyOptions>>().Value.ShowInRecordings
                                   || Environment.GetEnvironmentVariable("JARVIS_OVERLAY_CAPTURABLE") == "1";
        _host.Start();

        // SIGTERM (kill, logout) stops the host; the AppKit loop must end with it.
        Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            if (!_quitting)
            {
                MainThread.Quit();
            }
        });

        var pause = Services.GetRequiredService<PauseSwitch>();
        var presenter = Services.GetRequiredService<MacNotchPresenter>();
        presenter.Initialize(pause);
        presenter.Escape = () => Services.GetRequiredService<CompanionSession>().Cancel();
        _settings = new MacSettings(Services, Restart);
        presenter.OpenSettings = ShowSettings;
        _enginesReady = PrewarmEnginesAsync();

        // Nothing else depends on the announcer; resolve it so it starts listening for finished jobs.
        Services.GetRequiredService<AgentAnnouncer>();
        _menu = new StatusMenu(BuildMenu) { Tooltip = "Jarvis" };

        // Resting the mouse on the sleeping notch opens its home (or what was put aside).
        var notch = Services.GetRequiredService<INotchPresenter>();
        overlay.DwellAllowed = () => notch.Current is NotchState.IdleState or NotchState.Working or NotchState.Parked && !pause.IsPaused;
        overlay.NotchDwelled += () => MainThread.Post(() =>
        {
            if (!notch.Restore())
            {
                ShowHome();
            }
        });

        Services.GetRequiredService<ScreenPrivacy>().Looked += look => MainThread.Post(() =>
        {
            _lastLook = look.Displays == 0 ? $"shared text with Claude {look.At.ToLocalTime():HH:mm}" : $"looked at your screen {look.At.ToLocalTime():HH:mm}";
            UpdateMenuBar();
        });

        CheckPermissions();
        if (!File.Exists(AppPaths.SettingsFile) && !Environment.GetCommandLineArgs().Contains("--background"))
        {
            ShowSettings("Permissions"); // first launch: say what Jarvis needs and why
        }

        RunDemoFromEnvironment();
    }

    /// <summary>
    /// Without Input Monitoring the shortcuts are dead, so that one gets a notch message with a
    /// button; the others are asked for when first used (the OS prompts) and listed in Settings.
    /// </summary>
    private void CheckPermissions()
    {
        LogPermissions(MacShortcutHook.Allowed, MacTextInsertion.Allowed, MacScreenCapture.Allowed, !MacMicrophone.BlockedByMacOS());
        if (!MacShortcutHook.Allowed)
        {
            var notch = Services.GetRequiredService<INotchPresenter>();
            _ = _enginesReady.ContinueWith(
                _ => notch.Show(new NotchState.Response(
                    "To hear the talk and dictation shortcuts, Jarvis needs Input Monitoring. Allow it in System Settings, then relaunch Jarvis.",
                    [new NotchAction("Open System Settings", () => Native.OpenUrl(MacShortcutHook.SettingsPage)), new NotchAction("Later", () => notch.Show(NotchState.Idle))],
                    KeepOnClickAway: true)),
                TaskScheduler.Default);
        }
    }

    private async Task PrewarmEnginesAsync()
    {
        try
        {
            await Task.WhenAll(
                Services.GetRequiredService<CompanionSession>().PrewarmAsync(CancellationToken.None),
                Services.GetRequiredService<DictationSession>().PrewarmAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogEnginesFailed(exception);
            var notch = Services.GetRequiredService<INotchPresenter>();
            notch.Show(exception is System.ComponentModel.Win32Exception
                ? new NotchState.Response(
                    "Jarvis answers with Claude Code, and it isn't installed on this Mac yet. Install it, sign in once in Terminal, then relaunch Jarvis.",
                    [new NotchAction("How to install", () => Native.OpenUrl("https://docs.claude.com/en/docs/claude-code/setup")), new NotchAction("Later", () => notch.Show(NotchState.Idle))],
                    KeepOnClickAway: true)
                : new NotchState.Response($"Jarvis couldn't start its voice engines: {exception.Message}"));
        }
    }

    private List<MenuEntry> BuildMenu()
    {
        var cursorVisible = Services.GetRequiredService<IOptionsMonitor<CursorOptions>>().CurrentValue.Visible;
        var paused = Services.GetRequiredService<PauseSwitch>().IsPaused;
        var companion = Services.GetRequiredService<CompanionSession>();
        var menu = new List<MenuEntry>
        {
            new("Ask Jarvis…", companion.Compose),
            new("Fill this form from my vault", () => Task.Run(companion.FillForm)),
            MenuEntry.Separator,
            new(paused ? "Resume Jarvis" : "Pause Jarvis", () => SetPaused(!paused)),
            new(cursorVisible ? "Hide floating cursor" : "Show floating cursor", () => SettingsFile.Set(CursorOptions.SectionName, nameof(CursorOptions.Visible), !cursorVisible)),
            new("Settings…", () => ShowSettings(null)),
        };

        var agents = Services.GetRequiredService<IAgentJobs>();
        var jobs = agents.Jobs.Take(5).ToList();
        if (jobs.Count > 0)
        {
            menu.Add(MenuEntry.Separator);
            menu.Add(new("Background jobs", Enabled: false));
            foreach (var job in jobs)
            {
                menu.Add(job.Status switch
                {
                    AgentJobStatus.Running or AgentJobStatus.Queued => new($"Stop: {job.Title} ({job.Activity ?? "waiting"})", () => agents.Cancel(job.Id), Indent: 1),
                    AgentJobStatus.Finished => new($"Open: {job.Title}", () => Native.OpenUrl(Path.Combine(job.Folder, "output")), Indent: 1),
                    _ => new($"{job.Status}: {job.Title}", () => Native.OpenUrl(job.Folder), Indent: 1),
                });
            }
        }

        if (_lastLook is not null)
        {
            menu.Add(MenuEntry.Separator);
            menu.Add(new($"Jarvis {_lastLook}", Enabled: false));
        }

        menu.Add(MenuEntry.Separator);
        menu.Add(new("Quit Jarvis", Quit));
        return menu;
    }

    private void SetPaused(bool paused)
    {
        var pause = Services.GetRequiredService<PauseSwitch>();
        if (pause.IsPaused == paused)
        {
            return;
        }

        if (paused)
        {
            Services.GetRequiredService<CompanionSession>().Cancel();
            Services.GetRequiredService<DictationSession>().Cancel();
        }

        pause.Set(paused);
        UpdateMenuBar();
    }

    /// <summary>Launched again while running: wake up if paused and show Settings.</summary>
    public void Activated() => MainThread.Post(() =>
    {
        SetPaused(false);
        ShowSettings(null);
    });

    private void UpdateMenuBar()
    {
        if (_menu is null)
        {
            return;
        }

        var paused = Services.GetRequiredService<PauseSwitch>().IsPaused;
        _menu.Dimmed = paused;
        var tooltip = paused ? "Jarvis (paused)" : "Jarvis";
        _menu.Tooltip = _lastLook is null ? tooltip : $"{tooltip} · {_lastLook}";
    }

    private void ShowSettings(string? section) => _settings?.Show(section);

    /// <summary>The notch's home, as on Windows: Ask, Fill a form, Private on/off, Settings, then recent jobs and the last form.</summary>
    private void ShowHome()
    {
        var notch = Services.GetRequiredService<INotchPresenter>();
        var companion = Services.GetRequiredService<CompanionSession>();
        var privacy = Services.GetRequiredService<ScreenPrivacy>();
        var hour = DateTime.Now.Hour;
        var name = Environment.UserName is { Length: > 1 } user && char.IsLetter(user[0]) ? char.ToUpperInvariant(user[0]) + user[1..] : null;
        var greeting = $"{(hour < 12 ? "Morning" : hour < 18 ? "Afternoon" : "Evening")}{(name is null ? string.Empty : $", {name}")}.";
        var talk = MacSettings.MacChord(Services.GetRequiredService<IOptionsMonitor<Jarvis.Core.Input.ShortcutOptions>>().CurrentValue.Talk);
        HomeItem[] actions =
        [
            new("ask", "Ask Jarvis", "Type a question (or double-tap Control)", companion.Compose),
            new("form", "Fill a form", "Fill the form in your browser from your vault", companion.FillForm),
            new("private", privacy.PrivateMode ? "Private: on" : "Private: off", "Keep everything on this Mac", () =>
            {
                var on = !privacy.PrivateMode;
                SettingsFile.Set(PrivacyOptions.SectionName, nameof(PrivacyOptions.PrivateMode), on);
                notch.Show(new NotchState.Response(on ? "Private mode is on: everything stays on this Mac." : "Private mode is off."));
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => notch.Show(NotchState.Idle), TaskScheduler.Default);
            }),
            new("settings", "Settings", null, () => ShowSettings(null)),
        ];

        var agents = Services.GetRequiredService<IAgentJobs>();
        List<HomeItem> Recent()
        {
            var recent = new List<HomeItem>();
            if (Services.GetRequiredService<Jarvis.Core.Vault.FormFiller>().LastTitle is { Length: > 0 } lastForm)
            {
                recent.Add(new("form", lastForm, "Fill again", companion.FillForm));
            }

            foreach (var job in agents.Jobs.Take(4))
            {
                var retry = job.Brief is { } brief ? new NotchAction("Retry", () => agents.Start(brief)) : null;
                recent.Add(job.Status switch
                {
                    AgentJobStatus.Running or AgentJobStatus.Queued => new HomeItem("job", job.Title, job.Activity ?? "Working…", () => notch.Show(NotchState.Idle)),
                    AgentJobStatus.Finished => new HomeItem("job", job.Title, "Ready to view", () => Native.OpenUrl(Path.Combine(job.Folder, "output"))),
                    AgentJobStatus.Cancelled => new HomeItem("job", job.Title, "Stopped before it finished", () => Native.OpenUrl(job.Folder), retry),
                    _ => new HomeItem("job", job.Title, "Didn't finish", () => Native.OpenUrl(job.Folder), retry),
                });
            }

            return recent;
        }

        // The cards follow the jobs while the home is open.
        NotchState.Home? shown = null;
        void Changed(AgentJobInfo _)
        {
            if (shown is not null && ReferenceEquals(notch.Current, shown))
            {
                shown = shown with { Recent = Recent() };
                notch.Show(shown);
            }
            else
            {
                agents.Changed -= Changed;
            }
        }

        shown = new NotchState.Home(greeting, $"Hold {talk} and tell me what to take off your plate.", actions, Recent(), () =>
        {
            agents.Changed -= Changed;
            notch.Show(NotchState.Idle);
        });
        agents.Changed += Changed;
        notch.Show(shown);
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            // The new instance waits for this one to exit before taking the single-instance lock.
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { ArgumentList = { "--background", "--after", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false });
        }

        Quit();
    }

    private volatile bool _quitting;

    /// <summary>Stops the host, then ends the app loop; Program disposes the host (killing claude processes) and exits.</summary>
    public void Quit()
    {
        _quitting = true;
        try
        {
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        finally
        {
            MainThread.Quit();
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled UI exception")]
    private partial void LogUiFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engines failed to start")]
    private partial void LogEnginesFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permissions: input monitoring {Input}, accessibility {Accessibility}, screen recording {Screen}, microphone {Microphone}")]
    private partial void LogPermissions(bool input, bool accessibility, bool screen, bool microphone);
}
