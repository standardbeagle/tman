namespace Tman;

/// <summary>
/// Turns a captured run log into the answer to one question: which test failed?
///
/// Deliberately framework-agnostic pattern matching rather than per-runner parsers. tman supervises
/// whatever it is handed — pytest, go test, dotnet test, vitest, cargo, make — and a parser per
/// runner would be a maintenance surface that silently reports "no failures found" the first time a
/// runner changes its output. Matching the line shapes every runner already prints, then quoting
/// the lines that follow, degrades to "here is the noisy part of the log" instead of to silence.
/// </summary>
public static class Failures
{
    /// <summary>Lines quoted after each match — enough for an assertion message and a frame or two.</summary>
    public const int ContextLines = 15;

    /// <summary>Ceiling on quoted lines, so a suite that fails wholesale cannot copy its own log.</summary>
    public const int MaxDigestLines = 4000;

    /// <summary>Trailing lines always quoted: every runner prints its totals last.</summary>
    public const int TailLines = 40;

    // Line SHAPES, not runner names. A prefix match is anchored on the trimmed line so indented
    // per-test output (go subtests, xunit detail) matches the same way top-level output does.
    static readonly string[] Prefixes =
    {
        "FAILED ",              // pytest short summary
        "ERROR ",               // pytest collection error
        "E   ",                 // pytest traceback body
        "--- FAIL:",            // go test, including subtests
        "FAIL\t",               // go package result
        "FAIL ",                // vitest, generic runners
        "FAIL:",
        "panic:",               // go
        "not ok ",              // TAP
        "● ",                   // jest
        "× ",                   // vitest
        "✕ ",                   // vitest, jest
        "Failed ",              // dotnet vstest, per test
        "Failed!",              // dotnet vstest, summary
        "Assertion",            // AssertionError and friends
        "Unhandled exception",
        "Error:",
        "error:",
    };

    static readonly string[] Fragments =
    {
        "[FAIL]",               // dotnet console logger
        ": error ",             // msbuild, tsc, cargo, gcc
        "error CS",             // roslyn, when the path half is missing
    };

    /// <summary>True when the line is one a runner prints to announce something broke.</summary>
    public static bool IsFailureLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return false;
        foreach (var p in Prefixes)
            if (trimmed.StartsWith(p, StringComparison.Ordinal)) return true;
        foreach (var f in Fragments)
            if (line.Contains(f, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Quoted evidence from <paramref name="logPath"/>: every failure line with its following
    /// context, then the tail. Line numbers are into the full log, so the digest is a pointer into
    /// it rather than a replacement for it. Never throws — a digest that cannot be built must not
    /// take down the run that produced it.
    /// </summary>
    public static IReadOnlyList<string> Digest(string logPath)
    {
        var body = new List<string>();
        var tail = new string[TailLines];
        var tailCount = 0;
        var matches = 0;
        var remaining = 0;
        var lastQuoted = 0;
        var capped = false;

        try
        {
            using var reader = new StreamReader(logPath);
            string? line;
            var lineNo = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNo++;
                tail[tailCount % TailLines] = line;
                tailCount++;

                var hit = IsFailureLine(line);
                if (hit) { matches++; remaining = ContextLines; }
                else if (remaining > 0) remaining--;
                else continue;

                if (capped) continue;
                if (body.Count >= MaxDigestLines) { capped = true; continue; }

                if (lastQuoted != 0 && lineNo > lastQuoted + 1) body.Add("       ⋮");
                body.Add($"{lineNo,6}: {line}");
                lastQuoted = lineNo;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var result = new List<string>();
        result.Add(matches == 0
            ? "── no failure lines matched; the tail is below ──"
            : $"── {matches} failure line(s) ──");
        result.AddRange(body);
        if (capped) result.Add($"       ⋮ (digest capped at {MaxDigestLines} lines; read the full log)");

        result.Add("");
        result.Add($"── last {Math.Min(tailCount, TailLines)} line(s) ──");
        var first = tailCount > TailLines ? tailCount - TailLines : 0;
        for (var i = first; i < tailCount; i++)
            result.Add(tail[i % TailLines]);
        return result;
    }
}
