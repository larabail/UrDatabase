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

        /// <summary>
        /// Whether a write that has not been redirected is an error rather than a log line. Off in
        /// the application, and only ever turned on by a test assembly.
        /// </summary>
        /// <remarks>
        /// A plain static, unlike <see cref="Override"/>, and deliberately: this is a fact about
        /// the process — "these binaries are being exercised by a test run" — rather than about one
        /// logical call. An async-local here would be worse than useless, because the writes most
        /// likely to escape notice are the ones on a background task, and those are exactly the
        /// contexts an async-local flag set by a test method would not reach.
        /// </remarks>
        private static volatile bool _realDirectoryForbidden;

        /// <summary>The directory being written to, real or redirected.</summary>
        public static string Directory => Override.Value ?? PlatformPaths.LogDirectory;

        /// <summary>Whether <see cref="ForbidRealDirectory"/> has been called.</summary>
        public static bool IsRealDirectoryForbidden => _realDirectoryForbidden;

        /// <summary>
        /// Refuses, from here until the process ends, any write that has not been redirected.
        /// </summary>
        /// <remarks>
        /// <see cref="Redirect"/> gave tests a way to stay out of somebody's install;
        /// this is what stops the next one forgetting. Two years of "remember to redirect" is how
        /// the upload tests came to append a twelve-byte <c>Arrival (2016)</c> to a maintainer's
        /// real <c>jellyfin.log</c> on every full run, on every machine, unnoticed — a rule nobody
        /// is reminded of is a rule that holds until the next feature adds a log line.
        ///
        /// The refusal happens before the filesystem is touched, which is the guarantee that
        /// matters: an un-redirected write cannot reach the real directory even when the resulting
        /// exception is swallowed by the <c>catch</c> the log line was written inside, or lost on a
        /// fire-and-forget task nobody awaits. Throwing on top of that is what usually makes it
        /// loud, and it is the part that is best-effort rather than absolute.
        ///
        /// There is no way back. A scope would only invite a test to switch the guard off for the
        /// duration of the write it could not be bothered to redirect, which is the failure this
        /// exists to prevent, and the flag is process-wide so one test disarming it would disarm
        /// every collection running beside it.
        ///
        /// Nothing in the application calls this. With it unset — which is every shipped build —
        /// <see cref="Write"/> behaves exactly as it did, still swallowing everything, because
        /// logging must never be the reason the app fails.
        /// </remarks>
        public static void ForbidRealDirectory() => _realDirectoryForbidden = true;

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
            var directory = Override.Value;

            // Before the try, so it cannot be swallowed by the catch below, and before any
            // filesystem call, so the real directory is untouched whatever happens to the throw.
            if (directory is null && _realDirectoryForbidden)
                throw new UnredirectedLogWriteException(fileName, message);

            try
            {
                directory ??= PlatformPaths.LogDirectory;
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

    /// <summary>
    /// Thrown when a test writes a log line without saying where it should go.
    /// </summary>
    /// <remarks>
    /// Its own type rather than a bare <see cref="InvalidOperationException"/> so that the one test
    /// which triggers it deliberately can say so precisely, and so that a reader hitting it in a
    /// failure report has something to search for. The message carries the fix rather than the
    /// complaint, because whoever meets this will be someone who has just added a log line to a
    /// service and has no reason yet to know any of this history.
    /// </remarks>
    public sealed class UnredirectedLogWriteException : InvalidOperationException
    {
        public UnredirectedLogWriteException(string fileName, string message)
            : base(Describe(fileName, message))
        {
            FileName = fileName;
            LogMessage = message;
        }

        /// <summary>The log the refused line was headed for, such as <c>jellyfin.log</c>.</summary>
        public string FileName { get; }

        /// <summary>The line itself, which is usually enough to name the code that wrote it.</summary>
        public string LogMessage { get; }

        private static string Describe(string fileName, string message) =>
            $"This write to {fileName} was refused because nothing redirected it, so it would have " +
            $"gone to {PlatformPaths.LogDirectory} — somebody's real install, which AGENTS.md " +
            "forbids a test from touching because the same folder holds their catalogue and their " +
            "credentials. Wrap the code under test in `using (AppLog.Redirect(dir))`, pointing at a " +
            "temporary directory the test creates and deletes. The refused line was: " + message;
    }
}
