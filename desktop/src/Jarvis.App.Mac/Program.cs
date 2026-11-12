using Jarvis.App.Hosting;
using Jarvis.Platform.Mac;

namespace Jarvis.App.Mac;

internal static class Program
{
    /// <summary>
    /// Single instance (a lock file in the data folder): launching Jarvis again while it runs wakes
    /// the running one (it watches for the "wake" file) instead of starting another.
    /// "--after &lt;pid&gt;" waits for that process to exit first (Restart).
    /// </summary>
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if (Array.IndexOf(args, "--after") is var at and >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var previous))
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(previous).WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        var lockFile = Path.Combine(AppPaths.DataDirectory, "jarvis.lock");
        var wakeFile = Path.Combine(AppPaths.DataDirectory, "jarvis.wake");
        FileStream instance;
        try
        {
            instance = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            File.WriteAllText(wakeFile, DateTimeOffset.UtcNow.ToString("O"));
            return 0;
        }

        using (instance)
        {
            using var host = MacAppHost.Build();
            var app = new MacApp(host);
            using var wake = new FileSystemWatcher(AppPaths.DataDirectory, Path.GetFileName(wakeFile)) { EnableRaisingEvents = true };
            wake.Created += (_, _) => app.Activated();
            wake.Changed += (_, _) => app.Activated();
            MainThread.Run(app.Start); // runs the AppKit loop until Quit
        }

        MainThread.Exit(0); // the host is disposed; skip native teardown (see jv_exit)
        return 0;
    }
}
