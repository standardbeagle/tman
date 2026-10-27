using System.Text.Json;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// `tman queue --json` is the machine's capacity as admission sees it, for a scheduler deciding
/// what to start next: each queue's budget, the admissions holding it and what they book, the line
/// in order, and the queue's own verdict on a run of a given cost arriving now. The queue's records
/// are seeded in this process — their runner is this test, so the tman it starts sees them live.
/// </summary>
[Collection("cwd")]
public class QueueJsonTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _cwd = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public QueueJsonTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        _home.WriteFile("tman.kdl", """
            queue "q" {
                cores 4
                mem 8g
                queue-timeout "1h"
            }
            queue "idle" {
                cores 2
                mem 2g
            }
            """);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
        _cwd.Dispose();
    }

    void Seed(string id, double cores, long memMb, bool admitted, TimeSpan ago,
        bool exclusive = false, bool hold = false, string? task = null, string queue = "q")
    {
        var me = ProcUtil.OwnStart();
        var at = DateTime.UtcNow - ago;
        Store.Save(new RunRecord
        {
            Id = id, Name = id, Command = "/bin/sh", Args = ["-c", "work"], Group = id + "@" + _home.Path,
            State = admitted ? RunState.Running : RunState.Queued, Queue = queue,
            QueuedUtc = at, AdmittedUtc = admitted ? at : null, AdmittedCores = cores, AdmittedMemMb = memMb,
            Exclusive = exclusive, Hold = hold,
            StartedUtc = at, HeartbeatUtc = DateTime.UtcNow, Cwd = "/tmp",
            Pid = admitted ? Environment.ProcessId : 0, ChildStartUtc = admitted ? me.Utc : null,
            ChildStartTicks = admitted ? me.Ticks : null,
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
            Labels = task is null ? null : new(StringComparer.Ordinal) { ["task"] = task },
        });
    }

    async Task<JsonElement> Queues(params string[] args)
    {
        var result = await TmanProcess.Run(_home.Path, _cwd.Path, null, ["queue", "--json", .. args]);
        Assert.True(result.Exit == 0, result.Err);
        var root = JsonDocument.Parse(result.Out).RootElement.Clone();
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        return root;
    }

    static JsonElement Named(JsonElement root, string name) =>
        root.GetProperty("queues").EnumerateArray().Single(q => q.GetProperty("name").GetString() == name);

    [UnixFact("probes this test's own pid as the seeded runs' runner")]
    public async Task AQueue_ReportsItsBudgetAdmissionsLineAndVerdict()
    {
        Seed("admitted0001", cores: 3, memMb: 2048, admitted: true, TimeSpan.FromMinutes(2), task: "T1");
        Seed("waiting00001", cores: 2, memMb: 1024, admitted: false, TimeSpan.FromMinutes(1), task: "T2");
        Seed("waiting00002", cores: 1, memMb: 512, admitted: false, TimeSpan.FromSeconds(30), task: "T3");

        var root = await Queues();
        Assert.True(root.GetProperty("host").GetProperty("cores").GetDouble() > 0);
        var q = Named(root, "q");
        Assert.Equal(4, q.GetProperty("budget").GetProperty("cores").GetDouble());
        Assert.Equal(8192, q.GetProperty("budget").GetProperty("memMb").GetInt64());
        Assert.Equal(3600, q.GetProperty("timeoutSeconds").GetDouble());
        Assert.Equal(3, q.GetProperty("booked").GetProperty("cores").GetDouble());
        Assert.Equal(2048, q.GetProperty("booked").GetProperty("memMb").GetInt64());
        // the tman read here measures this host's real load, which no test controls: free is the
        // budget less the admissions and whatever outside load it measured beside them
        var outside = q.GetProperty("unattributed");
        var outsideCores = outside.ValueKind == JsonValueKind.Null ? 0 : outside.GetProperty("cores").GetDouble();
        Assert.Equal(4 - 3 - outsideCores, q.GetProperty("free").GetProperty("cores").GetDouble(), precision: 6);
        Assert.False(q.GetProperty("exclusiveHeld").GetBoolean());

        var admitted = Assert.Single(q.GetProperty("admitted").EnumerateArray());
        Assert.Equal("admitted0001", admitted.GetProperty("id").GetString());
        Assert.Equal(3, admitted.GetProperty("cores").GetDouble());
        Assert.Equal(2048, admitted.GetProperty("memMb").GetInt64());
        Assert.Equal(JsonValueKind.String, admitted.GetProperty("admittedAt").ValueKind);
        Assert.Equal("T1", admitted.GetProperty("labels").GetProperty("task").GetString());

        var line = q.GetProperty("waiting").EnumerateArray().ToList();
        Assert.Equal(["waiting00001", "waiting00002"], line.Select(w => w.GetProperty("id").GetString()));
        Assert.Equal([1, 2], line.Select(w => w.GetProperty("position").GetInt32()));
        Assert.True(line[0].GetProperty("waitedSeconds").GetDouble() >= 55);
        Assert.Equal(2, line[0].GetProperty("cores").GetDouble());
        Assert.False(line[0].GetProperty("starving").GetBoolean());

        // the verdict under this host's load is not the test's to know; the cost it was asked for is
        Assert.Equal(1, q.GetProperty("probe").GetProperty("cores").GetDouble());
        Assert.Equal(1024, q.GetProperty("probe").GetProperty("memMb").GetInt64());
        Assert.Contains(q.GetProperty("probe").GetProperty("admitsNow").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        var bigger = Named(await Queues("--cores", "2", "--mem", "1g"), "q");
        Assert.Equal(2, bigger.GetProperty("probe").GetProperty("cores").GetDouble());

        // an empty queue admits a lone run whatever the host is doing
        var idle = Named(root, "idle");
        Assert.Empty(idle.GetProperty("admitted").EnumerateArray());
        Assert.True(idle.GetProperty("probe").GetProperty("admitsNow").GetBoolean());
    }

    [UnixFact("probes this test's own pid as the seeded runs' runner")]
    public void TheVerdict_IsTheAdmissionRule_AskedAboutARunArrivingNow()
    {
        // in process, where the suite fakes an unmeasured host, so the verdict is the rule's alone
        Seed("admitted0001", cores: 3, memMb: 2048, admitted: true, TimeSpan.FromMinutes(2));
        Seed("waiting00001", cores: 2, memMb: 1024, admitted: false, TimeSpan.FromMinutes(1));
        var queue = Config.Queue("q");
        var all = Store.LoadAll();

        // the default cost (1 core, 1 GiB) fits the core left and the earlier 2-core waiter does
        // not, so the arrival may slip into the gap
        var small = QueueReport.Take(queue, all, null, null, DateTime.UtcNow);
        Assert.Equal(new Admission.AdmissionCost(1, 1024), small.Probe);
        Assert.Equal(new Admission.AdmissionCost(1, 6144), small.Free);
        Assert.True(small.AdmitsProbe);
        // a 2-core arrival fits no better than the waiter ahead of it, so it queues behind
        Assert.False(QueueReport.Take(queue, all, 2, 1024, DateTime.UtcNow).AdmitsProbe);
        // nor does a 1-core one once outside load fills the gap
        var prevHost = Admission.MeasureHost;
        try
        {
            Admission.MeasureHost = () => new Host.Usage(Cores: 3 + 0.5, MemMb: 2048);
            var loaded = QueueReport.Take(queue, all, null, null, DateTime.UtcNow);
            Assert.Equal(new Admission.AdmissionCost(0.5, 0), loaded.Unattributed);
            Assert.Equal(0.5, loaded.Free.Cores, precision: 6);
            Assert.False(loaded.AdmitsProbe);
        }
        finally { Admission.MeasureHost = prevHost; }
    }

    [UnixFact("probes this test's own pid as the seeded run's runner")]
    public async Task AnExclusiveAdmission_HoldsTheQueueAgainstEveryFiniteArrival()
    {
        Seed("exclusive001", cores: 1, memMb: 512, admitted: true, TimeSpan.FromSeconds(5), exclusive: true);

        var q = Named(await Queues("--cores", "0.5", "--mem", "100"), "q");
        Assert.True(q.GetProperty("exclusiveHeld").GetBoolean());
        Assert.True(Assert.Single(q.GetProperty("admitted").EnumerateArray()).GetProperty("exclusive").GetBoolean());
        Assert.False(q.GetProperty("probe").GetProperty("admitsNow").GetBoolean());
    }

    [UnixFact("probes this test's own pid as the seeded runs' runner")]
    public async Task ALabelFilter_NarrowsTheEntries_NeverTheTotals()
    {
        Seed("admitted0001", cores: 1, memMb: 1024, admitted: true, TimeSpan.FromMinutes(1), task: "T1");
        Seed("admitted0002", cores: 2, memMb: 1024, admitted: true, TimeSpan.FromMinutes(1), task: "T2");
        Seed("waiting00001", cores: 3, memMb: 1024, admitted: false, TimeSpan.FromSeconds(20), task: "T1");
        Seed("waiting00002", cores: 3, memMb: 1024, admitted: false, TimeSpan.FromSeconds(10), task: "T2");

        var q = Named(await Queues("--label", "task=T2"), "q");
        Assert.Equal("admitted0002", Assert.Single(q.GetProperty("admitted").EnumerateArray()).GetProperty("id").GetString());
        var waiter = Assert.Single(q.GetProperty("waiting").EnumerateArray());
        Assert.Equal("waiting00002", waiter.GetProperty("id").GetString());
        // its place is in the whole line, not among the runs the filter kept
        Assert.Equal(2, waiter.GetProperty("position").GetInt32());
        Assert.Equal(3, q.GetProperty("booked").GetProperty("cores").GetDouble());
        Assert.Equal(2, q.GetProperty("admittedCount").GetInt32());
        Assert.Equal(2, q.GetProperty("waitingCount").GetInt32());
    }

    [UnixFact("runs sleep through the built tman")]
    public async Task AHold_IsReportedWithItsLabels()
    {
        var hold = TmanProcess.Run(_home.Path, _cwd.Path, null,
            "hold", "--cores", "2", "--mem", "1g", "--queue", "q", "--label", "task=T7", "--pid", "999999999");
        // a hold for a pid that is gone ends at once; one for a live pid is what a scheduler books
        Assert.Equal(0, (await hold).Exit);
        var list = await TmanProcess.Run(_home.Path, _cwd.Path, null, "list", "--json", "--all", "--label", "task=T7");
        var run = Assert.Single(JsonDocument.Parse(list.Out).RootElement.GetProperty("runs").EnumerateArray());
        Assert.True(run.GetProperty("hold").GetBoolean());
        Assert.Equal("q", run.GetProperty("queue").GetString());
    }

    [UnixFact("runs the built tman beside a seeded exclusive admission")]
    public async Task ARunThatTimesOutInTheQueue_IsListedAsAQueueTimeout()
    {
        Seed("exclusive001", cores: 1, memMb: 512, admitted: true, TimeSpan.FromSeconds(5), exclusive: true);

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, null,
            "run", "--queue", "q", "--queue-timeout", "1s", "--label", "task=T9", "--", "true");
        Assert.Equal(75, ran.Exit);

        var list = await TmanProcess.Run(_home.Path, _cwd.Path, null, "list", "--json", "--all", "--label", "task=T9");
        var run = Assert.Single(JsonDocument.Parse(list.Out).RootElement.GetProperty("runs").EnumerateArray());
        Assert.Equal("exited", run.GetProperty("state").GetString());
        Assert.Equal("queue-timeout", run.GetProperty("endReason").GetString());
        Assert.Equal(JsonValueKind.String, run.GetProperty("queuedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("admittedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("startedAt").ValueKind);
        Assert.Equal(0, run.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task AMachineWithNoQueues_ReportsNone()
    {
        File.Delete(Path.Combine(_home.Path, "tman.kdl"));
        var root = await Queues();
        Assert.Empty(root.GetProperty("queues").EnumerateArray());
    }

    [Fact]
    public async Task AQueueTheMachineDoesNotDeclare_IsRefused()
    {
        var result = await TmanProcess.Run(_home.Path, _cwd.Path, null, "queue", "nope", "--json");
        Assert.Equal(127, result.Exit);
        Assert.Contains("queue \"nope\" is not declared", result.Err);
    }

    [UnixFact("probes this test's own pid as the seeded run's runner")]
    public async Task TheTextForm_NamesEachQueueAndItsEntries()
    {
        Seed("admitted0001", cores: 3, memMb: 2048, admitted: true, TimeSpan.FromMinutes(2));
        Seed("waiting00001", cores: 2, memMb: 1024, admitted: false, TimeSpan.FromMinutes(1));

        var result = await TmanProcess.Run(_home.Path, _cwd.Path, null, "queue", "q");
        Assert.Equal(0, result.Exit);
        Assert.StartsWith("queue \"q\": budget 4 cores, 8.0GB; booked 3 cores, 2.0GB;", result.Out);
        Assert.Contains("admitted0001", result.Out);
        Assert.Contains("#1  waiting00001", result.Out);
        Assert.DoesNotContain("\"idle\"", result.Out);
    }
}
