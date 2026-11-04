using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Jarvis.Contracts.Agents;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Browser;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Contracts.Speech;
using Jarvis.Contracts.Text;
using Jarvis.Contracts.Vault;
using Jarvis.Core.Companion;
using Jarvis.Core.Dictation;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Vault;

/// <summary>Everything form filling talks to. Only the vault, browser and notch are required.</summary>
/// <param name="Research">Looks up the organisation behind a form on the web (Claude); never given vault data.</param>
/// <param name="PrivateMode">True while private mode is on: no research then.</param>
/// <param name="ResearchDirectory">An empty folder research jobs run in.</param>
/// <param name="Vocabulary">The speech dictionary's words, so "Say it" spells names right.</param>
/// <param name="Dictionary">Where "add to dictionary" puts words.</param>
/// <param name="IsWord">Whether a word is ordinary English (a spell checker), so only new words are offered.</param>
public sealed record FormServices(
    IVault Vault,
    IBrowserLink Browser,
    INotchPresenter Notch,
    ICursorOverlay? Cursor = null,
    IAgentRuntime? Research = null,
    Func<bool>? PrivateMode = null,
    IMicrophone? Microphone = null,
    ISpeechToText? SpeechToText = null,
    string? ResearchDirectory = null,
    Func<IReadOnlyList<string>>? Vocabulary = null,
    IVocabularyStore? Dictionary = null,
    Func<string, bool>? IsWord = null);

/// <summary>
/// Fills the browser's form from the vault. The extension reads the questions; answers the user
/// has given before come straight from <see cref="FormMemory"/>; the rest are answered by the
/// vault's brain on this PC (with the resume and this form's own context files read in full), and
/// open questions ("why us?") are drafted there from the vault plus public research about the
/// organisation (Claude sees only the form, never the vault). The notch shows a card per question.
/// Asked to, Jarvis fills the page in while answers are still coming (it never submits). Whatever
/// the user picks, copies, keeps or saves is learned, and each form is kept in its own vault folder
/// (<see cref="FormsFolder"/>) with its answers and context files.
/// </summary>
public sealed partial class FormFiller
{
    public const string FromMemory = "your past forms";
    public const string FromYou = "You";
    public const string Drafted = "a draft from your vault";
    public const string FormsFolder = "Profile/forms";
    public const string ResumeFolder = "Profile/resume";
    public const string AnswersFile = "answers.md";
    private static readonly TimeSpan ResearchLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DraftLimit = TimeSpan.FromSeconds(150);

    private readonly FormServices _services;
    private readonly TimeProvider _time;
    private readonly ILogger<FormFiller> _logger;
    private readonly SemaphoreSlim _memoryGate = new(1, 1);

    /// <summary>The title of the last form read, for the notch's home.</summary>
    public string? LastTitle { get; private set; }

    public FormFiller(FormServices services, TimeProvider time, ILogger<FormFiller> logger)
    {
        _services = services;
        _time = time;
        _logger = logger;
    }

    /// <summary>This form's folder in the vault: its title, made safe for a file name.</summary>
    public static string FolderFor(string title)
    {
        var safe = InvalidName().Replace(title ?? string.Empty, " ").Trim().Trim('.');
        safe = string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"{FormsFolder}/{(safe.Length == 0 ? "Untitled form" : safe[..Math.Min(safe.Length, 60)])}";
    }

    /// <summary>The words of an answer worth teaching the speech engine: names, brands, numbers.</summary>
    public static IReadOnlyList<string> DictionaryWords(string answer)
    {
        var words = WordPattern().Matches(answer ?? string.Empty).Select(match => match.Value).Where(word => word.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var special = words.Where(word => word.Any(char.IsUpper) || word.Any(char.IsDigit)).ToList();
        return special.Count > 0 ? special : words.Count <= 4 ? words : [];
    }

    /// <param name="fillIn">Fill the page in too (the user asked Jarvis to take control).</param>
    /// <param name="retry">Starts it again (the Try again button when the form can't be read).</param>
    public async Task RunAsync(bool fillIn, CancellationToken cancellationToken, Action? retry = null)
    {
        var (vault, browser, notch) = (_services.Vault, _services.Browser, _services.Notch);
        if (!vault.IsConnected)
        {
            notch.Show(new NotchState.Response("To fill forms from your vault, connect Jarvis to it first in Settings → Vault."));
            return;
        }

        if (!browser.IsConnected)
        {
            notch.Show(new NotchState.Response("I read forms through the Jarvis Browser Link extension. Pair it in Settings → Browser, then ask again."));
            return;
        }

        notch.Show(new NotchState.Thinking("Reading the form"));
        var read = await browser.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (read is not { Questions.Count: > 0 } form)
        {
            var why = read?.Problem ?? "I couldn't find any questions on this page.";
            notch.Show(new NotchState.Response(why, retry is null
                ? [new NotchAction("Close", () => notch.Show(NotchState.Idle))]
                : [new NotchAction("Try again", retry), new NotchAction("Close", () => notch.Show(NotchState.Idle))]));
            return;
        }

        LastTitle = form.Title;
        var memory = await LoadMemoryAsync(cancellationToken).ConfigureAwait(false);
        var attachments = await AttachmentsAsync(form.Title, cancellationToken).ConfigureAwait(false);

        // The panel outlives this call (Save, Fill it in…); Done or the next turn ends it.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await new FormRun(this, form, memory, attachments, stop).ExecuteAsync(fillIn).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Dismissed with Done.
        }
    }

    private async Task<FormMemory> LoadMemoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return FormMemory.Parse(await _services.Vault.ReadFileAsync(FormMemory.FilePath, cancellationToken).ConfigureAwait(false));
        }
        catch (VaultException exception)
        {
            LogMemoryFailed(exception.Message);
            return FormMemory.Parse(null);
        }
    }

    /// <summary>The newest resume and this form's own context files, read in full when answering.</summary>
    internal async Task<List<string>> AttachmentsAsync(string title, CancellationToken cancellationToken)
    {
        try
        {
            var resume = (await _services.Vault.ListAsync(ResumeFolder, cancellationToken).ConfigureAwait(false))
                .Where(entry => !entry.IsFolder).OrderByDescending(entry => entry.Modified).Take(1);
            var context = (await _services.Vault.ListAsync(FolderFor(title), cancellationToken).ConfigureAwait(false))
                .Where(entry => !entry.IsFolder && entry.Name != AnswersFile);
            return [.. resume.Concat(context).Select(entry => entry.Path).Take(10)];
        }
        catch (VaultException exception)
        {
            LogMemoryFailed(exception.Message);
            return [];
        }
    }

    /// <summary>Writes the memory to the vault, one write at a time.</summary>
    private async Task WriteMemoryAsync(string json)
    {
        await _memoryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _services.Vault.WriteFileAsync(FormMemory.FilePath, json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (VaultException exception)
        {
            LogMemoryFailed(exception.Message);
        }
        finally
        {
            _memoryGate.Release();
        }
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]+")]
    private static partial Regex InvalidName();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’.\-]*")]
    private static partial Regex WordPattern();

    // Counts only: the questions and answers themselves are never logged.
    [LoggerMessage(Level = LogLevel.Information, Message = "Form: {Questions} questions, {Remembered} remembered, {Answered} answered from the vault, {Drafted} drafted")]
    private partial void LogFilled(int questions, int remembered, int answered, int drafted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Filled {Filled} of {Questions} form fields")]
    private partial void LogFilledIn(int filled, int questions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved {Count} answers to the vault's standard answers")]
    private partial void LogSaved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Form storage unavailable: {Reason}")]
    private partial void LogMemoryFailed(string reason);

    // The question's id and a few words of why; never the answer.
    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't fill {Question}: {Reason}")]
    private partial void LogFillFailed(string question, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drafting an open answer failed")]
    private partial void LogDraftFailed(Exception exception);

    /// <summary>One form being filled: its cards, and the answering, drafting and filling going on at once.</summary>
    private sealed class FormRun
    {
        private readonly FormFiller _owner;
        private readonly BrowserForm _form;
        private readonly FormMemory _memory;
        private readonly List<string> _attachments;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _cancellation;
        private readonly Lock _gate = new();
        private readonly FormCard[] _cards;
        private readonly HashSet<string> _open = [];
        private readonly HashSet<string> _couldNotFill = [];
        private readonly Dictionary<string, FormCard> _beforeReload = [];
        private readonly SemaphoreSlim _answering = new(1, 1);
        private bool _answeringNow;
        private string? _afterFilling;
        private Channel<string>? _toFill; // the current filling pass
        private CancellationTokenSource? _fill;
        private Task _fillLoop = Task.CompletedTask;
        private Task<string>? _research;
        private int _passTotal;
        private int _passDone;

        public FormRun(FormFiller owner, BrowserForm form, FormMemory memory, List<string> attachments, CancellationTokenSource stop)
        {
            _owner = owner;
            _form = form;
            _memory = memory;
            _attachments = attachments;
            _stop = stop;
            _cancellation = stop.Token;
            var now = owner._time.GetUtcNow();
            _cards = [.. form.Questions.Select(question =>
            {
                var choices = Choices(question, now);
                return memory.Best(question, form.Title, now) is { } known
                    ? new FormCard(question.Id, question.Text, known.Value, FromMemory, FormCardState.Answered, choices)
                    : new FormCard(question.Id, question.Text, null, null, FormCardState.Pending, choices);
            })];
        }

        private FormServices Services => _owner._services;

        private string Folder => FolderFor(_form.Title);

        private int Ready => _cards.Count(card => card is { State: FormCardState.Answered, Answer: not null });

        private int Filled => _cards.Count(card => card.Filled);

        public async Task ExecuteAsync(bool fillIn)
        {
            var remembered = _cards.Count(card => card.State == FormCardState.Answered);
            _answeringNow = true; // a pass started now keeps taking answers as they arrive
            if (fillIn)
            {
                StartFilling();
            }

            var (answered, drafted) = await AnswerAsync([.. _form.Questions.Where((_, i) => _cards[i].State == FormCardState.Pending)]).ConfigureAwait(false);
            _owner.LogFilled(_cards.Length, remembered, answered, drafted);
            await SaveMemoryAsync().ConfigureAwait(false);
            await WriteHistoryAsync().ConfigureAwait(false);
            await _fillLoop.ConfigureAwait(false);
        }

        /// <summary>Answers these questions from the vault, then drafts the open ones it couldn't; one round at a time.</summary>
        private async Task<(int Answered, int Drafted)> AnswerAsync(IReadOnlyList<FormQuestion> misses)
        {
            await _answering.WaitAsync(_cancellation).ConfigureAwait(false);
            _answeringNow = true;
            var answered = 0;
            string? failure = null;
            try
            {
                Show();
                if (misses.Count > 0)
                {
                    try
                    {
                        await foreach (var answer in Services.Vault.AnswerFormAsync(_form.Title, misses, [.. _attachments], _cancellation).ConfigureAwait(false))
                        {
                            answered += Answer(answer) ? 1 : 0;
                            Show();
                        }
                    }
                    catch (VaultException exception)
                    {
                        failure = exception.Message;
                    }
                }

                Update(card => card.State == FormCardState.Pending ? card with { State = FormCardState.Unknown } : card);
                var drafted = await DraftOpenAnswersAsync().ConfigureAwait(false);
                return (answered, drafted);
            }
            finally
            {
                _answeringNow = false;
                lock (_gate)
                {
                    _toFill?.Writer.TryComplete(); // nothing more will be answered for this pass
                }

                Show(failure);
                _answering.Release();
            }
        }

        /// <returns>Whether the vault answered it.</returns>
        private bool Answer(FormAnswer answer)
        {
            var at = Array.FindIndex(_cards, card => card.Id == answer.Id);
            if (at < 0)
            {
                return false;
            }

            var question = _form.Questions[at];
            lock (_gate)
            {
                if (answer.Answer is { } value)
                {
                    _memory.Remember(question.Text, value, answer.Source, _form.Title, _owner._time.GetUtcNow(), used: false);
                }

                var draft = answer.Answer is null && answer.Open;
                if (answer.Answer is null && !draft && _beforeReload.Remove(answer.Id, out var before) && before.Answer is not null)
                {
                    _cards[at] = before; // reloading found nothing new: keep what it said before
                    return false;
                }

                _beforeReload.Remove(answer.Id);
                _cards[at] = _cards[at] with
                {
                    Answer = answer.Answer,
                    Source = answer.Source,
                    State = answer.Answer is not null ? FormCardState.Answered : draft ? FormCardState.Working : FormCardState.Unknown,
                    Note = draft ? "Waiting to be drafted…" : null,
                    Filled = _cards[at].Filled && _cards[at].Answer == answer.Answer,
                };
                if (draft)
                {
                    _open.Add(answer.Id);
                    _research ??= ResearchAsync(); // start now: it runs on Claude while the vault answers the rest
                }
            }

            if (answer.Answer is not null)
            {
                ReadyToFill(answer.Id);
            }

            return answer.Answer is not null;
        }

        /// <summary>Drafts each open question in turn (the local model does one thing at a time anyway).</summary>
        private async Task<int> DraftOpenAnswersAsync()
        {
            var drafted = 0;
            string[] open;
            lock (_gate)
            {
                open = [.. _open];
                _open.Clear();
            }

            foreach (var id in open)
            {
                var at = Array.FindIndex(_cards, card => card.Id == id);
                var research = await WithTicker(at, "Looking up the organisation with Claude", _research ?? Task.FromResult(string.Empty), ResearchLimit).ConfigureAwait(false) ?? string.Empty;
                var text = string.Empty;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
                limit.CancelAfter(DraftLimit);
                var draft = Task.Run(async () =>
                {
                    await foreach (var token in Services.Vault.ComposeAnswerAsync(_form.Questions[at].Text, _form.Title, research, [.. _attachments], limit.Token).ConfigureAwait(false))
                    {
                        text += token;
                        Set(at, card => card with { Answer = text.TrimStart() });
                    }

                    return text.Trim();
                }, limit.Token);
                try
                {
                    var written = await WithTicker(at, "Writing from your vault, on this PC", draft, DraftLimit).ConfigureAwait(false);
                    if (written is { Length: > 0 })
                    {
                        Set(at, card => card with { Answer = written, Source = Drafted, State = FormCardState.Answered, Note = "A draft: check it reads like you." });
                        ReadyToFill(id);
                        drafted++;
                        continue;
                    }

                    await limit.CancelAsync().ConfigureAwait(false); // took too long
                    await draft.ConfigureAwait(false);
                }
                catch (Exception exception) when (!_cancellation.IsCancellationRequested)
                {
                    _owner.LogDraftFailed(exception);
                }

                Set(at, card => card with { Answer = null, State = FormCardState.Unknown, Note = "Couldn't draft this one in time. Type or say it." });
            }

            return drafted;
        }

        /// <summary>Waits for <paramref name="work"/> while the card's note counts the seconds; null if it took too long.</summary>
        private async Task<T?> WithTicker<T>(int at, string doing, Task<T> work, TimeSpan limit)
            where T : class
        {
            var started = _owner._time.GetTimestamp();
            while (!work.IsCompleted)
            {
                var elapsed = _owner._time.GetElapsedTime(started);
                if (elapsed > limit)
                {
                    return null;
                }

                Set(at, card => card with { State = FormCardState.Working, Note = $"{doing}… {elapsed.TotalSeconds:F0} s" });
                await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(1), _owner._time, _cancellation)).ConfigureAwait(false);
                _cancellation.ThrowIfCancellationRequested();
            }

            return await work.ConfigureAwait(false);
        }

        /// <summary>Public facts about the organisation behind the form, from Claude with web search; empty in private mode.</summary>
        private async Task<string> ResearchAsync()
        {
            if (Services.Research is not { } research || Services.PrivateMode?.Invoke() == true || Services.ResearchDirectory is not { } root)
            {
                return string.Empty;
            }

            var folder = Path.Combine(root, _owner._time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var brief = $"""
                A job or application form is open in the user's browser. Work out which company or organisation it is for, then find
                3 to 6 short, true, recent facts about it that an applicant could mention (what it does, mission, products, notable news, culture).
                Form title: {_form.Title}
                Form description: {_form.Description}
                Form address: {_form.Url}
                Reply with the organisation's name on the first line, then the facts as short lines. If you can't tell which
                organisation it is, reply exactly UNKNOWN. Don't write any files.
                """;
            const string instructions = "You research organisations for a job applicant. Use web search, be accurate, never invent facts, and keep the reply short.";
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
                limit.CancelAfter(ResearchLimit);
                await foreach (var update in research.RunAsync(new AgentJob(instructions, brief, folder), limit.Token).ConfigureAwait(false))
                {
                    if (update is AgentEvent.Finished finished)
                    {
                        return finished.Result.Trim() is var result && !result.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase) ? result : string.Empty;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_cancellation.IsCancellationRequested)
            {
                _owner.LogDraftFailed(exception); // drafting goes on from the vault alone
            }

            return string.Empty;
        }

        private bool Fillable(FormCard card) => card is { State: FormCardState.Answered, Answer: not null, Filled: false } && !_couldNotFill.Contains(card.Id);

        private void ReadyToFill(string id)
        {
            lock (_gate)
            {
                if (_toFill?.Writer.TryWrite(id) == true)
                {
                    _passTotal++;
                }
            }
        }

        private void StartFilling()
        {
            lock (_gate)
            {
                if (_fill is not null || _cancellation.IsCancellationRequested)
                {
                    return;
                }

                _fill = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
                _toFill = Channel.CreateUnbounded<string>();
                foreach (var id in _couldNotFill)
                {
                    var at = Array.FindIndex(_cards, card => card.Id == id);
                    _cards[at] = _cards[at] with { Note = null }; // Try again: each gets another go
                }

                _couldNotFill.Clear();
                (_passTotal, _passDone, _afterFilling) = (0, 0, null);
                foreach (var card in _cards.Where(Fillable))
                {
                    _toFill.Writer.TryWrite(card.Id);
                    _passTotal++;
                }

                if (!_answeringNow)
                {
                    _toFill.Writer.TryComplete(); // only what's ready now
                }

                _fillLoop = FillLoopAsync(_toFill.Reader, _fill.Token);
            }

            Show();
        }

        private void StopFilling()
        {
            lock (_gate)
            {
                _fill?.Cancel();
            }
        }

        /// <summary>Fills each answered field as it becomes ready, with the control border glowing; never submits.</summary>
        private async Task FillLoopAsync(ChannelReader<string> ready, CancellationToken cancellationToken)
        {
            Services.Cursor?.ShowControlBorder(true);
            var stopped = false;
            try
            {
                await foreach (var id in ready.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var at = Array.FindIndex(_cards, card => card.Id == id);
                    var card = _cards[at];
                    if (card is { Answer: { } value, State: FormCardState.Answered, Filled: false })
                    {
                        var filled = await FillOneAsync(id, value, cancellationToken).ConfigureAwait(false);
                        if (filled is { Filled: true })
                        {
                            if (filled is { X: { } x, Y: { } y })
                            {
                                Services.Cursor?.PointAt(x, y, "Filling");
                            }

                            lock (_gate)
                            {
                                _memory.Remember(card.Question, value, card.Source, _form.Title, _owner._time.GetUtcNow(), used: true);
                            }

                            Set(at, current => current.Answer == value ? current with { Filled = true } : current);
                        }
                        else
                        {
                            lock (_gate)
                            {
                                _couldNotFill.Add(id);
                            }

                            _owner.LogFillFailed(id, filled?.Reason ?? "the browser didn't answer");
                            Set(at, current => current with { Note = $"Couldn't put this in the page ({filled?.Reason ?? "the browser didn't answer"}). Copy it instead." });
                        }
                    }

                    Interlocked.Increment(ref _passDone);
                    Show();
                }

                await ReadBackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
            {
                stopped = true;
            }
            finally
            {
                Services.Cursor?.ShowControlBorder(false);
                Services.Cursor?.ReturnToMouse();
                var couldNot = _couldNotFill.Count;
                _afterFilling = stopped
                    ? $"Stopped after filling {Filled} of {_cards.Length}."
                    : $"Filled {Filled} of {_cards.Length}{(couldNot > 0 ? $" · {couldNot} to copy by hand" : string.Empty)} · check the page, then submit it yourself.";
                _owner.LogFilledIn(Filled, _cards.Length);
                lock (_gate)
                {
                    _fill?.Dispose();
                    _fill = null;
                    _toFill = null;
                }

                await SaveMemoryAsync().ConfigureAwait(false); // what was filled in counts as used
                if (!_cancellation.IsCancellationRequested)
                {
                    Show();
                    _ = WriteHistoryAsync();
                }
            }
        }

        /// <summary>
        /// One field, put in by the extension on that exact element (found by its HTML id, name or
        /// label, never by screen position) and read back. If the page lost the field, the form is
        /// read again once and it's tried once more. Nothing is ever typed at the screen.
        /// </summary>
        private async Task<FormFill?> FillOneAsync(string id, string value, CancellationToken cancellationToken)
        {
            var browser = Services.Browser;
            var filled = await browser.FillFormAsync(id, value, cancellationToken).ConfigureAwait(false);
            if (filled is { Filled: false, Reason: { } reason } && reason.Contains("gone", StringComparison.Ordinal)
                && await browser.ReadFormAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                filled = await browser.FillFormAsync(id, value, cancellationToken).ConfigureAwait(false);
            }

            return filled;
        }

        /// <summary>
        /// Pages that redraw after every change (PeopleSoft, many job portals) can drop a value put in
        /// earlier: once the pass is done, every filled field is read back, and one that lost its value
        /// is put back once; if it still won't stay, it's not counted and Try again offers another go.
        /// </summary>
        private async Task ReadBackAsync(CancellationToken cancellationToken)
        {
            foreach (var card in _cards.Where(card => card is { Filled: true, Answer: not null }).ToList())
            {
                if (await Services.Browser.CheckFormAsync(card.Id, card.Answer!, cancellationToken).ConfigureAwait(false)
                    || await FillOneAsync(card.Id, card.Answer!, cancellationToken).ConfigureAwait(false) is { Filled: true })
                {
                    continue;
                }

                lock (_gate)
                {
                    _couldNotFill.Add(card.Id);
                }

                Set(Array.FindIndex(_cards, c => c.Id == card.Id), current => current with { Filled = false, Note = "The page cleared this after it went in. Try again, or copy it." });
                Show();
            }
        }

        /// <summary>The user picked, copied, kept or typed a value: learn it, and (re)fill the field if Jarvis is filling.</summary>
        private void Used(string id, string value)
        {
            var at = Array.FindIndex(_cards, card => card.Id == id);
            if (at < 0 || value.Trim().Length == 0)
            {
                return;
            }

            var changed = false;
            lock (_gate)
            {
                _memory.Remember(_cards[at].Question, value, _cards[at].Source ?? FromYou, _form.Title, _owner._time.GetUtcNow(), used: true);
                if (_cards[at].Answer != value)
                {
                    changed = true;
                    _couldNotFill.Remove(id);
                    _cards[at] = _cards[at] with { Answer = value, Source = _cards[at].Source ?? FromYou, State = FormCardState.Answered, Filled = false, Note = null };
                }
            }

            if (changed)
            {
                ReadyToFill(id); // while filling, the page gets the new choice too
            }

            _ = SaveMemoryAsync();
            Show();
        }

        private async Task<string> SaveAsync(IReadOnlyList<FormEdit> edits)
        {
            try
            {
                var vault = Services.Vault;
                var existing = await vault.ReadFileAsync(StandardAnswers.FilePath, CancellationToken.None).ConfigureAwait(false);
                await vault.WriteFileAsync(StandardAnswers.FilePath, StandardAnswers.Merge(existing, edits), CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var edit in edits)
                    {
                        _memory.Remember(edit.Question, edit.Answer, FromYou, _form.Title, _owner._time.GetUtcNow(), used: true);
                    }
                }

                await SaveMemoryAsync().ConfigureAwait(false);
                await WriteHistoryAsync().ConfigureAwait(false);
                await vault.ReindexAsync(CancellationToken.None).ConfigureAwait(false); // so the vault's brain finds them too
                _owner.LogSaved(edits.Count);
                return $"Saved {(edits.Count == 1 ? "1 answer" : $"{edits.Count} answers")} to your vault.";
            }
            catch (VaultException exception)
            {
                return $"Couldn't save: {exception.Message}";
            }
        }

        /// <summary>Records until <paramref name="stop"/>, transcribed on this PC with the dictionary and this card's known answers as hints.</summary>
        private async Task<string> HearAsync(string id, Task stop)
        {
            if (Services.Microphone is not { } microphone || Services.SpeechToText is not { } speech)
            {
                return string.Empty;
            }

            var card = _cards.FirstOrDefault(candidate => candidate.Id == id);
            var hints = (Services.Vocabulary?.Invoke() ?? [])
                .Concat((card?.Choices ?? []).Concat(card?.Answer is { } answer ? [answer] : []).SelectMany(DictionaryWords))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(DictationOptions.MaximumWords)
                .ToList();
            Recording recording;
            try
            {
                recording = Recording.Start(microphone, speech, new TranscriptionRequest(null, hints), _ => { }, _owner._logger);
            }
            catch (MicrophoneBlockedException)
            {
                return string.Empty;
            }

            await using (recording.ConfigureAwait(false))
            {
                await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMinutes(2), _owner._time, _cancellation)).ConfigureAwait(false);
                return (await recording.FinishAsync(_cancellation).ConfigureAwait(false)).Trim();
            }
        }

        /// <summary>Teaches the speech engine the names and numbers in an answer.</summary>
        private string Remember(string answer)
        {
            if (Services.Dictionary is not { } dictionary)
            {
                return "The dictionary isn't available.";
            }

            var added = NewWords(answer).Where(dictionary.Add).ToList();
            return added.Count == 0 ? "No new words here: your dictionary and English already have them." : $"Added {string.Join(", ", added)} to your dictionary.";
        }

        /// <summary>Words of the answer the dictionary doesn't have and English doesn't either (names, brands, jargon).</summary>
        private IReadOnlyList<string> NewWords(string answer)
        {
            var known = new HashSet<string>(Services.Vocabulary?.Invoke() ?? [], StringComparer.OrdinalIgnoreCase);
            return [.. WordPattern().Matches(answer ?? string.Empty)
                .Select(match => match.Value.Trim('.', '\'', '’', '-'))
                .Where(word => word.Length >= 2 && word.Any(char.IsLetter) && !known.Contains(word))
                .Where(word => Services.IsWord?.Invoke(word) != true)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        /// <summary>Answers again from the vault with the latest files: one card, or every card the user hasn't typed over.</summary>
        private void Reload(string? id) => ReloadWhere((question, _) => id is null || question.Id == id);

        /// <summary>New files arrived: answer again what's still unknown, with them read in full.</summary>
        private void ReloadUnknown() => ReloadWhere((_, card) => card.State == FormCardState.Unknown);

        private void ReloadWhere(Func<FormQuestion, FormCard, bool> which)
        {
            FormQuestion[] questions;
            lock (_gate)
            {
                questions = [.. _form.Questions.Where((question, i) => which(question, _cards[i]) && _cards[i].State != FormCardState.Working)];
                foreach (var question in questions)
                {
                    var at = Array.FindIndex(_cards, card => card.Id == question.Id);
                    _beforeReload[question.Id] = _cards[at];
                    _cards[at] = _cards[at] with { State = FormCardState.Pending, Note = null };
                }
            }

            _ = ReloadAsync(questions);
        }

        private async Task ReloadAsync(FormQuestion[] questions)
        {
            try
            {
                var latest = await _owner.AttachmentsAsync(_form.Title, _cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var path in latest.Where(path => !_attachments.Contains(path)))
                    {
                        _attachments.Add(path); // files added outside the panel count too
                    }
                }

                await AnswerAsync(questions).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var (id, before) in _beforeReload)
                    {
                        var at = Array.FindIndex(_cards, card => card.Id == id);
                        if (_cards[at].State == FormCardState.Unknown && before.Answer is not null)
                        {
                            _cards[at] = before;
                        }
                    }

                    _beforeReload.Clear();
                }

                await SaveMemoryAsync().ConfigureAwait(false);
                await WriteHistoryAsync().ConfigureAwait(false);
                Show();
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // Dismissed meanwhile.
            }
        }

        private async Task<FormLibrary> LoadLibraryAsync()
        {
            static StoredFile Stored(VaultEntry entry) => new(entry.Path, entry.Name, entry.Modified);
            var vault = Services.Vault;
            var resumes = await vault.ListAsync(ResumeFolder, CancellationToken.None).ConfigureAwait(false);
            var context = await vault.ListAsync(Folder, CancellationToken.None).ConfigureAwait(false);
            var history = await vault.ListAsync(FormsFolder, CancellationToken.None).ConfigureAwait(false);
            return new FormLibrary(
                [.. resumes.Where(entry => !entry.IsFolder).OrderByDescending(entry => entry.Modified).Select(Stored)],
                [.. context.Where(entry => !entry.IsFolder && entry.Name != AnswersFile).OrderBy(entry => entry.Name).Select(Stored)],
                [.. history.Where(entry => entry.IsFolder).OrderByDescending(entry => entry.Modified).Select(Stored)]);
        }

        private async Task<string> AddContextAsync(IReadOnlyList<string> localFiles)
        {
            try
            {
                foreach (var file in localFiles)
                {
                    var path = $"{Folder}/{Path.GetFileName(file)}";
                    await Services.Vault.UploadAsync(path, file, CancellationToken.None).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_attachments.Contains(path))
                        {
                            _attachments.Add(path);
                        }
                    }
                }

                ReloadUnknown();
                return $"Added {(localFiles.Count == 1 ? Path.GetFileName(localFiles[0]) : $"{localFiles.Count} files")}. Answering again with {(localFiles.Count == 1 ? "it" : "them")}…";
            }
            catch (Exception exception) when (exception is VaultException or IOException or UnauthorizedAccessException)
            {
                return $"Couldn't add that: {exception.Message}";
            }
        }

        private async Task<string> AddResumeAsync(string localFile)
        {
            try
            {
                var path = $"{ResumeFolder}/{Path.GetFileName(localFile)}";
                await Services.Vault.UploadAsync(path, localFile, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _attachments.RemoveAll(attached => attached.StartsWith(ResumeFolder + "/", StringComparison.Ordinal));
                    _attachments.Insert(0, path);
                }

                ReloadUnknown();
                _ = Services.Vault.ReindexAsync(CancellationToken.None);
                return $"Saved {Path.GetFileName(localFile)} as your latest resume. Answering again with it…";
            }
            catch (Exception exception) when (exception is VaultException or IOException or UnauthorizedAccessException)
            {
                return $"Couldn't add that: {exception.Message}";
            }
        }

        private async Task<string> DeleteAsync(string path)
        {
            try
            {
                await Services.Vault.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _attachments.RemoveAll(attached => attached == path || attached.StartsWith(path + "/", StringComparison.Ordinal));
                }

                return $"Deleted {path[(path.LastIndexOf('/') + 1)..]}.";
            }
            catch (VaultException exception)
            {
                return $"Couldn't delete it: {exception.Message}";
            }
        }

        /// <summary>This form's answers as Markdown in its vault folder, so past forms can be looked at (and deleted).</summary>
        private async Task WriteHistoryAsync()
        {
            var text = new StringBuilder($"# {_form.Title}\n\n{_form.Url}\n");
            lock (_gate)
            {
                foreach (var card in _cards.Where(card => card.Answer is not null && card.State == FormCardState.Answered))
                {
                    text.Append("\n## ").Append(card.Question.ReplaceLineEndings(" ")).Append('\n').Append(card.Answer).Append('\n');
                }
            }

            try
            {
                await Services.Vault.WriteFileAsync($"{Folder}/{AnswersFile}", text.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (VaultException exception)
            {
                _owner.LogMemoryFailed(exception.Message);
            }
        }

        private async Task SaveMemoryAsync()
        {
            string? json;
            lock (_gate)
            {
                json = _memory.Dirty ? _memory.ToJson() : null;
            }

            if (json is not null)
            {
                await _owner.WriteMemoryAsync(json).ConfigureAwait(false);
            }
        }

        private IReadOnlyList<string> Choices(FormQuestion question, DateTimeOffset now)
            => [.. _memory.Choices(question, now).Concat(question.Options).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)];

        private void Set(int at, Func<FormCard, FormCard> change)
        {
            lock (_gate)
            {
                _cards[at] = change(_cards[at]);
            }

            Show();
        }

        private void Update(Func<FormCard, FormCard> change)
        {
            lock (_gate)
            {
                for (var i = 0; i < _cards.Length; i++)
                {
                    _cards[i] = change(_cards[i]);
                }
            }
        }

        /// <summary>One status line: filling progress, how the filling went, or how many answers are ready.</summary>
        private string Status()
        {
            var ready = Ready;
            if (_fill is not null)
            {
                return $"Filling the page… {Filled} in so far";
            }

            if (_answeringNow)
            {
                return $"Answering from your vault on this PC… {ready} of {_cards.Length} ready";
            }

            if (_afterFilling is { } filled)
            {
                return filled;
            }

            return ready == _cards.Length ? $"All {ready} ready." : $"{ready} of {_cards.Length} ready · {_cards.Length - ready} need you";
        }

        private void Show(string? note = null)
        {
            if (_cancellation.IsCancellationRequested)
            {
                return;
            }

            FormCard[] cards;
            bool filling;
            int fillable;
            double? progress;
            int retry;
            lock (_gate)
            {
                cards = [.. _cards];
                filling = _fill is not null;
                fillable = _cards.Count(Fillable);
                progress = filling ? Math.Clamp((double)_passDone / Math.Max(_passTotal, 1), 0, 1) : null;
                retry = _cards.Count(card => card is { State: FormCardState.Answered, Answer: not null, Filled: false } && _couldNotFill.Contains(card.Id));
            }

            var status = note is null ? Status() : $"{note} {Status()}";
            Services.Notch.Show(new NotchState.Form(_form.Title, cards, status, new FormActions(
                SaveAsync,
                () =>
                {
                    _stop.Cancel();
                    Services.Notch.Show(NotchState.Idle);
                },
                filling ? null : StartFilling,
                filling ? StopFilling : null,
                Used,
                Services.Microphone is null ? null : HearAsync,
                Remember,
                new FormFiles(LoadLibraryAsync, AddContextAsync, AddResumeAsync, DeleteAsync),
                Reload,
                NewWords), progress, fillable, retry));
        }
    }
}
