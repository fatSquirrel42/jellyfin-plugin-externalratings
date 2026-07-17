using FluentAssertions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

/// <summary>
/// Placeholder smoke test proving the environment/test harness is wired up.
/// The real spec-driven test catalogs (§12.1) replace this during the TDD build.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void TestHarness_IsWiredUp()
    {
        true.Should().BeTrue();
    }
}
