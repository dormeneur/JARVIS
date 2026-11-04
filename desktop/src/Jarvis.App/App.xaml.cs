using Jarvis.App.Hosting;
using Jarvis.App.Notch;
using Jarvis.App.Settings;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Core;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Cursor;
using Jarvis.Core.Dictation;
using Jarvis.Platform.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Jarvis.App;

public sealed partial class App : Application
{
    private readonly IHost _host;
    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private string? _lastLook;
    private Task _enginesReady = Task.CompletedTask;
#if DEBUG
    private bool _debugThinking;
#endif

    public App(AppInstance instance)
    {
        InitializeComponent();
        _host = AppHost.Build();
        WindowChrome.ShowInRecordings = _host.Services.GetRequiredService<IOptions<PrivacyOptions>>().Value.ShowInRecordings;
        UnhandledException += (_, e) => _host.Services.GetRequiredService<ILogger<App>>().LogCritical(e.Exception, "Unhandled UI exception");
        // Launched again (Start menu, exe) while running: wake up if paused and show Settings.
        instance.Activated += (_, _) => _host.Services.GetRequiredService<DispatcherQueue>().TryEnqueue(() =>
        {
            SetPaused(false);
            ShowSettings();
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host.Start();
        _host.Services.GetRequiredService<NotchPresenter>().Initialize(_host.Services.GetRequiredService<PauseSwitch>());
        _enginesReady = PrewarmEnginesAsync();

        // Nothing else depends on the announcer; resolve it so it starts listening for finished jobs.
        _host.Services.GetRequiredService<AgentAnnouncer>();
        _trayIcon = new TrayIcon("Jarvis", TrayIconFile(paused: false), BuildTrayMenu);

        _host.Services.GetRequiredService<Notch.NotchPresenter>().Escape = () => _host.Services.GetRequiredService<CompanionSession>().Cancel();

        // Resting the mouse on the sleeping notch opens its home.
        var overlay = _host.Services.GetRequiredService<Jarvis.Platform.Overlay.OverlayService>();
        var notch = _host.Services.GetRequiredService<INotchPresenter>();
        var pause = _host.Services.GetRequiredService<PauseSwitch>();
        overlay.DwellAllowed = () => notch.Current is NotchState.IdleState or NotchState.Working && !pause.IsPaused;
        overlay.NotchDwelled += () => _host.Services.GetRequiredService<DispatcherQueue>().TryEnqueue(ShowHome);

        // Hovering the tray icon always tells you when Jarvis last looked at your screen.
        _host.Services.GetRequiredService<ScreenPrivacy>().Looked += look => _host.Services.GetRequiredService<DispatcherQueue>().TryEnqueue(() =>
        {
            _lastLook = look.Displays == 0 ? $"shared text with Claude {look.At.ToLocalTime():HH:mm}" : $"looked at your screen {look.At.ToLocalTime():HH:mm}";
            UpdateTray();
        });
        if (Environment.ProcessPath is { } exe)
        {
            try
            {
                StartMenuShortcut.Ensure("Jarvis", exe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                _host.Services.GetRequiredService<ILogger<App>>().LogWarning(exception, "Could not add Jarvis to the Start menu");
            }
        }

        Settings.ViewModels.GeneralViewModel.RefreshLoginCommand();
        if (!Environment.GetCommandLineArgs().Contains(Settings.ViewModels.GeneralViewModel.BackgroundArgument))
        {
            ShowSettings();
        }

#if DEBUG
        RunDemoFromEnvironment();
#endif
    }

#if DEBUG
    /// <summary>JARVIS_DEMO=thinking|point drives the overlay at startup so visuals can be checked without the tray.</summary>
    private void RunDemoFromEnvironment()
    {
        var overlay = _host.Services.GetRequiredService<ICursorOverlay>();
        var screen = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        switch (Environment.GetEnvironmentVariable("JARVIS_DEMO"))
        {
            case "thinking":
                overlay.SetThinking(true);
                break;
            case "listening":
                // A fake voice: the level swings like speech, so the meter can be seen without talking.
                overlay.SetListening(true);
                _ = Task.Run(async () =>
                {
                    for (var t = 0.0; ; t += 0.05)
                    {
                        overlay.ShowLevel(Math.Abs(Math.Sin(t * 3)) * (0.5 + (0.5 * Math.Sin(t * 0.7))));
                        await Task.Delay(50);
                    }
                });
                break;
            case "point":
                overlay.PointAt(screen.X + (screen.Width / 4), screen.Y + (screen.Height / 4));
                break;
            case "notch":
                _ = CycleNotchDemoAsync(_host.Services.GetRequiredService<INotchPresenter>());
                break;
            case { } demo when demo.StartsWith("settings:", StringComparison.Ordinal):
                ShowSettings(demo["settings:".Length..]);
                break;
            case { } demo when demo.StartsWith("ask:", StringComparison.Ordinal):
                _ = AskWhenReadyAsync(demo["ask:".Length..]);
                break;
            case { } demo when demo.StartsWith("dictate:", StringComparison.Ordinal):
                _ = DictateWhenReadyAsync(demo["dictate:".Length..]);
                break;
            case "draw":
                DrawDemoScene();
                break;
            case { } demo when demo.StartsWith("agent:", StringComparison.Ordinal):
                _host.Services.GetRequiredService<IAgentJobs>().Start(demo["agent:".Length..]);
                break;
            case { } demo when demo.StartsWith("connect:", StringComparison.Ordinal):
                _ = ConnectVaultAsync(demo["connect:".Length..]);
                break;
            case "pause":
                SetPaused(true);
                break;
            case { } demo when demo.StartsWith("chat:", StringComparison.Ordinal):
                _ = ChatWhenReadyAsync(demo["chat:".Length..]);
                break;
            case "compose":
                _ = ComposeWhenReadyAsync();
                break;
            case "selftest":
                _ = SpeechSelfTestAsync();
                break;
            case { } demo when demo.StartsWith("hear:", StringComparison.Ordinal):
                _ = HearTestAsync(demo["hear:".Length..].Split('|'));
                break;
            case { } demo when demo.StartsWith("notch:", StringComparison.Ordinal):
                var pinned = DemoNotchStates.FirstOrDefault(entry => entry.Label == demo["notch:".Length..]);
                if (pinned.State is not null)
                {
                    // After warm-up, which ends by setting the notch back to idle.
                    _ = _enginesReady.ContinueWith(_ => _host.Services.GetRequiredService<INotchPresenter>().Show(pinned.State), TaskScheduler.Default);
                }

                break;
        }
    }

    /// <summary>Registers with the vault using an invite code, as Settings → Vault → Connect does.</summary>
    private async Task ConnectVaultAsync(string inviteCode)
    {
        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        try
        {
            await _host.Services.GetRequiredService<Jarvis.Contracts.Vault.IVault>().ConnectAsync(inviteCode, CancellationToken.None);
            logger.LogInformation("Demo: connected to the vault");
        }
        catch (Jarvis.Contracts.Vault.VaultException exception)
        {
            logger.LogWarning("Demo: vault connect failed: {Reason}", exception.Message);
        }
    }

    /// <summary>Asks a question, waits for the answer, then opens the text box with the chat above it.</summary>
    private async Task ChatWhenReadyAsync(string question)
    {
        await _enginesReady;
        var history = _host.Services.GetRequiredService<ConversationLog>();
        _host.Services.GetRequiredService<CompanionSession>().Ask(question);
        for (var i = 0; i < 120 && history.Recent.Count == 0; i++)
        {
            await Task.Delay(500);
        }

        await Task.Delay(TimeSpan.FromSeconds(4));
        _host.Services.GetRequiredService<CompanionSession>().Compose();
    }

    /// <summary>Opens the text box 6 s after the engines are ready, as double-tapping Ctrl would (time to focus another app).</summary>
    private async Task ComposeWhenReadyAsync()
    {
        await _enginesReady;
        await Task.Delay(TimeSpan.FromSeconds(6));
        _host.Services.GetRequiredService<CompanionSession>().Compose();
    }

    /// <summary>Types a transcript into whatever is focused 6 s after the engines are ready (time to click a field).</summary>
    private async Task DictateWhenReadyAsync(string transcript)
    {
        await _enginesReady;
        await Task.Delay(TimeSpan.FromSeconds(6));
        var outcome = await _host.Services.GetRequiredService<DictationSession>().InsertTranscriptAsync(transcript, CancellationToken.None);
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation("Demo dictation outcome: {Outcome}", outcome);
    }

    /// <summary>Sketches every kind of mark on the primary display, so the drawing look can be checked by eye.</summary>
    private void DrawDemoScene()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        var scale = (float)WindowChrome.DisplayScaleAt(area.X + (area.Width / 2), area.Y + (area.Height / 2));
        // Pretend the screenshot was taken at full resolution, so script pixels are desktop pixels.
        var screen = new Jarvis.Contracts.Capture.CapturedScreen(new(ReadOnlyMemory<byte>.Empty, area.Width, area.Height, true), area.X, area.Y, area.Width, area.Height, scale);
        var script = Jarvis.Core.Drawing.DrawingScript.Parse(
            $"""
            ring {area.Width * 0.12} {area.Height * 0.25} {area.Width * 0.2} {area.Height * 0.08} "Search"
            box {area.Width * 0.45} {area.Height * 0.3} {area.Width * 0.25} {area.Height * 0.25} "Results"
            arrow {area.Width * 0.85} {area.Height * 0.75} {area.Width * 0.72} {area.Height * 0.57} "Click here"
            num {area.Width * 0.1} {area.Height * 0.6} 1
            num {area.Width * 0.18} {area.Height * 0.6} 2
            note {area.Width * 0.25} {area.Height * 0.75} "Your files"
            figure {area.Width * 0.82} {area.Height * 0.28} {area.Width * 0.14} "Water"
            ball 0 0.15 0 0.42 red "Oxygen"
            ball -0.62 -0.38 0 0.26 white "Hydrogen"
            ball 0.62 -0.38 0 0.26 white
            rod 0 0.15 0 -0.62 -0.38 0 0.07 grey
            rod 0 0.15 0 0.62 -0.38 0 0.07 grey
            figure {area.Width * 0.6} {area.Height * 0.75} {area.Width * 0.1}
            block 0 -0.3 0 1.4 0.5 1.4 blue "Base"
            cylinder 0 0.25 0 0.35 0.6 green
            cone 0 0.8 0 0.45 0.5 orange "Roof"
            """,
            screen: 1);
        var ink = Jarvis.Core.Cursor.CursorColor.Parse(_host.Services.GetRequiredService<IOptionsMonitor<CursorOptions>>().CurrentValue.Color);
        _host.Services.GetRequiredService<IScreenDrawing>().Show(Jarvis.Core.Drawing.SceneBuilder.Build(script, screen, ink, scale, seed: 42));
    }

    private async Task AskWhenReadyAsync(string request)
    {
        await _enginesReady;
        _host.Services.GetRequiredService<CompanionSession>().Ask(request);
    }

    /// <summary>Kokoro speaks each request, Whisper hears it back, and the log says where the router would send it.</summary>
    private async Task HearTestAsync(IEnumerable<string> requests)
    {
        await _enginesReady;
        var tts = _host.Services.GetRequiredService<Jarvis.Contracts.Speech.ITextToSpeech>();
        var stt = _host.Services.GetRequiredService<Jarvis.Contracts.Speech.ISpeechToText>();
        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        foreach (var request in requests)
        {
            var spoken = new List<byte>();
            await foreach (var chunk in tts.SynthesizeAsync(request, new("af_heart"), CancellationToken.None))
            {
                spoken.AddRange(chunk.ToArray());
            }

            await using var session = await stt.StartSessionAsync(new(null, []), CancellationToken.None);
            await session.WriteAsync(Resample(spoken.ToArray(), tts.OutputFormat.SampleRate, stt.InputFormat.SampleRate), CancellationToken.None);
            var heard = await session.CompleteAsync(CancellationToken.None);
            logger.LogInformation("Hear test: said \"{Said}\", heard \"{Heard}\", routed to {Route}", request, heard, Jarvis.Core.Vault.TurnRouter.Route(heard, privateMode: false, browserInFront: true));
        }
    }

    /// <summary>Kokoro speaks a known sentence, Whisper transcribes it back: proves both engines and the audio maths.</summary>
    private async Task SpeechSelfTestAsync()
    {
        await _enginesReady;
        const string Sentence = "Testing one two three. Can you hear me clearly?";
        var tts = _host.Services.GetRequiredService<Jarvis.Contracts.Speech.ITextToSpeech>();
        var stt = _host.Services.GetRequiredService<Jarvis.Contracts.Speech.ISpeechToText>();
        var spoken = new List<byte>();
        await foreach (var chunk in tts.SynthesizeAsync(Sentence, new("af_heart"), CancellationToken.None))
        {
            spoken.AddRange(chunk.ToArray());
        }

        await using var session = await stt.StartSessionAsync(new(null, []), CancellationToken.None);
        await session.WriteAsync(Resample(spoken.ToArray(), tts.OutputFormat.SampleRate, stt.InputFormat.SampleRate), CancellationToken.None);
        var heard = await session.CompleteAsync(CancellationToken.None);
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation("Self-test: said \"{Said}\", heard \"{Heard}\"", Sentence, heard);
        _host.Services.GetRequiredService<INotchPresenter>().Show(new NotchState.Response($"Self-test heard: {heard}"));

        // Microphone: 1.5 s from the real device, at the speech engine's format.
        var recorded = new List<byte>();
        await using (var microphone = _host.Services.GetRequiredService<Jarvis.Contracts.Audio.IMicrophone>().Start(stt.InputFormat))
        {
            using var window = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            try
            {
                await foreach (var chunk in microphone.Audio.ReadAllAsync(window.Token))
                {
                    recorded.AddRange(chunk.ToArray());
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(recorded.ToArray());
        var rms = samples.Length == 0 ? 0 : Math.Sqrt(samples.ToArray().Average(sample => (double)sample * sample)) / short.MaxValue;
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
            "Self-test microphone: {Seconds:F2} s of audio at {Rate} Hz, RMS level {Rms:F4}", samples.Length / (double)stt.InputFormat.SampleRate, stt.InputFormat.SampleRate, rms);
    }

    /// <summary>Linear resampling of mono PCM16, for the speech tests.</summary>
    private static byte[] Resample(byte[] pcm16, int from, int to)
    {
        var input = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm16);
        var output = new short[(int)((long)input.Length * to / from)];
        for (var i = 0; i < output.Length; i++)
        {
            var position = (double)i * from / to;
            var left = (int)position;
            var right = Math.Min(left + 1, input.Length - 1);
            output[i] = (short)(input[left] + ((input[right] - input[left]) * (position - left)));
        }

        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
    }

    private static readonly (string Label, NotchState State)[] DemoNotchStates =
    [
        ("idle", NotchState.Idle),
        ("listening", new NotchState.Listening()),
        ("thinking", new NotchState.Thinking()),
        ("speaking", new NotchState.Speaking()),
        ("response", new NotchState.Response("(Sample reply, for checking the notch's look.) Replies wrap onto more lines as they grow.\n\nA blank line starts a new paragraph.")),
        ("working", new NotchState.Working()),
        ("result", new NotchState.Response("Lisbon trip plan: a 3-day itinerary with food spots near each stop, saved as plan.md.", [new("Open", () => { }), new("Dismiss", () => { })])),
        ("form", new NotchState.Form(
            "Volunteer sign-up",
            [
                new("q0", "Full name", "Sam Example", "your past forms", FormCardState.Answered, ["Sam", "S. Example"], Filled: true),
                new("q1", "Which days can you help?", "Saturday", "Profile/about.md", FormCardState.Answered, ["Saturday", "Sunday", "Weekdays"]),
                new("q2", "Why do you want to volunteer?", "I grew up near the shelter and", null, FormCardState.Working, Note: "Writing from your vault, on this PC… 7 s"),
                new("q3", "T-shirt size", null, null, FormCardState.Unknown, ["S", "M", "L"]),
            ],
            "Filling 2 of 3…",
            new FormActions(edits => Task.FromResult($"Saved {edits.Count} (demo: nothing written)."), () => { }, StopFilling: () => { }, Used: (_, _) => { }, Hear: (_, _) => Task.FromResult(string.Empty), Remember: _ => "Added (demo)."),
            FillProgress: 0.4,
            Fillable: 2)),
        ("home", new NotchState.Home(
            "Afternoon, Sam.",
            "Hold Ctrl + Alt and tell me what to take off your plate.",
            [new("", "Ask Jarvis", null, () => { }), new("", "Fill a form", null, () => { }), new("", "Private: off", null, () => { }), new("", "Settings", null, () => { })],
            [new("", "Lisbon trip plan", "Ready to view", () => { }), new("", "Reviewing GitHub activity", "Working…", () => { }), new("", "Volunteer sign-up", "Fill again", () => { })],
            () => { })),
        ("event", new NotchState.EventDraft(
            new Jarvis.Contracts.Calendar.CalendarEvent("Lunch with Sam", new DateTimeOffset(2030, 1, 15, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 15, 14, 0, 0, TimeSpan.Zero), false, "Cafe Example", string.Empty),
            "Google Calendar (sam@example.test)",
            _ => { },
            () => { })),
    ];

    /// <summary>Holds each notch state for 2.5 s, forever, so captures can be taken of each.</summary>
    private static async Task CycleNotchDemoAsync(INotchPresenter notch)
    {
        while (true)
        {
            foreach (var (_, state) in DemoNotchStates.Skip(1).Append(DemoNotchStates[0]))
            {
                notch.Show(state);
                await Task.Delay(2500);
            }
        }
    }
#endif

    /// <summary>The notch's home: things to start, then the latest jobs and form.</summary>
    private void ShowHome()
    {
        var notch = _host.Services.GetRequiredService<INotchPresenter>();
        var companion = _host.Services.GetRequiredService<CompanionSession>();
        var privacy = _host.Services.GetRequiredService<ScreenPrivacy>();
        var dispatcher = _host.Services.GetRequiredService<DispatcherQueue>();
        var hour = DateTime.Now.Hour;
        var name = Environment.UserName is { Length: > 1 } user && char.IsLetter(user[0]) ? char.ToUpperInvariant(user[0]) + user[1..] : null;
        var greeting = $"{(hour < 12 ? "Morning" : hour < 18 ? "Afternoon" : "Evening")}{(name is null ? string.Empty : $", {name}")}.";
        var talk = _host.Services.GetRequiredService<IOptionsMonitor<Jarvis.Core.Input.ShortcutOptions>>().CurrentValue.Talk;
        var hint = $"Hold {talk} and tell me what to take off your plate.";
        HomeItem[] actions =
        [
            new("\uE8BD", "Ask Jarvis", "Type a question (or double-tap Ctrl)", companion.Compose),
            new("\uE70F", "Fill a form", "Fill the form in your browser from your vault", companion.FillForm),
            new(privacy.PrivateMode ? "\uE72E" : "\uE785", privacy.PrivateMode ? "Private: on" : "Private: off", "Keep everything on this PC", () =>
            {
                var on = !privacy.PrivateMode;
                SettingsFile.Set(PrivacyOptions.SectionName, nameof(PrivacyOptions.PrivateMode), on);
                notch.Show(new NotchState.Response(on ? "Private mode is on: everything stays on this PC." : "Private mode is off."));
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => notch.Show(NotchState.Idle), TaskScheduler.Default);
            }),
            new("\uE713", "Settings", null, () => dispatcher.TryEnqueue(() => ShowSettings(null))),
        ];

        var agents = _host.Services.GetRequiredService<IAgentJobs>();
        var recent = new List<HomeItem>();
        var lastForm = _host.Services.GetRequiredService<Jarvis.Core.Vault.FormFiller>().LastTitle;
        if (lastForm is { Length: > 0 })
        {
            recent.Add(new("\uE70F", lastForm, "Fill again", companion.FillForm));
        }

        foreach (var job in agents.Jobs.Take(recent.Count > 0 ? 2 : 3)) // three cards fit across
        {
            recent.Add(job.Status switch
            {
                AgentJobStatus.Running or AgentJobStatus.Queued => new HomeItem("\uE916", job.Title, job.Activity ?? "Working\u2026", () => notch.Show(NotchState.Idle)),
                AgentJobStatus.Finished => new HomeItem("\uE73E", job.Title, "Ready to view", () => OpenFolder(Path.Combine(job.Folder, "output"))),
                _ => new HomeItem("\uE7BA", job.Title, job.Status == AgentJobStatus.Failed ? "Didn't finish" : job.Status.ToString(), () => OpenFolder(job.Folder)),
            });
        }

        notch.Show(new NotchState.Home(greeting, hint, actions, recent, () => notch.Show(NotchState.Idle)));
    }

    /// <summary>The five most recent background jobs: stop the running ones, open the finished ones.</summary>
    private IEnumerable<TrayMenuItem> JobMenuItems()
    {
        var agents = _host.Services.GetRequiredService<IAgentJobs>();
        var jobs = agents.Jobs.Take(5).ToList();
        if (jobs.Count == 0)
        {
            yield break;
        }

        yield return TrayMenuItem.Separator;
        foreach (var job in jobs)
        {
            yield return job.Status switch
            {
                AgentJobStatus.Running or AgentJobStatus.Queued => new($"Stop: {job.Title} ({job.Activity ?? "waiting"})", () => agents.Cancel(job.Id)),
                AgentJobStatus.Finished => new($"Open: {job.Title}", () => OpenFolder(Path.Combine(job.Folder, "output"))),
                _ => new($"{job.Status}: {job.Title}", () => OpenFolder(job.Folder)),
            };
        }
    }

    private static void OpenFolder(string folder)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Path.GetFullPath(folder)}\"") { UseShellExecute = true });

    /// <summary>First run downloads the speech models; later runs just load them.</summary>
    private async Task PrewarmEnginesAsync()
    {
        try
        {
            await Task.WhenAll(
                _host.Services.GetRequiredService<CompanionSession>().PrewarmAsync(CancellationToken.None),
                _host.Services.GetRequiredService<DictationSession>().PrewarmAsync(CancellationToken.None));
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<ILogger<App>>().LogError(exception, "Engines failed to start");
            _host.Services.GetRequiredService<INotchPresenter>().Show(new NotchState.Response($"Jarvis couldn't start its voice engines: {exception.Message}"));
        }
    }

    private List<TrayMenuItem> BuildTrayMenu()
    {
        var cursorVisible = _host.Services.GetRequiredService<IOptionsMonitor<CursorOptions>>().CurrentValue.Visible;
        var paused = _host.Services.GetRequiredService<PauseSwitch>().IsPaused;
        var menu = new List<TrayMenuItem>
        {
            new(paused ? "Resume Jarvis" : "Pause Jarvis", () => SetPaused(!paused)),
            new("Settings", ShowSettings),
            new("Fill this form from my vault", () => _host.Services.GetRequiredService<CompanionSession>().FillForm()),
            new(cursorVisible ? "Hide floating cursor" : "Show floating cursor", () => SettingsFile.Set(CursorOptions.SectionName, nameof(CursorOptions.Visible), !cursorVisible)),
        };
        menu.AddRange(JobMenuItems());
#if DEBUG
        var overlay = _host.Services.GetRequiredService<ICursorOverlay>();
        menu.Add(TrayMenuItem.Separator);
        var screen = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        menu.Add(new("Test: point at the bottom-left corner", () => overlay.PointAt(screen.X + 24, screen.Y + screen.Height - 24)));
        menu.Add(new("Test: point at screen centre", () => overlay.PointAt(screen.X + (screen.Width / 2), screen.Y + (screen.Height / 2))));
        menu.Add(new("Test: toggle thinking dots", () => overlay.SetThinking(_debugThinking = !_debugThinking)));
        var notch = _host.Services.GetRequiredService<INotchPresenter>();
        foreach (var (label, state) in DemoNotchStates)
        {
            menu.Add(new($"Test notch: {label}", () => notch.Show(state)));
        }
#endif
        menu.Add(TrayMenuItem.Separator);
        menu.Add(new("Quit Jarvis", Shutdown));
        return menu;
    }

    /// <summary>
    /// Paused: still in the tray (greyed) but otherwise asleep. Anything in progress stops; models
    /// stay loaded so resuming is instant.
    /// </summary>
    private void SetPaused(bool paused)
    {
        var pause = _host.Services.GetRequiredService<PauseSwitch>();
        if (pause.IsPaused == paused)
        {
            return;
        }

        if (paused)
        {
            _host.Services.GetRequiredService<CompanionSession>().Cancel();
            _host.Services.GetRequiredService<DictationSession>().Cancel();
        }

        pause.Set(paused);
        UpdateTray();
    }

    private void UpdateTray()
    {
        var paused = _host.Services.GetRequiredService<PauseSwitch>().IsPaused;
        var tooltip = paused ? "Jarvis (paused)" : "Jarvis";
        _trayIcon?.Update(_lastLook is null ? tooltip : $"{tooltip} · {_lastLook}", TrayIconFile(paused));
    }

    private static string TrayIconFile(bool paused) => Path.Combine(AppContext.BaseDirectory, paused ? "jarvis-paused.ico" : "jarvis.ico");

    private void ShowSettings() => ShowSettings(null);

    /// <summary>Opens Settings, on the page titled <paramref name="section"/> when given (the notch's form panel opens "Vault").</summary>
    internal void ShowSettings(string? section)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = _host.Services.GetRequiredService<SettingsWindow>();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (section is not null && _settingsWindow.ViewModel.Sections.FirstOrDefault(page => page.Title == section) is { } page)
        {
            _settingsWindow.ViewModel.SelectedSection = page;
        }

        // Bring it to the front even when it was minimised, hidden or behind other windows.
        if (_settingsWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _settingsWindow.AppWindow.Show(true);
        _settingsWindow.Activate();
        WindowChrome.Activate(WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow));
    }

    /// <summary>Quits Jarvis: the notch, cursor, shortcuts and engines all stop.</summary>
    internal void Shutdown()
    {
        _trayIcon?.Dispose();
        _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        _host.Dispose();
        Exit();
    }
}
