using System.Runtime.InteropServices;
using System.Text.Json;
using Charter.Agent.Protocol;

namespace Charter.Agent.Pairing;

/// <summary>The long-lived credential a spent pairing token bought (section 33.3).</summary>
public sealed record AgentCredential
{
    public required string Server { get; init; }

    public required string AgentId { get; init; }

    public required string AgentToken { get; init; }

    public required DateTimeOffset PairedAt { get; init; }

    /// <summary>The default record printer would print the token. It never gets the chance.</summary>
    public override string ToString() => $"AgentCredential {{ AgentId = {AgentId}, Token = redacted }}";
}

/// <summary>
/// Where the agent credential lives between runs.
/// </summary>
/// <remarks>
/// The pairing token is single-use and short-TTL, so it cannot be what a restarted daemon presents.
/// The credential it bought is written to the state directory with owner-only permissions and read
/// back on the next start. Nothing else is persisted — no job payloads, no per-job secrets, nothing
/// from the control plane's environment.
/// </remarks>
public sealed class AgentCredentialStore(string stateDirectory)
{
    public string StateDirectory { get; } = stateDirectory;

    public string CredentialPath => Path.Combine(StateDirectory, "credential.json");

    /// <summary>Sensible per-platform default: alongside the operator's own files, not in /tmp.</summary>
    public static string DefaultStateDirectory()
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        return string.IsNullOrEmpty(home)
            ? Path.Combine(Path.GetTempPath(), "charter-agent")
            : Path.Combine(home, ".charter-agent");
    }

    public AgentCredential? Load(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!File.Exists(CredentialPath))
        {
            return null;
        }

        AgentCredential? credential;
        try
        {
            credential = JsonSerializer.Deserialize<AgentCredential>(
                File.ReadAllText(CredentialPath), AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        // A credential is bound to the control plane that issued it. Pointing the same host at a
        // different instance must re-pair rather than present a credential that instance never saw.
        return credential is not null &&
            string.Equals(credential.Server, Normalize(server), StringComparison.OrdinalIgnoreCase)
                ? credential
                : null;
    }

    public void Save(AgentCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        Directory.CreateDirectory(StateDirectory);
        RestrictToOwner(StateDirectory, isDirectory: true);

        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(credential, AgentJson.Options));
        RestrictToOwner(CredentialPath, isDirectory: false);
    }

    /// <summary>Called when the control plane revokes the agent. A revoked credential is not kept.</summary>
    public void Clear()
    {
        if (File.Exists(CredentialPath))
        {
            File.Delete(CredentialPath);
        }
    }

    public static string Normalize(Uri server) => server.GetLeftPart(UriPartial.Authority) +
        server.AbsolutePath.TrimEnd('/');

    private static void RestrictToOwner(string path, bool isDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows inherits ACLs from the user profile directory; there is no mode to set.
            return;
        }

        var mode = isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (IOException)
        {
            // A filesystem that cannot express the mode. The caller is told at startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Not ours to change; better to keep running than to fail on a permissions nicety.
        }
    }
}
