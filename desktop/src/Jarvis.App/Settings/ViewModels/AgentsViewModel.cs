using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.App.Hosting;
using Jarvis.Core.Agents;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class AgentsViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    /// <param name="running">What this run of Jarvis started with (read once).</param>
    /// <param name="saved">What settings.json holds now.</param>
    public AgentsViewModel(SettingsStore store, IOptions<AgentOptions> running, IOptionsMonitor<AgentOptions> saved)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(saved);
        _store = store;
        var current = saved.CurrentValue;
        Model = current.Model;
        MaximumConcurrent = current.MaximumConcurrent;
        SpeakResults = saved.CurrentValue.SpeakResults;
        RestartNeeded = current.Model != running.Value.Model || current.MaximumConcurrent != running.Value.MaximumConcurrent;
        _ready = true;
    }

    public IReadOnlyList<string> Models { get; } = ["sonnet", "opus", "haiku"];

    [ObservableProperty]
    public partial string Model { get; set; }

    [ObservableProperty]
    public partial double MaximumConcurrent { get; set; }

    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    [RelayCommand]
    private static void OpenJobsFolder()
    {
        Directory.CreateDirectory(AppPaths.JobsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.JobsDirectory}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private static void Restart() => SettingsStore.RestartApp();

    partial void OnModelChanged(string value)
    {
        if (!_ready)
        {
            return;
        }

        _store.Set(AgentOptions.SectionName, nameof(AgentOptions.Model), value);
        RestartNeeded = true;
    }

    [ObservableProperty]
    public partial bool SpeakResults { get; set; }

    partial void OnSpeakResultsChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(AgentOptions.SectionName, nameof(AgentOptions.SpeakResults), value);
        }
    }

    partial void OnMaximumConcurrentChanged(double value)
    {
        if (!_ready)
        {
            return;
        }

        _store.Set(AgentOptions.SectionName, nameof(AgentOptions.MaximumConcurrent), (int)value);
        RestartNeeded = true;
    }
}
