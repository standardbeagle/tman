namespace Tman;

/// <summary>A container a test fixture labelled with the run that started it (<see cref="Containers.Label"/>).</summary>
/// <param name="RunId">The label's value; empty when the label was set to nothing.</param>
/// <param name="CreatedUtc">When docker created it; null when docker did not say in a form tman reads.</param>
public sealed record LabelledContainer(string Id, string Name, string RunId, DateTime? CreatedUtc = null);

/// <summary>What tman asks of docker: one listing and one removal.</summary>
public interface IContainerEngine
{
    /// <summary>Every container carrying the <see cref="Containers.Label"/> label; null when docker could not answer.</summary>
    IReadOnlyList<LabelledContainer>? ListLabelled();

    /// <summary>Removes the containers and their anonymous volumes (`docker rm -f -v`); returns the ids that went.</summary>
    IReadOnlySet<string> Remove(IReadOnlyList<LabelledContainer> containers);
}

/// <summary>Not yet implemented: removing the docker containers a finished run's fixtures labelled.</summary>
public static class Containers
{
    public const string Label = "tman.run";
    public const string EngineEnvVar = "TMAN_DOCKER";

    internal static Func<IContainerEngine?> FindEngine = DockerCli.Find;

    internal static string CheckedStampPath => Store.MarkerPath("containers.checked");
    internal static string BackoffStampPath => Store.MarkerPath("containers.backoff");

    public static bool Due(RunRecord r, IReadOnlyList<RunRecord> records) => false;

    public static List<LabelledContainer> Doomed(
        IEnumerable<LabelledContainer> found, IReadOnlyList<RunRecord> records, DateTime nowUtc) => [];

    public static IReadOnlyList<LabelledContainer> Sweep(IReadOnlyList<RunRecord> records, bool now, bool quiet) => [];

    public static void AfterRun(RunRecord record) { }
}

/// <summary>Not yet implemented: the docker CLI.</summary>
sealed class DockerCli
{
    internal static IContainerEngine? Find() => null;

    internal static string? Resolve(string command, string? pathVar, bool windows) => null;

    internal static List<LabelledContainer>? ParseInspect(string json) => [];
}
