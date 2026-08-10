using System.Globalization;
using Charter.Auth.Providers;
using Microsoft.Extensions.Logging;

namespace Charter.Auth.Setup;

/// <summary>
/// On boot with zero users, issues the one-time setup token and writes it to stdout (section 30.1).
/// </summary>
/// <remarks>
/// <para>
/// Written through the logger, which Serilog's console sink puts on stdout, so the operator reads it
/// with <c>docker logs</c> exactly as section 30.1 describes. It is deliberately loud and
/// multi-line: a single-line token buried in structured JSON is one somebody scrolls past.
/// </para>
/// <para>
/// Nothing here creates an account. The token is the only thing that can, and it is never a default
/// password: there is no account to have a password until the token is redeemed.
/// </para>
/// </remarks>
public sealed class SetupHostedService : IHostedService
{
    private readonly IServiceProvider services;
    private readonly SetupTokenStore tokens;
    private readonly ILogger<SetupHostedService> logger;

    public SetupHostedService(
        IServiceProvider services,
        SetupTokenStore tokens,
        ILogger<SetupHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);

        this.services = services;
        this.tokens = tokens;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var mode = scope.ServiceProvider.GetRequiredService<SetupModeService>();
        var registry = scope.ServiceProvider.GetRequiredService<IdentityProviderRegistry>();

        if (registry.SamlConfiguredButUnavailable)
        {
            logger.LogWarning(
                "CHARTER_SAML_METADATA_URL is set, but SAML sign-in is not available in this build. " +
                "The identity provider seam is shaped for it; no SAML button will be offered.");
        }

        if (!await mode.IsSetupRequiredAsync(cancellationToken))
        {
            logger.LogInformation("Setup is already complete; the setup route is closed");
            return;
        }

        var token = tokens.Issue();

        logger.LogWarning(
            "\n" +
            "  ┌───────────────────────────────────────────────────────────────────────┐\n" +
            "  │  Charter has no users yet and is in setup mode.                       │\n" +
            "  │  Only the setup route is being served.                                │\n" +
            "  └───────────────────────────────────────────────────────────────────────┘\n" +
            "\n" +
            "  Open {SetupPath} and paste this one-time token:\n" +
            "\n" +
            "      {SetupToken}\n" +
            "\n" +
            "  It creates exactly one admin account and then expires (at {ExpiresAt}).\n",
            "/setup",
            token.Value,
            token.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
