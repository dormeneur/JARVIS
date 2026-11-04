using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Security.Cryptography;

namespace Jarvis.Platform.Shell;

/// <summary>
/// Text in a file encrypted with DPAPI for the current Windows user: only this user on this PC can
/// read it. For secrets too long for Credential Manager (OAuth tokens run past its 2.5 KB limit).
/// </summary>
public static unsafe class ProtectedFile
{
    public static void Write(string path, string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = plain)
        {
            var input = new CRYPT_INTEGER_BLOB { cbData = (uint)plain.Length, pbData = data };
            CRYPT_INTEGER_BLOB output;
            if (!PInvoke.CryptProtectData(&input, null, null, null, null, 0, &output))
            {
                throw new InvalidOperationException($"Could not encrypt the secret (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray());
            }
            finally
            {
                PInvoke.LocalFree((Windows.Win32.Foundation.HLOCAL)(nint)output.pbData);
            }
        }
    }

    /// <summary>The text, or <see langword="null"/> if the file is missing or can't be decrypted.</summary>
    public static string? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var encrypted = File.ReadAllBytes(path);
        fixed (byte* data = encrypted)
        {
            var input = new CRYPT_INTEGER_BLOB { cbData = (uint)encrypted.Length, pbData = data };
            CRYPT_INTEGER_BLOB output;
            if (!PInvoke.CryptUnprotectData(&input, null, null, null, null, 0, &output))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(output.pbData, (int)output.cbData);
            }
            finally
            {
                PInvoke.LocalFree((Windows.Win32.Foundation.HLOCAL)(nint)output.pbData);
            }
        }
    }

    /// <summary>Gone afterwards, whether or not it (or its folder) was there.</summary>
    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path); // File.Delete throws when the folder itself is missing (a first connection)
        }
    }
}
