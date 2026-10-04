using System.Globalization;

namespace Tman;

public sealed record Caps
{
    public TimeSpan? MaxTime { get; init; }
    public TimeSpan? Stall { get; init; }
    public long? MaxMemMb { get; init; }
    public double? MaxCpuPct { get; init; }
    public int? MaxParallel { get; init; }
    /// <summary>
    /// How many CPUs the tree may run on, enforced by the kernel as an affinity mask rather than
    /// sampled — so `nproc` and every runner's worker count see the narrowed set. See <see cref="Confinement"/>.
    /// </summary>
    public int? LimitCpus { get; init; }
    /// <summary>
    /// A memory ceiling for the whole tree that the kernel enforces, where <see cref="MaxMemMb"/> is
    /// sampled once a second and can be outrun between samples. See <see cref="Confinement"/>.
    /// </summary>
    public long? LimitMemMb { get; init; }
    public TimeSpan? QueueTimeout { get; init; }
    /// <summary>How long finished run records survive before automatic pruning.</summary>
    public TimeSpan? Retain { get; init; }

    /// <summary>
    /// The hang backstop, used both here and by the config `tman init` scaffolds — one question,
    /// one number. It answers "is this process dead?", never "is this taking too long?"; that is
    /// <c>max-time</c>'s job. Sized above the longest quiet stretch healthy work is expected to
    /// have, because killing a live run costs the whole run while noticing a dead one late costs
    /// only idle minutes in a bucket that <c>max-parallel</c> already bounds.
    /// </summary>
    public static readonly TimeSpan DefaultStall = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Built-in floor. Deliberately only catches pathologies: a run that is both silent and idle,
    /// and a stampede within one bucket. Resource ceilings (max-time/max-mem/max-cpu) are opt-in —
    /// a real build is *supposed* to saturate cores and can legitimately want several GB, so
    /// culling it by default would break `tman run -- vite build` out of the box.
    /// </summary>
    public static readonly Caps SaneDefaults = new()
    {
        Stall = DefaultStall,
        MaxParallel = 2,
        QueueTimeout = TimeSpan.FromMinutes(5),
        Retain = TimeSpan.FromHours(24),
    };

    public Caps MergeOver(Caps? lower) => lower is null ? this : new Caps
    {
        MaxTime = MaxTime ?? lower.MaxTime,
        Stall = Stall ?? lower.Stall,
        MaxMemMb = MaxMemMb ?? lower.MaxMemMb,
        MaxCpuPct = MaxCpuPct ?? lower.MaxCpuPct,
        MaxParallel = MaxParallel ?? lower.MaxParallel,
        LimitCpus = LimitCpus ?? lower.LimitCpus,
        LimitMemMb = LimitMemMb ?? lower.LimitMemMb,
        QueueTimeout = QueueTimeout ?? lower.QueueTimeout,
        Retain = Retain ?? lower.Retain,
    };

    public static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var span = s.AsSpan();
        var numEnd = 0;
        while (numEnd < span.Length && (char.IsDigit(span[numEnd]) || span[numEnd] == '.'))
            numEnd++;
        if (numEnd == 0) return null;
        if (!double.TryParse(span[..numEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;
        var unit = span[numEnd..].ToString().ToLowerInvariant();
        // a figure past TimeSpan's range is as unreadable as a misspelt unit, not a crash
        try
        {
            return unit switch
            {
                "" or "s" or "sec" or "secs" => TimeSpan.FromSeconds(n),
                "ms" => TimeSpan.FromMilliseconds(n),
                "m" or "min" or "mins" => TimeSpan.FromMinutes(n),
                "h" or "hr" or "hrs" => TimeSpan.FromHours(n),
                _ => null,
            };
        }
        catch (OverflowException) { return null; }
    }

    public static long? ParseMemMb(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var span = s.AsSpan();
        var numEnd = 0;
        while (numEnd < span.Length && (char.IsDigit(span[numEnd]) || span[numEnd] == '.'))
            numEnd++;
        if (numEnd == 0) return null;
        if (!double.TryParse(span[..numEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;
        var unit = span[numEnd..].ToString().ToLowerInvariant();
        double? mb = unit switch
        {
            "" or "m" or "mb" => Math.Ceiling(n),
            "k" or "kb" => Math.Ceiling(n / 1024),
            "g" or "gb" => Math.Ceiling(n * 1024),
            _ => null,
        };
        return mb is { } v && v <= long.MaxValue ? (long)v : null;
    }

    /// <summary>The settings a caps-bearing `.tman.kdl` block may hold, in the order the docs list them.</summary>
    public static readonly IReadOnlyList<string> Keys =
        ["max-time", "stall", "max-mem", "max-cpu", "max-parallel", "limit-cpus", "limit-mem", "queue-timeout", "retain"];

    /// <summary>
    /// <paramref name="caps"/> with <paramref name="key"/> set from <paramref name="value"/>: the one
    /// reading of a cap value, shared by `.tman.kdl` and the `--key` flags so neither can accept what
    /// the other refuses. A value it cannot read throws, naming <paramref name="label"/> — a cap
    /// someone wrote down is enforced or refused, never read as absent, which would run the command
    /// without the bound it was given.
    /// </summary>
    public static Caps With(Caps caps, string key, string? value, string label)
    {
        FormatException Bad(string expected) => new($"bad {label} \"{value}\": expected {expected}");
        const string duration = "a duration such as 30s, 10m or 2h";
        const string size = "megabytes, or a size such as 512m or 2g";
        return key switch
        {
            "max-time" => caps with { MaxTime = ParseDuration(value) ?? throw Bad(duration) },
            "stall" => caps with { Stall = ParseDuration(value) ?? throw Bad(duration) },
            "queue-timeout" => caps with { QueueTimeout = ParseDuration(value) ?? throw Bad(duration) },
            "retain" => caps with { Retain = ParseDuration(value) ?? throw Bad(duration) },
            "max-mem" => caps with { MaxMemMb = ParseMemMb(value) ?? throw Bad(size) },
            // zero would be a ceiling nothing can run under, which is a typo, not a limit
            "limit-mem" => caps with { LimitMemMb = ParseMemMb(value) is { } mb and > 0 ? mb : throw Bad(size) },
            "limit-cpus" => caps with
            {
                LimitCpus = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cpus) && cpus > 0
                    ? cpus
                    : throw Bad("a whole number of CPUs, at least 1"),
            },
            "max-cpu" => caps with
            {
                MaxCpuPct = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                            && double.IsFinite(pct) && pct >= 0
                    ? pct
                    : throw Bad("a non-negative percentage"),
            },
            "max-parallel" => caps with
            {
                MaxParallel = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : throw Bad("a non-negative whole number"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a cap key"),
        };
    }
}
