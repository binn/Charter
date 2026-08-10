using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Runners;
using Charter.Domain;
using Charter.Runners;

namespace Charter.Tests;

/// <summary>
/// Settings → Runners (sections 33.3, 32.2, 27.3).
/// </summary>
/// <remarks>
/// The two things worth pinning here are the ones a client could get wrong on its own and therefore
/// must not be asked to: which agents could run a queued session, and what a capability set means
/// once it has been expanded for matching. Both are server verdicts; the UI renders the reasoning.
/// </remarks>
public class ApiRunnersTests
{
    [Fact]
    public void TheExpandedMatchingSetIsFoldedBackIntoOneRowPerCapability()
    {
        // A registration stores `dotnet:10.0.100` alongside `dotnet:10.0`, `dotnet:10` and `dotnet`
        // so that matching is set containment. Showing a person four rows for one SDK would be the
        // matching representation leaking into a list somebody reads.
        var advertised = RunnerCapability.ExpandAll(["dotnet:10.0.100", "macos", "xcode:16.2", "usb_device:stm32f4"]);

        var described = AgentCapabilityProbes.Describe(advertised, DateTimeOffset.UtcNow);

        Assert.Equal(
            ["dotnet:10.0.100", "macos", "usb_device:stm32f4", "xcode:16.2"],
            described.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void TwoVersionsOfOneToolStayTwoRows()
    {
        // The boundary check matters: `dotnet:10` refines `dotnet`, and `dotnet:100` does not refine
        // `dotnet:10`. A plain prefix test would collapse two genuinely different SDKs into one.
        var described = AgentCapabilityProbes.Describe(
            RunnerCapability.ExpandAll(["dotnet:8.0.404", "dotnet:10.0.100"]),
            DateTimeOffset.UtcNow);

        Assert.Equal(
            ["dotnet:10.0.100", "dotnet:8.0.404"],
            described.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void ProbedByNamesTheCommandThatFoundItOrNothingAtAll()
    {
        // Section 32.2: probed, never declared. `probedBy` is the difference between a claim and a
        // measurement, and it is what answers "why does this agent think it has Xcode 16.2".
        Assert.Equal("xcodebuild -version", AgentCapabilityProbes.ProbeFor("xcode:16.2"));
        Assert.Equal("dotnet --list-sdks", AgentCapabilityProbes.ProbeFor("dotnet:10.0.100"));
        Assert.Equal("probe-rs list", AgentCapabilityProbes.ProbeFor("usb_device:stm32f4"));
        Assert.Equal("sw_vers -productVersion", AgentCapabilityProbes.ProbeFor("macos"));

        // A capability this build has no probe for gets no key rather than a plausible command
        // nobody ran — pasting that command and getting a different answer is worse than absence.
        Assert.Null(AgentCapabilityProbes.ProbeFor("unity_license"));
    }

    [Fact]
    public void TheOperatingSystemsGroupUnderOneFamilySoTheListReadsAsAMachine()
    {
        Assert.Equal("os", AgentCapabilityProbes.FamilyOf("macos"));
        Assert.Equal("os", AgentCapabilityProbes.FamilyOf("linux"));
        Assert.Equal("xcode", AgentCapabilityProbes.FamilyOf("xcode:16.2"));
        Assert.Equal("usb_device", AgentCapabilityProbes.FamilyOf("usb_device:stm32f4"));
    }

    [Fact]
    public async Task ACapabilityRowOmitsTheVersionAndProbeItDoesNotHave()
    {
        var described = AgentCapabilityProbes.Describe(["unity_license"], DateTimeOffset.UtcNow);
        var body = await ApiPayloads.RenderAsync(Assert.Single(described));

        using var document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.TryGetProperty("version", out _));
        Assert.False(document.RootElement.TryGetProperty("probedBy", out _));
        Assert.Equal("unity_license", document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void AnAgentWithEveryCapabilityIsEligibleEvenWhileItIsSwitchedOff()
    {
        // Section 27.3: a session with no eligible runner queues rather than failing, and a Mac mini
        // that is off is the reason it queues. Calling it ineligible would say nothing here can ever
        // run it, which is a different and wrong answer.
        var mac = Paired("mac-mini-01", ["macos", "xcode:16.2"], online: false);

        Assert.True(RunnersQueryService.IsEligible(mac, ["macos", "xcode:16"]));
    }

    [Fact]
    public void AnAgentOnAnIncompatibleProtocolIsNotEligibleHoweverWellEquipped()
    {
        // Section 33.6: a mismatch is a refusal to claim work. Listing it as eligible would produce
        // exactly the "session that mysteriously never starts" that section exists to prevent.
        var bench = Paired("bench-pi", ["linux", "usb_device:stm32f4"], online: true, protocolVersion: 0);

        Assert.False(RunnersQueryService.IsEligible(bench, ["linux", "usb_device:stm32f4"]));
    }

    [Fact]
    public void ARevokedAgentIsNeverEligible()
    {
        var revoked = Paired("old-builder", ["linux", "dotnet:10.0.100"], online: true);
        revoked.Revoke("Revoked by an administrator.");

        Assert.False(RunnersQueryService.IsEligible(revoked, ["linux"]));
    }

    [Fact]
    public void CoarseRequirementsMatchPreciseAdvertisements()
    {
        // What a runner advertises is precise and what a session requires is coarse, so matching is
        // containment over the expanded set rather than string equality.
        var linux = Paired("build-01", ["linux", "docker", "dotnet:10.0.100"], online: true);

        Assert.True(RunnersQueryService.IsEligible(linux, ["linux", "dotnet:10"]));
        Assert.False(RunnersQueryService.IsEligible(linux, ["linux", "dotnet:11"]));
        Assert.False(RunnersQueryService.IsEligible(linux, ["macos"]));
    }

    [Fact]
    public void AnAgentHoldingLeasesAfterItStoppedHeartbeatingReadsAsDraining()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = Paired("build-01", ["linux"], online: true);
        agent.Heartbeat(now - RunnerAgent.HeartbeatGrace - TimeSpan.FromMinutes(5));

        Assert.Equal(ApiAgentStatus.Draining, RunnersQueryService.StatusOf(agent, inFlight: 2, now));
        Assert.Equal(ApiAgentStatus.Offline, RunnersQueryService.StatusOf(agent, inFlight: 0, now));

        agent.Heartbeat(now);
        Assert.Equal(ApiAgentStatus.Online, RunnersQueryService.StatusOf(agent, inFlight: 1, now));

        agent.Revoke("Revoked by an administrator.", now);
        Assert.Equal(ApiAgentStatus.Revoked, RunnersQueryService.StatusOf(agent, inFlight: 0, now));
    }

    [Fact]
    public async Task AnIncompatibleAgentCarriesTheSentenceThatExplainsItself()
    {
        var now = DateTimeOffset.UtcNow;
        var bench = Paired("bench-pi", ["linux"], online: true, protocolVersion: 0);

        var body = await ApiPayloads.RenderAsync(RunnersQueryService.Describe(bench, inFlight: 0, now));

        using var document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.GetProperty("protocolCompatible").GetBoolean());
        Assert.NotEmpty(document.RootElement.GetProperty("protocolNote").GetString()!);

        // A compatible one carries no note at all rather than an empty string.
        var healthy = Paired("build-01", ["linux"], online: true);
        using var compatible = JsonDocument.Parse(
            await ApiPayloads.RenderAsync(RunnersQueryService.Describe(healthy, inFlight: 0, now)));

        Assert.False(compatible.RootElement.TryGetProperty("protocolNote", out _));
    }

    [Fact]
    public async Task AnAgentRowCarriesEveryFieldTheRunnersListRenders()
    {
        var now = DateTimeOffset.UtcNow;
        var mac = Paired("mac-mini-01", ["macos", "xcode:16.2"], online: true, mode: RunnerAgentMode.Native);

        var body = await ApiPayloads.RenderAsync(RunnersQueryService.Describe(mac, inFlight: 1, now));
        using var document = JsonDocument.Parse(body);

        Assert.Equal("native", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal("mac-mini-01", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("concurrency").GetProperty("limit").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("concurrency").GetProperty("inFlight").GetInt32());
        Assert.Equal("macOS 15.2", document.RootElement.GetProperty("os").GetString());
        Assert.Equal("arm64", document.RootElement.GetProperty("arch").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("capabilities").EnumerateArray());
    }

    private static RunnerAgent Paired(
        string name,
        IReadOnlyList<string> capabilities,
        bool online,
        int protocolVersion = 1,
        RunnerAgentMode mode = RunnerAgentMode.Docker)
    {
        var agent = RunnerAgent.Invite(Guid.CreateVersion7(), name, "hash");

        agent.CompletePairing(
            "credential-hash",
            name,
            mode,
            "0.4.1",
            protocolVersion,
            RunnerAgent.DefaultConcurrency,
            new RunnerAgentPlatform("macOS 15.2", "arm64", "osx-arm64", name, 8),
            RunnerCapability.ExpandAll(capabilities));

        if (online)
        {
            agent.Heartbeat();
        }

        return agent;
    }
}
