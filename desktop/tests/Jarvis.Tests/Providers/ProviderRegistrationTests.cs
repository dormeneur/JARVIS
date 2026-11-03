using Jarvis.Core.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests.Providers;

public sealed class ProviderRegistrationTests
{
    private interface IEngine
    {
        string Name { get; }
    }

    private sealed class FreeEngine : IEngine
    {
        public string Name => "free";
    }

    private sealed class PaidEngine : IEngine
    {
        public string Name => "paid";
    }

    [Theory]
    [InlineData("free")]
    [InlineData("paid")]
    public void Resolves_TheEngineSettingsSelect(string selected)
    {
        using var services = BuildServices(selected);

        Assert.Equal(selected, services.GetRequiredService<IEngine>().Name);
    }

    [Fact]
    public void UnknownKey_FailsWithActionableMessage()
    {
        using var services = BuildServices("missing");

        var error = Assert.Throws<InvalidOperationException>(services.GetRequiredService<IEngine>);
        Assert.Contains("'missing'", error.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildServices(string selectedKey)
        => new ServiceCollection()
            .Configure<ProviderOptions>(options => options.Brain = selectedKey)
            .AddKeyedSingleton<IEngine, FreeEngine>("free")
            .AddKeyedSingleton<IEngine, PaidEngine>("paid")
            .AddSelectedProvider<IEngine>(options => options.Brain)
            .BuildServiceProvider();
}
