using System.Runtime.CompilerServices;
using Tman;

namespace Tman.Tests;

static class AssumeAnIdleHost
{
    /// <summary>
    /// The host's real load is whatever else this machine is doing, so a test admitting beside an
    /// admitted run would pass or fail with it. Every test sees an unmeasured host unless it fakes one.
    /// </summary>
    [ModuleInitializer]
    internal static void Install() => Admission.MeasureHost = () => null;
}
