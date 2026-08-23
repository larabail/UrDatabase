using System;
using System.IO;
using UrDatabase.Services;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A throwaway log directory, pointed at for as long as it is held and deleted afterwards.
    /// </summary>
    /// <remarks>
    /// One line in a test class instead of five, which is the point: the reason a dozen classes
    /// were writing to a maintainer's real <c>jellyfin.log</c> was never that anyone disagreed with
    /// the rule, it was that obeying it meant remembering a temporary path, a scope field, a
    /// <c>Dispose</c> and a recursive delete for a log nothing in the test ever reads. Make the
    /// right thing a field declaration and it gets done.
    ///
    /// The directory is not created up front. <c>AppLog.Write</c> creates it when there is
    /// something to put in it, so a test class that logs nothing leaves nothing behind, and the
    /// delete in <see cref="Dispose"/> tolerates its absence.
    ///
    /// Construct it in a field initializer or a constructor rather than inside a test method. The
    /// redirect is async-local, so it reaches the test body from there, and a class whose
    /// constructor redirects covers every one of its tests including the ones added next year —
    /// which is the failure mode here, since the tests that logged were never the ones anybody
    /// thought were about logging.
    /// </remarks>
    internal sealed class TempLog : IDisposable
    {
        private readonly IDisposable _scope;

        public TempLog()
        {
            Directory = Path.Combine(Path.GetTempPath(), "urdb-log-" + Guid.NewGuid().ToString("N"));
            _scope = AppLog.Redirect(Directory);
        }

        /// <summary>Where the log went, for the rare test that wants to read it back.</summary>
        public string Directory { get; }

        public void Dispose()
        {
            _scope.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
        }
    }
}
