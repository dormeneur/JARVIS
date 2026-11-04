using Jarvis.App.Notch;
using Jarvis.App.Settings;
using Jarvis.Contracts.Agents;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Contracts.Speech;
using Jarvis.Contracts.Text;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Cursor;
using Jarvis.Core.Dictation;
using Jarvis.Core.Providers;
using Jarvis.Core.Input;
using Jarvis.Core.Proactive;
using Jarvis.Platform.Audio;
using Jarvis.Platform.Capture;
using Jarvis.Platform.Input;
using Jarvis.Platform.Overlay;
using Jarvis.Platform.Text;
using Jarvis.Providers.ClaudeCli;
using Jarvis.Providers.Kokoro;
using Jarvis.Providers.Local;
using Jarvis.Providers.Whisper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;

namespace Jarvis.App.Hosting;

/// <summary>Composition root: the only place that knows concrete types.</summary>
internal static class AppHost
{
    public static IHost Build()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Jarvis",
        });

        Directory.CreateDirectory(AppPaths.DataDirectory);
        builder.Configuration.AddJsonFile(AppPaths.SettingsFile, optional: true, reloadOnChange: true);

        var services = builder.Services;
        var configuration = builder.Configuration;
        services.Configure<ProviderOptions>(configuration.GetSection(ProviderOptions.SectionName));
        services.Configure<CursorOptions>(configuration.GetSection(CursorOptions.SectionName));
        services.Configure<CompanionOptions>(configuration.GetSection(CompanionOptions.SectionName));
        services.Configure<DictationOptions>(configuration.GetSection(DictationOptions.SectionName));
        services.Configure<ShortcutOptions>(configuration.GetSection(ShortcutOptions.SectionName));
        services.Configure<PrivacyOptions>(configuration.GetSection(PrivacyOptions.SectionName));
        services.Configure<BrowserOptions>(configuration.GetSection(BrowserOptions.SectionName));
        services.Configure<Jarvis.Core.Sound.SoundOptions>(configuration.GetSection(Jarvis.Core.Sound.SoundOptions.SectionName));
        services.Configure<MicrophoneOptions>(configuration.GetSection(MicrophoneOptions.SectionName));
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Jarvis.Core.PauseSwitch>();
        services.AddSingleton<HttpClient>();

        AddEngines(services, configuration);
        AddDesktop(services);
        AddCompanion(services);
        AddDictation(services);
        AddAgents(services, configuration);
        AddProactive(services, configuration);

        services.AddSingleton<SettingsStore>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.GeneralViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.VoiceViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.MicrophoneViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.DictationViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.ShortcutsViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.CursorViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.AgentsViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.RoutinesViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.PrivacyViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.BrowserViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.ConnectorsViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.ProactiveViewModel>();
        services.AddTransient<Jarvis.App.Settings.ViewModels.VaultViewModel>();

        return builder.Build();
    }

    /// <summary>Every AI engine, registered under its key; settings pick one per capability.</summary>
    private static void AddEngines(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhisperOptions>(configuration.GetSection(WhisperOptions.SectionName));
        services.PostConfigure<WhisperOptions>(options => options.ModelDirectory = AppPaths.ModelsDirectory);
        services.AddKeyedSingleton<ISpeechToText, WhisperSpeechToText>(ProviderKeys.Whisper);
        services.AddSelectedProvider<ISpeechToText>(providers => providers.SpeechToText, (provider, engine) => new TrackedSpeechToText(engine, provider.GetRequiredService<IdleEngines>()));

        services.Configure<KokoroOptions>(configuration.GetSection(KokoroOptions.SectionName));
        services.PostConfigure<KokoroOptions>(options => options.ModelDirectory = AppPaths.ModelsDirectory);
        services.AddKeyedSingleton<ITextToSpeech, KokoroTextToSpeech>(ProviderKeys.Kokoro);
        services.AddSelectedProvider<ITextToSpeech>(providers => providers.TextToSpeech, (provider, engine) => new TrackedTextToSpeech(engine, provider.GetRequiredService<IdleEngines>()));

        // The selected engines' models are freed after idle minutes (Settings → General → Memory).
        services.Configure<MemoryOptions>(configuration.GetSection(MemoryOptions.SectionName));
        services.AddSingleton(provider =>
        {
            var providers = provider.GetRequiredService<IOptions<ProviderOptions>>().Value;
            object[] engines =
            [
                provider.GetRequiredKeyedService<ISpeechToText>(providers.SpeechToText),
                provider.GetRequiredKeyedService<ITextToSpeech>(providers.TextToSpeech),
            ];
            return new IdleEngines(engines, provider.GetRequiredService<IOptionsMonitor<MemoryOptions>>(), provider.GetRequiredService<TimeProvider>());
        });

        services.Configure<ClaudeCliOptions>(configuration.GetSection(ClaudeCliOptions.SectionName));
        services.PostConfigure<ClaudeCliOptions>(options => options.WorkingDirectory = AppPaths.BrainDirectory);
        services.AddKeyedSingleton<IAssistantBrain, ClaudeCliBrain>(ProviderKeys.ClaudeCli);
        services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.SectionName));
        services.AddKeyedSingleton<IAssistantBrain>(ProviderKeys.Ollama, (provider, _) => new OllamaBrain(
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, // a cold model can take a minute to load
            provider.GetRequiredService<IOptionsMonitor<OllamaOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSelectedProvider<IAssistantBrain>(providers => providers.Brain);
        AddVault(services, configuration);
    }

    /// <summary>
    /// The vault, as a registered device of jv-api on this PC, and everything answered on this PC:
    /// vault questions, form filling and private mode's local model.
    /// </summary>
    private static void AddVault(IServiceCollection services, IConfiguration configuration)
    {
        const string credential = "Jarvis/vault";
        services.Configure<VaultOptions>(configuration.GetSection(VaultOptions.SectionName));
        services.AddSingleton<Jarvis.Contracts.Vault.IVault>(provider => new JvApiVault(
            new HttpClient { Timeout = TimeSpan.FromMinutes(15) }, // a long form on a small GPU takes minutes
            provider.GetRequiredService<IOptionsMonitor<VaultOptions>>(),
            new VaultCredentials(
                () => Jarvis.Platform.Shell.CredentialVault.Read(credential),
                secret =>
                {
                    if (secret is null)
                    {
                        Jarvis.Platform.Shell.CredentialVault.Delete(credential);
                    }
                    else
                    {
                        Jarvis.Platform.Shell.CredentialVault.Save(credential, secret);
                    }
                }),
            provider.GetRequiredService<ILogger<JvApiVault>>()));
        services.AddSingleton(provider => new Jarvis.Core.Vault.FormFiller(
            new Jarvis.Core.Vault.FormServices(
                provider.GetRequiredService<Jarvis.Contracts.Vault.IVault>(),
                provider.GetRequiredService<Jarvis.Contracts.Browser.IBrowserLink>(),
                provider.GetRequiredService<INotchPresenter>(),
                provider.GetRequiredService<ICursorOverlay>(),
                provider.GetRequiredService<IAgentRuntime>(), // public research about the form's organisation only
                () => provider.GetRequiredService<ScreenPrivacy>().PrivateMode,
                provider.GetRequiredService<IMicrophone>(),
                provider.GetRequiredService<ISpeechToText>(),
                Path.Combine(AppPaths.DataDirectory, "research"),
                () => provider.GetRequiredService<IOptionsMonitor<DictationOptions>>().CurrentValue.Words,
                provider.GetRequiredService<IVocabularyStore>(),
                Jarvis.Platform.Text.EnglishWords.IsKnown),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<Jarvis.Core.Vault.FormFiller>>()));
        services.AddSingleton(provider => new LocalTurns(
            provider.GetRequiredService<Jarvis.Contracts.Vault.IVault>(),
            new Jarvis.Core.Vault.VaultBrain(provider.GetRequiredService<Jarvis.Contracts.Vault.IVault>()),
            provider.GetRequiredKeyedService<IAssistantBrain>(ProviderKeys.Ollama),
            provider.GetRequiredService<Jarvis.Core.Vault.FormFiller>(),
            new CalendarBrain(
                async (from, to, cancellationToken) =>
                {
                    var address = provider.GetRequiredService<IOptionsMonitor<ProactiveOptions>>().CurrentValue.CalendarAddress;
                    var time = provider.GetRequiredService<TimeProvider>();
                    return string.IsNullOrWhiteSpace(address) ? null
                        : await provider.GetRequiredService<CalendarFeed>().MeetingsAsync(address, from, to, time.LocalTimeZone, cancellationToken).ConfigureAwait(false);
                },
                provider.GetRequiredService<TimeProvider>()),
            () => provider.GetRequiredService<IOptionsMonitor<ProactiveOptions>>().CurrentValue.CalendarAddress,
            provider.GetRequiredService<Jarvis.Core.Google.GoogleCalendar>(),
            new Jarvis.Core.Assistant.LocalActions(provider.GetRequiredService<Jarvis.Core.Assistant.IApps>())));
    }

    /// <summary>Windows-specific devices and surfaces.</summary>
    private static void AddDesktop(IServiceCollection services)
    {
        services.AddSingleton<IMicrophone, WasapiMicrophone>();
        services.AddSingleton<IAudioPlayer, WasapiAudioPlayer>();
        services.AddSingleton<ISoundEffects, Jarvis.Core.Sound.SoundEffects>();
        services.AddSingleton<IScreenCapture, GdiScreenCapture>();
        services.AddSingleton<ITextInsertion, UiaTextInsertion>();
        services.AddSingleton<Jarvis.Contracts.Control.IComputerControl, ComputerControl>();
        services.AddSingleton<Jarvis.Platform.Browser.BrowserLinkServer>();
        services.AddSingleton<Jarvis.Contracts.Browser.IBrowserLink>(provider => provider.GetRequiredService<Jarvis.Platform.Browser.BrowserLinkServer>());
        services.AddHostedService(provider => provider.GetRequiredService<Jarvis.Platform.Browser.BrowserLinkServer>());

        services.AddSingleton<OverlayService>();
        services.AddSingleton<ICursorOverlay>(provider => provider.GetRequiredService<OverlayService>());
        services.AddSingleton<IScreenDrawing>(provider => provider.GetRequiredService<OverlayService>());
        services.AddHostedService(provider => provider.GetRequiredService<OverlayService>());

        // Everyone talks to the ambient notch, which shows the working light at rest while jobs run.
        services.AddSingleton<NotchPresenter>();
        services.AddSingleton<INotchPresenter>(provider => new AmbientNotch(provider.GetRequiredService<NotchPresenter>(), provider.GetRequiredService<IAgentJobs>()));
    }

    /// <summary>The voice loop and what drives it.</summary>
    private static void AddCompanion(IServiceCollection services)
    {
        services.AddSingleton(provider => new CompanionServices(
            provider.GetRequiredService<IMicrophone>(),
            provider.GetRequiredService<ISpeechToText>(),
            provider.GetRequiredService<IScreenCapture>(),
            provider.GetRequiredService<IAssistantBrain>(),
            provider.GetRequiredService<ITextToSpeech>(),
            provider.GetRequiredService<IAudioPlayer>(),
            provider.GetRequiredService<ICursorOverlay>(),
            provider.GetRequiredService<IScreenDrawing>(),
            provider.GetRequiredService<INotchPresenter>(),
            provider.GetRequiredService<IAgentJobs>(),
            provider.GetRequiredService<ISoundEffects>(),
            provider.GetRequiredService<IRoutineStore>(),
            provider.GetRequiredService<ScreenPrivacy>(),
            provider.GetRequiredService<ConversationLog>(),
            provider.GetRequiredService<Jarvis.Contracts.Control.IComputerControl>(),
            provider.GetRequiredService<Jarvis.Contracts.Browser.IBrowserLink>(),
            provider.GetRequiredService<IOptionsMonitor<BrowserOptions>>(),
            provider.GetRequiredService<LocalTurns>()));
        services.AddSingleton<ConversationLog>();
        services.AddSingleton(provider => new ScreenPrivacy(
            provider.GetRequiredService<IOptionsMonitor<PrivacyOptions>>(),
            Path.Combine(AppPaths.DataDirectory, "screen-looks.log"),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CompanionSession>();
        services.AddHostedService<ShortcutHook>();
    }

    /// <summary>
    /// Hold-to-dictate. Clean-up runs on its own small, fast model in its own warm process, so it
    /// never waits behind (or disturbs the conversation of) the main brain.
    /// </summary>
    private static void AddDictation(IServiceCollection services)
    {
        services.AddSingleton<IVocabularyStore, SettingsVocabularyStore>();
        services.AddKeyedSingleton<IAssistantBrain>(DictationCleanupKey, (provider, _) =>
        {
            var brain = provider.GetRequiredService<IOptions<ClaudeCliOptions>>().Value;
            var cleanup = new ClaudeCliOptions
            {
                Executable = brain.Executable,
                Model = provider.GetRequiredService<IOptions<DictationOptions>>().Value.CleanupModel,
                WorkingDirectory = brain.WorkingDirectory,
                FreshConversationAfter = TimeSpan.Zero, // every dictation stands alone
            };

            // Private mode cleans up on this PC, with its own memory-less local model.
            var local = new OllamaBrain(
                new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
                provider.GetRequiredService<IOptionsMonitor<OllamaOptions>>(),
                provider.GetRequiredService<TimeProvider>(),
                remember: false);
            var privacy = provider.GetRequiredService<ScreenPrivacy>();
            return new Jarvis.Core.Vault.PrivateModeBrain(
                new ClaudeCliBrain(Options.Create(cleanup), provider.GetRequiredService<ILogger<ClaudeCliBrain>>()), local, () => privacy.PrivateMode);
        });
        services.AddSingleton(provider => new DictationServices(
            provider.GetRequiredService<IMicrophone>(),
            provider.GetRequiredService<ISpeechToText>(),
            provider.GetRequiredService<ITextInsertion>(),
            provider.GetRequiredKeyedService<IAssistantBrain>(DictationCleanupKey),
            provider.GetRequiredService<INotchPresenter>(),
            provider.GetRequiredService<IVocabularyStore>(),
            provider.GetRequiredService<ICursorOverlay>()));
        services.AddSingleton<DictationSession>();
    }

    private const string DictationCleanupKey = "dictation-cleanup";

    /// <summary>Background jobs, each a Claude Code run in its own folder.</summary>
    private static void AddAgents(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.PostConfigure<AgentOptions>(options => options.JobsDirectory = AppPaths.JobsDirectory);
        services.AddSingleton<IAgentRuntime>(provider =>
        {
            var cli = provider.GetRequiredService<IOptions<ClaudeCliOptions>>().Value;
            var agents = provider.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new ClaudeCliAgentRuntime(Options.Create(new ClaudeCliOptions { Executable = cli.Executable, Model = agents.Model }));
        });
        services.AddSingleton<IAgentJobs, AgentJobManager>();
        services.AddSingleton<AgentAnnouncer>();
        services.AddSingleton(provider => new McpOAuth(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IConnectorStore, SettingsConnectorStore>();
        services.AddSingleton<Jarvis.Core.Google.IGoogleAccounts, SettingsGoogleAccounts>();
        services.AddSingleton<Jarvis.Core.Assistant.IApps, Jarvis.Platform.Shell.InstalledApps>();
        services.AddSingleton(provider => new Jarvis.Core.Google.GoogleCalendar(
            provider.GetRequiredService<Jarvis.Core.Google.IGoogleAccounts>(),
            provider.GetRequiredService<McpOAuth>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        services.AddSingleton<IRoutineStore, SettingsRoutineStore>();
        services.AddSingleton<RoutineScheduler>();
        services.AddHostedService<RoutineTimer>();
    }

    /// <summary>Jarvis speaking first: the morning brief, meeting heads-ups and (opt-in) suggestions.</summary>
    private static void AddProactive(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProactiveOptions>(configuration.GetSection(ProactiveOptions.SectionName));
        services.AddSingleton<Jarvis.Contracts.Activity.IActivityWatch, Jarvis.Platform.Shell.ActivityWatch>();
        services.AddSingleton(provider => new CalendarFeed(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ProactiveCoach(
            new ProactiveServices(
                provider.GetRequiredService<Jarvis.Contracts.Activity.IActivityWatch>(),
                provider.GetRequiredService<CalendarFeed>(),
                provider.GetRequiredService<IAgentJobs>(),
                provider.GetRequiredService<INotchPresenter>(),
                () =>
                {
                    // A fresh process per suggestion (an hour apart): nothing stays running in between.
                    var cli = provider.GetRequiredService<IOptions<ClaudeCliOptions>>().Value;
                    var once = new ClaudeCliOptions { Executable = cli.Executable, Model = cli.Model, WorkingDirectory = cli.WorkingDirectory };
                    return new ClaudeCliBrain(Options.Create(once), provider.GetRequiredService<ILogger<ClaudeCliBrain>>());
                },
                provider.GetRequiredService<ScreenPrivacy>(),
                provider.GetRequiredService<Jarvis.Core.PauseSwitch>(),
                (caption, spoken, actions) => provider.GetRequiredService<AgentAnnouncer>().SayAsync(caption, spoken, actions)),
            provider.GetRequiredService<IOptionsMonitor<ProactiveOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ProactiveCoach>>(),
            Path.Combine(AppPaths.DataDirectory, "morning.txt")));
        services.AddHostedService<ProactiveTimer>();
    }
}
