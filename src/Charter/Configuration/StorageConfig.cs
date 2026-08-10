namespace Charter.Configuration;

/// <summary>
/// S3-compatible object storage for verification artifacts and large transcripts (sections 4.2, 27.1).
/// </summary>
/// <remarks>
/// Optional only until something needs it. Charter starts without it, but any project type producing
/// a <c>build_artifact</c>, <c>capture</c> or <c>hil_report</c> requires it, and so does transcript
/// offload on a PaaS with no durable disk. Presence of <c>CHARTER_STORAGE_ENDPOINT</c> is the switch:
/// once it is set, the bucket and both keys are required and validated together, because a half
/// configured bucket fails at the moment an artifact is produced - which is the worst moment.
/// </remarks>
public sealed record StorageConfig
{
    /// <summary><c>CHARTER_STORAGE_ENDPOINT</c>. MinIO, R2, B2 and Wasabi all work.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary><c>CHARTER_STORAGE_BUCKET</c>.</summary>
    public required string Bucket { get; init; }

    /// <summary><c>CHARTER_STORAGE_ACCESS_KEY</c>.</summary>
    public required Secret AccessKey { get; init; }

    /// <summary><c>CHARTER_STORAGE_SECRET_KEY</c>.</summary>
    public required Secret SecretKey { get; init; }

    /// <summary><c>CHARTER_STORAGE_REGION</c>, default <c>auto</c>.</summary>
    public required string Region { get; init; }

    /// <summary>
    /// <c>CHARTER_STORAGE_FORCE_PATH_STYLE</c>, default <c>true</c>: MinIO and most self-hosted
    /// gateways need path-style addressing.
    /// </summary>
    public required bool ForcePathStyle { get; init; }

    /// <summary>Variables that become required once the endpoint is set.</summary>
    internal static IReadOnlyList<string> DependentVariables { get; } =
    [
        "CHARTER_STORAGE_BUCKET",
        "CHARTER_STORAGE_ACCESS_KEY",
        "CHARTER_STORAGE_SECRET_KEY",
    ];

    internal static StorageConfig? Parse(EnvReader reader)
    {
        var endpoint = reader.Url(
            "CHARTER_STORAGE_ENDPOINT",
            "an absolute http(s) URL for an S3-compatible endpoint, for example https://minio.internal:9000",
            required: false);

        var bucket = reader.Optional("CHARTER_STORAGE_BUCKET");
        var accessKey = reader.OptionalSecret("CHARTER_STORAGE_ACCESS_KEY");
        var secretKey = reader.OptionalSecret("CHARTER_STORAGE_SECRET_KEY");
        var region = reader.Optional("CHARTER_STORAGE_REGION") ?? "auto";
        var forcePathStyle = reader.Bool("CHARTER_STORAGE_FORCE_PATH_STYLE", true);

        if (endpoint is null)
        {
            // The endpoint is unset, so anything else in the block has no effect. Say so rather than
            // leaving the operator to wonder why artifact storage is off.
            foreach (var (variable, isSet) in new[]
                     {
                         ("CHARTER_STORAGE_BUCKET", bucket is not null),
                         ("CHARTER_STORAGE_ACCESS_KEY", accessKey is not null),
                         ("CHARTER_STORAGE_SECRET_KEY", secretKey is not null),
                     })
            {
                if (isSet)
                {
                    reader.Warn(
                        variable,
                        $"{variable} is set but CHARTER_STORAGE_ENDPOINT is not, so object storage " +
                        "stays disabled and artifacts are confined to Postgres (section 27.5).");
                }
            }

            return null;
        }

        var missing = new List<string>();
        if (bucket is null)
        {
            missing.Add("CHARTER_STORAGE_BUCKET");
        }

        if (accessKey is null)
        {
            missing.Add("CHARTER_STORAGE_ACCESS_KEY");
        }

        if (secretKey is null)
        {
            missing.Add("CHARTER_STORAGE_SECRET_KEY");
        }

        foreach (var variable in missing)
        {
            reader.Error(
                variable,
                $"{variable} is required when CHARTER_STORAGE_ENDPOINT is set. Object storage is " +
                "configured as a block: endpoint, bucket, access key and secret key together " +
                $"(section 4.2). Missing: {string.Join(", ", missing)}.");
        }

        if (missing.Count > 0 || bucket is null || accessKey is null || secretKey is null)
        {
            return null;
        }

        return new StorageConfig
        {
            Endpoint = endpoint,
            Bucket = bucket,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = region,
            ForcePathStyle = forcePathStyle,
        };
    }
}

/// <summary>
/// Default spend limits for new budgets (sections 4.2, 34.9).
/// </summary>
public sealed record BudgetConfig
{
    /// <summary><c>CHARTER_DEFAULT_SESSION_BUDGET_USD</c>, default 5.00.</summary>
    public required decimal DefaultSessionUsd { get; init; }

    /// <summary><c>CHARTER_DEFAULT_MONTHLY_BUDGET_USD</c>, default 100.00.</summary>
    public required decimal DefaultMonthlyUsd { get; init; }

    internal static BudgetConfig Parse(EnvReader reader)
    {
        var session = reader.Money("CHARTER_DEFAULT_SESSION_BUDGET_USD", 5.00m);
        var monthly = reader.Money("CHARTER_DEFAULT_MONTHLY_BUDGET_USD", 100.00m);

        if (monthly > 0m && session > monthly)
        {
            reader.Warn(
                "CHARTER_DEFAULT_SESSION_BUDGET_USD",
                "CHARTER_DEFAULT_SESSION_BUDGET_USD is larger than " +
                "CHARTER_DEFAULT_MONTHLY_BUDGET_USD, so a single session can exhaust the month " +
                "(section 34.3).");
        }

        return new BudgetConfig { DefaultSessionUsd = session, DefaultMonthlyUsd = monthly };
    }
}

/// <summary>
/// The daily release check of section 28 - the only outbound request Charter initiates.
/// </summary>
public sealed record UpdateCheckConfig
{
    /// <summary><c>CHARTER_UPDATE_CHECK</c>, default <c>true</c>.</summary>
    public required bool Enabled { get; init; }

    /// <summary><c>CHARTER_UPDATE_CHANNEL</c>, default <c>stable</c>.</summary>
    public required UpdateChannel Channel { get; init; }

    internal static UpdateCheckConfig Parse(EnvReader reader) => new()
    {
        Enabled = reader.Bool("CHARTER_UPDATE_CHECK", true),
        Channel = reader.Choice<UpdateChannel>("CHARTER_UPDATE_CHANNEL", UpdateChannel.Stable,
        [
            ("stable", UpdateChannel.Stable),
            ("prerelease", UpdateChannel.Prerelease),
        ]),
    };
}
