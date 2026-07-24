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
        Assert.Null(caps.MaxTime);
        Assert.Equal(Caps.SaneDefaults.MaxParallel, caps.MaxParallel);
    }
}
