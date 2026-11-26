using System.Runtime.CompilerServices;
using Jarvis.Contracts.Browser;
using Jarvis.Contracts.Vault;
using Jarvis.Core;
using Jarvis.Core.Agents;
using Jarvis.Core.Proactive;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests.Agents;

public sealed class RelayCoachTests
{
    [Fact]
    public async Task AJobFromThePhone_RunsHere_AndItsResultGoesBack()
    {
        var (coach, vault, jobs) = Make(new RelayJob("r1", "Summarise my downloads folder"));
        await coach.TickAsync(CancellationToken.None);
        Assert.Equal(["Summarise my downloads folder"], jobs.Briefs);

        jobs.Finish(AgentJobStatus.Finished, "3 PDFs, 2 installers.");
        await vault.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([("r1", "finished", "3 PDFs, 2 installers.")], vault.Reports);
    }

    [Fact]
    public async Task AStoppedJob_IsReportedAsFailed()
    {
        var (coach, vault, jobs) = Make(new RelayJob("r1", "Tidy my desktop"));
        await coach.TickAsync(CancellationToken.None);
        jobs.Finish(AgentJobStatus.Cancelled, null);
        await vault.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("failed", vault.Reports.Single().Status);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("private")]
    public async Task Waits_WhenPausedOrPrivate(string why)
    {
        var (coach, vault, jobs) = Make(new RelayJob("r1", "Tidy my desktop"), paused: why == "paused", privateMode: why == "private");
        await coach.TickAsync(CancellationToken.None);
        Assert.Equal(0, vault.Claims);
        Assert.Empty(jobs.Briefs);
    }

    [Fact]
    public async Task OtherJobsEnding_AreNotReported()
    {
        var (coach, vault, jobs) = Make(null);
        await coach.TickAsync(CancellationToken.None);
        jobs.Raise(new AgentJobInfo(Guid.NewGuid(), "mine", string.Empty, AgentJobStatus.Finished, DateTimeOffset.UnixEpoch, Result: "done"));
        Assert.Empty(vault.Reports);
    }

    private static (RelayCoach, RelayVault, FakeJobs) Make(RelayJob? job, bool paused = false, bool privateMode = false)
    {
        var vault = new RelayVault(job);
        var jobs = new FakeJobs();
        var pause = new PauseSwitch();
        pause.Set(paused);
        return (new RelayCoach(vault, jobs, () => privateMode, pause, NullLogger<RelayCoach>.Instance), vault, jobs);
    }

    private sealed class FakeJobs : IAgentJobs
    {
        private AgentJobInfo? _last;

        public List<string> Briefs { get; } = [];

        public IReadOnlyList<AgentJobInfo> Jobs => [];

        public event Action<AgentJobInfo>? Changed;

        public AgentJobInfo Start(string brief)
        {
            Briefs.Add(brief);
            return _last = new AgentJobInfo(Guid.NewGuid(), brief, string.Empty, AgentJobStatus.Queued, DateTimeOffset.UnixEpoch);
        }

        public void Finish(AgentJobStatus status, string? result) => Raise(_last! with { Status = status, Result = result });

        public void Raise(AgentJobInfo job) => Changed?.Invoke(job);

        public void Cancel(Guid id)
        {
        }
    }

    private sealed class RelayVault(RelayJob? job) : IVault
    {
        private RelayJob? _job = job;

        public int Claims { get; private set; }

        public List<(string Id, string Status, string Result)> Reports { get; } = [];

        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConnected => true;

        public Task<RelayJob?> ClaimRelayJobAsync(CancellationToken cancellationToken)
        {
            Claims++;
            var next = _job;
            _job = null;
            return Task.FromResult(next);
        }

        public Task ReportRelayJobAsync(string id, string status, string result, CancellationToken cancellationToken)
        {
            Reports.Add((id, status, result));
            Reported.TrySetResult();
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string inviteCode, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Disconnect()
        {
        }

        public async IAsyncEnumerable<FormAnswer> AnswerFormAsync(string title, IReadOnlyList<FormQuestion> questions, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<string> ComposeAnswerAsync(string question, string title, string research, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<VaultEntry>> ListAsync(string folder, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VaultEntry>>([]);

        public Task UploadAsync(string path, string localFile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<string> AskAsync(string question, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
