using Jarvis.Contracts.Browser;

namespace Jarvis.Contracts.Vault;

/// <summary>
/// The owner's JARVIS vault, reached through its server (jv-api) as a registered device. Every
/// answer drawn from it is made by the local brain on that server, never by a cloud model.
/// </summary>
public interface IVault
{
    /// <summary>This PC is registered with the vault server.</summary>
    bool IsConnected { get; }

    /// <summary>Registers this PC with a one-time invite code. Throws <see cref="VaultException"/> with a reason the user can act on.</summary>
    Task ConnectAsync(string inviteCode, CancellationToken cancellationToken);

    /// <summary>Forgets this PC's registration (the server keeps the device until it is revoked there).</summary>
    void Disconnect();

    /// <summary>An answer per question, in order, as the local brain finishes them.</summary>
    /// <param name="attachments">Vault files to read in full as well (the resume, this form's context files).</param>
    IAsyncEnumerable<FormAnswer> AnswerFormAsync(string title, IReadOnlyList<FormQuestion> questions, IReadOnlyList<string> attachments, CancellationToken cancellationToken);

    /// <summary>
    /// Drafts the user's answer to an open question (why this role…) from the vault, plus public
    /// <paramref name="research"/> about the organisation, streamed as text.
    /// </summary>
    IAsyncEnumerable<string> ComposeAnswerAsync(string question, string title, string research, IReadOnlyList<string> attachments, CancellationToken cancellationToken);

    /// <summary>What's in a vault folder, or nothing if it doesn't exist.</summary>
    Task<IReadOnlyList<VaultEntry>> ListAsync(string folder, CancellationToken cancellationToken);

    /// <summary>Copies a file from this PC into the vault (versioned, so phones sync it).</summary>
    Task UploadAsync(string path, string localFile, CancellationToken cancellationToken);

    /// <summary>Deletes a vault file, or a folder and everything in it.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>Asks the vault's brain to index changed files (e.g. answers just saved).</summary>
    Task ReindexAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A question answered from the vault, streamed as text.</summary>
    IAsyncEnumerable<string> AskAsync(string question, CancellationToken cancellationToken);

    /// <summary>A vault file's text, or <see langword="null"/> if it doesn't exist.</summary>
    Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a vault file; the server versions it, so phones sync it as usual.</summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken);
}

/// <param name="Answer">What to put in the form, or <see langword="null"/> when the vault doesn't say.</param>
/// <param name="Source">The vault file the answer came from.</param>
/// <param name="Open">The question asks for motivation or a pitch ("why us?"), so it can be drafted.</param>
public sealed record FormAnswer(string Id, string? Answer, string? Source, bool Open = false);

/// <summary>A file or folder in the vault.</summary>
public sealed record VaultEntry(string Path, string Name, bool IsFolder, DateTimeOffset? Modified);

/// <summary>The vault server couldn't be reached or refused; <see cref="Exception.Message"/> says why, for the user.</summary>
public sealed class VaultException : Exception
{
    public VaultException()
    {
    }

    public VaultException(string message)
        : base(message)
    {
    }

    public VaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
