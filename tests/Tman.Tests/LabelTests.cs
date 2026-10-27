using System.Text.Json;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A run carries the scope its supervisor gave it — task, step, attempt — as labels. They come from
/// `--label k=v` and from TMAN_LABELS, which a supervisor sets once in an agent's environment so
/// every tman that agent's shell starts carries them without its command changing. A run hands its
/// labels down to its children the same way, so a nested run is found under its parent's scope.
/// </summary>
public class LabelTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _cwd = new();

    public void Dispose()
    {
        _home.Dispose();
        _cwd.Dispose();
    }

    /// <summary>The one record the store holds, read as tman wrote it rather than through its type.</summary>
    JsonElement OnlyRecord()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_home.Path, "runs"), "*.json"));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
    }

    static Dictionary<string, string> LabelsOf(JsonElement record) =>
        record.GetProperty("Labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    [UnixFact("runs sh through the built tman")]
    public async Task LabelsFromTheEnvironmentAndFlags_LandInTheRecord_FlagsWinningAKey()
    {
        var result = await TmanProcess.Run(_home.Path, _cwd.Path,
            new Dictionary<string, string> { ["TMAN_LABELS"] = "task=T1,step=implement" },
            "run", "--no-queue", "--label", "attempt=2", "--label", "step=review", "--", "true");

        Assert.Equal(0, result.Exit);
        Assert.Equal(
            new Dictionary<string, string> { ["attempt"] = "2", ["step"] = "review", ["task"] = "T1" },
            LabelsOf(OnlyRecord()));
    }

    [UnixFact("runs sh through the built tman")]
    public async Task ARunHandsItsLabelsToItsChildren()
    {
        var result = await TmanProcess.Run(_home.Path, _cwd.Path,
            new Dictionary<string, string> { ["TMAN_LABELS"] = "task=T1" },
            "run", "--no-queue", "--label", "attempt=3", "--", "sh", "-c", "printf '%s' \"$TMAN_LABELS\"");

        Assert.Equal(0, result.Exit);
        // the child reads the merged set in the one form TMAN_LABELS takes, so a tman it starts
        // records exactly what its parent did
        Assert.Equal("attempt=3,task=T1", result.Out);
    }

    [UnixFact("runs sh through the built tman")]
    public async Task ARunWithNoLabels_RecordsNone()
    {
        var result = await TmanProcess.Run(_home.Path, _cwd.Path, null, "run", "--no-queue", "--", "true");

        Assert.Equal(0, result.Exit);
        Assert.False(OnlyRecord().TryGetProperty("Labels", out var labels) && labels.ValueKind != JsonValueKind.Null,
            "a run nobody labelled must not read as labelled with an empty scope");
    }

    [UnixTheory("runs the built tman")]
    [InlineData("--label", "attempt", null)]
    [InlineData("--label", "=2", null)]
    [InlineData("--label", "attempt=", null)]
    [InlineData("--label", "a b=2", null)]
    [InlineData("--label", "attempt=2,3", null)]
    [InlineData(null, null, "task")]
    [InlineData(null, null, "task=T1,,step=x")]
    [InlineData(null, null, "task=T1,task=T2")]
    public async Task ALabelTmanCannotRead_RefusesTheRun(string? flag, string? value, string? env)
    {
        var args = new List<string> { "run", "--no-queue" };
        if (flag is not null) args.AddRange([flag, value!]);
        args.AddRange(["--", "true"]);
        var result = await TmanProcess.Run(_home.Path, _cwd.Path,
            env is null ? null : new Dictionary<string, string> { ["TMAN_LABELS"] = env }, [.. args]);

        // refused before anything runs: a scope tman cannot read would file the run under no task,
        // and a supervisor that counts its runs by label would never see it
        Assert.Equal(127, result.Exit);
        Assert.Contains("label", result.Err);
        // refused for the label, not for a flag a tman without labels never heard of
        Assert.DoesNotContain("unknown flag", result.Err);
        Assert.Empty(Directory.Exists(Path.Combine(_home.Path, "runs"))
            ? Directory.GetFiles(Path.Combine(_home.Path, "runs"), "*.json")
            : []);
    }
}
