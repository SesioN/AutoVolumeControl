using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoVolumeControl
{
    /// <summary>
    /// Keeps the volume of every enabled app equal to the master volume of the default playback device.
    /// <para>
    /// All audio work runs as one "reconcile" step on a single <see cref="AudioThread"/>: attach to the device
    /// if needed, read the sessions, update the app list and correct volumes that differ. It is event driven:
    /// master volume changes, session changes (created, state, own volume, process exit), device changes and
    /// preference changes trigger it. A timer only runs while the device is not usable (e.g. at logon before the
    /// audio service is ready, or after it restarted) and retries with a growing delay until it works again, or
    /// when a paused correction (see <see cref="CorrectionThrottle"/>) is due again.
    /// </para>
    /// </summary>
    sealed class AutoVolumeService : IDisposable
    {
        public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(3);
        /// <summary>Upper bound for the retry delay, so a device or service that comes back is picked up quickly.</summary>
        public static readonly TimeSpan MaxRetryInterval = TimeSpan.FromSeconds(15);

        private readonly IAudioBackend backend;
        private readonly AppPreferences preferences;
        private readonly Apps apps;
        private readonly TimeSpan retryInterval;
        private readonly AudioThread audioThread = new AudioThread();
        private readonly Timer retryTimer;
        private readonly CorrectionThrottle throttle;
        private int started;
        private int healthy;
        private int reconcilePending;
        private int attachRequested;
        private int disposeCalled;
        // Only read and written on the audio thread.
        private bool disposed;
        private int consecutiveFailures;
        private Exception lastError;
        private readonly Stopwatch sinceLastAttach = new Stopwatch();

        public AutoVolumeService(IAudioBackend backend, AppPreferences preferences, Apps apps, TimeSpan? retryInterval = null, CorrectionThrottle throttle = null)
        {
            this.backend = backend;
            this.preferences = preferences;
            this.apps = apps;
            this.retryInterval = retryInterval ?? DefaultRetryInterval;
            this.throttle = throttle ?? new CorrectionThrottle();
            retryTimer = new Timer(_ => RequestSync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Binds to the default playback device and runs the first sync. The task faults if that first
        /// attempt fails; retries continue in the background.
        /// </summary>
        public Task Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                throw new InvalidOperationException("The service was already started.");

            backend.MasterVolumeChanged += OnChanged;
            backend.SessionsChanged += OnChanged;
            backend.ReattachRequired += OnReattachRequired;
            preferences.Changed += OnChanged;

            Interlocked.Exchange(ref attachRequested, 1);
            return audioThread.Post(() =>
            {
                var error = Reconcile();
                if (error != null)
                    throw error;
            });
        }

        /// <summary>True when the last reconcile succeeded.</summary>
        public bool IsHealthy => Volatile.Read(ref healthy) == 1;

        /// <summary>The failure of the last reconcile, or null.</summary>
        public Exception LastError => Volatile.Read(ref lastError);

        /// <summary>
        /// Re-reads the session list from Windows and brings the app list and volumes up to date. Used when the
        /// menu opens, so anything a missed notification left behind is corrected at the latest then.
        /// </summary>
        public Task RefreshAsync() => audioThread.Post(() =>
        {
            if (backend.IsAttached)
                backend.InvalidateSessions();
            Reconcile();
        });

        /// <summary>
        /// Schedules a reconcile. Requests arriving while one is still queued are merged into it, and the
        /// master volume is read when it runs, so the last change always wins.
        /// </summary>
        public void RequestSync()
        {
            if (Volatile.Read(ref disposeCalled) != 0)
                return;
            if (Interlocked.Exchange(ref reconcilePending, 1) == 1)
                return;

            audioThread.Post(() =>
            {
                Interlocked.Exchange(ref reconcilePending, 0);
                Reconcile();
            }).ContinueWith(LogFailure, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Completes once all work posted so far has run.</summary>
        public Task Flush() => audioThread.Post(() => { });

        private void OnChanged(object sender, EventArgs e) => RequestSync();

        private void OnReattachRequired(object sender, EventArgs e)
        {
            Interlocked.Exchange(ref attachRequested, 1);
            RequestSync();
        }

        /// <summary>Runs on the audio thread. Returns the failure, if any; never throws.</summary>
        private Exception Reconcile()
        {
            if (disposed)
                return null;

            var error = AttachIfNeeded() ?? SyncSessions();
            Volatile.Write(ref healthy, error == null ? 1 : 0);
            Volatile.Write(ref lastError, error);
            ScheduleTimer(error != null);
            return error;
        }

        private Exception AttachIfNeeded()
        {
            if (backend.IsAttached && Volatile.Read(ref attachRequested) == 1 && AttachTooSoon())
            {
                // Requested again right after an attach: keep the request and let the timer do it later, so no
                // chain of notifications can make the app re-attach in a tight loop.
                return null;
            }

            if (Interlocked.Exchange(ref attachRequested, 0) == 0 && backend.IsAttached)
                return null;

            sinceLastAttach.Restart();
            try
            {
                backend.Attach();
                Trace.WriteLine("Attached to the default playback device.");
                return null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Attaching to the default playback device failed: {ex.Message}");
                apps.Update(Enumerable.Empty<string>());
                return ex;
            }
        }

        private Exception SyncSessions()
        {
            try
            {
                var (volume, muted) = backend.GetMasterVolume();
                var sessions = backend.GetSessions();
                try
                {
                    UpdateApps(sessions);
                    VolumeSynchronizer.Sync(volume, muted, sessions, preferences, throttle);
                    throttle.Retain(sessions.Select(s => s.Id ?? s.Name));
                }
                finally
                {
                    DisposeAll(sessions);
                }
                return null;
            }
            catch (Exception ex)
            {
                // Typically the audio service restarted or the device vanished; all COM objects are stale.
                Trace.WriteLine($"Reading the playback device failed, re-attaching: {ex.Message}");
                Interlocked.Exchange(ref attachRequested, 1);
                return ex;
            }
        }

        /// <summary>
        /// The timer only runs while something failed (the delay doubles with every failure: 3 s, 6 s, 12 s, then
        /// every 15 s) or while an app's corrections are paused (then it fires when the pause ends).
        /// In normal operation everything is event driven.
        /// </summary>
        private void ScheduleTimer(bool failed)
        {
            consecutiveFailures = failed ? consecutiveFailures + 1 : 0;
            if (Volatile.Read(ref disposeCalled) != 0)
                return;

            var due = failed ? RetryDelay(consecutiveFailures) : Timeout.InfiniteTimeSpan;
            if (Volatile.Read(ref attachRequested) == 1)
            {
                // A deferred re-attach (see AttachIfNeeded) runs once the minimum distance has passed.
                var remaining = retryInterval - sinceLastAttach.Elapsed;
                var deferred = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                if (due == Timeout.InfiniteTimeSpan || deferred < due)
                    due = deferred;
            }
            var resume = throttle.TimeUntilNextResume();
            if (resume.HasValue && (due == Timeout.InfiniteTimeSpan || resume.Value < due))
                due = resume.Value;

            try
            {
                retryTimer.Change(due, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently.
            }
        }

        private bool AttachTooSoon() => sinceLastAttach.IsRunning && sinceLastAttach.Elapsed < retryInterval;

        private TimeSpan RetryDelay(int failures) => RetryDelay(retryInterval, failures);

        /// <summary>interval * 2^(failures - 1), capped at <see cref="MaxRetryInterval"/> (or the interval if larger).</summary>
        internal static TimeSpan RetryDelay(TimeSpan interval, int failures)
        {
            var max = interval > MaxRetryInterval ? interval : MaxRetryInterval;
            var ticks = interval.Ticks * Math.Pow(2, Math.Max(0, Math.Min(failures - 1, 16)));
            return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
        }

        private void UpdateApps(IReadOnlyList<IAudioSession> sessions)
        {
            var names = sessions
                .Where(s => !s.IsSystemSound && !string.IsNullOrEmpty(s.Name))
                .Select(s => s.Name)
                .Distinct()
                .ToList();

            foreach (var name in names)
                preferences.Register(name);

            apps.Update(names);
        }

        private static void DisposeAll(IReadOnlyList<IAudioSession> sessions)
        {
            foreach (var session in sessions)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Disposing session failed: {ex.Message}");
                }
            }
        }

        private static void LogFailure(Task task)
        {
            Trace.WriteLine($"Audio work failed: {task.Exception?.GetBaseException().Message}");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeCalled, 1) != 0)
                return;

            retryTimer.Dispose();
            backend.MasterVolumeChanged -= OnChanged;
            backend.SessionsChanged -= OnChanged;
            backend.ReattachRequired -= OnReattachRequired;
            preferences.Changed -= OnChanged;

            // Release the COM objects on the thread that created them, after any queued work is done.
            var shutdown = audioThread.Post(() =>
            {
                disposed = true;
                backend.Dispose();
            });
            try
            {
                shutdown.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException ex)
            {
                Trace.WriteLine($"Disposing the audio backend failed: {ex.GetBaseException().Message}");
            }
            audioThread.Dispose();
        }
    }
}
