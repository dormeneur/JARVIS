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

    /// <summary>What was in front (app, window title, how long), for the vault's brain to learn from. Local only.</summary>
    Task ObserveAsync(IReadOnlyList<ActivityObservation> samples, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The next hunch to ask in this session (the stack first), or none. Unprompted asks are capped per
    /// day by the brain; <paramref name="onDemand"/> (the user asked for one) is not.
    /// </summary>
    Task<HunchBatch?> NextHunchAsync(string session, bool onDemand, CancellationToken cancellationToken) => Task.FromResult<HunchBatch?>(null);

    /// <summary>Answers a hunch: <paramref name="choice"/> 0/1/2 = agree/partly/disagree, or the user's own words. Returns the file it went to.</summary>
    Task<string?> AnswerHunchAsync(string id, int? choice, string? text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>
    /// The Profile file a new fact about the user belongs in (an existing one, or a new topic or
    /// folder), decided by the local brain. Throws <see cref="VaultException"/> for text it won't file (a password).
    /// </summary>
    Task<string?> PlaceFactAsync(string fact, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>The vault folder on this PC (for "Show in Explorer"), or null when the vault lives on another machine.</summary>
    Task<string?> LocalFolderAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>
    /// Where outside text belongs in the vault (the local brain decides): a note keeping the text word
    /// for word, plus any facts about the user for Profile. Nothing is written; the caller shows the
    /// plan and writes it if the user agrees. Throws <see cref="VaultException"/> for text it won't keep.
    /// </summary>
    Task<IngestPlan?> PlanIngestAsync(string text, string source, string? url, CancellationToken cancellationToken) => Task.FromResult<IngestPlan?>(null);

    /// <summary>The oldest job sent from the phone, now marked running by this PC; null when there's none.</summary>
    Task<RelayJob?> ClaimRelayJobAsync(CancellationToken cancellationToken) => Task.FromResult<RelayJob?>(null);

    /// <summary>Tells the phone how a job it sent went: "finished" or "failed", with the result.</summary>
    Task ReportRelayJobAsync(string id, string status, string result, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Puts a hunch on the stack: not asked again this session, first in the next.</summary>
    Task SkipHunchAsync(string id, string session, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What saving outside text would write: a note (path and full content) and facts for Profile files.</summary>
public sealed record IngestPlan(string NotePath, string NoteContent, IReadOnlyList<PlacedFact> Facts);

/// <summary>A fact about the user and the Profile file it belongs in.</summary>
public sealed record PlacedFact(string Path, string Fact);

/// <summary>A job the user sent from the phone for this PC to do.</summary>
public sealed record RelayJob(string Id, string Brief);

/// <summary>A run of time with one window in front.</summary>
public sealed record ActivityObservation(DateTimeOffset At, string App, string Title, int Seconds);

/// <summary>A guess about the user and a one-tap question to test it (three answers: agree, partly, disagree).</summary>
/// <param name="Evidence">What it noticed, for "Why?".</param>
public sealed record Hunch(string Id, string Question, IReadOnlyList<string> Options, IReadOnlyList<string> Evidence);

/// <param name="Stacked">Hunches the user put aside for later.</param>
public sealed record HunchBatch(Hunch? Hunch, int Stacked, int LeftToday);

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
