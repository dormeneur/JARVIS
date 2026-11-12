using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.App.Hosting;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Speech;
using Jarvis.Contracts.Vault;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Cursor;
using Jarvis.Core.Dictation;
using Jarvis.Core.Input;
using Jarvis.Core.Notch;
using Jarvis.Core.Proactive;
using Jarvis.Core.Providers;
using Jarvis.Core.Sound;
using Jarvis.Platform.Audio;
using Jarvis.Platform.Mac;
using Jarvis.Platform.Mac.Audio;
using Jarvis.Platform.Mac.Capture;
using Jarvis.Platform.Mac.Input;
using Jarvis.Platform.Mac.Shell;
using Jarvis.Platform.Mac.Text;
using Jarvis.Providers.ClaudeCli;
using Jarvis.Providers.Local;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Mac;

/// <summary>
/// The Settings window's pages, built from the live options (IOptionsMonitor.CurrentValue), the
/// same settings the Windows pages edit. A change is written with SettingsFile.Set (the file is
/// watched, so everything applies live) and the pages are re-sent; settings read once at start-up
/// show "applies after restart" with a Restart button.
/// </summary>
internal sealed class MacSettings
{
    private readonly IServiceProvider _services;
    private readonly Action _restart;
    private readonly Dictionary<int, Action> _buttons = [];
    private readonly string _startupSnapshot;
    private string? _vaultMessage;
    private string _invite = string.Empty;
    private string _routineSchedule = "weekdays 09:00";
    private string _routineBrief = string.Empty;
    private string? _routineMessage;
    private string _connectorName = string.Empty;
    private string _connectorUrl = string.Empty;
    private string _connectorKey = string.Empty;
    private string? _connectorMessage;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public MacSettings(IServiceProvider services, Action restart)
    {
        _services = services;
        _restart = restart;
        _startupSnapshot = RestartOnlyValues();
        MainThread.Post(() => SettingsWindow.Start(Changed, Clicked));

        // Re-send the pages as soon as settings.json has reloaded (after a change here or anywhere).
        var configuration = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        Microsoft.Extensions.Primitives.ChangeToken.OnChange(configuration.GetReloadToken, () => MainThread.Post(() => Send(show: false, null)));
    }

    public void Show(string? section) => MainThread.Post(() => Send(show: true, section));

    /// <summary>Sends the pages without opening the window: the notch's Settings shows the same ones.</summary>
    public void Prepare() => MainThread.Post(() => Send(show: false, null));

    private T Options<T>() where T : class => _services.GetRequiredService<IOptionsMonitor<T>>().CurrentValue;

    /// <summary>Brain and agent models and concurrency are read once at start-up.</summary>
    private string RestartOnlyValues()
        => $"{Options<ClaudeCliOptions>().Model}|{Options<AgentOptions>().Model}|{Options<AgentOptions>().MaximumConcurrent}|{Options<ClaudeCliOptions>().FreshConversationAfter}";

    private void Send(bool show, string? section)
    {
        _buttons.Clear();
        var banner = RestartOnlyValues() == _startupSnapshot ? null : "Some changes apply after Jarvis restarts. Use General → Restart Jarvis.";
        SettingsWindow.Update(Pages(), banner, show, section);
    }

    /// <summary>Re-sends the pages now; file-backed settings are re-sent again when the file reloads.</summary>
    private void Refresh() => MainThread.Post(() => Send(show: false, null));

    private void Changed(string key, JsonElement value)
    {
        switch (key)
        {
            case "app:login":
                LoginItem.Set(value.GetBoolean());
                break;
            case "app:invite":
                _invite = value.GetString() ?? string.Empty;
                break;
            case "app:routine-schedule":
                _routineSchedule = value.GetString() ?? string.Empty;
                break;
            case "app:routine-brief":
                _routineBrief = value.GetString() ?? string.Empty;
                break;
            case "app:connector-name":
                _connectorName = value.GetString() ?? string.Empty;
                break;
            case "app:connector-url":
                _connectorUrl = value.GetString() ?? string.Empty;
                break;
            case "app:connector-key":
                _connectorKey = value.GetString() ?? string.Empty;
                break;
            default:
                // "Section:Name#view": a second row editing the same setting (the custom colour field).
                var setting = key.Split('#')[0];
                var split = setting.LastIndexOf(':');
                if (split > 0)
                {
                    SettingsFile.Set(setting[..split], setting[(split + 1)..], JsonNode.Parse(value.GetRawText()));
                }

                return; // the reload re-sends the pages
        }

        Refresh();
    }

    private void Clicked(int id)
    {
        if (_buttons.TryGetValue(id, out var action))
        {
            action();
        }
    }

    private int Button(Action action)
    {
        var id = _buttons.Count + 1;
        _buttons[id] = action;
        return id;
    }

    private static object Page(string title, string symbol, params object[] rows) => new { title, symbol, rows };

    private static object Group(string label) => new { id = "group:" + label, type = "group", label };

    private static object Note(string label, string? detail = null) => new { id = "note:" + label, type = "note", label, detail };

    private static object Toggle(string key, string label, bool on, string? detail = null) => new { id = key, type = "toggle", label, detail, on };

    private static object Text(string key, string label, string text, string? detail = null, bool secret = false) => new { id = key, type = secret ? "secret" : "text", label, detail, text };

    private static object Number(string key, string label, double number, double minimum, double maximum, double step, string? detail = null)
        => new { id = key, type = "number", label, detail, number, minimum, maximum, step };

    private static object Choice(string key, string label, string text, IEnumerable<(string Value, string Label)> choices, string? detail = null)
        => new { id = key, type = "choice", label, detail, text, choices = choices.Select(choice => new { value = choice.Value, label = choice.Label }).ToList() };

    private object Action(string label, string button, Action action, string? detail = null, bool enabled = true)
        => new { id = "button:" + label, type = "button", label, detail, text = button, action = Button(action), enabled };

    /// <summary>"Ctrl + Alt" as a Mac user reads it.</summary>
    internal static string MacChord(string chord) => chord.Replace("Ctrl", "Control", StringComparison.Ordinal).Replace("Alt", "Option", StringComparison.Ordinal).Replace("Win", "Command", StringComparison.Ordinal);

    private static readonly (string Value, string Label)[] Colours =
        [("#3380FF", "Blue"), ("#26C281", "Green"), ("#D4A017", "Yellow"), ("#FF4C4C", "Red"), ("#A35CFF", "Purple"), ("#FF5CA8", "Pink"), ("#FF8A1F", "Orange"), ("#F2F2F2", "White")];

    /// <summary>The presets, plus the current colour when it's a custom one (so the picker still shows it).</summary>
    private static IEnumerable<(string, string)> CursorColours(string current)
        => Colours.Any(colour => colour.Value.Equals(current, StringComparison.OrdinalIgnoreCase)) ? Colours : [.. Colours, (current, "Custom")];

    private static readonly (string, string)[] Models = [("sonnet", "Sonnet"), ("opus", "Opus"), ("haiku", "Haiku")];

    private List<object> Pages()
    {
        var cursor = Options<CursorOptions>();
        var notch = Options<NotchOptions>();
        var privacy = Options<PrivacyOptions>();
        var voice = Options<CompanionOptions>();
        var dictation = Options<DictationOptions>();
        var shortcuts = Options<ShortcutOptions>();
        var agents = Options<AgentOptions>();
        var proactive = Options<ProactiveOptions>();
        var sounds = Options<SoundOptions>();
        var memory = Options<MemoryOptions>();
        var brain = Options<ClaudeCliOptions>();
        var ollama = Options<OllamaOptions>();
        var vault = _services.GetRequiredService<IVault>();
        var browser = Options<BrowserOptions>();
        var login = LoginItem.Enabled;

        return
        [
            Page(
                "General",
                "gearshape",
                Toggle("app:login", "Open Jarvis when you log in", login == true, login is null ? "Available when Jarvis runs from Jarvis.app (or waiting for approval in System Settings → General → Login Items)." : null),
                Choice($"{ClaudeCliOptions.SectionName}:{nameof(ClaudeCliOptions.Model)}", "Claude model for screen help", brain.Model, Models, "Applies after restart."),
                Group("Sounds"),
                Toggle($"{SoundOptions.SectionName}:{nameof(SoundOptions.Enabled)}", "Play sounds", sounds.Enabled),
                Toggle($"{SoundOptions.SectionName}:{nameof(SoundOptions.DrawingSounds)}", "Pen sounds while drawing", sounds.DrawingSounds),
                Group("Memory"),
                Number($"{MemoryOptions.SectionName}:{nameof(MemoryOptions.UnloadVoiceAfterMinutes)}", "Free the voice engines after idle minutes", memory.UnloadVoiceAfterMinutes, 0, 120, 5, "0 keeps them loaded (fastest replies)."),
                Group("Jarvis"),
                Action("Restart Jarvis", "Restart", _restart),
                Action("Data folder", "Show in Finder", () => Native.OpenUrl(AppPaths.DataDirectory), AppPaths.DataDirectory),
                Action("Settings file", "Open", () => Native.OpenUrl(AppPaths.SettingsFile), "Every setting, as JSON. Edits apply live.")),
            Page(
                "Permissions",
                "lock.shield",
                Note("macOS asks once for each. Jarvis works without any of them, just with less."),
                Action("Microphone", MacMicrophone.BlockedByMacOS() ? "Allow…" : "Allowed", () => Native.OpenUrl(MacMicrophone.SettingsPage), "To hear you while you hold the talk keys."),
                Action("Screen Recording", MacScreenCapture.Allowed ? "Allowed" : "Allow…", () => { Native.ScreenAccess(1); Native.OpenUrl(MacScreenCapture.SettingsPage); }, "To see your screen during a turn, and only then."),
                Action("Input Monitoring", MacShortcutHook.Allowed ? "Allowed" : "Allow…", () => Native.OpenUrl(MacShortcutHook.SettingsPage), "For the hold-to-talk and dictation shortcuts. Relaunch Jarvis after allowing it."),
                Action("Accessibility", MacTextInsertion.Allowed ? "Allowed" : "Allow…", () => { MacTextInsertion.AskForAccess(); Native.OpenUrl(MacTextInsertion.SettingsPage); }, "To type dictation into the app you're in, and to drive when you let it.")),
            Page(
                "Privacy & memory",
                "hand.raised",
                Toggle($"{PrivacyOptions.SectionName}:{nameof(PrivacyOptions.ScreenAccess)}", "Let Jarvis see my screen", privacy.ScreenAccess, "Only during a turn you start. Every look is logged."),
                Toggle($"{PrivacyOptions.SectionName}:{nameof(PrivacyOptions.ShowWhenLooking)}", "Say \"Looking at your screen\" while it does", privacy.ShowWhenLooking),
                Toggle($"{PrivacyOptions.SectionName}:{nameof(PrivacyOptions.PrivateMode)}", "Private mode", privacy.PrivateMode, "Everything stays on this Mac: a local model through Ollama answers instead of Claude."),
                Toggle($"{PrivacyOptions.SectionName}:{nameof(PrivacyOptions.ShowInRecordings)}", "Show Jarvis in screenshots and recordings", privacy.ShowInRecordings, "Applies after restart."),
                Group("Let Jarvis drive"),
                Toggle($"{PrivacyOptions.SectionName}:{nameof(PrivacyOptions.LetJarvisDrive)}", "Let Jarvis click and type for me", privacy.LetJarvisDrive, "Needs screen access and Accessibility."),
                Toggle($"{PrivacyOptions.SectionName}:{nameof(PrivacyOptions.AskEveryStep)}", "Ask before every step", privacy.AskEveryStep, "Off: asks once, then stops the moment you move the mouse or press a key."),
                Group("Conversation"),
                Action("Forget the conversation", "Forget", () =>
                {
                    _services.GetRequiredService<Jarvis.Contracts.Brain.IAssistantBrain>().ForgetConversation();
                    _services.GetRequiredService<ConversationLog>().Clear();
                }, "Your next question starts a fresh conversation."),
                Action("Screen looks log", "Clear", () => _services.GetRequiredService<ScreenPrivacy>().Clear(), "Every time Jarvis looked at your screen (never what you asked).")),
            Page(
                "Voice",
                "speaker.wave.2",
                Choice($"{CompanionOptions.SectionName}:{nameof(CompanionOptions.Voice)}", "Voice", voice.Voice, Voices()),
                Number($"{CompanionOptions.SectionName}:{nameof(CompanionOptions.Speed)}", "Speed", voice.Speed, 0.5, 2, 0.1),
                Number($"{CompanionOptions.SectionName}:{nameof(CompanionOptions.CaptionSeconds)}", "Keep replies on screen (seconds)", voice.CaptionSeconds, 2, 60, 1),
                Action("Hear it", "Play", PreviewVoice)),
            Page(
                "Microphone",
                "mic",
                Choice($"{MicrophoneOptions.SectionName}:{nameof(MicrophoneOptions.DeviceId)}", "Microphone", Options<MicrophoneOptions>().DeviceId, [(string.Empty, "System default"), .. MacMicrophone.Devices().Select(device => (device.Id, device.Name))]),
                MacMicrophone.BlockedByMacOS() ? Action("macOS is blocking the microphone", "Open System Settings", () => Native.OpenUrl(MacMicrophone.SettingsPage)) : Note("Jarvis records only while you hold the keys (2 minutes at most).")),
            Page(
                "Dictation",
                "text.cursor",
                Note($"Hold {MacChord(shortcuts.Dictate)} and speak; release to type it where your cursor is. Double-tap to keep going hands-free."),
                Toggle($"{DictationOptions.SectionName}:{nameof(DictationOptions.Cleanup)}", "Clean up punctuation and filler words", dictation.Cleanup),
                Choice($"{DictationOptions.SectionName}:{nameof(DictationOptions.CleanupModel)}", "Clean-up model", dictation.CleanupModel, Models),
                Choice($"{DictationOptions.SectionName}:{nameof(DictationOptions.Language)}", "Language", dictation.Language, [(string.Empty, "Auto-detect"), ("en", "English"), ("hi", "Hindi"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("pt", "Portuguese"), ("it", "Italian"), ("ja", "Japanese"), ("zh", "Chinese")]),
                Note("Learned words", dictation.Words.Count == 0 ? "None yet: fix a name after dictating it and Jarvis learns it." : string.Join(", ", dictation.Words))),
            Page(
                "Shortcuts",
                "keyboard",
                Choice($"{ShortcutOptions.SectionName}:{nameof(ShortcutOptions.Talk)}", "Hold to talk", shortcuts.Talk, ShortcutOptions.HoldChoices.Select(choice => (choice, MacChord(choice)))),
                Choice($"{ShortcutOptions.SectionName}:{nameof(ShortcutOptions.Dictate)}", "Hold to dictate", shortcuts.Dictate, ShortcutOptions.HoldChoices.Select(choice => (choice, MacChord(choice)))),
                Note("Double-tap Control", "Type a question in the notch."),
                Note("Escape", "Stop whatever Jarvis is doing.")),
            Page(
                "Cursor",
                "cursorarrow.rays",
                Toggle($"{CursorOptions.SectionName}:{nameof(CursorOptions.Visible)}", "Show the floating cursor", cursor.Visible),
                Choice($"{CursorOptions.SectionName}:{nameof(CursorOptions.Color)}", "Colour", cursor.Color, CursorColours(cursor.Color)),
                Text($"{CursorOptions.SectionName}:{nameof(CursorOptions.Color)}#custom", "Custom colour", cursor.Color, "Any hex colour, e.g. #FF8800. Press Enter to apply."),
                Number($"{CursorOptions.SectionName}:{nameof(CursorOptions.Size)}", "Size", cursor.Size, 0.5, 2, 0.1),
                Choice($"{CursorOptions.SectionName}:{nameof(CursorOptions.FollowSpeed)}", "Follows the mouse", cursor.FollowSpeed, [("instant", "Instantly"), ("fast", "Fast"), ("natural", "Natural (bouncy)")]),
                Toggle($"{CursorOptions.SectionName}:{nameof(CursorOptions.ShakeToDetach)}", "Shake the mouse to leave it behind", cursor.ShakeToDetach, "Then click it to talk."),
                Toggle($"{CursorOptions.SectionName}:{nameof(CursorOptions.HideInFullscreen)}", "Hide while something is fullscreen", cursor.HideInFullscreen),
                Toggle($"{CursorOptions.SectionName}:{nameof(CursorOptions.Highlighter)}", "Highlighter trail while talking", cursor.Highlighter, "Circle something with the mouse and say \"this\".")),
            Page(
                "Notch",
                "rectangle.topthird.inset.filled",
                Toggle($"{NotchOptions.SectionName}:{nameof(NotchOptions.OpenOnHover)}", "Open when the mouse rests on the notch", notch.OpenOnHover),
                Number($"{NotchOptions.SectionName}:{nameof(NotchOptions.HoverDelayMs)}", "Hover delay (ms)", notch.HoverDelayMs, 0, 1000, 50, "0 opens it the moment the mouse arrives."),
                Choice($"{NotchOptions.SectionName}:{nameof(NotchOptions.Speed)}", "Animation", notch.Speed, [("snappy", "Snappy"), ("smooth", "Smooth"), ("relaxed", "Relaxed")]),
                Toggle($"{NotchOptions.SectionName}:{nameof(NotchOptions.CloseOnLeave)}", "Close when the mouse leaves it", notch.CloseOnLeave, "Off: it stays open until you click somewhere else."),
                Toggle($"{NotchOptions.SectionName}:{nameof(NotchOptions.Haptics)}", "Tap the trackpad when it opens", notch.Haptics),
                Group("Look"),
                Choice($"{NotchOptions.SectionName}:{nameof(NotchOptions.Size)}", "Size", notch.Size, [("compact", "Compact"), ("regular", "Regular"), ("large", "Large")]),
                Choice($"{NotchOptions.SectionName}:{nameof(NotchOptions.Corners)}", "Corners", notch.Corners, [("soft", "Soft"), ("default", "Default"), ("sharp", "Sharp")]),
                Toggle($"{NotchOptions.SectionName}:{nameof(NotchOptions.Glow)}", "Colour glow", notch.Glow),
                Choice($"{NotchOptions.SectionName}:{nameof(NotchOptions.GlowColour)}", "Glow colour", notch.GlowColour, [("activity", "By activity"), ("cursor", "Match the cursor")]),
                Group("Shelf"),
                Toggle($"{NotchOptions.SectionName}:{nameof(NotchOptions.Shelf)}", "Hold files dragged to the notch", notch.Shelf, "Drag them out again wherever you need them.")),
            Page(
                "Agents",
                "person.2.wave.2",
                Choice($"{AgentOptions.SectionName}:{nameof(AgentOptions.Model)}", "Model for background jobs", agents.Model, Models, "Applies after restart."),
                Number($"{AgentOptions.SectionName}:{nameof(AgentOptions.MaximumConcurrent)}", "Jobs at once", agents.MaximumConcurrent, 1, 5, 1, "Applies after restart."),
                Toggle($"{AgentOptions.SectionName}:{nameof(AgentOptions.SpeakResults)}", "Say when a job finishes", agents.SpeakResults),
                Action("Jobs folder", "Show in Finder", () => Native.OpenUrl(AppPaths.JobsDirectory))),
            Page("Routines", "clock.arrow.circlepath", [.. RoutineRows()]),
            Page("Connectors", "puzzlepiece.extension", [.. ConnectorRows()]),
            Page(
                "Proactive",
                "sun.max",
                Toggle($"{ProactiveOptions.SectionName}:{nameof(ProactiveOptions.MorningBrief)}", "Morning brief", proactive.MorningBrief),
                Text($"{ProactiveOptions.SectionName}:{nameof(ProactiveOptions.MorningFrom)}", "From", proactive.MorningFrom, "HH:MM"),
                Text($"{ProactiveOptions.SectionName}:{nameof(ProactiveOptions.CalendarAddress)}", "Calendar (private iCal address)", proactive.CalendarAddress, secret: true),
                Number($"{ProactiveOptions.SectionName}:{nameof(ProactiveOptions.MeetingHeadsUpMinutes)}", "Meeting heads-up (minutes before)", proactive.MeetingHeadsUpMinutes, 0, 30, 1),
                Toggle($"{ProactiveOptions.SectionName}:{nameof(ProactiveOptions.Suggestions)}", "Suggest things to take off my plate", proactive.Suggestions, "Off by default. Reads the app and window title in front, never the screen."),
                Toggle($"{ProactiveOptions.SectionName}:{nameof(ProactiveOptions.Speak)}", "Say them out loud", proactive.Speak)),
            Page(
                "Vault",
                "archivebox",
                Note(vault.IsConnected ? "Connected to your vault." : "Not connected.", _vaultMessage),
                Text($"{VaultOptions.SectionName}:{nameof(VaultOptions.Address)}", "Vault address (jv-api)", Options<VaultOptions>().Address),
                Text("app:invite", "Invite code", _invite, "docker exec jv-api python -m app.invite"),
                vault.IsConnected
                    ? Action("Disconnect this Mac", "Disconnect", () => { vault.Disconnect(); _vaultMessage = "Disconnected."; Refresh(); })
                    : Action("Connect this Mac", "Connect", () => _ = ConnectVaultAsync(vault)),
                Group("Private mode"),
                Text($"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Address)}", "Ollama address", ollama.Address),
                Text($"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Model)}", "Local model", ollama.Model)),
            Page(
                "Browser",
                "globe",
                Toggle($"{BrowserOptions.SectionName}:{nameof(BrowserOptions.ReadPage)}", "Read the page in front", browser.ReadPage, "Through the Jarvis extension in Chrome, Edge, Brave or Arc."),
                Note("Allowed extensions", browser.AllowedExtensions.Count == 0 ? "None yet." : string.Join(", ", browser.AllowedExtensions)),
                _services.GetRequiredService<Jarvis.Platform.Browser.BrowserLinkServer>().WaitingForApproval is { } waiting
                    ? Action($"Extension {waiting} wants to connect", "Allow", () => SettingsFile.Set(BrowserOptions.SectionName, nameof(BrowserOptions.AllowedExtensions), new JsonArray([.. browser.AllowedExtensions.Append(waiting).Distinct().Select(id => (JsonNode)id)])))
                    : Note("Load the extension (browser-extension/ next to the app) unpacked, then allow it here.")),
        ];
    }

    /// <summary>Scheduled jobs, as Settings → Routines on Windows: each with its schedule in words and its next run.</summary>
    private IEnumerable<object> RoutineRows()
    {
        var store = _services.GetRequiredService<IRoutineStore>();
        var time = _services.GetRequiredService<TimeProvider>();
        yield return Note("Background jobs on a schedule", "daily 08:00 · weekdays 09:00 · monday 18:30 · every 2 hours (15 minutes at least). Or just ask Jarvis: \"every morning, summarise my inbox\".");
        foreach (var routine in store.All)
        {
            var next = RoutineSchedule.IsValid(routine.Schedule) ? RoutineSchedule.NextAfter(routine.Schedule, routine.LastRun ?? routine.Created, time.LocalTimeZone) : (DateTimeOffset?)null;
            var status = $"{RoutineSchedule.Describe(routine.Schedule)}{(next is { } at ? $" · next {at.ToLocalTime():ddd HH:mm}" : string.Empty)}{(routine.LastRun is { } last ? $" · last ran {last.ToLocalTime():ddd HH:mm}" : string.Empty)}";
            var id = routine.Id;
            yield return Action(routine.Brief, "Remove", () => { store.Remove(id); Refresh(); }, status);
        }

        yield return Group("New routine");
        yield return Text("app:routine-schedule", "When", _routineSchedule);
        yield return Text("app:routine-brief", "What to do", _routineBrief, "What you'd tell Jarvis, e.g. \"collect today's AI news into a one-page brief\".");
        yield return Action("Add the routine", "Add", AddRoutine, _routineMessage);
    }

    private void AddRoutine()
    {
        if (!RoutineSchedule.IsValid(_routineSchedule))
        {
            _routineMessage = "That schedule isn't one I know: try \"daily 08:00\", \"weekdays 09:00\", \"friday 17:00\" or \"every 2 hours\".";
        }
        else if (_routineBrief.Trim().Length == 0)
        {
            _routineMessage = "Say what the routine should do.";
        }
        else
        {
            _services.GetRequiredService<IRoutineStore>().Add(new Routine(Guid.NewGuid().ToString("N"), _routineSchedule.Trim(), _routineBrief.Trim(), _services.GetRequiredService<TimeProvider>().GetUtcNow(), null));
            _routineMessage = $"Added: {RoutineSchedule.Describe(_routineSchedule)}.";
            _routineBrief = string.Empty;
        }

        Refresh();
    }

    /// <summary>
    /// Remote MCP servers background jobs may use: with no credentials, an API key (Keychain) or a
    /// browser sign-in (MCP authorization, tokens in the Keychain). Test sends a real initialize.
    /// </summary>
    private IEnumerable<object> ConnectorRows()
    {
        var store = _services.GetRequiredService<IConnectorStore>();
        yield return Note("Tools for background jobs", "Remote MCP servers (https, or http on this Mac). Jobs can use them; nothing else can.");
        foreach (var connector in store.All)
        {
            var name = connector.Name;
            var how = connector.Auth switch { ConnectorAuth.ApiKey => "API key", ConnectorAuth.SignIn => "signed in", _ => "no sign-in" };
            yield return Action(name, "Test", () => _ = TestConnectorAsync(connector), $"{connector.Url} · {how}");
            if (connector.Auth == ConnectorAuth.SignIn)
            {
                yield return Action($"{name}: sign in again", "Sign in", () => _ = SignInAsync(connector));
            }

            yield return Action($"{name}: remove", "Remove", () => { store.Remove(name); _connectorMessage = $"Removed {name}."; Refresh(); });
        }

        if (_connectorMessage is not null)
        {
            yield return Note(_connectorMessage);
        }

        yield return Group("Add a connector");
        yield return Text("app:connector-name", "Name", _connectorName, "Letters, digits and dashes, e.g. linear.");
        yield return Text("app:connector-url", "Server URL", _connectorUrl, "e.g. https://mcp.linear.app/mcp");
        yield return Text("app:connector-key", "API key (optional)", _connectorKey, secret: true);
        yield return Action("Add with the key (or none)", "Add", () => _ = AddConnectorAsync(signIn: false));
        yield return Action("Add and sign in with the browser", "Sign in", () => _ = AddConnectorAsync(signIn: true));
    }

    private async Task AddConnectorAsync(bool signIn)
    {
        var name = _connectorName.Trim();
        var url = _connectorUrl.Trim();
        var key = _connectorKey.Trim().Length == 0 ? null : _connectorKey.Trim();
        var store = _services.GetRequiredService<IConnectorStore>();
        if (!Connector.IsValidName(name))
        {
            _connectorMessage = "Give it a short name: letters, digits and dashes.";
        }
        else if (store.All.Any(connector => connector.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _connectorMessage = $"There's already a connector called {name}.";
        }
        else if (!Connector.IsValidUrl(url))
        {
            _connectorMessage = "The URL must be https (or http on this Mac).";
        }
        else if (signIn)
        {
            _connectorMessage = "Finish signing in in your browser…";
            MainThread.Post(() => Send(show: false, null));
            _connectorMessage = await SignInAsync(new Connector(name, url, ConnectorAuth.SignIn)).ConfigureAwait(false) ?? $"Signed in: {name} is ready for jobs.";
            ClearConnectorForm();
        }
        else
        {
            var check = await ConnectorCheck.TestAsync(_http, url, key).ConfigureAwait(false);
            store.Save(new Connector(name, url, key is null ? ConnectorAuth.None : ConnectorAuth.ApiKey), key);
            _connectorMessage = $"Added {name}. {check}";
            ClearConnectorForm();
        }

        MainThread.Post(() => Send(show: false, null));
    }

    private void ClearConnectorForm() => (_connectorName, _connectorUrl, _connectorKey) = (string.Empty, string.Empty, string.Empty);

    /// <summary>Runs the browser sign-in and saves the tokens; returns a message if it failed.</summary>
    private async Task<string?> SignInAsync(Connector connector)
    {
        try
        {
            var tokens = await _services.GetRequiredService<McpOAuth>().SignInAsync(connector.Url, page => { Native.OpenUrl(page.AbsoluteUri); return ValueTask.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            _services.GetRequiredService<IConnectorStore>().SaveSignIn(connector, tokens);
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or OperationCanceledException or JsonException)
        {
            return $"Sign-in didn't finish: {exception.Message}";
        }
    }

    private async Task TestConnectorAsync(Connector connector)
    {
        var store = _services.GetRequiredService<IConnectorStore>();
        _connectorMessage = $"{connector.Name}: {await ConnectorCheck.TestAsync(_http, connector.Url, await store.TokenAsync(connector.Name, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false)}";
        MainThread.Post(() => Send(show: false, null));
    }

    private async Task ConnectVaultAsync(IVault vault)
    {
        _vaultMessage = "Connecting…";
        MainThread.Post(() => Send(show: false, null));
        try
        {
            await vault.ConnectAsync(_invite.Trim(), CancellationToken.None).ConfigureAwait(false);
            _invite = string.Empty;
            _vaultMessage = "Connected. Ask Jarvis to fill a form, or about your notes.";
        }
        catch (VaultException exception)
        {
            _vaultMessage = exception.Message;
        }

        MainThread.Post(() => Send(show: false, null));
    }

    private void PreviewVoice()
    {
        var speech = _services.GetRequiredService<ITextToSpeech>();
        var player = _services.GetRequiredService<IAudioPlayer>();
        var voice = Options<CompanionOptions>();
        _ = Task.Run(() => player.PlayAsync(speech.SynthesizeAsync("Hi, I'm Jarvis. This is how I sound.", new SpeechStyle(voice.Voice, voice.Speed), CancellationToken.None), speech.OutputFormat, CancellationToken.None));
    }

    /// <summary>Kokoro's voices shipped beside the app (voices/*.npy), as on Windows.</summary>
    private static IEnumerable<(string, string)> Voices()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "voices");
        if (!Directory.Exists(folder))
        {
            return [("af_heart", "Heart (American, female)")];
        }

        return Directory.EnumerateFiles(folder, "*.npy")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(id => id.Length > 3 && id[0] is 'a' or 'b' && id[2] == '_')
            .Order(StringComparer.Ordinal)
            .Select(id => (id, $"{char.ToUpperInvariant(id[3]) + id[4..]} ({(id[0] == 'a' ? "American" : "British")}, {(id[1] == 'f' ? "female" : "male")})"));
    }
}
