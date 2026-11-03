using System.Runtime.CompilerServices;
using Jarvis.Contracts.Agents;
using Jarvis.Contracts.Notch;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Providers.ClaudeCli;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Agents;

public sealed class AgentTests : IDisposable
{
    private readonly string _jobs = Path.Combine(Path.GetTempPath(), "jarvis-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_jobs))
        {
            Directory.Delete(_jobs, recursive: true);
        }
    }

    [Fact]
    public void ReplyParser_ExtractsTheBrief_AndNeverSpeaksIt()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("[AGENT]Compare three laptops under 1000 dollars").Concat(parser.Feed(" and write a table.[/AGENT] I'm on it.")).Concat(parser.Complete()).ToList();

        Assert.Equal(
            [new ReplyItem.Delegate("Compare three laptops under 1000 dollars and write a table."), new ReplyItem.Line("I'm on it.")],
            items);
    }

    [Fact]
    public async Task Job_GetsItsOwnFolder_ReportsProgress_AndSavesTheResult()
    {
        var runtime = new ScriptedRuntime(new AgentEvent.ToolUse("WebSearch", "laptops 2026"), new AgentEvent.Finished("Done, it's in output/laptops.md."));
        using var manager = Manager(runtime);
        var updates = new List<AgentJobInfo>();
        var finished = new TaskCompletionSource<AgentJobInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += job =>
        {
            lock (updates)
            {
                updates.Add(job);
            }

            if (job.Status == AgentJobStatus.Finished)
            {
                finished.TrySetResult(job);
            }
        };

        var started = manager.Start("Compare three laptops under 1000 dollars and write a table.");
        var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("Compare three laptops under 1000 dollars…", started.Title);
        Assert.True(Directory.Exists(Path.Combine(started.Folder, "output")));
        Assert.True(Directory.Exists(Path.Combine(started.Folder, "tmp")));
        Assert.Equal("Compare three laptops under 1000 dollars and write a table.", File.ReadAllText(Path.Combine(started.Folder, "brief.md")));
        Assert.Equal("Done, it's in output/laptops.md.", File.ReadAllText(Path.Combine(started.Folder, "result.md")));
        Assert.Equal(Path.GetFullPath(started.Folder), runtime.LastJob!.WorkingDirectory);
        Assert.Contains(updates, job => job.Activity == "Searching the web for laptops 2026");
        Assert.Equal("Done, it's in output/laptops.md.", done.Result);
    }

    [Fact]
    public async Task OnlyThreeRunAtOnce_TheRestWait_AndCancelStopsAJob()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = Manager(new ScriptedRuntime(gate.Task, new AgentEvent.Finished("ok")));

        var jobs = Enumerable.Range(1, 4).Select(i => manager.Start($"job number {i}")).ToList();
        await WaitUntil(() => manager.Jobs.Count(job => job.Status == AgentJobStatus.Running) == 3);
        Assert.Single(manager.Jobs, job => job.Status == AgentJobStatus.Queued);

        manager.Cancel(jobs[0].Id);
        await WaitUntil(() => manager.Jobs.Single(job => job.Id == jobs[0].Id).Status == AgentJobStatus.Cancelled);
        gate.SetResult();
        await WaitUntil(() => manager.Jobs.Count(job => job.Status == AgentJobStatus.Finished) == 3);
    }

    [Fact]
    public async Task AmbientNotch_ShowsWorkingWhileJobsRun_ThenIdle()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = Manager(new ScriptedRuntime(gate.Task, new AgentEvent.Finished("ok")));
        var inner = new RecordingNotch();
        using var notch = new AmbientNotch(inner, manager);

        manager.Start("long job");
        notch.Show(NotchState.Idle);
        Assert.Equal(new NotchState.Working(), inner.Last);

        notch.Show(new NotchState.Thinking());
        Assert.Equal(new NotchState.Thinking(), inner.Last);

        notch.Show(NotchState.Idle);
        gate.SetResult();
        await WaitUntil(() => inner.Last == NotchState.Idle);
    }

    [Fact]
    public void FilesSavedBesideTheBrief_AreMovedIntoOutput()
    {
        var folder = Path.Combine(_jobs, "stray");
        Directory.CreateDirectory(Path.Combine(folder, "output"));
        File.WriteAllText(Path.Combine(folder, "brief.md"), "brief");
        File.WriteAllText(Path.Combine(folder, "Lisbon.md"), "plan");

        AgentJobManager.CollectStrayOutput(folder);

        Assert.True(File.Exists(Path.Combine(folder, "output", "Lisbon.md")));
        Assert.True(File.Exists(Path.Combine(folder, "brief.md")));
        Assert.False(File.Exists(Path.Combine(folder, "Lisbon.md")));
    }

    [Theory]
    [InlineData("Done, it's in **output/plan.md**. It covers five days.", "Done, it's in output/plan.md.")]
    [InlineData("The best pick is the Dell XPS 13 at $999! Details below.", "The best pick is the Dell XPS 13 at $999!")]
    [InlineData("No sentence end", "No sentence end")]
    public void Announcer_SpeaksOnlyTheFirstSentence(string result, string spoken)
        => Assert.Equal(spoken, AgentAnnouncer.FirstSentence(result));

    [Fact]
    public void Protocol_ParsesAgentNotesToolsAndResult()
    {
        var tools = ClaudeProtocol.ParseAgent("""{"type":"assistant","message":{"content":[{"type":"text","text":"Found five."},{"type":"tool_use","name":"WebFetch","input":{"url":"https://www.example.com/page"}},{"type":"tool_use","name":"Write","input":{"file_path":"C:\\jobs\\x\\output\\plan.md"}}]}}""");
        var result = ClaudeProtocol.ParseAgent("""{"type":"result","is_error":false,"result":"Done."}""");

        Assert.Equal(
            [
                new ClaudeOutput.Agent(new AgentEvent.Note("Found five.")),
                new ClaudeOutput.Agent(new AgentEvent.ToolUse("WebFetch", "www.example.com")),
                new ClaudeOutput.Agent(new AgentEvent.ToolUse("Write", "plan.md")),
            ],
            tools);
        Assert.Equal([new ClaudeOutput.Result(false, "Done.")], result);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private AgentJobManager Manager(IAgentRuntime runtime)
        => new(runtime, Options.Create(new AgentOptions { JobsDirectory = _jobs, MaximumConcurrent = 3 }), TimeProvider.System, NullLogger<AgentJobManager>.Instance);

    private sealed class ScriptedRuntime(Task? gate, params AgentEvent[] events) : IAgentRuntime
    {
        public ScriptedRuntime(params AgentEvent[] events)
            : this(null, events)
        {
        }

        public AgentJob? LastJob { get; private set; }

        public async IAsyncEnumerable<AgentEvent> RunAsync(AgentJob job, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastJob = job;
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken);
            }

            foreach (var agentEvent in events)
            {
                await Task.Yield();
                yield return agentEvent;
            }
        }
    }

    private sealed class RecordingNotch : INotchPresenter
    {
        private readonly Lock _gate = new();
        private NotchState _last = NotchState.Idle;

        public NotchState Last
        {
            get
            {
                lock (_gate)
                {
                    return _last;
                }
            }
        }

        public void Show(NotchState state)
        {
            lock (_gate)
            {
                _last = state;
            }
        }
    }
}
