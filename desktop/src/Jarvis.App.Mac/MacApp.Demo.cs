using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Cursor;
using Jarvis.Core.Dictation;
using Jarvis.Platform.Mac.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Mac;

/// <summary>
/// JARVIS_DEMO, as on Windows: drive the app at start-up to check it by eye. thinking | listening |
/// point | draw | notch | notch:&lt;state&gt; | compose | ask:&lt;question&gt; | dictate:&lt;text&gt; |
/// agent:&lt;brief&gt; | selftest | pause | settings[:&lt;page&gt;].
/// </summary>
internal sealed partial class MacApp
{
    private void RunDemoFromEnvironment()
    {
        // JARVIS_SNAPSHOT=<folder>: Jarvis's own windows as PNGs every 3 s (no Screen Recording needed).
        if (Environment.GetEnvironmentVariable("JARVIS_SNAPSHOT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            _ = Task.Run(async () =>
            {
                for (var shot = 0; shot < 40; shot++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    Jarvis.Platform.Mac.Native.DebugSnapshot(folder);
                }
            });
        }

        if (Environment.GetEnvironmentVariable("JARVIS_DEMO") is not { Length: > 0 } demo)
        {
            return;
        }

        LogDemo(demo);
        var overlay = Services.GetRequiredService<ICursorOverlay>();
        var notch = Services.GetRequiredService<INotchPresenter>();
        var main = Screen.All()[0];
        switch (demo)
        {
            case "thinking":
                overlay.SetThinking(true);
                break;
            case "listening":
                overlay.SetListening(true);
                _ = Task.Run(async () =>
                {
                    for (var t = 0.0; ; t += 0.05)
                    {
                        overlay.ShowLevel(Math.Abs(Math.Sin(t * 3)) * (0.5 + (0.5 * Math.Sin(t * 0.7))));
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                });
                break;
            case "point":
                overlay.PointAt((int)(main.Left + (main.Width / 4)), (int)(main.Top + (main.Height / 4)), "Right here");
                break;
            case "draw":
                DrawDemoScene(main);
                break;
            case "notch":
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        foreach (var (_, state) in DemoStates().Skip(1).Append(DemoStates()[0]))
                        {
                            notch.Show(state);
                            await Task.Delay(2500).ConfigureAwait(false);
                        }
                    }
                });
                break;
            case { } pinned when pinned.StartsWith("notch:", StringComparison.Ordinal):
                var state = DemoStates().FirstOrDefault(entry => entry.Label == pinned["notch:".Length..]).State;
                if (state is not null)
                {
                    _ = _enginesReady.ContinueWith(_ => notch.Show(state), TaskScheduler.Default);
                }

                break;
            case "compose":
                _ = AfterEnginesAsync(() => Services.GetRequiredService<CompanionSession>().Compose());
                break;
            case { } ask when ask.StartsWith("ask:", StringComparison.Ordinal):
                _ = AfterEnginesAsync(() => Services.GetRequiredService<CompanionSession>().Ask(ask["ask:".Length..]), TimeSpan.Zero);
                break;
            case { } dictate when dictate.StartsWith("dictate:", StringComparison.Ordinal):
                _ = AfterEnginesAsync(async () =>
                {
                    var outcome = await Services.GetRequiredService<DictationSession>().InsertTranscriptAsync(dictate["dictate:".Length..], CancellationToken.None).ConfigureAwait(false);
                    LogDemoOutcome(outcome.ToString());
                });
                break;
            case { } agent when agent.StartsWith("agent:", StringComparison.Ordinal):
                Services.GetRequiredService<IAgentJobs>().Start(agent["agent:".Length..]);
                break;
            case "selftest":
                _ = AfterEnginesAsync(SpeechSelfTestAsync, TimeSpan.Zero);
                break;
            case "pause":
                SetPaused(true);
                break;
            case { } settings when settings.StartsWith("settings", StringComparison.Ordinal):
                ShowSettings(settings.Length > "settings:".Length ? settings["settings:".Length..] : null);
                break;
        }
    }

    private async Task AfterEnginesAsync(Action action, TimeSpan? wait = null)
    {
        await _enginesReady.ConfigureAwait(false);
        await Task.Delay(wait ?? TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        action();
    }

    private async Task AfterEnginesAsync(Func<Task> action, TimeSpan? wait = null)
    {
        await _enginesReady.ConfigureAwait(false);
        await Task.Delay(wait ?? TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        await action().ConfigureAwait(false);
    }

    /// <summary>Kokoro says a sentence, Whisper hears it back, then 1.5 s from the real microphone.</summary>
    private async Task SpeechSelfTestAsync()
    {
        const string Sentence = "Testing one two three. Can you hear me clearly?";
        var tts = Services.GetRequiredService<Jarvis.Contracts.Speech.ITextToSpeech>();
        var stt = Services.GetRequiredService<Jarvis.Contracts.Speech.ISpeechToText>();
        var spoken = new List<byte>();
        await foreach (var chunk in tts.SynthesizeAsync(Sentence, new("af_heart"), CancellationToken.None).ConfigureAwait(false))
        {
            spoken.AddRange(chunk.ToArray());
        }

        await Services.GetRequiredService<Jarvis.Contracts.Audio.IAudioPlayer>().PlayAsync(Once(spoken.ToArray()), tts.OutputFormat, CancellationToken.None).ConfigureAwait(false);
        await using (var session = await stt.StartSessionAsync(new(null, []), CancellationToken.None).ConfigureAwait(false))
        {
            await session.WriteAsync(Resample(spoken.ToArray(), tts.OutputFormat.SampleRate, stt.InputFormat.SampleRate), CancellationToken.None).ConfigureAwait(false);
            var heard = await session.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            LogSelfTest(Sentence, heard);
            Services.GetRequiredService<INotchPresenter>().Show(new NotchState.Response($"Self-test heard: {heard}"));
        }

        var recorded = new List<byte>();
        try
        {
            await using var microphone = Services.GetRequiredService<Jarvis.Contracts.Audio.IMicrophone>().Start(stt.InputFormat);
            using var window = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            await foreach (var chunk in microphone.Audio.ReadAllAsync(window.Token).ConfigureAwait(false))
            {
                recorded.AddRange(chunk.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Jarvis.Contracts.Audio.MicrophoneBlockedException)
        {
            LogSelfTestMicrophone(0, stt.InputFormat.SampleRate, -1);
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(recorded.ToArray()).ToArray();
        var rms = samples.Length == 0 ? 0 : Math.Sqrt(samples.Average(sample => (double)sample * sample)) / short.MaxValue;
        LogSelfTestMicrophone(samples.Length / (double)stt.InputFormat.SampleRate, stt.InputFormat.SampleRate, rms);

        static async IAsyncEnumerable<ReadOnlyMemory<byte>> Once(byte[] pcm)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield return pcm;
        }
    }

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

    /// <summary>Every kind of mark plus two turning 3D figures, on the main display (points, scale 1).</summary>
    private void DrawDemoScene(Screen main)
    {
        var (w, h) = (main.Width, main.Height);
        var screen = new CapturedScreen(new(ReadOnlyMemory<byte>.Empty, (int)w, (int)h, true), (int)main.Left, (int)main.Top, (int)w, (int)h, 1);
        var script = Jarvis.Core.Drawing.DrawingScript.Parse(
            $"""
            ring {w * 0.12} {h * 0.25} {w * 0.2} {h * 0.08} "Search"
            box {w * 0.45} {h * 0.3} {w * 0.25} {h * 0.25} "Results"
            arrow {w * 0.85} {h * 0.75} {w * 0.72} {h * 0.57} "Click here"
            num {w * 0.1} {h * 0.6} 1
            num {w * 0.18} {h * 0.6} 2
            note {w * 0.25} {h * 0.75} "Your files"
            figure {w * 0.82} {h * 0.28} {w * 0.14} "Water"
            ball 0 0.15 0 0.42 red "Oxygen"
            ball -0.62 -0.38 0 0.26 white "Hydrogen"
            ball 0.62 -0.38 0 0.26 white
            rod 0 0.15 0 -0.62 -0.38 0 0.07 grey
            rod 0 0.15 0 0.62 -0.38 0 0.07 grey
            """,
            screen: 1);
        var ink = CursorColor.Parse(Services.GetRequiredService<IOptionsMonitor<CursorOptions>>().CurrentValue.Color);
        Services.GetRequiredService<IScreenDrawing>().Show(Jarvis.Core.Drawing.SceneBuilder.Build(script, screen, ink, 1, seed: 42));
    }

    private static List<(string Label, NotchState State)> DemoStates() =>
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
                new("q2", "Why do you want to volunteer?", "I grew up near the shelter and", null, FormCardState.Working, Note: "Writing from your vault, on this Mac… 7 s"),
                new("q3", "T-shirt size", null, null, FormCardState.Unknown, ["S", "M", "L"]),
            ],
            "Filling 2 of 3…",
            new FormActions(edits => Task.FromResult($"Saved {edits.Count} (demo: nothing written)."), () => { }, StopFilling: () => { }, Used: (_, _) => { }),
            FillProgress: 0.4,
            Fillable: 2)),
        ("chat", new NotchState.Composing(
            _ => { },
            () => { },
            [
                new ChatTurn("where is the share button?", "Top right, next to your profile picture.", DateTimeOffset.Now.AddMinutes(-14)),
                new ChatTurn("and how do I invite someone to edit?", "Press Share, type their email, set them to Editor, then Send.", DateTimeOffset.Now.AddMinutes(-2)),
            ])),
        ("home", new NotchState.Home(
            "Afternoon, Sam.",
            "Hold Control + Option and tell me what to take off your plate.",
            [new("ask", "Ask Jarvis", null, () => { }), new("form", "Fill a form", null, () => { }), new("private", "Private: off", null, () => { }), new("settings", "Settings", null, () => { })],
            [new("job", "Lisbon trip plan", "Ready to view", () => { }), new("job", "Reviewing GitHub activity", "Working…", () => { }), new("job", "Clearing inbox", "Didn't finish", () => { }, new NotchAction("Retry", () => { }))],
            () => { })),
        ("event", new NotchState.EventDraft(
            new Jarvis.Contracts.Calendar.CalendarEvent("Lunch with Sam", new DateTimeOffset(2030, 1, 15, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 15, 14, 0, 0, TimeSpan.Zero), false, "Cafe Example", string.Empty),
            "Google Calendar",
            _ => { },
            () => { })),
    ];

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo: {Demo}")]
    private partial void LogDemo(string demo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo dictation outcome: {Outcome}")]
    private partial void LogDemoOutcome(string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Self-test: said \"{Said}\", heard \"{Heard}\"")]
    private partial void LogSelfTest(string said, string heard);

    [LoggerMessage(Level = LogLevel.Information, Message = "Self-test microphone: {Seconds:F2} s of audio at {Rate} Hz, RMS level {Rms:F4}")]
    private partial void LogSelfTestMicrophone(double seconds, int rate, double rms);
}
