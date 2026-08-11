using System.Net;
using System.Net.Sockets;
using Charter.Deployments;

namespace Charter.Tests;

/// <summary>
/// The gate every preview URL passes before Charter stores it, fetches it, or shows it to somebody.
/// </summary>
/// <remarks>
/// <para>
/// A preview URL is a value the execution plane supplies (section 16.3), and Charter does two
/// dangerous things with it: it fetches it on a loop from inside the control plane, which is a
/// recurring request forgery against anything the container can reach, and it renders it as a
/// requester's button under the sentence "Nothing you do here touches the real one".
/// </para>
/// <para>
/// Every case below fails without <see cref="PreviewUrlPolicy"/>: before it, the only check anywhere
/// on this path was <c>string.IsNullOrWhiteSpace</c> at the binder and a scheme test at the probe.
/// </para>
/// </remarks>
public class PreviewUrlPolicyTests
{
    private static PreviewUrlPolicy Policy(
        StubPreviewHostResolver? resolver = null,
        bool allowPrivateHosts = false)
        => new(
            DeploymentOptions.WebhookOnly with { AllowPrivatePreviewHosts = allowPrivateHosts },
            resolver ?? new StubPreviewHostResolver());

    [Theory]
    [InlineData("https://quote-tool-pr-142.up.railway.app")]
    [InlineData("http://myapp-pr-142.onrender.com")]
    [InlineData("https://preview.example.com:8443/quotes?new=1")]
    public async Task AnOrdinaryPublicPreviewUrlIsAllowed(string url)
    {
        var verdict = await Policy().ValidateAsync(url, TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed, verdict.Reason);
        Assert.NotNull(verdict.Url);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com:70/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/preview")]
    public async Task ASchemeThatIsNotHttpIsRefused(string url)
    {
        var verdict = await Policy().ValidateAsync(url, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Contains("http", verdict.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialsInTheUrlAreRefused()
    {
        // A link that reads as one host and authenticates to another is exactly the shape a requester
        // cannot evaluate, and Charter's own copy tells them it is safe.
        var verdict = await Policy().ValidateAsync(
            "https://admin:hunter2@preview.example.com/",
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Contains("credentials", verdict.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://127.1.2.3/")]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://charter.localhost/")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://0.0.0.0/")]
    public async Task TheLoopbackInterfaceIsRefusedAsALiteralOrAsAName(string url)
    {
        var verdict = await Policy().ValidateAsync(url, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://169.254.170.2/v2/credentials/")]
    [InlineData("http://[fe80::1]/")]
    public async Task LinkLocalAddressesAreRefused(string url)
    {
        // Where every cloud provider parks instance metadata, and the single highest-value target for
        // a server-side request Charter can be made to repeat every fifteen seconds.
        var verdict = await Policy().ValidateAsync(url, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Contains("link-local", verdict.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.20:9000/")]
    [InlineData("http://172.16.4.4/")]
    [InlineData("http://100.72.3.4/")]
    [InlineData("http://[fd00::1]/")]
    public async Task PrivateAddressesAreRefusedByDefault(string url)
    {
        var verdict = await Policy().ValidateAsync(url, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Contains("CHARTER_PREVIEW_ALLOW_PRIVATE_HOSTS", verdict.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostnameThatResolvesToLoopbackIsRefused()
    {
        // The case a name-shaped rule cannot see. "preview.example.com" looks like every other
        // preview; where it points is the only thing that matters, and only DNS knows.
        var resolver = new StubPreviewHostResolver().Map("preview.example.com", "127.0.0.1");

        var verdict = await Policy(resolver).ValidateAsync(
            "https://preview.example.com/",
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Contains("preview.example.com", resolver.Asked);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.0.9")]
    public async Task AHostnameThatResolvesInsideTheNetworkIsRefused(string address)
    {
        var resolver = new StubPreviewHostResolver().Map("preview.example.com", address);

        var verdict = await Policy(resolver).ValidateAsync(
            "https://preview.example.com/",
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public async Task OneBadAddressAmongSeveralIsEnoughToRefuse()
    {
        // A name with two A records, one of them inside the network, is a name that reaches inside the
        // network — round-robin decides which, and Charter does not get to pick.
        var resolver = new StubPreviewHostResolver().Map("preview.example.com", "203.0.113.5", "10.1.2.3");

        var verdict = await Policy(resolver).ValidateAsync(
            "https://preview.example.com/",
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public async Task AnUnresolvableNameIsAllowedThroughAndLeftToTheFetchPath()
    {
        // A transient DNS failure must not permanently mark a preview broken, and a name that resolves
        // to nothing cannot be reached. Nothing rests on this: PreviewHttpClient checks the address it
        // is about to dial, which is the check a changing name cannot race.
        var resolver = new StubPreviewHostResolver().Unresolvable("preview.example.com");

        var verdict = await Policy(resolver).ValidateAsync(
            "https://preview.example.com/",
            TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed, verdict.Reason);
    }

    [Fact]
    public async Task ALiteralAddressIsSettledWithoutAskingDns()
    {
        var resolver = new StubPreviewHostResolver();

        var verdict = await Policy(resolver).ValidateAsync(
            "https://203.0.113.10/",
            TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed, verdict.Reason);
        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public async Task TheEscapeHatchReadmitsPrivateRangesAndNothingElse()
    {
        var policy = Policy(allowPrivateHosts: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        // A self-hoster whose previews genuinely live on their own network.
        Assert.True((await policy.ValidateAsync("http://10.0.4.12:3000/", cancellationToken)).Allowed);
        Assert.True((await policy.ValidateAsync("http://192.168.1.20/", cancellationToken)).Allowed);

        // Still refused, because no preview has ever lived at either of these.
        Assert.False((await policy.ValidateAsync("http://127.0.0.1:8080/", cancellationToken)).Allowed);
        Assert.False((await policy.ValidateAsync("http://169.254.169.254/", cancellationToken)).Allowed);
    }

    [Fact]
    public async Task AUrlLongerThanTheLimitIsRefusedRatherThanStored()
    {
        var url = "https://preview.example.com/" + new string('a', PreviewUrlPolicy.MaxUrlLength);

        var verdict = await Policy().ValidateAsync(url, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/", false)]
    [InlineData("http://169.254.169.254/", false)]
    [InlineData("http://localhost/", false)]
    [InlineData("https://admin:hunter2@preview.example.com/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData("https://preview.example.com/", true)]
    [InlineData("http://10.0.4.12:3000/", true)]
    public void TheDisplayRuleWithholdsWhatIsNeverLegitimateAndNothingElse(string url, bool displayable)
    {
        // The rule the API projection and the SPA both apply on the way out, on a row that may predate
        // every check above (section 16.3: an upgrade does not rewrite rows). Private ranges pass here
        // on purpose — a self-hoster's 10.x preview is a working link, and the requester's browser is
        // on that network too.
        Assert.Equal(displayable, PreviewUrlPolicy.IsDisplayable(url));
    }
}

/// <summary>
/// The second half of the defence: what the socket does, not what the string said.
/// </summary>
/// <remarks>
/// DNS can answer differently between the moment a URL is checked and the moment it is fetched — the
/// technique has a name — so the address is checked again where it cannot be raced, immediately before
/// the connection is opened.
/// </remarks>
public class PreviewHttpClientTests
{
    [Fact]
    public void RedirectsAreNotFollowed()
    {
        // A redirect is a second URL that passed no check, chosen by whoever supplied the first. The
        // probe only needs to know whether something answered, and a 302 answers that.
        using var handler = PreviewHttpClient.CreateHandler(PreviewUrlPolicy.Default);

        Assert.False(handler.AllowAutoRedirect);

        // A proxy would carry the request to an address the connect callback never saw, which would
        // make checking the address decorative.
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("203.0.113.10", true)]
    [InlineData("140.82.121.4", true)]
    public void TheAddressCheckTheSocketAppliesIsTheSameOneTheUrlGateApplies(string address, bool allowed)
    {
        // The connect callback filters candidate addresses through exactly this, which is what makes a
        // redirect to a private address unreachable as well as unfollowed.
        Assert.Equal(allowed, PreviewUrlPolicy.Default.IsAllowedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task AGuardedClientNeverReachesALoopbackListenerEvenWhenOneIsThere()
    {
        // The end-to-end proof over a real socket: something genuinely is listening, and the handler
        // still does not reach it. Without the connect callback this request succeeds.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var handler = PreviewHttpClient.CreateHandler(PreviewUrlPolicy.Default);
        using var client = new HttpClient(handler);
        PreviewHttpClient.Configure(client, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync(
                new Uri($"http://127.0.0.1:{port}/"),
                TestContext.Current.CancellationToken));

        // Nothing was ever dialled, so the listener still has nobody waiting to be accepted.
        Assert.False(listener.Poll(TimeSpan.FromMilliseconds(50), SelectMode.SelectRead));
    }

    [Fact]
    public async Task TheRefusalNamesTheHostSoAnOperatorKnowsWhichHalfOfTheSystemToLookAt()
    {
        using var handler = PreviewHttpClient.CreateHandler(PreviewUrlPolicy.Default);
        using var client = new HttpClient(handler);
        PreviewHttpClient.Configure(client, TimeSpan.FromSeconds(5));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync(
                new Uri("http://169.254.169.254/latest/meta-data/"),
                TestContext.Current.CancellationToken));

        var message = thrown.ToString();

        Assert.Contains("169.254.169.254", message, StringComparison.Ordinal);
        Assert.Contains("will not open a connection", message, StringComparison.Ordinal);
    }
}
