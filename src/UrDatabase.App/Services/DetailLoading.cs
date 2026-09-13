using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{

    /// <summary>
    /// Shows a page before enriching it. Each lookup has its own deadline; a slow optional
    /// service must not consume the time allowed to every other service or discard the page.
    /// </summary>
    public sealed class DetailLoading : IDisposable
    {
        private readonly CancellationToken _appLifetime;
        private readonly CancellationTokenSource _cts;
        private readonly Action<string> _report;
        private readonly TimeSpan _requestTimeout;
        private readonly List<string> _failures = new();

        public DetailLoading(CancellationToken appLifetime, Action<string> report, TimeSpan? requestTimeout = null)
        {
            _appLifetime = appLifetime;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
            _report = report;
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(12);
        }

        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        public async Task ShowAsync(Func<Task> show, Func<DetailLoading, Task> enrich)
        {
            _appLifetime.ThrowIfCancellationRequested();
            var closed = show().WaitAsync(_appLifetime);
            _report("Loading details...");
            var pending = EnrichAsync(enrich);

            try
            {
                // Observe failures promptly, but successful enrichment does not close the page.
                var first = await Task.WhenAny(closed, pending);
                await first;
                await closed;
            }
            finally
            {
                Cancel();
                await pending;
            }
        }

        private async Task EnrichAsync(Func<DetailLoading, Task> enrich)
        {
            await enrich(this);
            if (!IsCancellationRequested)
                _report(FailureNotice());
        }

        /// <summary>
        /// Applies an answer only while this page and this request are still current. Callers
        /// start independent lookups together and await prerequisites only where an id is needed.
        /// </summary>
        public async Task<bool> RunAsync<T>(string source, Func<CancellationToken, Task<T>> request, Action<T> apply)
        {
            if (IsCancellationRequested) return false;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            deadline.CancelAfter(_requestTimeout);
            T result;
            try
            {
                // Also bounds work that cannot be interrupted, such as a filesystem probe on an
                // unavailable Windows share. Its late answer never reaches the model or the view.
                result = await request(deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                ReportFailure(source, "timed out", ex);
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or JellyfinException
                                       or IOException or SqliteException)
            {
                if (!IsCancellationRequested) ReportFailure(source, "could not be loaded", ex);
                return false;
            }

            apply(result);
            return true;
        }

        private void ReportFailure(string source, string reason, Exception exception)
        {
            var notice = $"{source} {reason}.";
            _failures.Add(notice);
            // Exception messages can contain request URLs, including API keys and server tokens.
            AppLog.Write("details.log", $"{notice} ({exception.GetType().Name})");
            _report(FailureNotice());
        }

        private string FailureNotice() => _failures.Count == 0 ? ""
            : string.Join(" ", _failures) + " Other details remain available. Reopen the page to retry.";

        public void Cancel() => _cts.Cancel();

        public void Dispose()
        {
            Cancel();
            _cts.Dispose();
        }
    }
}
