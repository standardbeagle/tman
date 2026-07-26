using System.Globalization;

namespace Tman;

public sealed record Caps
{
    public TimeSpan? MaxTime { get; init; }
    public TimeSpan? Stall { get; init; }
    public long? MaxMemMb { get; init; }
    public double? MaxCpuPct { get; init; }
    public int? MaxParallel { get; init; }
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
        return unit switch
        {
            "" or "s" or "sec" or "secs" => TimeSpan.FromSeconds(n),
            "ms" => TimeSpan.FromMilliseconds(n),
            "m" or "min" or "mins" => TimeSpan.FromMinutes(n),
            "h" or "hr" or "hrs" => TimeSpan.FromHours(n),
            _ => null,
        };
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
        return unit switch
        {
            "" or "m" or "mb" => (long)Math.Ceiling(n),
            "k" or "kb" => (long)Math.Ceiling(n / 1024),
            "g" or "gb" => (long)Math.Ceiling(n * 1024),
            _ => null,
        };
    }

    public static Caps FromNode(KdlNode? node)
    {
        if (node is null) return new Caps();
        return new Caps
        {
            MaxTime = ParseDuration(node.Child("max-time")?.Arg(0)),
            Stall = ParseDuration(node.Child("stall")?.Arg(0)),
            MaxMemMb = ParseMemMb(node.Child("max-mem")?.Arg(0)),
            MaxCpuPct = ParseDouble(node.Child("max-cpu")?.Arg(0)),
            MaxParallel = ParseInt(node.Child("max-parallel")?.Arg(0)),
            QueueTimeout = ParseDuration(node.Child("queue-timeout")?.Arg(0)),
            Retain = ParseDuration(node.Child("retain")?.Arg(0)),
        };
    }

    static double? ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
