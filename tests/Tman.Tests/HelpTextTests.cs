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
/// Each assertion names one fact the claim must state and proves it is stated, rather than listing
/// phrasings that must not appear. A blacklist can only exclude the mistake already made: the
/// earlier form of this file banned the exact words the two wrong texts used, and two rewrites of
/// the same two false claims passed it. Presence checks also mean any phrasing that carries the
/// fact is allowed, so a faithful rewrite of an entry does not go red — the earlier form required
/// the literals "wait" and "release" from --replace and so punished a more complete restatement.
/// </para>
/// <para>
/// <b>What these cannot catch.</b> Prose is not fully pinnable and this file does not pretend
/// otherwise:
/// </para>
/// <list type="bullet">
/// <item>They prove a fact is <i>stated</i>, never that the surrounding sentence is <i>true</i>. A
/// fluent, wholly false entry that still carries the required structures passes. The regexes raise
/// the cost of writing one; they do not make it impossible.</item>
/// <item><b>Known survivors, measured not assumed.</b> Two rewordings of the deleted counting
/// admission gate pass <see cref="MaxParallel_SaysNCountsSlotsAndTheQueueEndsOnTakingOne"/>:
/// <c>queue until one of this bucket's N slot files can be held, admitted by tallying how many are
/// live</c>, and <c>queue while other runs hold this bucket's N slots, admitted once the live slot
/// count is taken below N</c>. Both name the slot pool and a holding verb and then describe
/// admission by tally anyway. No proximity form separates them from the true text: <c>slot files
/// can be held</c> and <c>slot count is taken</c> have the same shape — one noun between the slot
/// and the verb — so the only regex that excludes them would ban the word the wrong text happens to
/// use, which is the blacklist this file exists to stop building. Cruder counting-gate wordings do
/// die (<c>queue while N runs share this bucket</c>, and the same mutant with the slot noun removed),
/// so the assertion is not inert; its edge is exactly here. Closing it needs a reader, not another
/// conjunct.</item>
/// <item>Nothing here reads <c>Store.TryAcquireSlot</c> or <c>Reaper.Sweep</c>. The invariants were
/// transcribed from those mechanisms by hand, so if a mechanism changes again and no one updates
/// this file, these tests keep passing while the help text goes wrong — the exact drift they exist
/// to catch, one level up. Only a reader comparing the two closes that gap.</item>
/// <item>They cover the four claims that were wrong before and nothing else. The rest of the help
/// text is unpinned.</item>
/// <item><c>Console.SetOut</c> is process-global and this class is serialized only against the
/// other <c>Collection("cwd")</c> classes, so a class outside that collection can write into the
/// capture. Presence checks are monotone, so foreign text cannot cause a false failure — the
/// hazard runs the other way, and a foreign line carrying the right words turns a false help text
/// green. <see cref="ResolvedHelp"/> therefore reads only the write that produced the document
/// and requires exactly one of them; foreign writes are separate segments and are not read. What
/// that does not cover is a foreign writer that splices text into the same write call, which
/// nothing at this level can see.</item>
/// </list>
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

    /// <summary>
    /// Keeps each write its own segment instead of concatenating them, so text one writer emitted
    /// can be told apart from text another writer emitted into the same stream.
    /// </summary>
    sealed class SegmentedWriter : TextWriter
    {
        readonly List<string> _segments = new();
        System.Text.StringBuilder? _pending;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public IReadOnlyList<string> Segments
        {
            get
            {
                EndPending();
                return _segments;
            }
        }

        void EndPending()
        {
            if (_pending is null) return;
            _segments.Add(_pending.ToString());
            _pending = null;
        }

        // char-at-a-time writes have no boundary of their own, so they accumulate until some
        // other call ends the run
        public override void Write(char value) => (_pending ??= new()).Append(value);

        public override void Write(string? value)
        {
            EndPending();
            if (value is not null) _segments.Add(value);
        }

        public override void WriteLine(string? value)
        {
            EndPending();
            _segments.Add(value ?? string.Empty);
        }
    }

    /// <summary>The help text as a user reading it in a terminal gets it: through the real command.</summary>
    static async Task<string> ResolvedHelp()
    {
        var prev = Console.Out;
        var captured = new SegmentedWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await Program.Main(new[] { "--help" }));
        }
        finally
        {
            Console.SetOut(prev);
        }

        // Console.SetOut is process-global and this class is serialized only against the other
        // Collection("cwd") classes, so a class outside that collection can write into the
        // redirect. Every assertion below is a presence check and a presence check is monotone:
        // added text can only turn one green, never red, so a polluted capture is a false pass and
        // not a false failure — the more dangerous direction. PrintUsage emits the whole document
        // in a single WriteLine, so the help is the one segment carrying the "run flags:" heading
        // and a foreign writer's output is a different segment that no assertion reads.
        //
        // Requiring exactly one such segment is the canary. Zero means the capture caught nothing
        // and every "the claim states X" assertion would be measuring foreign text or empty
        // string; more than one means something else emitted a help document and there is no
        // longer a single answer to which one Main resolved.
        var documents = captured.Segments
            .Where(s => s.Contains("run flags:", StringComparison.Ordinal))
            .ToList();
        Assert.Single(documents);
        return documents[0];
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

    /// <summary>
    /// The paragraph describing the housekeeping sweep: the run of non-blank lines starting at the
    /// one that opens it. Bounded by the paragraph rather than running to the end of the document,
    /// so the position of an entry in the usage block is not inside what the sweep assertions read.
    /// The paragraph is the last thing <c>PrintUsage</c> writes, so this bound relies on
    /// <see cref="ResolvedHelp"/> having already excluded foreign output: appended foreign text is
    /// contiguous with the paragraph and a blank line would not separate it.
    /// </summary>
    static string SweepParagraph(string help)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(
            lines, l => l.Contains("every command sweeps", StringComparison.Ordinal));
        Assert.True(start >= 0, "help no longer describes the housekeeping sweep");

        var end = start + 1;
        while (end < lines.Length && lines[end].Trim().Length > 0) end++;
        return string.Join("\n", lines[start..end]);
    }

    [Fact]
    public async Task MaxParallel_SaysNCountsSlotsAndTheQueueEndsOnTakingOne()
    {
        var entry = FlagEntry(await ResolvedHelp(), "--max-parallel");

        // admission is Store.TryAcquireSlot: open one of the bucket's slot files with
        // FileShare.None and keep the handle. Counting was removed because every racer reads the
        // same count before any of them has a record to be counted.
        //
        // Invariant 1: N is the size of the slot pool, not a population of runs. This is the whole
        // difference between the two mechanisms — a counting gate quantifies runs ("N runs share
        // this bucket"), a slot gate quantifies slots ("this bucket's N slot files"). Proved by
        // requiring N to sit within a few words of the noun it quantifies.
        Assert.Matches(new Regex(@"\bN\b(?:\W+\w+){0,3}\W+slots?\b", RegexOptions.IgnoreCase), entry);

        // Invariant 2: the queue terminates on this run obtaining a slot — a reaching condition
        // ("queue until ... can be held"), not a duration ("queue while other runs are live").
        // Ordered, so a sentence that merely mentions slots somewhere does not satisfy it.
        //
        // What this does NOT prove is that admission is by obtaining a handle rather than by
        // reading a tally: a counting gate reworded to name the slot pool and a holding verb
        // passes. Both surviving mutants are quoted in this class's docstring. Do not answer them
        // by adding a branch that bans the wording they used.
        Assert.Matches(
            new Regex(@"\b(until|once|when)\b[^.]*\bslots?\b[^.]*\b(held|hold|acquir\w*|taken?)\b",
                RegexOptions.IgnoreCase),
            entry);
    }

    [Fact]
    public async Task Replace_SaysKillingIsNotTheEnd_AndTheBoundedWaitCanRefuseTheRun()
    {
        var entry = FlagEntry(await ResolvedHelp(), "--replace");

        // killing the child does not hand back the name — the runner holding the lock does, as it
        // winds up. CmdRun awaits that via AwaitNameLock and refuses the run if it times out, so
        // "kills the old run first" describes an outcome --replace does not guarantee.
        //
        // Invariant 1: the kill is followed by a second step. Proved by requiring a sequencing word
        // after the kill verb, rather than by requiring the literal "wait" — "then blocks until"
        // states the same fact and must be allowed to.
        Assert.Matches(
            new Regex(@"\bkill\w*\b[^.]*\b(then|until|before|and then)\b", RegexOptions.IgnoreCase),
            entry);

        // Invariant 2: that second step is bounded by --queue-timeout. The only real identifier
        // among the things this entry must say, so it is pinned as a literal.
        Assert.Matches(new Regex("--queue-timeout", RegexOptions.IgnoreCase), entry);

        // Invariant 3: exhausting the bound does not start the run. "kill the old run first" claims
        // an unconditional outcome and says nothing here; any honest phrasing negates starting.
        //
        // The negation has to carry its condition, or it is satisfied by a refusal for some other
        // reason entirely: "then start the new one at once (--queue-timeout is ignored); does not
        // start if the name is invalid" met all three invariants of this test with three unrelated
        // clauses while describing the opposite behavior. What must be stated is that the run is
        // refused *because the name is still held when the bound runs out* — so the negated start
        // reaches the holding or the timeout.
        Assert.Matches(
            new Regex(@"\b(refus\w*|will not|won't|does not|doesn't|never|cannot|can't)\b[^.]*\bstart\w*\b"
                    + @"[^.]*\b(held|holds|holding|hold|time[ -]?out\w*|timed out|expir\w*|elaps\w*)\b",
                RegexOptions.IgnoreCase),
            entry);
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
    public async Task Sweep_NamesItsTwoSubjects_AndSaysADeadHoldersLockIsTakenOverNotRemoved()
    {
        var sweep = SweepParagraph(await ResolvedHelp());

        // Reaper.Sweep is ReapOrphans + Store.Prune and opens no lock file. PruneStaleLocks and
        // IsLockStale are gone: a lock whose holder died is taken over in place by the next
        // claimant, because unlinking a name two runners may already have opened is what let two
        // runs share one name.
        //
        // Invariants 1 and 2: the sweep's two subjects, stated.
        Assert.Matches(new Regex("orphan", RegexOptions.IgnoreCase), sweep);
        Assert.Matches(new Regex("prune|retention", RegexOptions.IgnoreCase), sweep);

        // Invariant 3: a dead holder's lock is taken over *in place* by the next claimant. One
        // ordered pattern over one sentence rather than two searches over the paragraph: as two,
        // the takeover half and the in-place half could be satisfied by unrelated clauses, and
        // "a dead holder's lock file is removed in place, so the next run that claims the bucket
        // creates a fresh one" met both — the exact negation of the fact, passing the check named
        // for it, because a bare "in place" anywhere counted and "claims" supplied the takeover.
        //
        // So the lock noun must reach a takeover verb, and "in place" must attach to that verb
        // rather than float: it is the takeover that happens in place, and a removal cannot.
        Assert.Matches(
            new Regex(
                @"\b(lock|bucket)\w*\b[^.]*"
                    + @"\b(taken over|takeover|reclaim\w*|reus\w*|inherit\w*)\b"
                    + @"(?:\W+\w+){0,2}\W+in place\b",
                RegexOptions.IgnoreCase),
            sweep);
    }
}
