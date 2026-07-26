using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// The skip decision itself. On this Linux fleet the unsupported branch of every platform fact is
/// never taken by a real run, so left to the platform alone it would be unexecuted code that the
/// suite's honesty depends on — the shape rule 2 of honest-failure-and-verification exists to catch.
/// Each attribute therefore takes its verdict as a parameter, with the production constructor a thin
/// delegate over the real predicate, so both branches are observable here on every platform.
/// </summary>
public class PlatformFactTests
{
    [Fact]
    public void UnixFact_OffUnix_CarriesTheReasonAsItsSkip()
        => Assert.Equal("needs a POSIX shell", new UnixFactAttribute("needs a POSIX shell", onUnix: false).Skip);

    [Fact]
    public void UnixFact_OnUnix_DoesNotSkip()
        => Assert.Null(new UnixFactAttribute("needs a POSIX shell", onUnix: true).Skip);

    /// <summary>
    /// Windows is the third supported platform and the one this defect was found on, so asking it
    /// directly is independent of the disjunction the attribute delegates to: a verdict wired
    /// backwards fails here rather than going quiet until CI runs.
    /// </summary>
    [Fact]
    public void UnixFact_TakesItsVerdictFromTheRunningPlatform()
        => Assert.Equal(OperatingSystem.IsWindows(), new UnixFactAttribute("reason").Skip is not null);

    [Fact]
    public void TreeSamplingFact_WithoutTreeCoverage_CarriesTheReasonAsItsSkip()
        => Assert.Equal("needs /proc", new TreeSamplingFactAttribute("needs /proc", coversTree: false).Skip);

    [Fact]
    public void TreeSamplingFact_WithTreeCoverage_DoesNotSkip()
        => Assert.Null(new TreeSamplingFactAttribute("needs /proc", coversTree: true).Skip);

    [Fact]
    public void TreeSamplingFact_TakesItsVerdictFromTheProductionPredicate()
        => Assert.Equal(!TreeStats.CoversTree, new TreeSamplingFactAttribute("reason").Skip is not null);
}
