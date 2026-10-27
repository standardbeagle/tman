using System.Text.Json;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// `tman list --json` is the run store as another program reads it: one document, a schema number,
/// and each run's identity, scope, lifecycle and outcome named outright, so a supervisor never
/// matches runs by directory and time or decodes a state number out of tman's own record file.
/// </summary>
public class ListJsonTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _cwd = new();

    public void Dispose()
    {
        _home.Dispose();
        _cwd.Dispose();
    }

    Task<TmanProcess.Result> Tman(params string[] args) => TmanProcess.Run(_home.Path, _cwd.Path, null, args);

    async Task<List<JsonElement>> Runs(params string[] args)
    {
        var result = await Tman(["list", "--json", .. args]);
        Assert.Equal(0, result.Exit);
        var root = JsonDocument.Parse(result.Out).RootElement.Clone();
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        return root.GetProperty("runs").EnumerateArray().ToList();
    }

    static string? Str(JsonElement e, string name) =>
        e.GetProperty(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    static bool IsNull(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null;

    [UnixFact("runs sh through the built tman")]
    public async Task AFinishedRun_ReportsItsIdentityScopeAndOutcome()
    {
        var ran = await TmanProcess.Run(_home.Path, _cwd.Path,
            new Dictionary<string, string> { ["TMAN_LABELS"] = "task=T1" },
            "run", "--no-queue", "--name", "build", "--", "sh", "-c", "exit 3");
        Assert.Equal(3, ran.Exit);

        var run = Assert.Single(await Runs("--all"));
        Assert.Equal(12, Str(run, "id")!.Length);
        Assert.Equal("build", Str(run, "name"));
        Assert.True(IsNull(run, "alias"));
        Assert.Equal(TempDir.RealPath(_cwd.Path), Str(run, "cwd"));
        Assert.True(run.GetProperty("pid").GetInt32() > 0);
        Assert.Equal("exited", Str(run, "state"));
        Assert.Equal("exit", Str(run, "endReason"));
        Assert.Equal(3, run.GetProperty("exitCode").GetInt32());
        Assert.Equal(3, run.GetProperty("tmanExit").GetInt32());
        Assert.False(run.GetProperty("killRequested").GetBoolean());
        Assert.True(IsNull(run, "queuedAt"));
        Assert.True(IsNull(run, "admittedAt"));
        var started = run.GetProperty("startedAt").GetDateTime();
        var ended = run.GetProperty("endedAt").GetDateTime();
        Assert.True(ended >= started, $"ended {ended:O} before it started {started:O}");
        Assert.True(run.GetProperty("peakMemMb").GetInt64() >= 0);
        Assert.Equal("T1", run.GetProperty("labels").GetProperty("task").GetString());
    }

    [UnixFact("runs true through the built tman")]
    public async Task AnAliasRun_NamesItsAlias()
    {
        _cwd.WriteFile(".tman.kdl", "alias \"check\" {\n    command \"true\"\n}\n");
        Assert.Equal(0, (await Tman("check")).Exit);

        var run = Assert.Single(await Runs("--all"));
        Assert.Equal("check", Str(run, "alias"));
        Assert.Equal("check", Str(run, "name"));
    }

    [UnixFact("runs sleep through the built tman")]
    public async Task ALiveRun_IsListedRunning_AndOnceKilledNamesTheKill()
    {
        var running = TmanProcess.Run(_home.Path, _cwd.Path, null, "run", "--no-queue", "--", "sleep", "30");
        JsonElement live = default;
        for (var i = 0; i < 200; i++)
        {
            if ((await Runs()).FirstOrDefault(r => Str(r, "state") == "running") is { ValueKind: JsonValueKind.Object } r)
            { live = r; break; }
            await Task.Delay(50);
        }
        Assert.Equal(JsonValueKind.Object, live.ValueKind);
        Assert.True(live.GetProperty("pid").GetInt32() > 0);
        Assert.False(IsNull(live, "startedAt"));
        Assert.True(IsNull(live, "endedAt"));
        Assert.True(IsNull(live, "endReason"));
        Assert.True(IsNull(live, "tmanExit"));

        Assert.Equal(0, (await Tman("kill", Str(live, "id")!)).Exit);
        Assert.Equal(75, (await running).Exit);

        var killed = Assert.Single(await Runs("--all"));
        Assert.Equal("exited", Str(killed, "state"));
        Assert.Equal("killed", Str(killed, "endReason"));
        Assert.True(killed.GetProperty("killRequested").GetBoolean());
        Assert.Equal(75, killed.GetProperty("tmanExit").GetInt32());
        Assert.True(IsNull(killed, "exitCode"), "a killed child did not exit with a code of its own");
        Assert.False(IsNull(killed, "endedAt"));
    }

    [UnixTheory("runs sh through the built tman")]
    [InlineData("--max-time", "1s", "sleep 30", "max-time", 124)]
    [InlineData("--stall", "1s", "sleep 30", "stall", 125)]
    // a busy shell is one core, which is above 0.1% of any host
    [InlineData("--max-cpu", "0.1", "while :; do :; done", "cpu", 126)]
    [InlineData("--max-mem", "1", "x=$(head -c 4000000 /dev/zero | tr '\\0' a); sleep 30", "mem", 126)]
    public async Task ARunTmanEnded_NamesTheCapThatEndedIt(string flag, string value, string script, string reason, int exit)
    {
        var ran = await Tman("run", "--no-queue", flag, value, "--", "sh", "-c", script);
        Assert.Equal(exit, ran.Exit);

        var run = Assert.Single(await Runs("--all"));
        Assert.Equal("exited", Str(run, "state"));
        Assert.Equal(reason, Str(run, "endReason"));
        Assert.Equal(exit, run.GetProperty("tmanExit").GetInt32());
    }

    [UnixFact("runs true through the built tman")]
    public async Task ALabelFilter_KeepsOnlyTheRunsCarryingIt_InBothForms()
    {
        foreach (var task in new[] { "A", "B" })
            Assert.Equal(0, (await TmanProcess.Run(_home.Path, _cwd.Path, null,
                "run", "--no-queue", "--label", $"task={task}", "--label", "host=h1", "--", "true")).Exit);

        var all = await Runs("--all");
        Assert.Equal(2, all.Count);
        var a = Assert.Single(await Runs("--all", "--label", "task=A", "--label", "host=h1"));
        Assert.Equal("A", a.GetProperty("labels").GetProperty("task").GetString());
        Assert.Empty(await Runs("--all", "--label", "task=A", "--label", "host=h2"));

        var bId = Str(all.Single(r => r.GetProperty("labels").GetProperty("task").GetString() == "B"), "id")!;
        var human = await Tman("list", "--all", "--label", "task=A");
        Assert.Equal(0, human.Exit);
        Assert.StartsWith("ID ", human.Out);
        Assert.Contains(Str(a, "id")!, human.Out);
        Assert.DoesNotContain(bId, human.Out);
    }

    [Fact]
    public async Task AnEmptyStore_IsAnEmptyList_NotTheHumanNoRunsLine()
    {
        var result = await Tman("list", "--json");
        Assert.Equal(0, result.Exit);
        Assert.Empty(JsonDocument.Parse(result.Out).RootElement.GetProperty("runs").EnumerateArray());
    }
}
