using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace Jarvis.Platform.Shell;

/// <summary>
/// Secrets in Windows Credential Manager (generic credentials, this user only), so they are never
/// written to Jarvis's own files. Visible to the user under Control Panel → Credential Manager.
/// </summary>
public static unsafe class CredentialVault
{
    public static void Save(string target, string secret)
    {
        var blob = System.Text.Encoding.Unicode.GetBytes(secret);
        fixed (char* name = target)
        fixed (byte* data = blob)
        {
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE.CRED_TYPE_GENERIC,
                TargetName = name,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!PInvoke.CredWrite(&credential, 0))
            {
                throw new InvalidOperationException($"Could not save the secret to Credential Manager (error {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    public static string? Read(string target)
    {
        CREDENTIALW* credential;
        fixed (char* name = target)
        {
            if (!PInvoke.CredRead(name, CRED_TYPE.CRED_TYPE_GENERIC, 0, &credential))
            {
                return null;
            }
        }

        try
        {
            return new string((char*)credential->CredentialBlob, 0, (int)credential->CredentialBlobSize / sizeof(char));
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    public static void Delete(string target)
    {
        fixed (char* name = target)
        {
            PInvoke.CredDelete(name, CRED_TYPE.CRED_TYPE_GENERIC, 0);
        }
    }
}
