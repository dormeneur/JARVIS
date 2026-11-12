namespace Jarvis.Platform.Mac.Shell;

/// <summary>
/// Secrets in the login Keychain (service "com.jarvis.desktop", account = the Windows credential
/// target, e.g. "Jarvis/vault"): the Mac counterpart of Credential Manager and DPAPI files.
/// Items are this-device-only, readable after first unlock.
/// </summary>
public static class Keychain
{
    public const string Service = "com.jarvis.desktop";

    public static string? Read(string account) => Native.Take(Native.KeychainRead(Service, account));

    public static void Save(string account, string secret)
    {
        var status = Native.KeychainSave(Service, account, secret);
        if (status != 0)
        {
            throw new IOException($"The Keychain refused to save {account} (OSStatus {status}).");
        }
    }

    public static void Delete(string account) => Native.KeychainDelete(Service, account);
}
