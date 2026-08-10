using Charter.Agent.Jobs;
using Charter.Agent.Protocol;

namespace Charter.Agent.Execution;

/// <summary>Where a running job's output goes. The daemon forwards it as <c>job.event</c> frames.</summary>
public interface IJobEventSink
{
    /// <summary>
    /// Publishes one line of job output. The implementation scrubs it before it goes anywhere -
    /// a job's own tooling may echo the token the agent handed it (section 33.5).
    /// </summary>
    void Publish(string jobId, string kind, string message);
}

/// <summary>Runs one job on this host, either in a container or natively (section 33.2).</summary>
public interface IJobExecutor
{
    /// <summary>One line for the startup banner: what this executor will do and where.</summary>
    string Describe();

    /// <summary>
    /// Checks the host can actually run jobs before the agent advertises itself as able to. Returns
    /// the problems found; empty means ready.
    /// </summary>
    Task<IReadOnlyList<string>> PreflightAsync(CancellationToken cancellationToken = default);

    Task<JobCompletion> ExecuteAsync(
        JobAssignment job,
        IJobEventSink events,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds the environment a job runs with.
/// </summary>
/// <remarks>
/// This is the only place per-job secrets are turned into something a child process can read, and it
/// is deliberately short. What crosses is a short-TTL installation token for the one repository in
/// the job and a scoped model credential - never a refresh token, never the agent's own credential,
/// never anything for another repository (section 33.5). Signing identities, licences and registry
/// tokens are the operator's, configured locally on this host, and never appear here (section 32.8).
/// </remarks>
public static class JobEnvironment
{
    public static Dictionary<string, string> Build(JobAssignment job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CHARTER_JOB_ID"] = job.JobId,
            ["CHARTER_JOB_TYPE"] = job.Type,
        };

        foreach (var pair in job.Command.Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        if (job.Repo is { } repo)
        {
            environment["CHARTER_REPO"] = repo.FullName;
            environment["CHARTER_REPO_URL"] = repo.CloneUrl;
            if (repo.Branch is not null)
            {
                environment["CHARTER_BRANCH"] = repo.Branch;
            }
        }

        if (job.Secrets?.GitHub is { } github)
        {
            environment["GITHUB_TOKEN"] = github.Token;
            environment["CHARTER_GITHUB_REPOSITORY"] = github.Repository;
        }

        if (job.Secrets?.Model is { } model)
        {
            environment[ModelKeyVariable(model.Provider)] = model.ApiKey;
            environment["CHARTER_MODEL_PROVIDER"] = model.Provider;
            if (model.BaseUrl is not null)
            {
                environment["CHARTER_MODEL_BASE_URL"] = model.BaseUrl;
            }
        }

        return environment;
    }

    /// <summary>The variable each adapter's CLI already looks for (section 12b).</summary>
    public static string ModelKeyVariable(string provider) => provider.ToLowerInvariant() switch
    {
        "anthropic" => "ANTHROPIC_API_KEY",
        "openai" => "OPENAI_API_KEY",
        "openrouter" => "OPENROUTER_API_KEY",
        "google" or "gemini" => "GEMINI_API_KEY",
        _ => "CHARTER_MODEL_API_KEY",
    };
}
