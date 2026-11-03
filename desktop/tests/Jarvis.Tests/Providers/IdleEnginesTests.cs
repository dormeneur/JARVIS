using Jarvis.Contracts.Speech;
using Jarvis.Core.Providers;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Providers;

public sealed class IdleEnginesTests
{
    [Fact]
    public void Models_AreFreedAfterIdleMinutes_NeverWhileInUse_AndOnlyOnce()
    {
        var clock = new Clock { Now = DateTimeOffset.UnixEpoch };
        var engine = new Engine();
        using var idle = new IdleEngines([engine], new Monitor(new MemoryOptions { UnloadVoiceAfterMinutes = 5 }), clock);

        var lease = idle.Use();
        clock.Now += TimeSpan.FromMinutes(30);
        idle.Check();
        Assert.Equal(0, engine.Unloads); // still in use

        lease.Dispose();
        clock.Now += TimeSpan.FromMinutes(4);
        idle.Check();
        Assert.Equal(0, engine.Unloads); // not idle long enough

        clock.Now += TimeSpan.FromMinutes(2);
        idle.Check();
        idle.Check();
        Assert.Equal(1, engine.Unloads);
    }

    [Fact]
    public void Zero_KeepsModelsLoaded()
    {
        var clock = new Clock { Now = DateTimeOffset.UnixEpoch };
        var engine = new Engine();
        using var idle = new IdleEngines([engine], new Monitor(new MemoryOptions()), clock);

        clock.Now += TimeSpan.FromDays(1);
        idle.Check();

        Assert.Equal(0, engine.Unloads);
    }

    private sealed class Engine : IUnloadable
    {
        public int Unloads { get; private set; }

        public void Unload() => Unloads++;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor(MemoryOptions value) : IOptionsMonitor<MemoryOptions>
    {
        public MemoryOptions CurrentValue => value;

        public MemoryOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MemoryOptions, string?> listener) => null;
    }
}
