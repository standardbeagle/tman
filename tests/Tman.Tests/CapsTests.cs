using Tman;
using Xunit;

namespace Tman.Tests;

public class CapsParseTests
{
    [Theory]
    [InlineData("30", 30_000)]
    [InlineData("30s", 30_000)]
    [InlineData("500ms", 500)]
    [InlineData("10m", 600_000)]
    [InlineData("2h", 7_200_000)]
    [InlineData("1.5m", 90_000)]
    [InlineData(" 60s ", 60_000)]
    public void ParseDuration_Valid(string input, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), Caps.ParseDuration(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10x")]
    [InlineData("m")]
    public void ParseDuration_Invalid_ReturnsNull(string? input)
    {
        Assert.Null(Caps.ParseDuration(input));
    }

    [Theory]
    [InlineData("2048", 2048)]
    [InlineData("2048mb", 2048)]
    [InlineData("2g", 2048)]
    [InlineData("1.5g", 1536)]
    [InlineData("2048k", 2)]
    public void ParseMemMb_Valid(string input, long expectedMb)
    {
        Assert.Equal(expectedMb, Caps.ParseMemMb(input));
    }

    [Theory]
    [InlineData("512k", 1)]
    [InlineData("0.5m", 1)]
    [InlineData("1k", 1)]
    public void ParseMemMb_SubMegabyte_RoundsUpNotToZero(string input, long expectedMb)
    {
        Assert.Equal(expectedMb, Caps.ParseMemMb(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("10t")]
    public void ParseMemMb_Invalid_ReturnsNull(string? input)
    {
        Assert.Null(Caps.ParseMemMb(input));
    }

    [Fact]
    public void MergeOver_HigherPriorityWinsPerField()
    {
        var high = new Caps { MaxMemMb = 512 };
        var low = new Caps { MaxMemMb = 2048, Stall = TimeSpan.FromSeconds(30) };
        var merged = high.MergeOver(low);
        Assert.Equal(512, merged.MaxMemMb);
        Assert.Equal(TimeSpan.FromSeconds(30), merged.Stall);
    }

    [Fact]
    public void EffectiveCaps_FallsBackToSaneDefaults()
    {
        var caps = Config.EffectiveCaps(null, new Caps(), null);
        Assert.Equal(Caps.SaneDefaults.MaxParallel, caps.MaxParallel);
        Assert.Equal(Caps.SaneDefaults.Stall, caps.Stall);
        Assert.Equal(Caps.SaneDefaults.Retain, caps.Retain);
    }

    [Fact]
    public void Retain_IsConfigurable()
    {
        var node = Kdl.Parse("defaults {\n    retain \"2h\"\n}\n").Single();
        Assert.Equal(TimeSpan.FromHours(2), Caps.FromNode(node).Retain);
    }

    [Fact]
    public void BuiltInDefaults_ImposeNoResourceCeilings()
    {
        // A build is supposed to saturate cores and can want several GB; culling it by
        // default would break `tman run -- vite build` with no config at all.
        var caps = Config.EffectiveCaps(null, new Caps(), null);
        Assert.Null(caps.MaxTime);
        Assert.Null(caps.MaxMemMb);
        Assert.Null(caps.MaxCpuPct);
    }

    /// <summary>
    /// Path 1 of the built-in stall: `tman run -- go build ./...` in a directory with no
    /// `.tman.kdl` at all. A fleet audit of 959 run records found 32 stall kills at 60s and
    /// not one real hang; `go build ./...` was killed at 60s and 61s while succeeding 14
    /// times elsewhere, longest 75s. The built-in must be a hang backstop, not that budget.
    /// </summary>
    [Fact]
    public void NoConfigAtAll_ResolvesAStallBackstopNotASixtySecondBudget()
    {
        using var dir = new TempDir();

        // Precondition: this really is the config-less path, not a config found by walking up.
        Assert.Null(Config.Load(dir.Path));

        var caps = Config.EffectiveCaps(null, new Caps(), null);

        Assert.NotNull(caps.Stall);
        Assert.True(caps.Stall >= TimeSpan.FromMinutes(30),
            $"config-less stall {caps.Stall} is a runtime budget, not a hang backstop");
    }

    /// <summary>
    /// Path 2: an existing `.tman.kdl` that never mentions `stall`. Omission is silence, not
    /// a request for 60s — it resolves through the same built-in, so it gets the same backstop.
    /// </summary>
    [Fact]
    public void ConfigOmittingStall_ResolvesAStallBackstopNotASixtySecondBudget()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
defaults {
    max-parallel 2
}

alias "build" {
    command "go"
    args "build" "./..."
}
""");

        var config = Config.Load(dir.Path);

        Assert.NotNull(config);
        // Precondition: the fixture must actually omit `stall`, or this test proves nothing.
        Assert.Null(config.Defaults.Stall);

        var caps = Config.EffectiveCaps(config.Aliases["build"], new Caps(), config);

        Assert.NotNull(caps.Stall);
        Assert.True(caps.Stall >= TimeSpan.FromMinutes(30),
            $"stall {caps.Stall} inherited by a config that omits it is a runtime budget");
    }

    /// <summary>
    /// Widening the built-in only stays safe if an explicit `stall` still wins. Before this
    /// change the built-in and a typical explicit `60s` were the same number, so nothing here
    /// could be observed; they now differ by 30x, and a regression in the config layer would
    /// silently rewrite every explicit user's supervision.
    /// </summary>
    [Fact]
    public void ConfigDeclaringStall_WinsOverTheBuiltIn()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
defaults {
    stall "60s"
}
""");

        var config = Config.Load(dir.Path);

        Assert.NotNull(config);
        // Precondition: the fixture must actually declare `stall`, or this test proves nothing.
        Assert.Equal(TimeSpan.FromSeconds(60), config.Defaults.Stall);

        var caps = Config.EffectiveCaps(null, new Caps(), config);

        Assert.Equal(TimeSpan.FromSeconds(60), caps.Stall);
    }

    /// <summary>
    /// README:98 offers the CLI as the escape hatch for a user who wants a tighter bound than the
    /// config gives them. That promise is only kept if `cliCaps` is actually the top of the chain:
    /// starting the merge from an empty `Caps` instead turns every cap flag into a silent no-op.
    /// </summary>
    [Fact]
    public void CliStall_WinsOverTheDefaultsBlock()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
defaults {
    stall "60s"
}
""");

        var config = Config.Load(dir.Path);
        var cliStall = TimeSpan.FromSeconds(90);

        Assert.NotNull(config);
        // Precondition: the config must declare a stall that competes with — and differs from —
        // both the CLI value and the built-in, or the CLI value could win by default.
        Assert.Equal(TimeSpan.FromSeconds(60), config.Defaults.Stall);
        Assert.NotEqual(cliStall, config.Defaults.Stall);
        Assert.NotEqual(Caps.SaneDefaults.Stall, config.Defaults.Stall);

        var caps = Config.EffectiveCaps(null, new Caps { Stall = cliStall }, config);

        Assert.Equal(cliStall, caps.Stall);
    }

    /// <summary>
    /// The alias layer sits between the CLI and the `defaults` block. Every prior test threaded an
    /// alias that declared no caps of its own, so the layer carried nothing that could be lost.
    /// </summary>
    [Fact]
    public void AliasStall_BeatsTheDefaultsBlockAndLosesToTheCli()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
defaults {
    stall "60s"
}

alias "build" {
    command "go"
    args "build" "./..."
    stall "5m"
}
""");

        var config = Config.Load(dir.Path);
        var cliStall = TimeSpan.FromSeconds(90);

        Assert.NotNull(config);
        var alias = config.Aliases["build"];
        // Precondition: three distinct values, one per layer, so each layer's win is observable.
        Assert.Equal(TimeSpan.FromMinutes(5), alias.Caps.Stall);
        Assert.Equal(TimeSpan.FromSeconds(60), config.Defaults.Stall);
        Assert.NotEqual(Caps.SaneDefaults.Stall, alias.Caps.Stall);
        Assert.NotEqual(cliStall, alias.Caps.Stall);

        Assert.Equal(TimeSpan.FromMinutes(5),
            Config.EffectiveCaps(alias, new Caps(), config).Stall);
        Assert.Equal(cliStall,
            Config.EffectiveCaps(alias, new Caps { Stall = cliStall }, config).Stall);
    }

    /// <summary>
    /// `max-time` has no built-in, so a `defaults` block that declares one is the only thing
    /// standing between the user and an unbounded run.
    /// </summary>
    [Fact]
    public void DefaultsMaxTime_ResolvesByValue()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
defaults {
    max-time "4m"
}
""");

        var config = Config.Load(dir.Path);

        Assert.NotNull(config);
        // Precondition: the fixture must actually declare `max-time`, or this test proves nothing.
        Assert.Equal(TimeSpan.FromMinutes(4), config.Defaults.MaxTime);

        Assert.Equal(TimeSpan.FromMinutes(4), Config.EffectiveCaps(null, new Caps(), config).MaxTime);
    }

    [Fact]
    public void DefaultsQueueTimeout_ResolvesByValue()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
defaults {
    queue-timeout "45s"
}
""");

        var config = Config.Load(dir.Path);

        Assert.NotNull(config);
        // Precondition: declared, and different from the built-in it has to override.
        Assert.Equal(TimeSpan.FromSeconds(45), config.Defaults.QueueTimeout);
        Assert.NotEqual(Caps.SaneDefaults.QueueTimeout, config.Defaults.QueueTimeout);

        Assert.Equal(TimeSpan.FromSeconds(45),
            Config.EffectiveCaps(null, new Caps(), config).QueueTimeout);
    }

    /// <summary>
    /// The scaffolded default and the built-in default answer the same question, so they are
    /// one constant. They were two literals once and drifted (30m vs 60s) within a single release.
    /// </summary>
    [Fact]
    public void ScaffoldedStall_AndConfigLessStall_AreTheSameValue()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", Program.RenderConfig(new List<Program.DetectedAlias>()));

        var scaffolded = Config.Load(dir.Path);
        var builtIn = Config.EffectiveCaps(null, new Caps(), null);

        Assert.NotNull(scaffolded);
        Assert.Equal(TimeSpan.FromMinutes(30), builtIn.Stall);
        Assert.Equal(builtIn.Stall, scaffolded.Defaults.Stall);
    }
}
