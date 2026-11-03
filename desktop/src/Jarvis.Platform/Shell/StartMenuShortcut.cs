using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Jarvis.Platform.Shell;

/// <summary>Keeps a "Jarvis" entry in the user's Start menu, so it can be found by search and launched.</summary>
public static class StartMenuShortcut
{
    /// <summary>Creates the shortcut, or repoints it if the exe moved. Leaves a correct one untouched.</summary>
    public static unsafe void Ensure(string name, string exePath)
    {
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name + ".lnk");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            if (File.Exists(shortcut))
            {
                fixed (char* path = shortcut)
                {
                    file.Load(path, STGM.STGM_READ);
                }

                Span<char> target = stackalloc char[1024];
                fixed (char* buffer = target)
                {
                    link.GetPath(buffer, target.Length, null, 0);
                }

                if (string.Equals(new string(target[..target.IndexOf('\0')]), exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            fixed (char* target = exePath, folder = Path.GetDirectoryName(exePath), description = "Your AI buddy next to the cursor.", path = shortcut)
            {
                link.SetPath(target);
                link.SetWorkingDirectory(folder);
                link.SetDescription(description);
                file.Save(path, true);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
