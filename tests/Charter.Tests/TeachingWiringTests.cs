using Charter.Models;
using Charter.Teaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// <c>AddCharterTeaching()</c> registers section 13's generator and working defaults for the three
/// stores it needs, while standing down for anything the data layer registered first.
/// </summary>
public class TeachingWiringTests
{
    [Fact]
    public void TheGeneratorAndItsStoresResolveFromTheContainer()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<TeachingGenerator>(scope.ServiceProvider.GetRequiredService<ITeachingGenerator>());
        Assert.IsType<InMemoryConceptLedgerStore>(scope.ServiceProvider.GetRequiredService<IConceptLedgerStore>());
        Assert.IsType<InMemoryWalkthroughStore>(scope.ServiceProvider.GetRequiredService<IWalkthroughStore>());
        Assert.IsType<InMemoryExplainThisQuota>(scope.ServiceProvider.GetRequiredService<IExplainThisQuota>());
    }

    [Fact]
    public void ADataLayerImplementationRegisteredFirstWins()
    {
        var mine = new InMemoryConceptLedgerStore(TimeProvider.System);

        using var provider = Services(mine).BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IConceptLedgerStore>());
    }

    private static ServiceCollection Services(IConceptLedgerStore? concepts = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IModelClientFactory>(new RecapStubClientFactory(new RecapStubClient()));

        if (concepts is not null)
        {
            services.AddSingleton(concepts);
        }

        services.AddCharterTeaching();

        return services;
    }
}
