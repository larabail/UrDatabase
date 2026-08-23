using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Watches a playing film and tells the server where it has got to.
    /// </summary>
    /// <remarks>
    /// The loop between the two halves of progress reporting: it reads VLC through an
    /// <see cref="IVlcStatusReader"/>, asks a <see cref="PlaybackReportSchedule"/> what is due, and
    /// hands that to an <see cref="IPlaybackReportSink"/>. Everything it depends on is an
    /// interface or a delegate, including the clock and the wait, so the whole of it is tested
    /// without a socket, a player or a second passing.
    ///
    /// Nothing in here may become a reason a film stops playing. The player was launched before
    /// this ran and knows nothing about it; every failure is caught, written to the Jellyfin log
    /// and dropped. A viewer whose server has gone offline mid-film should notice nothing at all.
    /// </remarks>
    public sealed class PlaybackReporter
    {
        /// <summary>
        /// How often VLC is asked where it is. Five times more often than a report is sent, so a
        /// pause and a stop are both noticed promptly without the server hearing about either
        /// more than it needs to.
        /// </summary>
        public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long the closing report may take when the app is shutting down. It is sent on a
        /// fresh deadline rather than the cancelled token that prompted it, or it would be
        /// cancelled before it left.
        /// </summary>
        public static readonly TimeSpan DefaultFinalReportTimeout = TimeSpan.FromSeconds(5);

        private readonly IVlcStatusReader _reader;
        private readonly IPlaybackReportSink _sink;
        private readonly PlaybackReportSchedule _schedule;
        private readonly TimeSpan _pollInterval;
        private readonly Func<TimeSpan> _clock;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly Action<string>? _log;

        public PlaybackReporter(
            IVlcStatusReader reader,
            IPlaybackReportSink sink,
            PlaybackReportSchedule? schedule = null,
            TimeSpan? pollInterval = null,
            Func<TimeSpan>? clock = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            Action<string>? log = null)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _schedule = schedule ?? new PlaybackReportSchedule();
            _pollInterval = pollInterval ?? DefaultPollInterval;
            _delay = delay ?? Task.Delay;
            _log = log;

            if (clock is not null)
            {
                _clock = clock;
            }
            else
            {
                // Monotonic. A wall clock would jump when the machine slept or the clock was
                // corrected, and every interval measured across it would be nonsense.
                var stopwatch = Stopwatch.StartNew();
                _clock = () => stopwatch.Elapsed;
            }
        }

        /// <summary>
        /// Follows the film to its end, or until <paramref name="ct"/> is cancelled — which is the
        /// app closing while something is still playing, and is answered with a final stop at the
        /// last position seen.
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            var started = _clock();

            try
            {
                while (!ct.IsCancellationRequested && !_schedule.IsFinished)
                {
                    var status = VlcStatus.Parse(await _reader.ReadAsync(ct));

                    await SendAsync(_schedule.Next(status, _clock() - started), ct);

                    if (_schedule.IsFinished) break;

                    await _delay(_pollInterval, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // The app is closing. Handled below: a stop with the last known position is worth
                // far more than a clean exit that says nothing.
            }
            catch (Exception ex)
            {
                _log?.Invoke($"progress reporting stopped: {ex.Message}");
            }

            if (_schedule.IsFinished) return;

            using var deadline = new CancellationTokenSource(DefaultFinalReportTimeout);
            await SendAsync(_schedule.Finish(), deadline.Token);
        }

        private async Task SendAsync(PlaybackReport report, CancellationToken ct)
        {
            if (!report.IsSomething) return;

            try
            {
                switch (report.Kind)
                {
                    case PlaybackReportKind.Start:
                        await _sink.StartedAsync(report.PositionTicks, ct);
                        break;

                    case PlaybackReportKind.Progress:
                        await _sink.ProgressAsync(report.PositionTicks, report.IsPaused, ct);
                        break;

                    case PlaybackReportKind.Stopped:
                        await _sink.StoppedAsync(report.PositionTicks, ct);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Nothing to do about it, and nothing worth saying: the film is playing either way.
            }
            catch (Exception ex)
            {
                // A server that has gone away mid-film costs the viewer a resume position, not
                // their evening. Logged so it can be found, and never shown.
                _log?.Invoke($"could not report {report.Kind.ToString().ToLowerInvariant()}: {ex.Message}");
            }
        }
    }
}
