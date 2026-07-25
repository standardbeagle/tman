using System.Text.Json;
using Tman;
using Xunit;

namespace Tman.Tests;

public class HookTests
{
    static string Request(string command, string? cwd, string tool = "Bash") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "s1",
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["cwd"] = cwd,
            ["tool_input"] = new Dictionary<string, object?>
            {
                ["command"] = command,
                ["description"] = "run it",
            },
        });

    static TempDir SupervisedProject()
    {
        var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "defaults {\n    max-parallel 2\n}\n");
        return dir;
    }

    /// <summary>
    /// A stand-in for an installed tman: an absolute path, on disk, named the way tman ships.
    /// Every rewrite test injects this rather than letting the suite's own
    /// <see cref="Environment.ProcessPath"/> stand in for tman — under <c>dotnet test</c> that is
    /// the xUnit test host, so a test that relied on it would be asserting the identity check is
    /// satisfied by a binary that is not tman.
    /// </summary>
    static string InstalledTman(TempDir dir) => dir.WriteFile("bin/tman", "#!/bin/sh\nexec true\n");

    static string Render(TempDir dir, string command, string? parentRunId = null) =>
        Hook.Render(Request(command, dir.Path), parentRunId, InstalledTman(dir));

    static string? RewrittenCommand(string response)
    {
        if (response.Length == 0) return null;
        var root = JsonDocument.Parse(response).RootElement;
        if (!root.TryGetProperty("hookSpecificOutput", out var hook)) return null;
        if (!hook.TryGetProperty("updatedInput", out var updated)) return null;
        return updated.GetProperty("command").GetString();
    }

    /// <summary>argv[0] of a rewritten command line, unwrapping the shell quoting if it is quoted.</summary>
    static string SupervisorOf(string commandLine)
    {
        if (!commandLine.StartsWith('\'')) return commandLine.Split(' ')[0];
        var end = commandLine.IndexOf('\'', 1);
        return commandLine[1..end];
    }

    static string? Warning(string response)
    {
        if (response.Length == 0) return null;
        var root = JsonDocument.Parse(response).RootElement;
        if (!root.TryGetProperty("hookSpecificOutput", out var hook)) return null;
        return hook.TryGetProperty("additionalContext", out var ctx) ? ctx.GetString() : null;
    }

    [Theory]
    [InlineData("go test ./...")]
    [InlineData("go build ./...")]
    [InlineData("dotnet test")]
    [InlineData("dotnet build -c Release")]
    [InlineData("npm test")]
    [InlineData("npm run build")]
    [InlineData("pytest -k \"slow path\"")]
    [InlineData("cargo test")]
    [InlineData("make test")]
    // an absolute program path is the same invocation; the classifier normalizes it before matching
    [InlineData("/usr/local/bin/npm test")]
    [InlineData("/opt/homebrew/bin/pytest -q")]
    public void BareTestOrBuild_InSupervisedProject_IsRewritten(string command)
    {
        using var dir = SupervisedProject();

        Assert.EndsWith(" run -- " + command, RewrittenCommand(Render(dir, command)));
    }

    /// <summary>
    /// The Bash tool resolves argv[0] against its own PATH, not this process's. tman installs to
    /// <c>~/.local/bin</c> or a node bin dir, both routinely absent from a non-interactive PATH, so
    /// emitting the bare name <c>tman</c> turns a working build into exit 127 with nothing run.
    /// </summary>
    [Fact]
    public void Rewrite_NamesTheTmanBinaryByItsAbsolutePath_NotABareProgramName()
    {
        using var dir = SupervisedProject();

        var rewritten = RewrittenCommand(Render(dir, "go test ./..."));

        Assert.Equal(InstalledTman(dir), SupervisorOf(rewritten!));
    }

    /// <summary>
    /// The identity half of the invariant. Each of these is an absolute path to a file that really
    /// is on disk — the previous check passed all of them. <c>dotnet</c> is the one observed in the
    /// field: <c>dotnet bin/Debug/net10.0/tman.dll hook pretooluse</c> makes
    /// <see cref="Environment.ProcessPath"/> the shared host, and the rewrite became
    /// <c>/usr/lib/dotnet/dotnet run -- go test ./...</c> — which in any directory holding a
    /// <c>.csproj</c> builds and launches an unrelated application. Silently running a different
    /// program is worse than the <c>exit 127</c> that motivated the absolute path in the first place.
    /// </summary>
    [Theory]
    [InlineData("dotnet")]      // the managed host, not the apphost: the field failure
    [InlineData("dotnet.exe")]
    [InlineData("testhost")]    // what this very suite runs under
    [InlineData("sh")]
    [InlineData("tman.dll")]    // tman's own managed assembly is not something you can exec
    [InlineData("tmanx")]
    public void AnExistingAbsolutePathThatIsNotTman_IsRefused(string fileName)
    {
        using var dir = SupervisedProject();
        var impostor = dir.WriteFile("host/" + fileName, "#!/bin/sh\nexec true\n");

        var decision = Hook.Decide("go test ./...", dir.Path, parentRunId: null, impostor);

        Assert.Equal(HookAction.Warn, decision.Action);
        Assert.Null(decision.Command);
        Assert.Contains(impostor, decision.Reason);
    }

    [Theory]
    [InlineData("tman")]
    [InlineData("tman.exe")]    // the same binary as Windows publishes it
    public void TheNamesTmanShipsUnder_AreAccepted(string fileName)
    {
        using var dir = SupervisedProject();
        var tman = dir.WriteFile("bin/" + fileName, "#!/bin/sh\nexec true\n");

        var decision = Hook.Decide("go test ./...", dir.Path, parentRunId: null, tman);

        Assert.Equal(HookAction.Rewrite, decision.Action);
        Assert.Equal(tman + " run -- go test ./...", decision.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tman")]                    // a bare name is exactly what the Bash tool cannot resolve
    [InlineData("bin/tman")]                // relative: resolved against PATH, not cwd
    [InlineData("/no/such/dir/tman")]       // absolute and correctly named, but not there
    public void WithoutProofOfATmanBinary_WarnsRatherThanEmittingACommandThatCannotRun(string? supervisor)
    {
        using var dir = SupervisedProject();

        var decision = Hook.Decide("go test ./...", dir.Path, parentRunId: null, supervisor);

        Assert.Equal(HookAction.Warn, decision.Action);
        Assert.Null(decision.Command);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason), "the degrade must announce itself");
    }

    /// <summary>
    /// A degrade nobody is told about is indistinguishable from supervision that worked. The Warn
    /// branch's only channel is <c>additionalContext</c>, so an empty reason silently drops the
    /// whole safety path — pin it at the wire format, not just on the decision record.
    /// </summary>
    [Fact]
    public void TheDegrade_ReachesTheAgentAsNonEmptyAdditionalContext()
    {
        using var dir = SupervisedProject();
        var impostor = dir.WriteFile("host/dotnet", "#!/bin/sh\nexec true\n");

        var response = Hook.Render(Request("go test ./...", dir.Path), parentRunId: null, impostor);

        Assert.Null(RewrittenCommand(response));
        Assert.False(string.IsNullOrWhiteSpace(Warning(response)), "the degrade must announce itself");
    }

    [Fact]
    public void SupervisorPathWithASpace_IsQuotedSoArgv0SurvivesTheShell()
    {
        using var dir = SupervisedProject();
        var supervisor = dir.WriteFile("bin dir/tman", "#!/bin/sh\nexit 0\n");

        var decision = Hook.Decide("go test ./...", dir.Path, parentRunId: null, supervisor);

        Assert.Equal(HookAction.Rewrite, decision.Action);
        Assert.Equal($"'{supervisor}' run -- go test ./...", decision.Command);
    }

    [Fact]
    public void Rewrite_PreservesTheRestOfTheToolInput()
    {
        using var dir = SupervisedProject();

        var response = Render(dir, "go test ./...");

        var updated = JsonDocument.Parse(response).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("run it", updated.GetProperty("description").GetString());
    }

    [Fact]
    public void Rewrite_AnnouncesItselfSoTheTranscriptStillMatchesWhatRan()
    {
        using var dir = SupervisedProject();

        var response = Render(dir, "go test ./...");

        var message = JsonDocument.Parse(response).RootElement.GetProperty("systemMessage").GetString();
        Assert.Contains("run -- go test ./...", message);
    }

    [Fact]
    public void WithoutTmanKdl_WarnsAndLeavesTheCommandAlone()
    {
        using var dir = new TempDir();

        var response = Render(dir, "go test ./...");

        Assert.Null(RewrittenCommand(response));
        Assert.Contains(".tman.kdl", Warning(response));
    }

    [Fact]
    public void CompoundCommand_WarnsInsteadOfRewritingAShellItDidNotParse()
    {
        using var dir = SupervisedProject();

        var response = Render(dir, "cd packages/app && npm test");

        Assert.Null(RewrittenCommand(response));
        Assert.Contains("tman run --", Warning(response));
    }

    [Fact]
    public void EnvPrefixedCommand_WarnsInsteadOfRewritingIntoABrokenArgv()
    {
        using var dir = SupervisedProject();

        var response = Render(dir, "CI=1 npm test");

        Assert.Null(RewrittenCommand(response));
        Assert.NotNull(Warning(response));
    }

    [Fact]
    public void InsideASupervisedTree_PassesThroughUntouched()
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Render(dir, "go test ./...", parentRunId: "01abc"));
    }

    [Theory]
    [InlineData("tman run -- go test ./...")]
    [InlineData("tman test")]
    [InlineData("/usr/local/bin/tman run -- npm test")]
    public void TmanItself_IsNeverIntercepted(string command)
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Render(dir, command));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("npm run dev")]
    [InlineData("ls -la")]
    [InlineData("./test")]
    public void UnrelatedCommands_PassThrough(string command)
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Render(dir, command));
    }

    [Fact]
    public void NonBashTools_PassThrough()
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Hook.Render(Request("go test ./...", dir.Path, tool: "Read"), null, InstalledTman(dir)));
    }

    /// <summary>
    /// Everything above injects argv[0], so nothing above would notice if the production entry point
    /// supplied a different binary than the process it is running as. This pins that seam: what
    /// Claude Code gets must be exactly what <see cref="Environment.ProcessPath"/> decides, and the
    /// decision must name that path so the two forms are distinguishable at all.
    /// </summary>
    [Fact]
    public void TheProductionEntryPoint_SuppliesThisProcessesOwnPath_NotSomeOtherBinary()
    {
        using var dir = SupervisedProject();

        var viaEntryPoint = Hook.Decide("go test ./...", dir.Path, parentRunId: null);
        var viaProcessPath = Hook.Decide(
            "go test ./...", dir.Path, parentRunId: null, Environment.ProcessPath);

        Assert.Equal(viaProcessPath, viaEntryPoint);
        Assert.Contains(Environment.ProcessPath!, viaEntryPoint.Reason);
    }

    /// <summary>
    /// The field failure, reproduced through the production entry point: this suite runs under the
    /// xUnit test host, which is exactly the shape of <c>dotnet tman.dll</c> — a real absolute path
    /// to a real executable that is not tman. It must degrade, not rewrite.
    /// </summary>
    [Fact]
    public void RunningUnderAHostThatIsNotTman_TheEntryPointDegradesInsteadOfEmittingThatHost()
    {
        using var dir = SupervisedProject();
        Assert.NotEqual("tman", Path.GetFileNameWithoutExtension(Environment.ProcessPath));

        var decision = Hook.Decide("go test ./...", dir.Path, parentRunId: null);

        Assert.Equal(HookAction.Warn, decision.Action);
        Assert.Null(decision.Command);
    }
}

/// <summary>
/// Shares the "cwd" collection because it moves the process's working directory, which is global
/// state.
/// </summary>
[Collection("cwd")]
public class HookSupervisorPathTests
{
    /// <summary>
    /// A relative argv[0] is resolved through PATH, not cwd — the same trap that makes bare
    /// <c>./test</c> land on an unrelated binary on this fleet. So a relative supervisor path must
    /// be refused even when a file of that name really is sitting in the working directory: the
    /// shell that runs the rewritten command would not find it there.
    /// </summary>
    [Fact]
    public void ARelativeSupervisorPath_IsRefusedEvenWhenThatFileExistsInCwd()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "defaults {\n    max-parallel 2\n}\n");
        dir.WriteFile("bin/tman", "#!/bin/sh\nexit 0\n");
        var relative = Path.Combine("bin", "tman");

        var previous = Environment.CurrentDirectory;
        HookDecision decision;
        try
        {
            Environment.CurrentDirectory = dir.Path;
            Assert.True(File.Exists(relative), "precondition: the relative path resolves from cwd");
            decision = Hook.Decide("go test ./...", dir.Path, parentRunId: null, relative);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(HookAction.Warn, decision.Action);
        Assert.Null(decision.Command);
    }
}

/// <summary>
/// Drives the hook through <see cref="Program.Main"/>, the way Claude Code invokes it: JSON on
/// stdin, JSON on stdout, exit code decides whether the user's command is blocked. Shares the
/// "cwd" collection because redirecting <see cref="Console"/> is process-global state.
/// </summary>
[Collection("cwd")]
public class HookCommandTests
{
    /// <param name="parentRunId">
    /// The suite itself runs under <c>tman</c>, so TMAN_RUN_ID is set in this process and every
    /// command would look nested. Each case states the tree it means to be in.
    /// </param>
    static async Task<(int Code, string Stdout)> RunHook(
        string stdin, string? parentRunId = null, params string[] argv)
    {
        var prevIn = Console.In;
        var prevOut = Console.Out;
        var prevRunId = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
        var captured = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, parentRunId);
            Console.SetIn(new StringReader(stdin));
            Console.SetOut(captured);
            var code = await Program.Main(new[] { "hook" }.Concat(argv).ToArray());
            return (code, captured.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, prevRunId);
            Console.SetIn(prevIn);
            Console.SetOut(prevOut);
        }
    }

    static string BashRequest(string command, string cwd) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tool_name"] = "Bash",
            ["cwd"] = cwd,
            ["tool_input"] = new Dictionary<string, object?> { ["command"] = command },
        });

    /// <summary>
    /// Under the test host the identity check cannot pass, so this pins the shape the agent sees
    /// end to end — a note, no rewrite, exit 0 — rather than the rewrite it used to assert, which
    /// only ever passed because the test host was mistaken for tman.
    /// </summary>
    [Fact]
    public async Task PreToolUse_LeavesABareBuildAloneWithANoteWhenItCannotProveItsOwnIdentity()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "defaults {\n    max-parallel 2\n}\n");

        var (code, stdout) = await RunHook(
            BashRequest("dotnet build", dir.Path), parentRunId: null, "pretooluse");

        Assert.Equal(0, code);
        var hook = JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput");
        Assert.False(hook.TryGetProperty("updatedInput", out _));
        Assert.False(string.IsNullOrWhiteSpace(hook.GetProperty("additionalContext").GetString()));
    }

    [Fact]
    public async Task InsideASupervisedTree_TheSameBuildIsLeftAlone()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "defaults {\n    max-parallel 2\n}\n");

        var (code, stdout) = await RunHook(
            BashRequest("dotnet build", dir.Path), parentRunId: "01abc", "pretooluse");

        Assert.Equal(0, code);
        Assert.Equal("", stdout.Trim());
    }

    [Fact]
    public async Task MalformedStdin_SaysNothingAndExitsZeroRatherThanBlocking()
    {
        var (code, stdout) = await RunHook("{ this is not json", parentRunId: null, "pretooluse");

        Assert.Equal(0, code);
        Assert.Equal("", stdout.Trim());
    }

    [Fact]
    public async Task EmptyStdin_SaysNothingAndExitsZeroRatherThanBlocking()
    {
        var (code, stdout) = await RunHook("", parentRunId: null, "pretooluse");

        Assert.Equal(0, code);
        Assert.Equal("", stdout.Trim());
    }

    [Fact]
    public async Task UnknownHookEvent_FailsLoudlyWithoutBlocking()
    {
        var (code, stdout) = await RunHook("{}", parentRunId: null, "posttooluse");

        Assert.NotEqual(0, code);
        Assert.NotEqual(2, code); // 2 is the only exit code that blocks the tool call
        Assert.Equal("", stdout.Trim());
    }
}
