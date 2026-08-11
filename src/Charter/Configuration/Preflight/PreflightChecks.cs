using System.Globalization;
using Npgsql;

namespace Charter.Configuration.Preflight;

/// <summary>
/// Secret keys are long enough and are not the same key (sections 4.2, 20b.2).
/// </summary>
/// <remarks>
/// Pure: the parser already rejects a short or duplicated key, so this check restates the guarantee
/// in the first-run results an operator reads. It costs nothing and it means the printed list covers
/// every item section 30.1 asks for rather than quietly omitting the one that already passed.
/// </remarks>
public sealed class KeyStrengthPreflightCheck(CharterConfig config) : PurePreflightCheck
{
    /// <inheritdoc />
    public override string Name => "secret keys";

    /// <inheritdoc />
    public override PreflightResult Run()
    {
        ArgumentNullException.ThrowIfNull(config);

        var secret = config.Keys.SecretKey;
        var credential = config.Keys.CredentialKey;

        if (secret.EntropyBytes < KeyConfig.MinimumEntropyBytes ||
            credential.EntropyBytes < KeyConfig.MinimumEntropyBytes)
        {
            return PreflightResult.Fail(
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"CHARTER_SECRET_KEY carries {secret.EntropyBytes} bytes and CHARTER_CREDENTIAL_KEY " +
                    $"{credential.EntropyBytes}; {KeyConfig.MinimumEntropyBytes} is the minimum"),
                $"set both to a fresh value - {KeyConfig.GenerateHint}");
        }

        if (secret.Equals(credential))
        {
            return PreflightResult.Fail(
                Name,
                "CHARTER_SECRET_KEY and CHARTER_CREDENTIAL_KEY are the same value",
                "set CHARTER_CREDENTIAL_KEY to a different key so that rotating cookie signing does " +
                "not invalidate every stored credential (section 20b.2)");
        }

        return PreflightResult.Pass(
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"both keys carry at least {KeyConfig.MinimumEntropyBytes} bytes and differ"));
    }
}

/// <summary>
/// Whether this instance may talk to anything outside itself (sections 30.6, 19).
/// </summary>
/// <remarks>
/// Not one of the five checks section 30.1 enumerates, and here anyway: demo mode changes what the
/// instance is allowed to do, silently, and an operator reading the first-run report is exactly the
/// person who needs to know that no model provider, code host or mail server will be contacted. A
/// kill switch nobody can see is a kill switch nobody trusts.
/// </remarks>
public sealed class OutboundCallsPreflightCheck(CharterConfig config) : PurePreflightCheck
{
    /// <inheritdoc />
    public override string Name => "outbound calls";

    /// <inheritdoc />
    public override PreflightResult Run()
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.OutboundCallsAllowed
            ? PreflightResult.Pass(Name, "enabled; this instance may reach model providers, GitHub and SMTP")
            : PreflightResult.Pass(
                Name,
                "blocked by CHARTER_DEMO: no model provider, code host, or mail server will be " +
                "contacted, and the instance is seeded with demonstration data (section 30.6)");
    }
}

/// <summary>The public base URL resolves (section 30.1).</summary>
/// <remarks>
/// Webhook deliveries and every link Charter sends go to this host. If it does not resolve from
/// inside the container, the failure surfaces as a GitHub delivery that never arrives - a symptom
/// nobody traces back to a typo in an environment variable.
/// </remarks>
public sealed class BaseUrlPreflightCheck(CharterConfig config, IHostnameResolver resolver) : IPreflightCheck
{
    /// <inheritdoc />
    public string Name => "base URL";

    /// <inheritdoc />
    public bool RequiresIo => true;

    /// <summary>
    /// Advisory, and the only check here that is. The name is resolved by GitHub and by browsers,
    /// not by this container: split-horizon DNS, a PaaS private network, and a DNS record that
    /// propagates minutes after the first deploy all make this lookup fail on an instance that works
    /// perfectly from outside. A failure is a strong hint and not proof, so it is shouted rather than
    /// fatal - unlike the database, which is observed against the very resource that has to work.
    /// </summary>
    public PreflightSeverity Severity => PreflightSeverity.Advisory;

    /// <inheritdoc />
    public async ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(resolver);

        if (config.DemoMode)
        {
            // Section 30.6: demo mode makes no outbound call, and a DNS lookup is one.
            return PreflightResult.Skip(Name, "not run: CHARTER_DEMO makes no outbound call");
        }

        var host = config.BaseUrl.Host;
        var resolved = await resolver.CanResolveAsync(host, cancellationToken).ConfigureAwait(false);

        return resolved
            ? PreflightResult.Pass(Name, $"{config.BaseUrl} resolves ({host})")
            : PreflightResult.Fail(
                Name,
                $"{host} does not resolve from this container",
                "set CHARTER_BASE_URL to the public URL operators and GitHub webhooks reach this " +
                "instance on, and check the container's DNS");
    }
}

/// <summary>Postgres is reachable (section 30.1).</summary>
public sealed class DatabaseConnectivityPreflightCheck(CharterConfig config, IDatabaseProbe probe) : IPreflightCheck
{
    /// <inheritdoc />
    public string Name => "database";

    /// <inheritdoc />
    public bool RequiresIo => true;

    /// <inheritdoc />
    public async ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(probe);

        var database = config.Database;

        try
        {
            await probe.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            return PreflightResult.Pass(
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"connected to {database.Database} at {database.Host}:{database.Port}"));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return PreflightResult.Fail(
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"cannot connect to {database.Host}:{database.Port}: {ex.Message}"),
                "check DATABASE_URL, that Postgres is running, and that this container can reach it");
        }
    }
}

/// <summary>Migrations have been applied (sections 2.3, 30.1).</summary>
public sealed class MigrationsPreflightCheck(IDatabaseProbe probe) : IPreflightCheck
{
    /// <inheritdoc />
    public string Name => "migrations";

    /// <inheritdoc />
    public bool RequiresIo => true;

    /// <inheritdoc />
    public async ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        try
        {
            var applied = await probe.AppliedMigrationCountAsync(cancellationToken).ConfigureAwait(false);

            return applied switch
            {
                < 0 => PreflightResult.Fail(
                    Name,
                    "the migration history table does not exist, so no migration has ever run",
                    "migrations run automatically on boot (section 2.3); if this persists, check the " +
                    "startup log for the migration error and that the database role may create tables"),
                0 => PreflightResult.Fail(
                    Name,
                    "the migration history table is empty",
                    "the schema is not initialised; check the startup log for the migration error"),
                _ => PreflightResult.Pass(
                    Name,
                    string.Create(CultureInfo.InvariantCulture, $"{applied} migration(s) applied")),
            };
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return PreflightResult.Fail(
                Name,
                $"could not read the migration history: {ex.Message}",
                "fix database connectivity first; this check depends on it");
        }
    }
}

/// <summary>
/// At least one model credential is resolvable (section 4.2 footnote *, section 30.1).
/// </summary>
/// <remarks>
/// This is the check the config parser cannot make. An instance-level <c>ANTHROPIC_API_KEY</c> or
/// <c>OPENROUTER_API_KEY</c> satisfies it without touching the database; otherwise a linked
/// <c>CredentialGrant</c> does, and only the database knows whether one exists.
/// </remarks>
public sealed class ModelCredentialPreflightCheck(CharterConfig config, IDatabaseProbe probe) : IPreflightCheck
{
    /// <inheritdoc />
    public string Name => "model credential";

    /// <inheritdoc />
    public bool RequiresIo => true;

    /// <inheritdoc />
    public async ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(probe);

        if (config.DemoMode)
        {
            // Section 30.6 exists so someone can evaluate Charter without spending a token. Demanding
            // a token first would defeat the only thing demo mode is for.
            return PreflightResult.Skip(Name, "not run: CHARTER_DEMO never calls a model provider");
        }

        if (config.Models.AnthropicApiKey is not null)
        {
            return PreflightResult.Pass(Name, "ANTHROPIC_API_KEY is set as an instance-level credential");
        }

        if (config.Models.OpenRouterApiKey is not null)
        {
            return PreflightResult.Pass(Name, "OPENROUTER_API_KEY is set as an instance-level credential");
        }

        const string remediation =
            "set ANTHROPIC_API_KEY or OPENROUTER_API_KEY, or link a model credential for a user " +
            "(section 20b.3); without one, no session can run";

        try
        {
            var linked = await probe.LinkedModelCredentialCountAsync(cancellationToken).ConfigureAwait(false);

            return linked > 0
                ? PreflightResult.Pass(
                    Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"no instance-level key, but {linked} credential grant(s) are linked in the database"))
                : PreflightResult.Fail(
                    Name,
                    "no instance-level key and no credential grant linked in the database",
                    remediation);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return PreflightResult.Fail(
                Name,
                $"no instance-level key, and the database could not be asked for one: {ex.Message}",
                remediation);
        }
    }
}
