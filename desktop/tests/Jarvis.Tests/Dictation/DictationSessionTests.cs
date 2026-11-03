using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Speech;
using Jarvis.Contracts.Text;
using Jarvis.Core.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Dictation;

public sealed class DictationSessionTests
{
    [Fact]
    public async Task HoldAndRelease_CleansUpAndTypesIntoTheFieldCapturedAtPress()
    {
        var fakes = new Fakes(transcript: "um so the meeting is at three", cleaned: "So the meeting is at 3.");
        await using var session = fakes.Create();

        session.BeginDictation();
        session.EndDictation();
        var inserted = await fakes.Insertion.Inserted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(("So the meeting is at 3.", "Notepad"), inserted);
        Assert.Contains("um so the meeting is at three", fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.Contains("App: Notepad", fakes.Brain.LastTurn.UserText, StringComparison.Ordinal);
        Assert.Equal(DictationCleaner.Instructions, fakes.Brain.LastTurn.Instructions);
        Assert.Equal(new NotchState.Dictating(), fakes.Notch.Shown[0]);
    }

    [Fact]
    public async Task CleanupOff_TypesTheRawTranscript()
    {
        var fakes = new Fakes(transcript: "raw words", cleaned: "unused") { Options = new DictationOptions { Cleanup = false } };
        await using var session = fakes.Create();

        session.BeginDictation();
        session.EndDictation();
        var inserted = await fakes.Insertion.Inserted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("raw words", inserted.Text);
        Assert.Null(fakes.Brain.LastTurn);
    }

    [Fact]
    public async Task Silence_TypesNothing()
    {
        var fakes = new Fakes(transcript: "  ", cleaned: "unused");
        await using var session = fakes.Create();

        session.BeginDictation();
        session.EndDictation();
        for (var i = 0; i < 100 && fakes.Notch.Shown[^1] != NotchState.Idle; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(NotchState.Idle, fakes.Notch.Shown[^1]);
        Assert.False(fakes.Insertion.Inserted.Task.IsCompleted);
    }

    [Fact]
    public async Task NoField_ShowsTheCopiedNotice()
    {
        var fakes = new Fakes(transcript: "hello", cleaned: "Hello.") { Outcome = InsertOutcome.CopiedToClipboard, Options = new DictationOptions { NoticeSeconds = 0 } };
        await using var session = fakes.Create();

        var outcome = await session.InsertTranscriptAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(InsertOutcome.CopiedToClipboard, outcome);
        Assert.Contains(new NotchState.Response("Copied, press Ctrl+V to paste: Hello."), fakes.Notch.Shown);
    }

    [Fact]
    public async Task FixingADictatedName_LearnsIt()
    {
        var fakes = new Fakes(transcript: "send it to pria", cleaned: "Send it to Pria.")
        {
            // baseline, user mid-edit, then settled on the fix
            FieldReads = ["Dear Sam, Send it to Pria.", "Dear Sam, Send it to Pri.", "Dear Sam, Send it to Priya.", "Dear Sam, Send it to Priya."],
        };
        await using var session = fakes.Create();
        var learned = new TaskCompletionSource<Correction>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.WordLearned += correction => learned.TrySetResult(correction);

        await session.InsertTranscriptAsync("send it to pria", TestContext.Current.CancellationToken);
        var correction = await learned.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(new Correction("Pria", "Priya"), correction);
        Assert.Equal(["Priya"], fakes.Vocabulary.Words);
    }

    [Theory]
    [InlineData("so the meeting is at three", "So the meeting is at 3.", "So the meeting is at 3.")]
    [InlineData("so the meeting is at three", "\"So the meeting is at 3.\"", "So the meeting is at 3.")]
    [InlineData("what time is it", "", "what time is it")]
    [InlineData("hi", "Hi! It is currently 3pm in London, and here is a long explanation of time zones you did not ask for.", "hi")]
    public void Accept_FallsBackToRawWhenTheModelGoesOffScript(string raw, string cleaned, string expected)
        => Assert.Equal(expected, DictationCleaner.Accept(raw, cleaned));

    private sealed class Fakes(string transcript, string cleaned)
    {
        public DictationOptions Options { get; init; } = new() { NoticeSeconds = 0 };

        public InsertOutcome Outcome { get; init; } = InsertOutcome.Inserted;

        public FakeBrain Brain { get; } = new(cleaned);

        public FakeNotch Notch { get; } = new();

        public FakeVocabulary Vocabulary { get; } = new();

        /// <summary>What reading the field returns, in order; the last value repeats.</summary>
        public string?[] FieldReads { get; init; } = [null];

        public FakeInsertion Insertion { get; private set; } = null!;

        public DictationSession Create()
        {
            Insertion = new FakeInsertion(Outcome, FieldReads);
            var services = new DictationServices(new FakeMicrophone(), new FakeStt(transcript), Insertion, Brain, Notch, Vocabulary, new NoCursor());
            return new DictationSession(services, new Monitor(Options), TimeProvider.System, NullLogger<DictationSession>.Instance);
        }
    }

    private sealed class FakeMicrophone : IMicrophone
    {
        public string DeviceName => "Test microphone";

        public IMicrophoneSession Start(AudioFormat format) => new Session();

        private sealed class Session : IMicrophoneSession
        {
            private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

            public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;

            public ValueTask DisposeAsync()
            {
                _audio.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeStt(string transcript) : ISpeechToText
    {
        public AudioFormat InputFormat => AudioFormat.SpeechMono16K;

        public Task<ITranscriptionSession> StartSessionAsync(TranscriptionRequest request, CancellationToken cancellationToken)
            => Task.FromResult<ITranscriptionSession>(new Session(transcript));

        private sealed class Session(string transcript) : ITranscriptionSession
        {
            public ValueTask WriteAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public Task<string> CompleteAsync(CancellationToken cancellationToken) => Task.FromResult(transcript);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeField : IFocusedField
    {
        public string AppName => "Notepad";

        public string? ExistingText => "Dear Sam,";
    }

    private sealed class FakeInsertion(InsertOutcome outcome, string?[] reads) : ITextInsertion
    {
        private int _read;

        public Task<string?> ReadAsync(IFocusedField field, CancellationToken cancellationToken)
            => Task.FromResult(reads[Math.Min(Interlocked.Increment(ref _read) - 1, reads.Length - 1)]);

        public TaskCompletionSource<(string Text, string App)> Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IFocusedField?> CaptureFocusAsync(CancellationToken cancellationToken) => Task.FromResult<IFocusedField?>(new FakeField());

        public Task<InsertOutcome> InsertAsync(IFocusedField? field, string text, CancellationToken cancellationToken)
        {
            Inserted.TrySetResult((text, field?.AppName ?? string.Empty));
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeVocabulary : IVocabularyStore
    {
        public List<string> Words { get; } = [];

        public bool Add(string word)
        {
            Words.Add(word);
            return true;
        }
    }

    private sealed class FakeBrain(string reply) : IAssistantBrain
    {
        public BrainTurn? LastTurn { get; private set; }

        public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastTurn = turn;
            await Task.Yield();
            yield return new BrainEvent.TextDelta(reply);
            yield return new BrainEvent.Completed();
        }
    }

    private sealed class FakeNotch : INotchPresenter
    {
        private readonly Lock _gate = new();
        private readonly List<NotchState> _shown = [];

        public List<NotchState> Shown
        {
            get
            {
                lock (_gate)
                {
                    return [.. _shown];
                }
            }
        }

        public void Show(NotchState state)
        {
            lock (_gate)
            {
                _shown.Add(state);
            }
        }
    }

    private sealed class Monitor(DictationOptions value) : IOptionsMonitor<DictationOptions>
    {
        public DictationOptions CurrentValue => value;

        public DictationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<DictationOptions, string?> listener) => null;
    }

    private sealed class NoCursor : Jarvis.Contracts.Overlay.ICursorOverlay
    {
        public event Action? Clicked
        {
            add { }
            remove { }
        }

        public void SetThinking(bool thinking)
        {
        }

        public void SetListening(bool listening)
        {
        }

        public Jarvis.Contracts.Overlay.ScreenHighlight? TakeHighlight() => null;

        public void ShowControlBorder(bool on)
        {
        }

        public void ShowLevel(double level)
        {
        }

        public void PointAt(int x, int y, string? label = null)
        {
        }

        public void ReturnToMouse()
        {
        }
    }
}
