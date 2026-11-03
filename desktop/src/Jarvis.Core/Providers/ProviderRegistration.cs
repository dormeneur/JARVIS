using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Providers;

/// <summary>
/// Wires "pick an engine by settings key" on top of keyed DI services. Engines register themselves
/// under a key; consumers inject the plain interface and get whichever engine settings select.
/// </summary>
public static class ProviderRegistration
{
    /// <summary>
    /// Resolves unkeyed <typeparamref name="TService"/> to the keyed registration named by settings.
    /// The choice is read once, when the service is first resolved; restart to switch engines.
    /// </summary>
    public static IServiceCollection AddSelectedProvider<TService>(
        this IServiceCollection services,
        Func<ProviderOptions, string> selectKey,
        Func<IServiceProvider, TService, TService>? decorate = null)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(selectKey);
        return services.AddSingleton(serviceProvider =>
        {
            var key = selectKey(serviceProvider.GetRequiredService<IOptions<ProviderOptions>>().Value);
            var engine = serviceProvider.GetKeyedService<TService>(key)
                ?? throw new InvalidOperationException(
                    $"Settings select '{key}' for {typeof(TService).Name}, but no engine is registered under that key.");
            return decorate is null ? engine : decorate(serviceProvider, engine);
        });
    }
}
