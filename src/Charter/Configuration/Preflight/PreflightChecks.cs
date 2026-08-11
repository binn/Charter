using System.Globalization;
using Charter.Security;
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

        // Validation accepts *at least* 32 bytes; AES-256 accepts *exactly* 32, and the protector is
        // a lazily-constructed singleton — so a 40-byte key passed every check here, booted a healthy
        // instance, and then threw on the first credential decryption, which is inside the refine
        // job. The symptom was a request that never refined, which is the failure this whole check
        // exists upstream of. Derived here so the refusal happens at boot (section 4.1) instead of on
        // somebody's first request.
        try
        {
            _ = CredentialKeyDerivation.Derive(credential);
        }
        catch (CredentialProtectionException ex)
        {
            return PreflightResult.Fail(
                Name,
                // The message names the derived byte count and never the value.
                ex.Message,
                $"replace CHARTER_CREDENTIAL_KEY - {KeyConfig.GenerateHint}. Anything already "
                + "encrypted under the old value cannot be read back, so rotate it before you store "
                + "a credential rather than after");
        }

        return PreflightResult.Pass(
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"both keys carry at least {KeyConfig.MinimumEntropyBytes} bytes, differ, and CHARTER_CREDENTIAL_KEY derives to the {CredentialKeyDerivation.RequiredKeyBytes} bytes AES-256 needs"));
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

/// <summary>
/// The GitHub App credentials are the shape they claim to be (sections 4.2, 30.1).
/// </summary>
/// <remarks>
/// <para>
/// Pure, and mostly a receipt. Section 4.2 accepts <c>GITHUB_APP_PRIVATE_KEY</c> as PEM or as that
/// PEM base64-encoded, and the parser records which arrived - a fact it then told nobody. It matters
/// because the two failure modes look identical from outside: a key that was base64-encoded twice,
/// or a key pasted with its surrounding quotes, decodes to something that is not PEM, and the only
/// symptom is that every GitHub API call fails to sign. An operator staring at that needs to know
/// what Charter thinks it is holding.
/// </para>
/// <para>
/// Never a failure. A key that is not a key is refused by the parser before the process starts
/// (section 4.1), so anything reaching here is well-formed; the check exists to say so, and to say
/// how it arrived.
/// </para>
/// </remarks>
public sealed class GitHubAppPreflightCheck(CharterConfig config) : PurePreflightCheck
{
    /// <inheritdoc />
    public override string Name => "GitHub App";

    /// <inheritdoc />
    public override PreflightResult Run()
    {
        ArgumentNullException.ThrowIfNull(config);

        var github = config.GitHub;

        var key = github.PrivateKeyWasBase64
            ? "base64 and decoded to PEM; if GitHub rejects the signature, GITHUB_APP_PRIVATE_KEY was "
              + "probably encoded twice"
            : "PEM";

        return PreflightResult.Pass(
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"app {github.AppId}, private key accepted as {key}"));
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
                    // The login role is named because a permission failure looks nothing like a
                    // connectivity failure and is diagnosed entirely differently: "connected as
                    // readonly" is the whole answer to a migration that cannot create tables. It was
                    // parsed out of DATABASE_URL for exactly this and then never shown.
                    $"connected to {database.Database} at {database.Host}:{database.Port} as " +
                    $"{database.Username ?? "the server's default role"}"));
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
            return PreflightResult.Pass(
                Name,
                "ANTHROPIC_API_KEY is set as an instance-level credential" + Mismatch(config));
        }

        if (config.Models.OpenRouterApiKey is not null)
        {
            return PreflightResult.Pass(
                Name,
                "OPENROUTER_API_KEY is set as an instance-level credential" + Mismatch(config));
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

    /// <summary>
    /// Names any control-plane model the instance-level keys cannot serve, appended to the passing
    /// detail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "A key is set" and "a key can serve the configured model" are different questions, and the
    /// second is the one that decides whether a request refines. An <c>openrouter/</c>-qualified
    /// identifier — which is what <c>CHARTER_MODEL_REFINE</c> defaults to — can be served only by an
    /// OpenRouter key, so an instance carrying nothing but <c>ANTHROPIC_API_KEY</c> passes this check
    /// and still resolves nothing on the first request.
    /// </para>
    /// <para>
    /// Reported on the passing line rather than as a failure on purpose: the database may hold a grant
    /// that serves the model, and this check cannot see which kinds are linked, only how many. Refusing
    /// to boot on a suspicion the operator has already handled would be worse than saying so plainly.
    /// </para>
    /// </remarks>
    private static string Mismatch(CharterConfig config)
    {
        var unserved = new List<string>(2);

        foreach (var (variable, model) in new[]
                 {
                     ("CHARTER_MODEL_REFINE", config.Models.Refine),
                     ("CHARTER_MODEL_TEACH", config.Models.Teach),
                 })
        {
            if (!CanServe(config, model))
            {
                unserved.Add($"{variable}={model.Qualified}");
            }
        }

        return unserved.Count == 0
            ? string.Empty
            : $"; note that {string.Join(" and ", unserved)} cannot be served by the instance-level "
              + "key(s) set here, so those calls need a linked credential grant, a matching key, or a "
              + "model identifier the key can serve";
    }

    /// <summary>Whether an instance-level key exists that could authenticate against the model.</summary>
    private static bool CanServe(CharterConfig config, ModelIdentifier model)
    {
        // OpenRouter reaches every model, so its key serves any identifier. Anthropic's key serves
        // anthropic/ alone. Section 4.2 defines no other instance-level variable.
        if (config.Models.OpenRouterApiKey is not null && !model.IsAnthropic)
        {
            return true;
        }

        if (model.IsAnthropic)
        {
            return config.Models.AnthropicApiKey is not null || config.Models.OpenRouterApiKey is not null;
        }

        return false;
    }
}
