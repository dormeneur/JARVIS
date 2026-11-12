namespace Jarvis.Platform.Shell;

/// <summary>
/// The Windows CredentialVault API (Credential Manager targets such as "Jarvis/vault") on the
/// Keychain, so code shared with Windows (the settings stores) runs unchanged.
/// </summary>
public static class CredentialVault
{
    public static void Save(string target, string secret) => Jarvis.Platform.Mac.Shell.Keychain.Save(target, secret);

    public static string? Read(string target) => Jarvis.Platform.Mac.Shell.Keychain.Read(target);

    public static void Delete(string target) => Jarvis.Platform.Mac.Shell.Keychain.Delete(target);
}

/// <summary>
/// The Windows ProtectedFile API (DPAPI-encrypted files of sign-in tokens) on the Keychain: the
/// text is kept as a Keychain item named after the path, and nothing is written to disk.
/// </summary>
public static class ProtectedFile
{
    public static void Write(string path, string text) => Jarvis.Platform.Mac.Shell.Keychain.Save(Account(path), text);

    public static string? Read(string path) => Jarvis.Platform.Mac.Shell.Keychain.Read(Account(path));

    public static void Delete(string path) => Jarvis.Platform.Mac.Shell.Keychain.Delete(Account(path));

    private static string Account(string path) => "file:" + Path.GetFullPath(path);
}
