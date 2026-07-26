using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A fact whose subject only exists on Unix. Off Unix the run reports it as skipped carrying
/// <paramref name="because"/>, rather than entering a body that returns before asserting anything —
/// a green that exercised nothing is indistinguishable from a green that passed.
/// </summary>
/// <remarks>
/// xUnit 2.9.2 has no dynamic skip (<c>Assert.Skip</c> is v3), but v2 discovery reads
/// <see cref="FactAttribute.Skip"/> off the attribute instance, and the platform is already known
/// there. So the conditional fact needs no runner package and no new dependency.
/// </remarks>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string because)
        : this(because, OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) { }

    internal UnixFactAttribute(string because, bool onUnix)
    {
        if (!onUnix) Skip = because;
    }
}

/// <summary>
/// A fact that needs tman to see the whole process tree, which is <see cref="TreeStats.CoversTree"/>
/// — the same predicate production consults, so the gate cannot drift from what it guards. That
/// predicate is pinned to Linux by <c>TreeStatsTests.CoversTree_OnlyWhereParentPidsAreCheaplyAvailable</c>,
/// so naming tree coverage also carries the platform rather than restating it as a second conjunct.
/// </summary>
public sealed class TreeSamplingFactAttribute : FactAttribute
{
    public TreeSamplingFactAttribute(string because) : this(because, TreeStats.CoversTree) { }

    internal TreeSamplingFactAttribute(string because, bool coversTree)
    {
        if (!coversTree) Skip = because;
    }
}
