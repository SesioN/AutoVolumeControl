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
    /// audio service is ready, or after it restarted) and retries until attaching succeeds.
    /// </para>
    /// </summary>
    sealed class AutoVolumeService : IDisposable
    {
        public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(3);

        private readonly IAudioBackend backend;
        private readonly AppPreferences preferences;
        private readonly Apps apps;
        private readonly TimeSpan retryInterval;
        private readonly AudioThread audioThread = new AudioThread();
        private readonly Timer retryTimer;
        private int reconcilePending;
        private int attachRequested;
        private int disposeCalled;
        // Only read and written on the audio thread.
        private bool disposed;

        public AutoVolumeService(IAudioBackend backend, AppPreferences preferences, Apps apps, TimeSpan? retryInterval = null)
        {
            this.backend = backend;
            this.preferences = preferences;
            this.apps = apps;
            this.retryInterval = retryInterval ?? DefaultRetryInterval;
            retryTimer = new Timer(_ => RequestSync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Binds to the default playback device and runs the first sync. The task faults if that first
        /// attempt fails; retries continue in the background.
        /// </summary>
        public Task Start()
        {
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

        /// <summary>Brings the app list and the volumes up to date.</summary>
        public Task RefreshAsync() => audioThread.Post(() => { Reconcile(); });

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
            ScheduleRetry(error != null);
            return error;
        }

        private Exception AttachIfNeeded()
        {
            if (Interlocked.Exchange(ref attachRequested, 0) == 0 && backend.IsAttached)
                return null;

            try
            {
                backend.Attach();
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
                    VolumeSynchronizer.Sync(volume, muted, sessions, preferences);
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

        /// <summary>The timer only runs while something failed; in normal operation everything is event driven.</summary>
        private void ScheduleRetry(bool failed)
        {
            if (Volatile.Read(ref disposeCalled) != 0)
                return;

            try
            {
                retryTimer.Change(failed ? retryInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently.
            }
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
                shutdown.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                Trace.WriteLine($"Disposing the audio backend failed: {ex.GetBaseException().Message}");
            }
            audioThread.Dispose();
        }
    }
}
