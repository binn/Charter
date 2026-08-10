using Charter.Models;
using Charter.Recaps;
using Charter.VersionControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// <c>AddCharterRecap()</c> registers everything section 14 needs, and gives way to a host that
/// configured something first.
/// </summary>
public class RecapWiringTests
{
    [Fact]
    public void TheGeneratorAndPublisherResolveFromTheContainer()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<RecapGenerator>(scope.ServiceProvider.GetRequiredService<IRecapGenerator>());
        Assert.IsType<RecapPublisher>(scope.ServiceProvider.GetRequiredService<IRecapPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RecapPromptBuilder>());
    }

    [Fact]
    public void AHostThatSuppliedItsOwnOptionsKeepsThem()
    {
        var mine = new RecapOptions { MaxOutputTokens = 111 };

        using var provider = Services(mine).BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<RecapOptions>());
    }

    private static ServiceCollection Services(RecapOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IModelClientFactory>(new RecapStubClientFactory(new RecapStubClient()));
        services.AddSingleton<IVersionControlProviderRegistry>(new VersionControlProviderRegistry([]));

        // A host projecting CharterConfig registers before the extension; TryAdd then stands down.
        if (options is not null)
        {
            services.AddSingleton(options);
        }

        services.AddCharterRecap();

        return services;
    }
}
