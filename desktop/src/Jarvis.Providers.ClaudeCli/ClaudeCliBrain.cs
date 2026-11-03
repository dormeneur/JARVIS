using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Jarvis.Contracts.Brain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Providers.ClaudeCli;

/// <summary>
/// The brain, powered by the user's own Claude Code login. Keeps one `claude -p` process running in
/// stream-json mode so turns skip CLI start-up and share conversation context. The process is
/// isolated from the user's Claude Code setup: no settings, hooks, MCP servers, tools or CLAUDE.md.
/// </summary>
public sealed partial class ClaudeCliBrain : IAssistantBrain, IAsyncDisposable
{
    private readonly ClaudeCliOptions _options;
    private readonly ILogger<ClaudeCliBrain> _logger;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private int _forget;
    private ClaudeProcess? _process;
    private DateTimeOffset _lastUsed = DateTimeOffset.MinValue;

    public ClaudeCliBrain(IOptions<ClaudeCliOptions> options, ILogger<ClaudeCliBrain> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    public async Task PrewarmAsync(string instructions, CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProcess(instructions);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var finished = false;
        try
        {
            var process = EnsureProcess(turn.Instructions);
            _lastUsed = DateTimeOffset.UtcNow;
            await process.SendAsync(ClaudeProtocol.UserMessage(turn), cancellationToken).ConfigureAwait(false);

            await foreach (var line in process.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (ClaudeProtocol.Parse(line))
                {
                    case ClaudeOutput.Text text:
                        yield return new BrainEvent.TextDelta(text.Value);
                        break;

                    case ClaudeOutput.Result { IsError: true } failure:
                        finished = true;
                        throw new InvalidOperationException($"Claude Code reported an error: {failure.Message}");

                    case ClaudeOutput.Result:
                        finished = true;
                        if (_options.FreshConversationAfter == TimeSpan.Zero)
                        {
                            // One turn per conversation: swap in a new, already-warm process now,
                            // while the user is busy, rather than cold-starting on the next turn.
                            await ResetAsync().ConfigureAwait(false);
                            EnsureProcess(turn.Instructions);
                        }

                        yield return new BrainEvent.Completed();
                        yield break;
                }
            }

            throw new InvalidOperationException($"Claude Code exited mid-turn. {process.ErrorTail}".Trim());
        }
        finally
        {
            // A turn abandoned half-way leaves the CLI mid-reply; start clean next time.
            if (!finished)
            {
                await ResetAsync().ConfigureAwait(false);
            }

            _turnGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        _turnGate.Dispose();
    }

    public void ForgetConversation() => Volatile.Write(ref _forget, 1);

    private ClaudeProcess EnsureProcess(string instructions)
    {
        var stale = Interlocked.Exchange(ref _forget, 0) == 1
                    || (_options.FreshConversationAfter > TimeSpan.Zero
                        && _lastUsed != DateTimeOffset.MinValue
                        && DateTimeOffset.UtcNow - _lastUsed > _options.FreshConversationAfter);
        if (_process is { HasExited: false } running && running.Instructions == instructions && !stale)
        {
            return running;
        }

        _process?.Dispose();
        Directory.CreateDirectory(_options.WorkingDirectory);
        _process = ClaudeProcess.Start(_options, instructions);
        _lastUsed = DateTimeOffset.UtcNow;
        LogStarted(_options.Model);
        return _process;
    }

    private Task ResetAsync()
    {
        _process?.Dispose();
        _process = null;
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started Claude Code ({Model})")]
    private partial void LogStarted(string model);

    /// <summary>
    /// One running CLI: lines in on stdin, lines out on stdout, errors kept for diagnostics.
    /// ponytail: no Job Object. If Jarvis dies, the CLI sees stdin close and exits on its own
    /// (verified); add a kill-on-close job if a future CLI version stops doing that.
    /// </summary>
    private sealed class ClaudeProcess : IDisposable
    {
        private const int ErrorTailLength = 600;

        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly StringBuilder _errors = new();

        private ClaudeProcess(Process process, string instructions)
        {
            _process = process;
            Instructions = instructions;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    _lines.Writer.TryComplete();
                }
                else
                {
                    _lines.Writer.TryWrite(e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                lock (_errors)
                {
                    _errors.AppendLine(e.Data);
                    if (_errors.Length > ErrorTailLength * 2)
                    {
                        _errors.Remove(0, _errors.Length - ErrorTailLength);
                    }
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Instructions { get; }

        public bool HasExited => _process.HasExited;

        public string ErrorTail
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToString().Trim();
                }
            }
        }

        public static ClaudeProcess Start(ClaudeCliOptions options, string instructions)
        {
            var start = new ProcessStartInfo(options.Executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = options.WorkingDirectory,
            };
            foreach (var argument in new[]
            {
                "-p",
                "--input-format", "stream-json",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
                "--model", options.Model,
                "--system-prompt", instructions,
                "--tools", string.Empty,
                "--setting-sources", string.Empty,
                "--strict-mcp-config",
                "--no-session-persistence",
            })
            {
                start.ArgumentList.Add(argument);
            }

            var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start '{options.Executable}'. Is Claude Code installed and on PATH?");
            ChildProcessJob.Track(process);
            return new ClaudeProcess(process, instructions);
        }

        public async Task SendAsync(string line, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) => _lines.Reader.ReadAllAsync(cancellationToken);

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            _process.Dispose();
        }
    }
}

public sealed class ClaudeCliOptions
{
    public const string SectionName = "Brain:ClaudeCli";

    /// <summary>The Claude Code executable; resolved from PATH by default.</summary>
    public string Executable { get; set; } = "claude";

    /// <summary>Model alias or id passed to --model (sonnet, opus, haiku…).</summary>
    public string Model { get; set; } = "sonnet";

    /// <summary>An empty folder the CLI runs in, so no project files or CLAUDE.md are picked up.</summary>
    public string WorkingDirectory { get; set; } = "brain";

    /// <summary>
    /// After this much quiet, the next turn starts a fresh conversation. Zero means every turn is
    /// its own conversation (the next one's process is started as soon as a turn ends).
    /// </summary>
    public TimeSpan FreshConversationAfter { get; set; } = TimeSpan.FromMinutes(10);
}
