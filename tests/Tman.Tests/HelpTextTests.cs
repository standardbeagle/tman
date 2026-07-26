using System.Text.RegularExpressions;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// <c>tman --help</c> is the fourth copy of the cap table and the only one living inside production
/// source, so every docs-only sweep of `.md` and `site/` misses it — which is how it went on
/// describing a counting admission gate and a lock-releasing sweep for two releases after both were
/// deleted.
/// <para>
/// These assert on the help text <see cref="Program.Main"/> actually resolves and writes, not on the
/// literal in <c>Program.cs</c>. A test that re-declares the string it is checking moves with the
/// source it is meant to police and can never go red; driving the real <c>--help</c> path means a
/// behavior change that invalidates the wording turns the suite red instead.
/// </para>
/// <para>
/// They are deliberately concept-level — the presence or absence of a claim — rather than a
/// character comparison of a whole block, which would go red on rewrapping a line and teach the next
/// implementer to update the expected string without reading it.
/// </para>
/// </summary>
[Collection("cwd")]
public class HelpTextTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public HelpTextTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    /// <summary>The help text as a user reading it in a terminal gets it: through the real command.</summary>
    static async Task<string> ResolvedHelp()
    {
        var prev = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await Program.Main(new[] { "--help" }));
        }
        finally
        {
            Console.SetOut(prev);
        }

        var help = captured.ToString();
        // a capture that silently caught nothing would satisfy every "does not describe" assertion
        // below, so the absence checks are only worth anything once the text is known to be there
        Assert.Contains("run flags:", help);
        return help;
    }

    /// <summary>
    /// One flag's entry: its own line plus any continuation lines indented under it, so a claim
    /// wrapped onto a second line is still part of what the flag says.
    /// </summary>
    static string FlagEntry(string help, string flag)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(flag + " ", StringComparison.Ordinal));
        Assert.True(start >= 0, $"help has no entry for {flag}");

        var end = start + 1;
        while (end < lines.Length
               && lines[end].Trim().Length > 0
               && !lines[end].TrimStart().StartsWith("--", StringComparison.Ordinal))
            end++;
        return string.Join("\n", lines[start..end]);
    }

    /// <summary>The paragraph describing the housekeeping sweep every command runs.</summary>
    static string SweepParagraph(string help)
    {
        var i = help.IndexOf("every command sweeps", StringComparison.Ordinal);
        Assert.True(i >= 0, "help no longer describes the housekeeping sweep");
        return help[i..];
    }

    [Fact]
    public async Task MaxParallel_DescribesAdmissionByHoldingASlot_NotByCountingRuns()
    {
        var entry = FlagEntry(await ResolvedHelp(), "--max-parallel");

        // admission is Store.TryAcquireSlot: open one of the bucket's slot files with
        // FileShare.None and keep the handle. Counting was removed because every racer reads the
        // same count before any of them has a record to be counted.
        Assert.Matches(new Regex("slot", RegexOptions.IgnoreCase), entry);
        Assert.Matches(new Regex("hold|held", RegexOptions.IgnoreCase), entry);
        Assert.DoesNotMatch(new Regex(@"runs share|\bcount\w*|live runs", RegexOptions.IgnoreCase), entry);
    }

    [Fact]
    public async Task Replace_SaysItWaitsForTheNameToBeReleased_NotThatKillingIsEnough()
    {
        var entry = FlagEntry(await ResolvedHelp(), "--replace");

        // killing the child does not hand back the name — the runner holding the lock does, as it
        // winds up. CmdRun awaits that via AwaitNameLock and refuses the run if it times out, so
        // "kills the old run first" describes an outcome --replace does not guarantee.
        Assert.Matches(new Regex("wait", RegexOptions.IgnoreCase), entry);
        Assert.Matches(new Regex("queue-timeout", RegexOptions.IgnoreCase), entry);
        Assert.Matches(new Regex("release", RegexOptions.IgnoreCase), entry);
    }

    [Fact]
    public async Task Stall_CountsKernelIoWaitAsProgress()
    {
        var entry = FlagEntry(await ResolvedHelp(), "--stall");

        // TreeStats.ShowsProgress treats a tree containing an uninterruptible-sleep process as
        // progressing, so a run blocked in the kernel is not killed for being quiet.
        Assert.Matches(new Regex("io-wait", RegexOptions.IgnoreCase), entry);
    }

    [Fact]
    public async Task Sweep_ClaimsNoLockReleaseOrStaleReclamation()
    {
        var sweep = SweepParagraph(await ResolvedHelp());

        // Reaper.Sweep is ReapOrphans + Store.Prune and opens no lock file. PruneStaleLocks and
        // IsLockStale are gone: a lock whose holder died is taken over in place by the next
        // claimant, because unlinking a name two runners may already have opened is what let two
        // runs share one name.
        Assert.Matches(new Regex("orphan", RegexOptions.IgnoreCase), sweep);
        Assert.Matches(new Regex("prune|retention", RegexOptions.IgnoreCase), sweep);
        Assert.DoesNotMatch(new Regex(@"stale", RegexOptions.IgnoreCase), sweep);
        Assert.DoesNotMatch(
            new Regex(@"locks?\b[^.]*\b(released?|reclaim\w*|freed?)\b", RegexOptions.IgnoreCase),
            sweep);
    }
}
