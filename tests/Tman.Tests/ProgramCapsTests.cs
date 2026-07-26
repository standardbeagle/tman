using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// The cap flags, pinned through <see cref="Program.Main"/> so that the parsing and the wiring of
/// the parsed value into <c>EffectiveCaps</c> are both under test. Constructing a <see cref="Caps"/>
/// in the test would leave <c>case "--stall": Next(); break;</c> — parse the flag and drop it —
/// alive. Every run records the effective caps it started under, so the resolved value is
/// observable from the run record without timing anything.
/// </summary>
[Collection("cwd")]
public class ProgramCapsTests : IDisposable
{
    static bool Unix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);

    public ProgramCapsTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    /// <summary>
    /// Runs `tman run &lt;flags&gt; -- sleep 0` in a throwaway project holding <paramref name="configText"/>,
    /// and returns the caps the run was actually supervised under. <paramref name="assertConfigCompetes"/>
    /// runs against the parsed config first: if the fixture does not really declare a competing
    /// value, the flag would win by default and the test would prove nothing.
    /// </summary>
    async Task<Caps> ResolvedCaps(string configText, Action<TmanConfig> assertConfigCompetes, params string[] flags)
    {
        using var proj = new TempDir();
        proj.WriteFile(".tman.kdl", configText);

        var config = Config.Load(proj.Path);
        Assert.NotNull(config);
        assertConfigCompetes(config);

        var argv = new[] { "run" }.Concat(flags).Concat(new[] { "--", "sleep", "0" }).ToArray();
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(proj.Path);
        try
        {
            Assert.Equal(0, await Program.Main(argv));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }

        return Assert.Single(Store.LoadAll()).Caps;
    }

    [Fact]
    public async Task StallFlag_OverridesTheDefaultsBlock()
    {
        if (!Unix) return;

        var caps = await ResolvedCaps(
            """
            defaults {
                stall "60s"
            }
            """,
            config =>
            {
                Assert.Equal(TimeSpan.FromSeconds(60), config.Defaults.Stall);
                Assert.NotEqual(TimeSpan.FromSeconds(90), config.Defaults.Stall);
                Assert.NotEqual(Caps.SaneDefaults.Stall, config.Defaults.Stall);
            },
            "--stall", "90s");

        Assert.Equal(TimeSpan.FromSeconds(90), caps.Stall);
    }

    [Fact]
    public async Task MaxTimeFlag_OverridesTheDefaultsBlock()
    {
        if (!Unix) return;

        var caps = await ResolvedCaps(
            """
            defaults {
                max-time "9m"
            }
            """,
            config =>
            {
                Assert.Equal(TimeSpan.FromMinutes(9), config.Defaults.MaxTime);
                Assert.NotEqual(TimeSpan.FromSeconds(45), config.Defaults.MaxTime);
            },
            "--max-time", "45s");

        Assert.Equal(TimeSpan.FromSeconds(45), caps.MaxTime);
    }
}
