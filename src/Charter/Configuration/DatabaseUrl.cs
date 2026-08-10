using System.Web;
using Npgsql;

namespace Charter.Configuration;

/// <summary>
/// Converts a <c>DATABASE_URL</c> in URI form into an Npgsql keyword/value connection string.
/// </summary>
/// <remarks>
/// Npgsql does not natively accept URI-form connection strings, but every PaaS that provisions
/// Postgres hands one out. See section 4.3 of the specification, which this implements:
/// <list type="bullet">
///   <item>accept both <c>postgres://</c> and <c>postgresql://</c>;</item>
///   <item>URL-decode username and password, which routinely contain <c>@</c>, <c>/</c> and <c>:</c>;</item>
///   <item>default the port to 5432 when absent;</item>
///   <item>map <c>sslmode</c> onto Npgsql's <c>SSL Mode</c>, treating <c>require</c> as
///         <c>SSL Mode=Require;Trust Server Certificate=true</c> unless <c>verify-full</c>;</item>
///   <item>reject a missing scheme, host or database with a clear error.</item>
/// </list>
/// </remarks>
public static class DatabaseUrl
{
    /// <summary>The port assumed when the URL does not carry one.</summary>
    public const int DefaultPort = 5432;

    /// <summary>Converts <paramref name="url"/> to an Npgsql connection string.</summary>
    /// <exception cref="ConfigException">The URL is not a usable Postgres URL.</exception>
    public static string ToNpgsql(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ConfigException("DATABASE_URL is empty");
        }

        Uri uri;
        try
        {
            uri = new Uri(url, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            throw new ConfigException($"DATABASE_URL is not a valid URL: {ex.Message}", ex);
        }

        if (uri.Scheme is not ("postgres" or "postgresql"))
        {
            throw new ConfigException("DATABASE_URL must use postgres:// or postgresql://");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new ConfigException("DATABASE_URL is missing a host");
        }

        var database = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(database))
        {
            throw new ConfigException("DATABASE_URL is missing a database name");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var hasCredentials = !string.IsNullOrEmpty(uri.UserInfo);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? DefaultPort : uri.Port,
            Database = Uri.UnescapeDataString(database),
            Username = hasCredentials ? Uri.UnescapeDataString(userInfo[0]) : null,
            Password = hasCredentials && userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        var query = HttpUtility.ParseQueryString(uri.Query);
        builder.SslMode = query["sslmode"] switch
        {
            "disable" => SslMode.Disable,
            "verify-full" => SslMode.VerifyFull,
            "require" or null or "" => SslMode.Require,
            var other => throw new ConfigException(
                $"DATABASE_URL has an unsupported sslmode: {other}. Accepted values are disable, require, verify-full."),
        };

        // Section 4.3 asks for `require` to become `SSL Mode=Require;Trust Server Certificate=true`.
        // Npgsql 8 changed what SslMode.Require means and Npgsql 10 marks TrustServerCertificate
        // obsolete ("no longer needed and does nothing"): Require now encrypts the connection
        // without validating the server certificate, which is exactly the behaviour the spec asked
        // for, and VerifyCA / VerifyFull are the modes that validate. Setting the obsolete keyword
        // as well would be a no-op, so the intent is preserved and the redundant keyword dropped.
        return builder.ConnectionString;
    }
}
