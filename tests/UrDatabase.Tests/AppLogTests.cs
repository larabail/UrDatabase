using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The log is the one service here that used to have no way of being pointed anywhere else,
    /// so a test exercising any failure path appended to the real install's log — the directory
    /// AGENTS.md forbids a test from touching at all, because it also holds somebody's catalogue
    /// and their credentials.
    ///
    /// The assertions about the real directory being restored are the reason the redirect is
    /// async-local rather than a plain static: with a static, another class redirecting in
    /// parallel makes them fail, and that is the mild symptom of the actual problem, which is
    /// that class writing into a temporary folder somebody else is about to delete.
    /// </summary>
    public class AppLogTests : IDisposable
    {
        private readonly string _dir;

        public AppLogTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void A_redirected_log_is_written_where_it_was_pointed()
        {
            using (AppLog.Redirect(_dir))
            {
                AppLog.Write("test.log", "a line");
            }

            Assert.Contains("a line", File.ReadAllText(Path.Combine(_dir, "test.log")));
        }

        [Fact]
        public void The_real_directory_is_restored_when_the_scope_ends()
        {
            using (AppLog.Redirect(_dir))
            {
                Assert.Equal(Path.GetFullPath(_dir), AppLog.Directory);
            }

            Assert.Equal(PlatformPaths.LogDirectory, AppLog.Directory);
        }

        /// <summary>
        /// The restore has to happen even when the test that redirected it fails, or one failing
        /// assertion leaves the whole rest of the suite writing into a deleted temporary folder.
        /// </summary>
        [Fact]
        public void The_directory_is_restored_even_when_the_scope_body_throws()
        {
            // Not a lambda: `using (...) { throw; }` as an expression body binds to the
            // Func<Task> overload of Assert.Throws, which xUnit rightly refuses.
            try
            {
                using (AppLog.Redirect(_dir))
                {
                    throw new InvalidOperationException("boom");
                }
            }
            catch (InvalidOperationException)
            {
                // Expected: the point is what the directory is afterwards.
            }

            Assert.Equal(PlatformPaths.LogDirectory, AppLog.Directory);
        }

        [Fact]
        public void Redirects_nest_and_unwind_in_order()
        {
            var inner = Path.Combine(_dir, "inner");

            using (AppLog.Redirect(_dir))
            {
                using (AppLog.Redirect(inner))
                {
                    Assert.Equal(Path.GetFullPath(inner), AppLog.Directory);
                }

                Assert.Equal(Path.GetFullPath(_dir), AppLog.Directory);
            }

            Assert.Equal(PlatformPaths.LogDirectory, AppLog.Directory);
        }

        [Fact]
        public void Disposing_a_scope_twice_does_not_undo_somebody_elses_redirect()
        {
            var scope = AppLog.Redirect(_dir);
            scope.Dispose();

            var other = Path.Combine(_dir, "other");
            using (AppLog.Redirect(other))
            {
                scope.Dispose();
                Assert.Equal(Path.GetFullPath(other), AppLog.Directory);
            }
        }

        [Fact]
        public void A_blank_directory_is_refused_rather_than_silently_meaning_the_real_one()
        {
            Assert.Throws<ArgumentException>(() => AppLog.Redirect(""));
            Assert.Throws<ArgumentException>(() => AppLog.Redirect("   "));
            Assert.Throws<ArgumentException>(() => AppLog.Redirect(null!));
        }

        [Fact]
        public void Writing_never_throws_however_bad_the_path_is()
        {
            using (AppLog.Redirect(Path.Combine(_dir, "nested", "deeper")))
            {
                AppLog.Write("test.log", "still fine");
            }

            // A filename that cannot exist: the write is swallowed, because logging must never be
            // the reason the app fails.
            using (AppLog.Redirect(_dir))
            {
                AppLog.Write(new string('x', 4096), "unwritable");
            }
        }
    }
}
