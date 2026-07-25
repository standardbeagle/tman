using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class RunnerTests : IDisposable
{
    static bool Unix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public RunnerTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    static Caps StallOnly(int seconds) => new() { Stall = TimeSpan.FromSeconds(seconds) };

    [Fact]
    public async Task IdleProcess_IsStalled()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sleep", new[] { "30" }, StallOnly(1), null, null);

        Assert.Equal(Runner.ExitStalled, exit);
    }

    [Fact]
    public async Task SilentBusyChild_IsNotStalled()
    {
        if (!Unix) return;

        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try
        {
            exit = await Runner.RunAsync("sh",
                new[] { "-c", "yes > /dev/null & p=$!; sleep 3; kill $p; wait $p 2>/dev/null; exit 0" },
                StallOnly(1), null, null);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.True(exit == 0, $"exit={exit} stderr: {err}");
    }

    [Fact]
    public async Task PartialLineOutput_CountsAsActivity()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sh",
            new[] { "-c", "for i in 1 2 3 4 5 6; do printf x; sleep 0.5; done" },
            StallOnly(1), null, null);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task MaxTime_StillKills()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sleep", new[] { "30" },
            new Caps { MaxTime = TimeSpan.FromSeconds(2) }, null, null);

        Assert.Equal(Runner.ExitTimeout, exit);
    }

    [Fact]
    public async Task ExitCode_PassesThrough()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sh", new[] { "-c", "exit 42" },
            new Caps(), null, null);

        Assert.Equal(42, exit);
    }
}
