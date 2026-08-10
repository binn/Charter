namespace Charter.Models;

/// <summary>Resolves a provider to the registered client that speaks it. Section 20b.1.</summary>
public sealed class ModelClientFactory : IModelClientFactory
{
    private readonly Dictionary<ModelProvider, IModelClient> _clients = [];

    /// <summary>Creates a factory over the registered clients.</summary>
    /// <exception cref="ArgumentException">Two clients claim the same provider.</exception>
    public ModelClientFactory(IEnumerable<IModelClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);

        foreach (var client in clients)
        {
            foreach (var provider in client.SupportedProviders)
            {
                if (!_clients.TryAdd(provider, client))
                {
                    throw new ArgumentException(
                        $"Two model clients both claim provider '{provider}': " +
                        $"{_clients[provider].GetType().Name} and {client.GetType().Name}.",
                        nameof(clients));
                }
            }
        }
    }

    /// <inheritdoc />
    public IModelClient GetClient(ModelIdentifier model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return GetClient(model.Provider);
    }

    /// <inheritdoc />
    public IModelClient GetClient(ModelProvider provider) =>
        Find(provider)
        ?? throw new ModelClientException(
            $"No model client is registered for provider '{provider}'.",
            provider);

    /// <inheritdoc />
    public IModelClient? Find(ModelProvider provider) =>
        _clients.TryGetValue(provider, out var client) ? client : null;
}
