namespace Charter.Adapters;

/// <summary>
/// Reports what the adapter catalog loaded, once, at startup.
/// </summary>
/// <remarks>
/// Section 8 says unknown keys warn rather than fail. A warning nobody ever sees is the same as no
/// warning at all, so they are logged here — and on <see cref="IAdapterCatalog.Warnings"/> for the
/// admin UI — rather than being collected and dropped.
/// </remarks>
internal sealed class AdapterCatalogReport : IHostedService
{
    private readonly IAdapterCatalog _catalog;
    private readonly ILogger<AdapterCatalogReport> _logger;

    public AdapterCatalogReport(IAdapterCatalog catalog, ILogger<AdapterCatalogReport> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Loaded {AdapterCount} agent adapters ({AdapterIds}) from {Directories}",
            _catalog.Adapters.Count,
            string.Join(", ", _catalog.Adapters.Select(adapter => adapter.Id)),
            string.Join(", ", _catalog.Sources.Directories));

        foreach (var replaced in _catalog.Overrides)
        {
            _logger.LogInformation(
                "Adapter {AdapterId} from {SourcePath} overrides the one shipped at {ReplacedSourcePath}",
                replaced.Id,
                replaced.SourcePath,
                replaced.ReplacedSourcePath);
        }

        foreach (var warning in _catalog.Warnings)
        {
            _logger.LogWarning(
                "Adapter file {SourcePath}: '{Field}' {Message}",
                warning.SourcePath,
                warning.Field,
                warning.Message);
        }

        var degraded = _catalog.Adapters.Where(adapter => !adapter.IsStructured).Select(adapter => adapter.Id).ToList();
        if (degraded.Count > 0)
        {
            _logger.LogInformation(
                "Adapters {AdapterIds} emit unstructured output: pane 2 is a raw log and milestones are not "
                + "promoted for sessions using them. See docs/adapters.md",
                string.Join(", ", degraded));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
