using System.Runtime.CompilerServices;
using UrDatabase.Services;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Shuts the suite out of the real log directory before a single test runs.
    /// </summary>
    /// <remarks>
    /// <c>AppLog.Redirect</c> has existed for a while and is well documented, and the upload tests
    /// still appended a twelve-byte <c>Arrival (2016)</c> to a maintainer's real
    /// <c>jellyfin.log</c> on every full run of this suite, on every machine, for as long as those
    /// tests have existed. Nobody was careless; a rule that has to be remembered every time a
    /// service gains a log line is a rule that lapses. So the suite now refuses the write instead
    /// of trusting itself to redirect it, and a test that reaches un-redirected logging fails
    /// immediately with a message saying what to do about it.
    ///
    /// A module initializer rather than a fixture, because a fixture only covers the collections
    /// that ask for it and the next test class would have to remember to ask. This runs before any
    /// code in the assembly can, whichever test the runner happens to start with, and whether the
    /// run is one <c>[Fact]</c> from an editor or the whole suite in CI.
    ///
    /// What it does not cover: a test that writes under the real install directly, with
    /// <c>File.WriteAllText</c> or by leaving <c>URDATABASE_DATA_DIR</c> alone while calling
    /// something that defaults to <c>PlatformPaths.AppDataRoot</c>. Every other service here takes
    /// an explicit path, so that is a narrower hole than logging was, but it is still a hole.
    /// </remarks>
    internal static class LogIsolation
    {
        [ModuleInitializer]
        internal static void Arm() => AppLog.ForbidRealDirectory();
    }
}
