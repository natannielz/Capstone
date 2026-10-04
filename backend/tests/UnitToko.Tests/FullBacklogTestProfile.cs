using System.Runtime.CompilerServices;

namespace UnitToko.Tests;

internal static class FullBacklogTestProfile
{
    // Retained regression coverage exercises the full backlog explicitly. Pilot HTTP tests override
    // this setting to release1; production code defaults to release1 without a test exception.
    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable("Release__Profile", "full");
}
