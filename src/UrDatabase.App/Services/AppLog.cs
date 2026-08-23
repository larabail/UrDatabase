using System;
using System.IO;
using System.Threading;

namespace UrDatabase.Services
{
    /// <summary>Best-effort diagnostics written under the per-user app data folder on any OS.</summary>
    public static class AppLog
    {
        /// <summary>
        /// Where the log actually goes. Null means the real per-user directory, which is what the
        /// application itself always uses.
        /// </summary>
        /// <remarks>
        /// <see cref="AsyncLocal{T}"/> rather than a plain static, and this is not a detail. xUnit
        /// runs test collections in parallel, so a plain static redirect set by one class is
        /// visible to every class running beside it — which both breaks their assertions and,
        /// worse, points them at a temporary directory that is about to be deleted. A first
        /// attempt here used a plain field and the suite caught it immediately.
        ///
        /// An async-local value belongs to the logical call context that set it, so a redirect
        /// reaches the code under test, follows it across <c>await</c>, and is invisible to
        /// anything running in parallel.
        /// </remarks>
        private static readonly AsyncLocal<string?> Override = new();

        /// <summary>The directory being written to, real or redirected.</summary>
        public static string Directory => Override.Value ?? PlatformPaths.LogDirectory;

        /// <summary>
        /// Points the log at a directory of the caller's choosing until the returned scope is
        /// disposed.
        /// </summary>
        /// <remarks>
        /// This exists because of the rule in <c>AGENTS.md</c> written after a harness destroyed a
        /// maintainer's credentials: nothing under <c>tests/</c>, and no throwaway script, may read
        /// or write the real app data directory. Every other service here already takes an explicit
        /// path — <c>AppConfig</c>, <c>ConfigStore</c>, <c>Database</c> — and this one did not, so a
        /// test that merely exercised a failure path appended to the log of whoever ran it. That is
        /// a bug in this class rather than a licence to keep doing it.
        ///
        /// A disposable scope rather than a settable property, for two reasons: a redirect left
        /// switched on is a suite writing somewhere nobody is looking, and the restore has to
        /// happen even when an assertion throws.
        ///
        /// The redirect is scoped to the calling context rather than to the process — see the
        /// remarks on the field — so two test classes running in parallel cannot see each other's.
        /// </remarks>
        public static IDisposable Redirect(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A log directory is required.", nameof(directory));

            var previous = Override.Value;
            Override.Value = Path.GetFullPath(directory);

            return new Scope(() => Override.Value = previous);
        }

        public static void Write(string fileName, string message)
        {
            try
            {
                var directory = Directory;
                System.IO.Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the reason the app fails.
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _restore;
            private bool _disposed;

            public Scope(Action restore) => _restore = restore;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _restore();
            }
        }
    }
}
