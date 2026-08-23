using System;

namespace UrDatabase.Services
{
    /// <summary>What, if anything, the server should be told right now.</summary>
    public enum PlaybackReportKind
    {
        /// <summary>Nothing is due. By far the commonest answer.</summary>
        None = 0,

        /// <summary><c>POST /Sessions/Playing</c>. Sent once, when a film actually starts.</summary>
        Start = 1,

        /// <summary><c>POST /Sessions/Playing/Progress</c>. Sent periodically, and on a pause.</summary>
        Progress = 2,

        /// <summary><c>POST /Sessions/Playing/Stopped</c>. Sent once, and only after a start.</summary>
        Stopped = 3
    }

    /// <summary>One thing to tell the server, or <see cref="PlaybackReportKind.None"/>.</summary>
    public sealed record PlaybackReport(PlaybackReportKind Kind, long PositionTicks, bool IsPaused)
    {
        public static readonly PlaybackReport Nothing = new(PlaybackReportKind.None, 0, false);

        public bool IsSomething => Kind != PlaybackReportKind.None;
    }

    /// <summary>
    /// Decides when a playback report is due, from nothing but readings and a clock.
    /// </summary>
    /// <remarks>
    /// The whole of the judgement in progress reporting, kept away from the socket that does it so
    /// every rule can be asserted. The rules that matter:
    ///
    /// A film nobody started is never reported. The viewer can point the app at a stream and close
    /// the player before a frame is drawn, and telling the server they watched it would put a film
    /// in their Continue watching row that they never saw. So the first report waits for VLC to
    /// say it is actually playing.
    ///
    /// A paused film is not an abandoned one. Progress keeps being sent while paused, flagged as
    /// paused, because a session that simply goes quiet is one the server eventually times out —
    /// so silence would turn "gone to make tea" into "stopped watching".
    ///
    /// A stop is reported with the last position actually seen, not the one in the reading that
    /// announced the stop. VLC reports a time of zero the instant a film ends, and forwarding that
    /// would rewind the film to the beginning on the server — the exact opposite of the point.
    /// </remarks>
    public sealed class PlaybackReportSchedule
    {
        /// <summary>
        /// How often progress is sent while a film runs. Jellyfin's own clients use ten seconds,
        /// and it is the number that decides how much of the film is lost when something dies
        /// without saying so.
        /// </summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long the interface may go unanswered, after a film has started, before it is taken
        /// as the player having gone. Long enough to ride out a missed poll, short enough that the
        /// stop is recorded while the position is still nearly right.
        /// </summary>
        public static readonly TimeSpan DefaultSilenceBeforeStopped = TimeSpan.FromSeconds(6);

        /// <summary>
        /// How long to wait for the interface to answer at all before giving up on this film.
        /// Reached when VLC is a build without the HTTP interface, when something else took the
        /// port, or when the viewer closed the player immediately. Nothing is reported, and
        /// nothing is shown: the film played, which was the point.
        /// </summary>
        public static readonly TimeSpan DefaultGiveUpAfterSilence = TimeSpan.FromSeconds(30);

        private readonly TimeSpan _interval;
        private readonly TimeSpan _silenceBeforeStopped;
        private readonly TimeSpan _giveUpAfterSilence;

        private bool _started;
        private bool _finished;
        private long _lastPositionTicks;
        private bool _lastReportedPaused;
        private TimeSpan _lastReportAt;
        private TimeSpan? _silentSince;

        public PlaybackReportSchedule(
            TimeSpan? interval = null,
            TimeSpan? silenceBeforeStopped = null,
            TimeSpan? giveUpAfterSilence = null)
        {
            _interval = interval ?? DefaultInterval;
            _silenceBeforeStopped = silenceBeforeStopped ?? DefaultSilenceBeforeStopped;
            _giveUpAfterSilence = giveUpAfterSilence ?? DefaultGiveUpAfterSilence;
        }

        /// <summary>True once there is nothing further to say and polling should stop.</summary>
        public bool IsFinished => _finished;

        /// <summary>True once a start has been reported. Nothing else may be sent before it.</summary>
        public bool HasStarted => _started;

        /// <summary>The most recent position the player actually reported.</summary>
        public long LastPositionTicks => _lastPositionTicks;

        /// <summary>
        /// What is due, given one reading and how long the film has been open.
        /// </summary>
        /// <param name="status">The reading, or null when the interface did not answer.</param>
        /// <param name="elapsed">Time since the player was launched, from a monotonic clock.</param>
        public PlaybackReport Next(VlcStatus? status, TimeSpan elapsed)
        {
            if (_finished) return PlaybackReport.Nothing;

            if (status is null) return NoAnswer(elapsed);

            _silentSince = null;

            // Remembered from every reading that holds a film, so a stop can be reported at the
            // place the film actually reached rather than at whatever VLC says once it has let go.
            if (status.HasFilm && status.PositionTicks > 0) _lastPositionTicks = status.PositionTicks;

            if (!_started)
            {
                // Only a film that is running starts a session. A player still opening the stream,
                // one already paused before a frame was drawn, and one that never got going at all
                // are all films nobody watched.
                if (!status.IsPlaying) return PlaybackReport.Nothing;

                _started = true;
                _lastReportAt = elapsed;
                _lastReportedPaused = false;

                return new PlaybackReport(PlaybackReportKind.Start, status.PositionTicks, false);
            }

            if (status.State == VlcPlaybackState.Stopped) return Stop();

            // A pause is worth an immediate report rather than one at the next interval: it is the
            // difference between a session the server shows as paused and one it shows as playing
            // for another ten seconds after the viewer walked away.
            var pauseChanged = status.IsPaused != _lastReportedPaused;

            if (!pauseChanged && elapsed - _lastReportAt < _interval) return PlaybackReport.Nothing;

            _lastReportAt = elapsed;
            _lastReportedPaused = status.IsPaused;

            return new PlaybackReport(
                PlaybackReportKind.Progress,
                status.PositionTicks > 0 ? status.PositionTicks : _lastPositionTicks,
                status.IsPaused);
        }

        /// <summary>
        /// The last word, for a film still running when the app is asked to close. Reports the
        /// stop if one is owed, and nothing otherwise — so it is safe to call unconditionally.
        /// </summary>
        public PlaybackReport Finish() => _finished || !_started ? PlaybackReport.Nothing : Stop();

        private PlaybackReport NoAnswer(TimeSpan elapsed)
        {
            _silentSince ??= elapsed;
            var silentFor = elapsed - _silentSince.Value;

            if (_started)
                return silentFor >= _silenceBeforeStopped ? Stop() : PlaybackReport.Nothing;

            // Never answered and never played. The interface is not there — a VLC built without
            // it, a port taken between being offered and being bound, or a player already closed.
            // Give up quietly; there is nothing to report and nothing worth telling the viewer.
            if (silentFor >= _giveUpAfterSilence) _finished = true;

            return PlaybackReport.Nothing;
        }

        private PlaybackReport Stop()
        {
            _finished = true;
            return new PlaybackReport(PlaybackReportKind.Stopped, _lastPositionTicks, false);
        }
    }
}
