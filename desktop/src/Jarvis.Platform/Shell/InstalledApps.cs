using System.Diagnostics;
using Jarvis.Core.Assistant;

namespace Jarvis.Platform.Shell;

/// <summary>
/// The apps Windows lists in Start (shell:AppsFolder: desktop and Store apps alike), read through
/// the Shell's own folder and launched the way Start launches them. The list is read on a
/// background thread and kept for ten minutes, so a lookup is instant.
/// </summary>
public sealed class InstalledApps : IApps
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private readonly Lock _gate = new();
    private IReadOnlyList<InstalledApp> _apps = [];
    private DateTime _readAt = DateTime.MinValue;

    public IReadOnlyList<InstalledApp> All
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _readAt > Fresh)
                {
                    _apps = Read();
                    _readAt = DateTime.UtcNow;
                }

                return _apps;
            }
        }
    }

    /// <summary>Virtual-key codes of the media keys a keyboard has.</summary>
    private static readonly Dictionary<string, byte> MediaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playpause"] = 0xB3, ["play"] = 0xB3, ["pause"] = 0xB3, ["next"] = 0xB0, ["previous"] = 0xB1, ["stop"] = 0xB2,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["mute"] = 0xAD,
    };

    public bool PressKey(string key)
    {
        if (!MediaKeys.TryGetValue(key.Replace(" ", string.Empty, StringComparison.Ordinal), out var code))
        {
            return false;
        }

        const uint KeyUp = 0x0002;
        const uint Extended = 0x0001;
        keybd_event(code, 0, Extended, 0);
        keybd_event(code, 0, Extended | KeyUp, 0);
        return true;
    }

    public void EnsureVolume(int percent)
    {
        try
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var speakers = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var volume = speakers.AudioEndpointVolume;
            volume.Mute = false;
            if (volume.MasterVolumeLevelScalar < percent / 100f)
            {
                volume.MasterVolumeLevelScalar = percent / 100f;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No speakers: nothing to turn up.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    public bool Launch(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (Path.IsPathFullyQualified(app.LaunchId) && File.Exists(app.LaunchId))
        {
            // Some desktop apps are listed by their program's path rather than an app id.
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(app.LaunchId) { UseShellExecute = true });
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        return Start("explorer.exe", @"shell:AppsFolder\" + app.LaunchId);
    }

    public bool Open(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (uri.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return Start("explorer.exe", uri);
        }

        // Only links and registered deep links (scheme:...), never a program path or a command.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.IsFile || parsed.IsUnc)
        {
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // no app for that kind of link
        }
    }

    private static bool Start(string program, string argument)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(program, [argument]) { UseShellExecute = false });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The AppsFolder's items, read on an STA thread (the Shell's objects want one).</summary>
    private static IReadOnlyList<InstalledApp> Read()
    {
        var apps = new List<InstalledApp>();
        var thread = new Thread(() =>
        {
            try
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, ["shell:AppsFolder"])!;
                foreach (var item in folder.Items())
                {
                    string name = item.Name;
                    string id = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    {
                        apps.Add(new InstalledApp(name, id));
                    }
                }
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
            {
                // No list: opening by name falls back to the brain, which can still click.
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return apps;
    }
}
