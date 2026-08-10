using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Charter.Agent.Protocol;

namespace Charter.Agent.Pairing;

/// <summary>Outcome of trading a pairing token for a credential.</summary>
/// <param name="Credential">Present on success.</param>
/// <param name="Error">A message fit to show an operator, present on failure.</param>
/// <param name="Retryable">
/// True for a transport failure or a 5xx — the control plane may simply be restarting. False for a
/// spent, expired or unknown pairing token, where retrying will never help.
/// </param>
public sealed record PairingOutcome(AgentCredential? Credential, string? Error, bool Retryable)
{
    public bool Ok => Credential is not null;
}

/// <summary>Trades the single-use pairing token for a long-lived credential (section 33.3).</summary>
public interface IPairingClient
{
    Task<PairingOutcome> PairAsync(
        Uri server,
        PairRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>The real one: a single HTTPS POST, outbound, before the socket is opened.</summary>
public sealed class HttpPairingClient(HttpClient httpClient) : IPairingClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<PairingOutcome> PairAsync(
        Uri server,
        PairRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = new Uri(
            server.GetLeftPart(UriPartial.Authority) +
            server.AbsolutePath.TrimEnd('/') +
            AgentProtocol.PairPath);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request, options: AgentJson.Options),
        };
        message.Headers.TryAddWithoutValidation(
            AgentProtocol.VersionHeader,
            AgentProtocol.Version.ToString(CultureInfo.InvariantCulture));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return new PairingOutcome(null, $"Could not reach {endpoint}: {exception.Message}", Retryable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PairingOutcome(null, $"Timed out contacting {endpoint}.", Retryable: true);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<PairResponse>(AgentJson.Options, cancellationToken);
                if (body is null || string.IsNullOrWhiteSpace(body.AgentToken))
                {
                    return new PairingOutcome(
                        null, "The control plane accepted the pairing token but returned no credential.", true);
                }

                var negotiated = ProtocolNegotiation.Evaluate(body.ProtocolVersion, null);
                return negotiated.Ok
                    ? new PairingOutcome(
                        new AgentCredential
                        {
                            Server = AgentCredentialStore.Normalize(server),
                            AgentId = body.AgentId,
                            AgentToken = body.AgentToken,
                            PairedAt = now,
                        },
                        null,
                        Retryable: false)
                    : new PairingOutcome(null, negotiated.Message, Retryable: false);
            }

            var detail = await ReadErrorAsync(response, cancellationToken);
            var retryable = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;

            var explanation = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "The pairing token was rejected. Pairing tokens are single-use and short-lived - " +
                    "generate a fresh one in Settings -> Runners.",
                HttpStatusCode.Gone or HttpStatusCode.NotFound =>
                    "The pairing token has expired or was already used. Generate a fresh one in " +
                    "Settings -> Runners.",
                HttpStatusCode.UpgradeRequired =>
                    "The control plane refused this agent's protocol version. Upgrade charter-agent.",
                _ => $"Pairing failed with HTTP {(int)response.StatusCode}.",
            };

            return new PairingOutcome(
                null,
                detail is null ? explanation : $"{explanation} ({detail})",
                retryable);
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(AgentJson.Options, cancellationToken);
            return body?.Message ?? body?.Error;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
