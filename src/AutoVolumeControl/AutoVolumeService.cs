using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoVolumeControl
{
    /// <summary>
    /// Keeps the volume of every enabled app equal to the master volume of the default playback device.
    /// All audio work runs on one <see cref="AudioThread"/>.
    /// </summary>
    sealed class AutoVolumeService : IDisposable
    {
        private readonly IAudioBackend backend;
        private readonly AppPreferences preferences;
        private readonly Apps apps;
        private readonly AudioThread audioThread = new AudioThread();
        private int syncPending;
        private int reattachPending;
        private int disposeCalled;
        // Only read and written on the audio thread.
        private bool disposed;

        public AutoVolumeService(IAudioBackend backend, AppPreferences preferences, Apps apps)
        {
            this.backend = backend;
            this.preferences = preferences;
            this.apps = apps;
        }

        /// <summary>Binds to the default playback device and runs the first sync. Faults if no device is available.</summary>
        public Task Start()
        {
            backend.MasterVolumeChanged += OnMasterVolumeChanged;
            backend.SessionCreated += OnSessionCreated;
            backend.DefaultDeviceChanged += OnDefaultDeviceChanged;

            return audioThread.Post(AttachAndSync);
        }

        /// <summary>Re-reads the session list without touching any volume.</summary>
        public Task RefreshAsync()
        {
            return audioThread.Post(() =>
            {
                if (!backend.IsAttached)
                    return;

                var sessions = backend.GetSessions();
                try
                {
                    UpdateApps(sessions);
                }
                finally
                {
                    DisposeAll(sessions);
                }
            });
        }

        /// <summary>
        /// Schedules a sync. Requests arriving while one is still queued are merged into it,
        /// and the sync reads the master volume when it runs, so the last change always wins.
        /// </summary>
        public void RequestSync()
        {
            if (Interlocked.Exchange(ref syncPending, 1) == 1)
                return;

            audioThread.Post(() =>
            {
                Interlocked.Exchange(ref syncPending, 0);
                SyncCore();
            }).ContinueWith(LogFailure, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Completes once all work posted so far has run.</summary>
        public Task Flush() => audioThread.Post(() => { });

        private void OnMasterVolumeChanged(object sender, EventArgs e) => RequestSync();

        // A new app starts at its own volume; bring it in line right away.
        private void OnSessionCreated(object sender, EventArgs e) => RequestSync();

        private void OnDefaultDeviceChanged(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref reattachPending, 1) == 1)
                return;

            audioThread.Post(() =>
            {
                Interlocked.Exchange(ref reattachPending, 0);
                AttachAndSync();
            }).ContinueWith(LogFailure, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void AttachAndSync()
        {
            if (disposed)
                return;

            try
            {
                backend.Attach();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Attaching to the default playback device failed: {ex.Message}");
                apps.Update(Enumerable.Empty<string>());
                throw;
            }

            SyncCore();
        }

        private void SyncCore()
        {
            if (disposed || !backend.IsAttached)
                return;

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
        }

        private void UpdateApps(System.Collections.Generic.IReadOnlyList<IAudioSession> sessions)
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

        private static void DisposeAll(System.Collections.Generic.IReadOnlyList<IAudioSession> sessions)
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

            backend.MasterVolumeChanged -= OnMasterVolumeChanged;
            backend.SessionCreated -= OnSessionCreated;
            backend.DefaultDeviceChanged -= OnDefaultDeviceChanged;

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
