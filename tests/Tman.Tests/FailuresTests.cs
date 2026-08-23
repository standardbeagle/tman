using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// Line shapes real runners print. The point of the pairs below is the negative half: a matcher
/// that also fires on the passing line next to it produces a digest that quotes the whole log,
/// which is the same as producing nothing.
/// </summary>
public class FailuresTests
{
    [Theory]
    [InlineData("FAILED tests/test_api.py::test_widget - AssertionError")]        // pytest
    [InlineData("E       assert 1 == 2")]                                          // pytest traceback
    [InlineData("--- FAIL: TestParse (0.00s)")]                                    // go
    [InlineData("    --- FAIL: TestParse/subtest (0.00s)")]                        // go subtest
    [InlineData("FAIL\tgithub.com/x/y\t0.014s")]                                   // go package
    [InlineData("panic: runtime error: index out of range")]                       // go
    [InlineData("  [FAIL] Tman.Tests.CapsTests.Parses")]                           // dotnet
    [InlineData("Failed!  - Failed:     2, Passed:   118")]                        // dotnet summary
    [InlineData("Failed Tman.Tests.CapsTests.Parses [12 ms]")]                     // dotnet per-test
    [InlineData("Caps.cs(31,9): error CS0103: The name 'x' does not exist")]       // roslyn
    [InlineData("src/a.ts(4,1): error TS2304: Cannot find name 'x'.")]             // tsc
    [InlineData(" FAIL  src/widget.test.ts > renders")]                            // vitest
    [InlineData("   × renders the widget")]                                        // vitest
    [InlineData("  ● Widget › renders")]                                           // jest
    [InlineData("not ok 3 - widget renders")]                                      // TAP
    public void FailureShapes_AreMatched(string line) =>
        Assert.True(Failures.IsFailureLine(line), $"missed: {line}");

    [Theory]
    [InlineData("ok      github.com/x/y  0.014s")]                                 // go pass
    [InlineData("=== RUN   TestParse")]                                            // go
    [InlineData("PASS")]
    [InlineData("tests/test_api.py .....                        [100%]")]          // pytest
    [InlineData("Passed!  - Failed:     0, Passed:   120")]                        // dotnet pass
    [InlineData("  ✓ renders the widget")]                                         // vitest pass
    [InlineData("")]
    [InlineData("   ")]
    public void PassingAndNoiseLines_AreNotMatched(string line) =>
        Assert.False(Failures.IsFailureLine(line), $"false positive: {line}");

    [Fact]
    public void Digest_QuotesEachFailureWithItsFollowingContext()
    {
        var log = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(log, new[]
            {
                "=== RUN   TestParse",
                "--- FAIL: TestParse (0.00s)",
                "    caps_test.go:31: got 5, want 7",
                "ok      github.com/x/other  0.01s",
            });

            var digest = string.Join('\n', Failures.Digest(log));

            Assert.Contains("1 failure line(s)", digest);
            Assert.Contains("--- FAIL: TestParse", digest);
            Assert.Contains("got 5, want 7", digest);       // context follows the match
            Assert.Contains("     2:", digest);             // line numbers point into the full log
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Digest_WithNothingMatched_StillQuotesTheTailAndSaysSo()
    {
        // A runner whose output nobody anticipated must degrade to "here is the end of the log",
        // never to an empty file that reads like a clean run.
        var log = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(log, new[] { "something", "unrecognisable", "happened here" });

            var digest = string.Join('\n', Failures.Digest(log));

            Assert.Contains("no failure lines matched", digest);
            Assert.Contains("happened here", digest);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Digest_IsCappedSoAWholesaleFailureCannotCopyItsOwnLog()
    {
        var log = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(log, Enumerable.Range(0, 20_000).Select(i => $"FAILED test_{i}"));

            var digest = Failures.Digest(log);

            Assert.True(digest.Count < Failures.MaxDigestLines + Failures.TailLines + 10,
                $"digest was {digest.Count} lines");
            Assert.Contains(digest, l => l.Contains("capped at"));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Digest_OfAMissingLog_ReturnsAnEmptyReportRatherThanThrowing() =>
        Assert.NotEmpty(Failures.Digest(Path.Combine(Path.GetTempPath(), "tman-no-such-" + Guid.NewGuid())));
}
