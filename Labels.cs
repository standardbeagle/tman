namespace Tman;

/// <summary>
/// A run's scope as its supervisor names it — task, step, attempt, host — in `key=value` pairs.
/// tman does not interpret a label; it records it, hands it to the run's children, and filters by
/// it. Two doors, one reading: `--label k=v` on the command line, and <see cref="EnvVar"/>, which a
/// supervisor sets once in an agent's environment so every tman that agent's shell starts carries
/// the scope without its command changing. A run sets the merged set on its child as the same
/// variable, so a nested run is filed under its parent's scope.
///
/// THE INVARIANT: <em>a label set read from either door can be written back to
/// <see cref="EnvVar"/> and read again unchanged.</em> That is why a key is a plain identifier and a
/// value holds no comma — the separator — and why a label tman cannot read refuses the run rather
/// than being dropped: a run filed under no scope is one its supervisor never counts.
/// </summary>
public static class Labels
{
    /// <summary>Comma-separated `key=value` pairs, as in `task=01M4,step=implement,attempt=2`.</summary>
    public const string EnvVar = "TMAN_LABELS";

    /// <summary>
    /// The labels a new run carries: <see cref="EnvVar"/>'s, with <paramref name="flags"/> replacing
    /// any key both name — the command line is the narrower scope. Null when neither names any.
    /// </summary>
    public static SortedDictionary<string, string>? ForNewRun(IReadOnlyDictionary<string, string>? flags)
    {
        var merged = Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } inherited
            ? ParseList(inherited, EnvVar)
            : new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (flags is not null)
            foreach (var (key, value) in flags) merged[key] = value;
        return merged.Count == 0 ? null : merged;
    }

    /// <summary>One `--label` value, added to <paramref name="into"/>; a key given twice is refused.</summary>
    public static void AddFlag(SortedDictionary<string, string> into, string text)
    {
        var (key, value) = ParsePair(text, "--label");
        if (!into.TryAdd(key, value))
            throw new FormatException($"--label {key} is given twice; a run has one value per label");
    }

    /// <summary><see cref="EnvVar"/>'s form: pairs in key order, comma-separated.</summary>
    public static string Format(IReadOnlyDictionary<string, string> labels) =>
        string.Join(',', labels.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>True when <paramref name="labels"/> carries every pair of <paramref name="filter"/>.</summary>
    public static bool Match(IReadOnlyDictionary<string, string>? labels, IReadOnlyDictionary<string, string> filter) =>
        filter.All(f => labels is not null && labels.TryGetValue(f.Key, out var v) && v == f.Value);

    static SortedDictionary<string, string> ParseList(string list, string source)
    {
        var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in list.Split(','))
        {
            var (key, value) = ParsePair(pair, source);
            if (!labels.TryAdd(key, value))
                throw new FormatException($"{source} names label {key} twice; a run has one value per label");
        }
        return labels;
    }

    static (string Key, string Value) ParsePair(string text, string source)
    {
        FormatException Bad(string why) => new(
            $"bad label \"{text}\" in {source}: {why} (expected key=value, as in task=01M4,step=implement)");
        var eq = text.IndexOf('=');
        if (eq < 0) throw Bad("no =");
        var key = text[..eq];
        var value = text[(eq + 1)..];
        if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw Bad("a key is letters, digits, '_', '-' or '.'");
        if (value.Length == 0) throw Bad("the value is empty");
        if (value.Any(c => c == ',' || char.IsControl(c)))
            throw Bad("a value holds no comma or control character");
        return (key, value);
    }
}
