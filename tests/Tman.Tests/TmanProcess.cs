using System.Diagnostics;

namespace Tman.Tests;

/// <summary>
/// The built tman, run as its own process. A report meant for another program is tested through
/// that program's door: the bytes on a real stdout, which no other test class can write into, and
/// an environment that is the child's own, so setting TMAN_LABELS for one run cannot leak into a
/// test running beside it.
/// </summary>
static class TmanProcess
{
    /// <summary>
    /// The environment a supervisor's own run would hand down, removed so the child is a top-level
    /// tman: under `tman test` this suite carries TMAN_RUN_ID, and a child that inherited it would
    /// read itself as nested and skip its queue.
    /// </summary>
    static readonly string[] Inherited = ["TMAN_RUN_ID", "TMAN_LABELS", "CLAUDE_CODE_SESSION_ID"];

    public sealed record Result(int Exit, string Out, string Err);

    /// <summary>Runs tman with <paramref name="args"/> against the store at <paramref name="home"/>.</summary>
    public static async Task<Result> Run(
        string home, string cwd, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(typeof(Tman.Program).Assembly.Location);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var name in Inherited) psi.Environment.Remove(name);
        psi.Environment["TMAN_HOME"] = home;
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await proc.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException($"tman {string.Join(' ', args)} did not exit within 60s");
        }
        return new Result(proc.ExitCode, await stdout, await stderr);
    }
}
