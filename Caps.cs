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

    public static readonly Caps SaneDefaults = new()
    {
        MaxTime = TimeSpan.FromMinutes(10),
        Stall = TimeSpan.FromSeconds(60),
        MaxMemMb = 2048,
        MaxCpuPct = 95,
        MaxParallel = 2,
        QueueTimeout = TimeSpan.FromMinutes(5),
    };

    public Caps MergeOver(Caps? lower) => lower is null ? this : new Caps
    {
        MaxTime = MaxTime ?? lower.MaxTime,
        Stall = Stall ?? lower.Stall,
        MaxMemMb = MaxMemMb ?? lower.MaxMemMb,
        MaxCpuPct = MaxCpuPct ?? lower.MaxCpuPct,
        MaxParallel = MaxParallel ?? lower.MaxParallel,
        QueueTimeout = QueueTimeout ?? lower.QueueTimeout,
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
            "" or "m" or "mb" => (long)n,
            "k" or "kb" => (long)(n / 1024),
            "g" or "gb" => (long)(n * 1024),
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
        };
    }

    static double? ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
