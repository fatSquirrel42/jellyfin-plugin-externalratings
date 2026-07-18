using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class ExclusiveOperationGateTests
{
    [Fact]
    public void FreshGate_AllOperationsAvailable_NoFlagsSet()
    {
        var gate = new ExclusiveOperationGate();

        gate.IsFullRunInProgress.Should().BeFalse();
        gate.IsRestoreInProgress.Should().BeFalse();
    }

    [Fact]
    public void FullRunInProgress_BlocksEveryOtherOperation()
    {
        var gate = new ExclusiveOperationGate();

        gate.TryBeginFullRun().Should().BeTrue();
        gate.IsFullRunInProgress.Should().BeTrue();

        // While a full run holds the gate, nothing else may begin — including a second full run.
        gate.TryBeginRestore().Should().BeFalse();
        gate.TryBeginClearCache().Should().BeFalse();
        gate.TryBeginFullRun().Should().BeFalse();
    }

    [Fact]
    public void RestoreInProgress_BlocksFullRun_Symmetric()
    {
        var gate = new ExclusiveOperationGate();

        gate.TryBeginRestore().Should().BeTrue();
        gate.IsRestoreInProgress.Should().BeTrue();
        gate.IsFullRunInProgress.Should().BeFalse();

        gate.TryBeginFullRun().Should().BeFalse();
        gate.TryBeginClearCache().Should().BeFalse();
        gate.TryBeginRestore().Should().BeFalse();
    }

    [Fact]
    public void End_ReleasesTheGate_AllowingTheNextOperation()
    {
        var gate = new ExclusiveOperationGate();

        gate.TryBeginFullRun().Should().BeTrue();
        gate.End();

        gate.IsFullRunInProgress.Should().BeFalse();
        gate.TryBeginRestore().Should().BeTrue();
        gate.IsRestoreInProgress.Should().BeTrue();

        gate.End();
        gate.IsRestoreInProgress.Should().BeFalse();
        gate.TryBeginClearCache().Should().BeTrue();
    }
}
