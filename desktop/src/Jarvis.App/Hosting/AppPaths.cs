namespace Jarvis.App.Hosting;

/// <summary>Where Jarvis keeps per-user state: %LOCALAPPDATA%\Jarvis.</summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>Downloaded speech models.</summary>
    public static string ModelsDirectory { get; } = Path.Combine(DataDirectory, "models");

    /// <summary>One folder per background job: its brief, output/, tmp/ and result.</summary>
    public static string JobsDirectory { get; } = Path.Combine(DataDirectory, "jobs");

    /// <summary>Empty folder the Claude CLI runs in, so it picks up no project files.</summary>
    public static string BrainDirectory { get; } = Path.Combine(DataDirectory, "brain");
}
