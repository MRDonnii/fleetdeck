using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>"Hold kørende": en tjeneste, systemd bevidst springer over (betingelse ikke opfyldt), er ikke en fejl.</summary>
public class KeptSkippedTests
{
    [Theory]
    [InlineData("inactive", "no", true)]       // ExecCondition/Condition… ikke opfyldt (fx NFS-watchdog på SD-kort)
    [InlineData("inactive\n", " no\n", true)]
    [InlineData("inactive", "yes", false)]     // stoppet helt almindeligt
    [InlineData("failed", "no", false)]        // fejlet er fejlet
    [InlineData("active", "no", false)]
    [InlineData("", "", false)]
    public void Skipped_by_condition(string isActive, string cond, bool expected) =>
        Assert.Equal(expected, KeptService.SkippedByCondition(isActive, cond));

    [Fact]
    public void Alert_only_when_not_running_and_not_skipped()
    {
        Assert.True(KeptService.NeedsAlert(new KeptService { Name = "a.service", Active = false, State = "failed" }));
        Assert.False(KeptService.NeedsAlert(new KeptService { Name = "a.service", Active = false, Skipped = true, State = "sprunget over" }));
        Assert.False(KeptService.NeedsAlert(new KeptService { Name = "a.service", Active = true, State = "active" }));
    }
}
