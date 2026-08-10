using Charter.Agent.Jobs;

namespace Charter.Tests;

/// <summary>
/// Leases (section 33.4). A claim carries a TTL renewed by heartbeat; a crashed agent's jobs return
/// to the queue because it stops renewing, and a partitioned agent stops working because it notices.
/// </summary>
public class AgentLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AClaimIsHeldUntilItsTtlLapses()
    {
        var leases = new LeaseBook();
        leases.Add("job-1", Now.AddMinutes(5));

        Assert.True(leases.Holds("job-1"));
        Assert.Equal(1, leases.Count);
        Assert.Empty(leases.TakeExpired(Now.AddMinutes(4)));
        Assert.Equal(["job-1"], leases.TakeExpired(Now.AddMinutes(5)));
        Assert.Equal(0, leases.Count);
    }

    [Fact]
    public void AHeartbeatRenewalPushesTheDeadlineOut()
    {
        var leases = new LeaseBook();
        leases.Add("job-1", Now.AddMinutes(5));

        Assert.True(leases.Renew("job-1", Now.AddMinutes(10)));

        Assert.Empty(leases.TakeExpired(Now.AddMinutes(9)));
        Assert.Equal(["job-1"], leases.TakeExpired(Now.AddMinutes(10)));
    }

    [Fact]
    public void RenewingAJobThisAgentDoesNotHoldChangesNothing()
    {
        // The control plane may acknowledge a lease the agent has already given up. Accepting it
        // would resurrect a job another agent now owns.
        var leases = new LeaseBook();

        Assert.False(leases.Renew("job-1", Now.AddMinutes(10)));
        Assert.Equal(0, leases.Count);
    }

    [Fact]
    public void ExpiryTakesOnlyTheLapsedLeases()
    {
        var leases = new LeaseBook();
        leases.Add("job-1", Now.AddMinutes(1));
        leases.Add("job-2", Now.AddMinutes(9));
        leases.Add("job-3", Now.AddMinutes(1));

        var expired = leases.TakeExpired(Now.AddMinutes(2));

        Assert.Equal(["job-1", "job-3"], expired);
        Assert.Equal(["job-2"], leases.HeldJobIds);
        Assert.Equal(Now.AddMinutes(9), leases.EarliestExpiry);
    }

    [Fact]
    public void ReleasingIsIdempotent()
    {
        var leases = new LeaseBook();
        leases.Add("job-1", Now.AddMinutes(5));

        Assert.True(leases.Release("job-1"));
        Assert.False(leases.Release("job-1"));
        Assert.Null(leases.EarliestExpiry);
    }

    [Fact]
    public void DrainingHandsBackEverythingAtOnce()
    {
        var leases = new LeaseBook();
        leases.Add("job-2", Now.AddMinutes(5));
        leases.Add("job-1", Now.AddMinutes(5));

        Assert.Equal(["job-1", "job-2"], leases.TakeAll());
        Assert.Equal(0, leases.Count);
    }
}
