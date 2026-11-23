using System.Diagnostics;
using System.Text.Json;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// Container cleanup through the built tman, as a fixture meets it: the run's child labels a
/// container with $TMAN_RUN_ID, and the tman supervising it removes it once the run is over. Docker
/// is a script TMAN_DOCKER names, which serves one container labelled with whatever the child wrote,
/// logs every call, and can be told the daemon is down.
/// </summary>
public class ContainerRunTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _cwd = new();
    readonly TempDir _docker = new();

    public void Dispose()
    {
        _home.Dispose();
        _cwd.Dispose();
        _docker.Dispose();
    }

    const string FakeDocker = """
        #!/bin/sh
        state="$FAKE_DOCKER_STATE"
        echo "$*" >> "$state/calls"
        if [ -e "$state/down" ]; then echo "Cannot connect to the Docker daemon" >&2; exit 1; fi
        case "$1" in
          ps)
            if [ -e "$state/vanishing" ]; then echo f1x7ure; fi
            if [ -s "$state/label" ] && [ ! -e "$state/removed" ]; then echo c0ffee; fi ;;
          inspect)
            # a fixture's container that removes itself as tman looks: gone by the time it is inspected
            if [ -e "$state/vanishing" ]; then
              rm -f "$state/vanishing"; echo '[]'; echo "Error response from daemon: No such container: f1x7ure" >&2; exit 1
            fi
            printf '[{"Id":"c0ffee","Created":"2026-01-01T00:00:00Z","Name":"/fixture-pg","Config":{"Labels":{"tman.run":"%s"}}}]\n' "$(cat "$state/label")" ;;
          rm)
            shift
            for a in "$@"; do case "$a" in -*) ;; *) echo "$a"; touch "$state/removed" ;; esac; done ;;
          *) exit 1 ;;
        esac
        """;

    /// <summary>The child's half of a fixture: label "its container" with the run it is part of.</summary>
    const string LabelAContainer = "printf %s \"$TMAN_RUN_ID\" > \"$FAKE_DOCKER_STATE/label\"";

    Dictionary<string, string> Env()
    {
        var script = _docker.WriteFile("docker", FakeDocker.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
        var tman = $"{Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"} {typeof(Tman.Program).Assembly.Location}";
        return new()
        {
            [Containers.EngineEnvVar] = script,
            ["FAKE_DOCKER_STATE"] = _docker.Path,
            ["TMAN_CMD"] = tman,
        };
    }

    string Calls => File.Exists(Path.Combine(_docker.Path, "calls")) ? File.ReadAllText(Path.Combine(_docker.Path, "calls")) : "";
    string Label => File.ReadAllText(Path.Combine(_docker.Path, "label"));

    List<JsonElement> Records() =>
        Directory.GetFiles(Path.Combine(_home.Path, "runs"), "*.json")
            .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone())
            .ToList();

    [UnixFact("runs sh and a docker script through the built tman")]
    public async Task ARun_RemovesTheContainerItLabelled_AndSaysSo()
    {
        var env = Env();

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c", LabelAContainer);

        Assert.Equal(0, ran.Exit);
        Assert.Contains("rm -f -v c0ffee", Calls);
        Assert.Contains("fixture-pg", ran.Err);
        Assert.Contains(Label, ran.Err);
        var record = Assert.Single(Records());
        Assert.True(record.GetProperty("ContainersSwept").GetBoolean());
        Assert.Equal("fixture-pg", Assert.Single(record.GetProperty("RemovedContainers").EnumerateArray()).GetString());

        var status = await TmanProcess.Run(_home.Path, _cwd.Path, env, "status", Label);
        Assert.Contains("removed fixture-pg", status.Out);
    }

    [UnixFact("runs sh and a docker script through the built tman")]
    public async Task ANestedRunsContainer_OutlivesTheNestedRun_AndGoesWithItsOuterRun()
    {
        var script = $"""
            $TMAN_CMD run --no-queue -- sh -c '{LabelAContainer}'
            if [ -e "$FAKE_DOCKER_STATE/removed" ]; then echo removed-while-the-outer-run-lived >&2; fi
            exit 0
            """;

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, Env(), "run", "--no-queue", "--", "sh", "-c", script);

        Assert.Equal(0, ran.Exit);
        Assert.DoesNotContain("removed-while-the-outer-run-lived", ran.Err);
        var outer = Records().Single(r => r.GetProperty("ParentId").ValueKind == JsonValueKind.Null);
        var nested = Records().Single(r => r.GetProperty("ParentId").ValueKind == JsonValueKind.String);
        // the nested tman handed its child its own id, not the outer run's
        Assert.Equal(nested.GetProperty("Id").GetString(), Label);
        Assert.Equal(outer.GetProperty("Id").GetString(), nested.GetProperty("ParentId").GetString());
        Assert.Contains($"stays up until outer run {outer.GetProperty("Id").GetString()} ends", ran.Err);
        Assert.Contains("rm -f -v c0ffee", Calls);
    }

    [UnixFact("runs sh and a docker script through the built tman")]
    public async Task Clean_RemovesWhatADownDockerKeptARunFromRemoving_AndCountsIt()
    {
        var env = Env();
        File.WriteAllText(Path.Combine(_docker.Path, "down"), "");
        Assert.Equal(0, (await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c", LabelAContainer)).Exit);
        Assert.DoesNotContain("rm ", Calls);
        File.Delete(Path.Combine(_docker.Path, "down"));

        var clean = await TmanProcess.Run(_home.Path, _cwd.Path, env, "clean");

        Assert.Equal(0, clean.Exit);
        Assert.Contains("removed 1 container(s)", clean.Out);
        Assert.Contains("rm -f -v c0ffee", Calls);
    }

    [UnixFact("runs sh and a docker script through the built tman")]
    public async Task AContainerLabelledWhileDockerWasDown_IsRemovedAfterTheBackoff_WithoutClean()
    {
        var env = Env();
        File.WriteAllText(Path.Combine(_docker.Path, "down"), "");

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c", LabelAContainer);

        Assert.Equal(0, ran.Exit);
        Assert.Contains($"docker cleanup deferred", ran.Err);
        Assert.Contains($"tman.run={Label}", ran.Err);
        Assert.Contains("removal pending", (await TmanProcess.Run(_home.Path, _cwd.Path, env, "status", Label)).Out);

        // docker is back, and the short backoff a pending reap gets is up
        File.Delete(Path.Combine(_docker.Path, "down"));
        File.SetLastWriteTimeUtc(Path.Combine(_home.Path, "runs", "containers.backoff"), DateTime.UtcNow - TimeSpan.FromSeconds(70));
        Assert.Equal(0, (await TmanProcess.Run(_home.Path, _cwd.Path, env, "list")).Exit);

        Assert.Contains("rm -f -v c0ffee", Calls);
        var status = await TmanProcess.Run(_home.Path, _cwd.Path, env, "status", Label);
        Assert.Contains("removed fixture-pg", status.Out);
        Assert.DoesNotContain("pending", status.Out);
    }

    [UnixFact("runs sh and a docker script through the built tman")]
    public async Task AContainerThatRemovedItselfAsARunEnded_DoesNotKeepTheNextRunsContainerUp()
    {
        // build3, 2026-11-23: a container vanishing between tman's `ps` and `inspect` read as docker
        // not answering, and the backoff that started kept every run's containers in the store up
        // for ten minutes, silently, until `tman clean`
        var env = Env();
        var first = await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c",
            "touch \"$FAKE_DOCKER_STATE/vanishing\"");
        Assert.Equal(0, first.Exit);

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c", LabelAContainer);

        Assert.Equal(0, ran.Exit);
        Assert.Contains("rm -f -v c0ffee", Calls);
        Assert.Contains($"labelled tman.run={Label}: fixture-pg", ran.Err);
    }

    [UnixFact("runs sh through the built tman")]
    public async Task ADockerThatIsNotThere_NeitherFailsNorSlowsTheRun()
    {
        var env = Env();
        env[Containers.EngineEnvVar] = Path.Combine(_docker.Path, "no-docker-here");

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c", "exit 4");

        Assert.Equal(4, ran.Exit);
        Assert.DoesNotContain("container", ran.Err);
    }

    [UnixFact("runs sh and a docker script through the built tman")]
    public async Task ADockerThatHangs_IsGivenUpOn_AndTheRunKeepsItsExit()
    {
        var env = Env();
        var hang = _docker.WriteFile("hanging-docker", "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(hang, File.GetUnixFileMode(hang) | UnixFileMode.UserExecute);
        env[Containers.EngineEnvVar] = hang;
        var clock = Stopwatch.StartNew();

        var ran = await TmanProcess.Run(_home.Path, _cwd.Path, env, "run", "--no-queue", "--", "sh", "-c", "exit 3");

        Assert.Equal(3, ran.Exit);
        // one bounded listing at the sweep, then the backoff spares the run's end another
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [DockerFact]
    public async Task ARealContainerLabelledByARun_IsGoneWhenTheRunEnds()
    {
        var name = "tman-test-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var ran = await TmanProcess.Run(_home.Path, _cwd.Path,
                new Dictionary<string, string> { [Containers.EngineEnvVar] = "docker" },
                "run", "--no-queue", "--", "sh", "-c",
                $"docker create --label {Containers.Label}=\"$TMAN_RUN_ID\" --name {name} {RealDocker.Image} sh >/dev/null");

            Assert.Equal(0, ran.Exit);
            Assert.Contains(name, ran.Err);
            Assert.Equal("", RealDocker.Run("ps", "-a", "-q", "--filter", $"name=^{name}$")?.Trim());
        }
        finally
        {
            RealDocker.Run("rm", "-f", name);
        }
    }
}

/// <summary>
/// A fact that needs a real docker daemon with an image already pulled to create a container from;
/// skipped, saying so, where there is none — a test must never pull an image or need the network.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "docker fixtures are Linux containers";
        else if (RealDocker.Image is null) Skip = "no docker daemon with a local image to create a container from";
    }
}

static class RealDocker
{
    static readonly Lazy<string?> FirstImage = new(() =>
        Run("image", "ls", "-q")?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault());

    public static string? Image => FirstImage.Value;

    /// <summary>docker's stdout, or null when it is not there, failed, or took longer than ten seconds.</summary>
    public static string? Run(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }
            return proc.ExitCode == 0 ? stdout.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
