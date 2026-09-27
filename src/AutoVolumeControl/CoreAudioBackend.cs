using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    /// <summary>
    /// <see cref="IAudioBackend"/> on top of the Windows Core Audio API (via CSCore).
    /// <para>
    /// Every session of a running app is kept in a <see cref="SessionWatch"/> that caches its name and volume
    /// interface and forwards its events. The session list is only re-read after an event that can change it
    /// (session created, state changed, process exited, attach), so a master volume change costs one read and
    /// the necessary writes.
    /// </para>
    /// </summary>
    sealed class CoreAudioBackend : IAudioBackend
    {
        /// <summary>Tags our own volume writes so the resulting notifications can be ignored.</summary>
        internal static readonly Guid EventContext = new Guid("5d6c3a52-6f0b-4d1e-9a51-0c8f3f3e7a11");

        /// <summary>HRESULT_FROM_WIN32(ERROR_NOT_FOUND): there is no playback device.</summary>
        private const int ErrorNotFound = unchecked((int)0x80070490);

        private readonly Dictionary<string, SessionWatch> watches = new Dictionary<string, SessionWatch>();
        // Objects handed to us inside notification callbacks; released on the audio thread instead.
        private readonly ConcurrentQueue<IDisposable> pendingReleases = new ConcurrentQueue<IDisposable>();
        private MMDeviceEnumerator enumerator;
        private MMDevice device;
        private AudioEndpointVolume endpointVolume;
        private VolumeCallback volumeCallback;
        private AudioSessionManager2 sessionManager;
        private int sessionsDirty = 1;

        public event EventHandler MasterVolumeChanged;
        public event EventHandler SessionsChanged;
        public event EventHandler ReattachRequired;

        public bool IsAttached => sessionManager != null;

        internal int WatchedSessionCount => watches.Count;

        public void Attach()
        {
            Detach();
            try
            {
                if (enumerator == null)
                {
                    enumerator = new MMDeviceEnumerator();
                    enumerator.DefaultDeviceChanged += OnDefaultDeviceChanged;
                }

                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                endpointVolume = AudioEndpointVolume.FromDevice(device);
                volumeCallback = new VolumeCallback(this);
                endpointVolume.RegisterControlChangeNotify(volumeCallback);

                var manager = AudioSessionManager2.FromMMDevice(device);
                try
                {
                    manager.SessionCreated += OnSessionCreated;
                    // Windows discards session notifications until IAudioSessionEnumerator::GetCount was called once.
                    using (var sessions = manager.GetSessionEnumerator())
                        _ = sessions.Count;
                }
                catch
                {
                    DisposeQuietly(manager);
                    manager.SessionCreated -= OnSessionCreated;
                    throw;
                }

                // Assigned last: IsAttached only becomes true once everything is registered.
                sessionManager = manager;
                Interlocked.Exchange(ref sessionsDirty, 1);
            }
            catch (COMException ex) when (ex.ErrorCode == ErrorNotFound)
            {
                // No playback device. Keep the enumerator so its DefaultDeviceChanged reports the next one.
                Detach();
                throw;
            }
            catch
            {
                // Never stay half attached. The enumerator is recreated as well, in case the audio service restarted.
                Dispose();
                throw;
            }
        }

        public (float Volume, bool Muted) GetMasterVolume()
        {
            EnsureAttached();
            return (endpointVolume.GetMasterVolumeLevelScalar(), endpointVolume.GetMute());
        }

        public IReadOnlyList<IAudioSession> GetSessions()
        {
            EnsureAttached();
            ReleasePending();

            if (Interlocked.Exchange(ref sessionsDirty, 0) == 1)
            {
                try
                {
                    Rescan();
                }
                catch
                {
                    Interlocked.Exchange(ref sessionsDirty, 1);
                    throw;
                }
            }

            // The watches own the COM objects; disposing the returned sessions is a no-op.
            return watches.Values.Cast<IAudioSession>().ToList();
        }

        private void Rescan()
        {
            var seen = new HashSet<string>();
            using (var sessionEnumerator = sessionManager.GetSessionEnumerator())
            {
                foreach (var session in sessionEnumerator)
                {
                    bool kept = false;
                    try
                    {
                        using var control = session.QueryInterface<AudioSessionControl2>();
                        var id = control.SessionInstanceIdentifier;
                        if (id == null)
                            continue;

                        if (watches.TryGetValue(id, out var existing))
                        {
                            if (SessionFilter.BelongsToRunningApp(control.SessionState, existing.ProcessExited))
                                seen.Add(id);
                            continue;
                        }

                        var process = ProcessInfo.Get(control.ProcessID, control.SessionIdentifier);
                        if (!SessionFilter.BelongsToRunningApp(control.SessionState, process.Exited))
                            continue;

                        var name = SessionNameResolver.Resolve(process.Name, control.DisplayName, control.SessionIdentifier);
                        watches.Add(id, new SessionWatch(this, session, name, control.IsSystemSoundSession, control.ProcessID, control.SessionIdentifier));
                        seen.Add(id);
                        kept = true;
                    }
                    catch (Exception ex)
                    {
                        // A session that vanished while being read.
                        Trace.WriteLine($"Skipping audio session: {ex.Message}");
                    }
                    finally
                    {
                        if (!kept)
                            DisposeQuietly(session);
                    }
                }
            }

            foreach (var id in watches.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                watches[id].Release();
                watches.Remove(id);
            }
        }

        private void EnsureAttached()
        {
            if (!IsAttached)
                throw new InvalidOperationException("No playback device attached.");
        }

        private void MarkSessionsDirty() => Interlocked.Exchange(ref sessionsDirty, 1);

        private void OnDefaultDeviceChanged(object sender, DefaultDeviceChangedEventArgs e)
        {
            if (AudioEventRules.IsWatchedDefaultDevice(e.DataFlow, e.Role))
                ReattachRequired?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionCreated(object sender, SessionCreatedEventArgs e)
        {
            // CSCore adds a reference for the handler. Releasing it here would call into the audio API from
            // inside the notification, so it is released on the audio thread.
            if (e.NewSession != null)
                pendingReleases.Enqueue(e.NewSession);
            MarkSessionsDirty();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ReleasePending()
        {
            while (pendingReleases.TryDequeue(out var disposable))
                DisposeQuietly(disposable);
        }

        private void Detach()
        {
            foreach (var watch in watches.Values)
                watch.Release();
            watches.Clear();

            if (endpointVolume != null && volumeCallback != null)
            {
                try
                {
                    endpointVolume.UnregisterControlChangeNotify(volumeCallback);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"UnregisterControlChangeNotify failed: {ex.Message}");
                }
            }

            if (sessionManager != null)
            {
                // Unregister at the audio API first, then remove the handler: CSCore reads the handler twice
                // while raising the event, so removing it while notifications can still arrive could fail.
                DisposeQuietly(sessionManager);
                sessionManager.SessionCreated -= OnSessionCreated;
            }

            DisposeQuietly(endpointVolume);
            DisposeQuietly(device);
            sessionManager = null;
            endpointVolume = null;
            volumeCallback = null;
            device = null;
            ReleasePending();
        }

        public void Dispose()
        {
            Detach();

            if (enumerator != null)
            {
                enumerator.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                DisposeQuietly(enumerator);
                enumerator = null;
            }
            ReleasePending();
        }

        /// <summary>
        /// Releasing objects of a stopped audio service can fail. That must not prevent re-attaching, and CSCore's
        /// finalizer must not retry the release: an exception on the finalizer thread would end the process.
        /// </summary>
        private static void DisposeQuietly(IDisposable disposable)
        {
            if (disposable == null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                GC.SuppressFinalize(disposable);
                Trace.WriteLine($"Releasing an audio object failed: {ex.Message}");
            }
        }

        private sealed class VolumeCallback : IAudioEndpointVolumeCallback
        {
            private readonly CoreAudioBackend owner;

            public VolumeCallback(CoreAudioBackend owner)
            {
                this.owner = owner;
            }

            public int OnNotify(IntPtr pNotifyData)
            {
                // Only signal the change; the sync reads the current volume itself, so a burst of
                // notifications can never leave the apps on an outdated value.
                owner.MasterVolumeChanged?.Invoke(owner, EventArgs.Empty);
                return 0;
            }
        }

        /// <summary>
        /// One session of a running app: caches its name and volume interface and forwards its events —
        /// state changes (e.g. expired when the app closed), volume changes made by the app itself, disconnects
        /// (device removed, audio service shut down) and the exit of its process. For the system sounds session
        /// only disconnects are watched; it exists as long as the audio service runs, so a service restart is
        /// noticed even when no app plays audio.
        /// </summary>
        private sealed class SessionWatch : IAudioSession
        {
            private readonly CoreAudioBackend owner;
            private readonly AudioSessionControl session;
            private readonly SimpleAudioVolume simpleAudioVolume;
            private readonly int processId;
            private readonly string sessionIdentifier;
            private readonly Process process;
            private volatile bool exited;

            public SessionWatch(CoreAudioBackend owner, AudioSessionControl session, string name, bool isSystemSound, int processId, string sessionIdentifier)
            {
                this.owner = owner;
                this.session = session;
                this.processId = processId;
                this.sessionIdentifier = sessionIdentifier;
                Name = name;
                IsSystemSound = isSystemSound;

                simpleAudioVolume = session.QueryInterface<SimpleAudioVolume>();
                try
                {
                    session.SessionDisconnected += OnSessionDisconnected;
                    if (!isSystemSound)
                    {
                        session.StateChanged += OnStateChanged;
                        session.SimpleVolumeChanged += OnSimpleVolumeChanged;
                        process = TryWatchExit(processId);
                    }
                }
                catch
                {
                    // The caller releases the session itself.
                    session.SessionDisconnected -= OnSessionDisconnected;
                    session.StateChanged -= OnStateChanged;
                    session.SimpleVolumeChanged -= OnSimpleVolumeChanged;
                    DisposeQuietly(simpleAudioVolume);
                    throw;
                }
            }

            public string Name { get; }

            public bool IsSystemSound { get; }

            public bool ProcessExited =>
                process != null ? exited : ProcessInfo.Get(processId, sessionIdentifier).Exited;

            public float Volume
            {
                get => simpleAudioVolume.MasterVolume;
                set => Check(simpleAudioVolume.SetMasterVolumeNative(value, EventContext));
            }

            public bool Muted
            {
                get => simpleAudioVolume.IsMuted;
                set => Check(simpleAudioVolume.SetMuteNative(value, EventContext));
            }

            /// <summary>The backend owns the watch; callers of GetSessions do not release it.</summary>
            void IDisposable.Dispose()
            {
            }

            public void Release()
            {
                session.SessionDisconnected -= OnSessionDisconnected;
                session.StateChanged -= OnStateChanged;
                session.SimpleVolumeChanged -= OnSimpleVolumeChanged;
                DisposeQuietly(simpleAudioVolume);
                DisposeQuietly(session);

                if (process != null)
                {
                    process.Exited -= OnProcessExited;
                    process.Dispose();
                }
            }

            private Process TryWatchExit(int id)
            {
                if (id == 0)
                    return null;

                Process watched = null;
                try
                {
                    watched = Process.GetProcessById(id);
                    watched.Exited += OnProcessExited;
                    // Waits on the process handle (no polling). Raises Exited right away if it already exited.
                    watched.EnableRaisingEvents = true;
                    return watched;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
                {
                    // Already exited, or protected; ProcessExited then checks on demand.
                    if (watched != null)
                    {
                        watched.Exited -= OnProcessExited;
                        watched.Dispose();
                    }
                    return null;
                }
            }

            private void OnStateChanged(object sender, AudioSessionStateChangedEventArgs e)
            {
                owner.MarkSessionsDirty();
                owner.SessionsChanged?.Invoke(owner, EventArgs.Empty);
            }

            private void OnSimpleVolumeChanged(object sender, AudioSessionSimpleVolumeChangedEventArgs e)
            {
                // Our own writes are tagged; only changes made by the app itself need a correction.
                if (e.EventContext != EventContext)
                    owner.SessionsChanged?.Invoke(owner, EventArgs.Empty);
            }

            private void OnSessionDisconnected(object sender, AudioSessionDisconnectedEventArgs e)
            {
                if (AudioEventRules.RequiresReattach(e.DisconnectReason))
                {
                    owner.ReattachRequired?.Invoke(owner, EventArgs.Empty);
                }
                else
                {
                    owner.MarkSessionsDirty();
                    owner.SessionsChanged?.Invoke(owner, EventArgs.Empty);
                }
            }

            private void OnProcessExited(object sender, EventArgs e)
            {
                exited = true;
                owner.MarkSessionsDirty();
                owner.SessionsChanged?.Invoke(owner, EventArgs.Empty);
            }

            private static void Check(int hresult)
            {
                if (hresult < 0)
                    Marshal.ThrowExceptionForHR(hresult);
            }
        }

        private readonly struct ProcessInfo
        {
            private ProcessInfo(string name, bool exited)
            {
                Name = name;
                Exited = exited;
            }

            public string Name { get; }

            public bool Exited { get; }

            public static ProcessInfo Get(int processId, string sessionIdentifier)
            {
                // 0: sessions that are not tied to a single process (e.g. system sounds).
                if (processId == 0)
                    return new ProcessInfo(null, false);

                try
                {
                    using var process = Process.GetProcessById(processId);
                    var name = process.ProcessName;
                    // A different executable under the same ID means the app exited and the ID was reused.
                    return SessionFilter.IsSameProcess(name, sessionIdentifier)
                        ? new ProcessInfo(name, false)
                        : new ProcessInfo(null, true);
                }
                catch (ArgumentException)
                {
                    return new ProcessInfo(null, true);
                }
                catch (InvalidOperationException)
                {
                    // Exited between the lookup and reading the name.
                    return new ProcessInfo(null, true);
                }
                catch (Win32Exception)
                {
                    // Running but not accessible; the session identifier still names the executable.
                    return new ProcessInfo(null, false);
                }
            }
        }
    }
}
