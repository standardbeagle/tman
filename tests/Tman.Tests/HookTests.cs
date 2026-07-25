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

    static string? RewrittenCommand(string response)
    {
        if (response.Length == 0) return null;
        var root = JsonDocument.Parse(response).RootElement;
        if (!root.TryGetProperty("hookSpecificOutput", out var hook)) return null;
        if (!hook.TryGetProperty("updatedInput", out var updated)) return null;
        return updated.GetProperty("command").GetString();
    }

    static string? Warning(string response)
    {
        if (response.Length == 0) return null;
        var root = JsonDocument.Parse(response).RootElement;
        if (!root.TryGetProperty("hookSpecificOutput", out var hook)) return null;
        return hook.TryGetProperty("additionalContext", out var ctx) ? ctx.GetString() : null;
    }

    [Theory]
    [InlineData("go test ./...", "tman run -- go test ./...")]
    [InlineData("go build ./...", "tman run -- go build ./...")]
    [InlineData("dotnet test", "tman run -- dotnet test")]
    [InlineData("dotnet build -c Release", "tman run -- dotnet build -c Release")]
    [InlineData("npm test", "tman run -- npm test")]
    [InlineData("npm run build", "tman run -- npm run build")]
    [InlineData("pytest -k \"slow path\"", "tman run -- pytest -k \"slow path\"")]
    [InlineData("cargo test", "tman run -- cargo test")]
    [InlineData("make test", "tman run -- make test")]
    public void BareTestOrBuild_InSupervisedProject_IsRewritten(string command, string expected)
    {
        using var dir = SupervisedProject();

        var response = Hook.Render(Request(command, dir.Path), parentRunId: null);

        Assert.Equal(expected, RewrittenCommand(response));
    }

    [Fact]
    public void Rewrite_PreservesTheRestOfTheToolInput()
    {
        using var dir = SupervisedProject();

        var response = Hook.Render(Request("go test ./...", dir.Path), parentRunId: null);

        var updated = JsonDocument.Parse(response).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("run it", updated.GetProperty("description").GetString());
    }

    [Fact]
    public void Rewrite_AnnouncesItselfSoTheTranscriptStillMatchesWhatRan()
    {
        using var dir = SupervisedProject();

        var response = Hook.Render(Request("go test ./...", dir.Path), parentRunId: null);

        var message = JsonDocument.Parse(response).RootElement.GetProperty("systemMessage").GetString();
        Assert.Contains("tman run -- go test ./...", message);
    }

    [Fact]
    public void WithoutTmanKdl_WarnsAndLeavesTheCommandAlone()
    {
        using var dir = new TempDir();

        var response = Hook.Render(Request("go test ./...", dir.Path), parentRunId: null);

        Assert.Null(RewrittenCommand(response));
        Assert.Contains(".tman.kdl", Warning(response));
    }

    [Fact]
    public void CompoundCommand_WarnsInsteadOfRewritingAShellItDidNotParse()
    {
        using var dir = SupervisedProject();

        var response = Hook.Render(Request("cd packages/app && npm test", dir.Path), parentRunId: null);

        Assert.Null(RewrittenCommand(response));
        Assert.Contains("tman run --", Warning(response));
    }

    [Fact]
    public void EnvPrefixedCommand_WarnsInsteadOfRewritingIntoABrokenArgv()
    {
        using var dir = SupervisedProject();

        var response = Hook.Render(Request("CI=1 npm test", dir.Path), parentRunId: null);

        Assert.Null(RewrittenCommand(response));
        Assert.NotNull(Warning(response));
    }

    [Fact]
    public void InsideASupervisedTree_PassesThroughUntouched()
    {
        using var dir = SupervisedProject();

        var response = Hook.Render(Request("go test ./...", dir.Path), parentRunId: "01abc");

        Assert.Equal("", response);
    }

    [Theory]
    [InlineData("tman run -- go test ./...")]
    [InlineData("tman test")]
    [InlineData("/usr/local/bin/tman run -- npm test")]
    public void TmanItself_IsNeverIntercepted(string command)
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Hook.Render(Request(command, dir.Path), parentRunId: null));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("npm run dev")]
    [InlineData("ls -la")]
    [InlineData("./test")]
    public void UnrelatedCommands_PassThrough(string command)
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Hook.Render(Request(command, dir.Path), parentRunId: null));
    }

    [Fact]
    public void NonBashTools_PassThrough()
    {
        using var dir = SupervisedProject();

        Assert.Equal("", Hook.Render(Request("go test ./...", dir.Path, tool: "Read"), parentRunId: null));
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

    [Fact]
    public async Task PreToolUse_RewritesABareBuildAndExitsZero()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "defaults {\n    max-parallel 2\n}\n");

        var (code, stdout) = await RunHook(
            BashRequest("dotnet build", dir.Path), parentRunId: null, "pretooluse");

        Assert.Equal(0, code);
        var updated = JsonDocument.Parse(stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("tman run -- dotnet build", updated.GetProperty("command").GetString());
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
