using System.Runtime.CompilerServices;
using Tman;

namespace Tman.Tests;

static class NoContainerEngine
{
    /// <summary>
    /// Whatever docker the host has is not the suite's to talk to: a run ending here would list the
    /// host's containers, and a test's sweep would be timed by the daemon. Every test sees no docker
    /// unless it fakes one — in process through the seam, and in the tman processes it starts
    /// through TMAN_DOCKER, which they inherit.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
    {
        Containers.FindEngine = () => null;
        Environment.SetEnvironmentVariable(Containers.EngineEnvVar, "off");
    }
}
