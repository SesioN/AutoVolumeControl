using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    /// <summary><see cref="IAudioBackend"/> on top of the Windows Core Audio API (via CSCore).</summary>
    sealed class CoreAudioBackend : IAudioBackend
    {
        /// <summary>Tags our own volume writes so the resulting notifications can be ignored.</summary>
        internal static readonly Guid EventContext = new Guid("5d6c3a52-6f0b-4d1e-9a51-0c8f3f3e7a11");

        private readonly Dictionary<string, SessionWatch> watches = new Dictionary<string, SessionWatch>();
        private MMDeviceEnumerator enumerator;
        private MMNotificationClient notificationClient;
        private MMDevice device;
        private AudioEndpointVolume endpointVolume;
        private VolumeCallback volumeCallback;
        private AudioSessionManager2 sessionManager;

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
                    notificationClient = new MMNotificationClient(enumerator);
                    notificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;
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
                    UnregisterSessionCreated(manager);
                    DisposeQuietly(manager);
                    throw;
                }

                // Assigned last: IsAttached only becomes true once everything is registered.
                sessionManager = manager;
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

            var sessions = new List<IAudioSession>();
            var seen = new HashSet<string>();
            try
            {
                using var sessionEnumerator = sessionManager.GetSessionEnumerator();
                foreach (var session in sessionEnumerator)
                {
                    bool watched = false;
                    try
                    {
                        using var control = session.QueryInterface<AudioSessionControl2>();
                        var process = ProcessInfo.Get(control.ProcessID);
                        if (!SessionFilter.BelongsToRunningApp(control.SessionState, process.Exited))
                            continue;

                        var name = SessionNameResolver.Resolve(process.Name, control.DisplayName, control.SessionIdentifier);
                        sessions.Add(new CoreAudioSession(name, control.IsSystemSoundSession, session.QueryInterface<SimpleAudioVolume>()));

                        var id = control.IsSystemSoundSession ? null : control.SessionInstanceIdentifier;
                        if (id != null && seen.Add(id) && !watches.ContainsKey(id))
                        {
                            watches.Add(id, new SessionWatch(this, session, control.ProcessID));
                            watched = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        // A session that vanished while being read.
                        Trace.WriteLine($"Skipping audio session: {ex.Message}");
                    }
                    finally
                    {
                        if (!watched)
                            session.Dispose();
                    }
                }
            }
            catch
            {
                foreach (var session in sessions)
                    session.Dispose();
                throw;
            }

            // Stop watching sessions that ended; they are not reported by the enumerator anymore.
            foreach (var id in watches.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                watches[id].Dispose();
                watches.Remove(id);
            }

            return sessions;
        }

        private void EnsureAttached()
        {
            if (!IsAttached)
                throw new InvalidOperationException("No playback device attached.");
        }

        private void OnDefaultDeviceChanged(object sender, DefaultDeviceChangedEventArgs e)
        {
            if (e.DataFlow == DataFlow.Render && e.Role == Role.Multimedia)
                ReattachRequired?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionCreated(object sender, SessionCreatedEventArgs e)
        {
            // CSCore adds a reference for the handler; the session is re-read on the audio thread.
            e.NewSession?.Dispose();
            RaiseSessionsChanged();
        }

        private void RaiseSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

        private void RaiseReattachRequired() => ReattachRequired?.Invoke(this, EventArgs.Empty);

        private void Detach()
        {
            foreach (var watch in watches.Values)
                watch.Dispose();
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
                UnregisterSessionCreated(sessionManager);

            DisposeQuietly(sessionManager);
            DisposeQuietly(endpointVolume);
            DisposeQuietly(device);
            sessionManager = null;
            endpointVolume = null;
            volumeCallback = null;
            device = null;
        }

        public void Dispose()
        {
            Detach();

            if (notificationClient != null)
            {
                notificationClient.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                DisposeQuietly(notificationClient);
                notificationClient = null;
            }

            DisposeQuietly(enumerator);
            enumerator = null;
        }

        private void UnregisterSessionCreated(AudioSessionManager2 manager)
        {
            try
            {
                manager.SessionCreated -= OnSessionCreated;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Unregistering session notification failed: {ex.Message}");
            }
        }

        /// <summary>Releasing objects of a stopped audio service can fail; that must not prevent re-attaching.</summary>
        private static void DisposeQuietly(IDisposable disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch (Exception ex)
            {
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
        /// Keeps one session alive and forwards its events: state changes (e.g. expired when the app closed),
        /// volume changes made by the app itself, disconnects, and the exit of its process.
        /// </summary>
        private sealed class SessionWatch : IDisposable
        {
            private readonly CoreAudioBackend owner;
            private readonly AudioSessionControl session;
            private readonly Process process;

            public SessionWatch(CoreAudioBackend owner, AudioSessionControl session, int processId)
            {
                this.owner = owner;
                this.session = session;
                session.StateChanged += OnStateChanged;
                session.SimpleVolumeChanged += OnSimpleVolumeChanged;
                session.SessionDisconnected += OnSessionDisconnected;
                process = TryWatchExit(processId);
            }

            private Process TryWatchExit(int processId)
            {
                if (processId == 0)
                    return null;

                Process watched = null;
                try
                {
                    watched = Process.GetProcessById(processId);
                    watched.EnableRaisingEvents = true;
                    watched.Exited += OnProcessExited;
                    return watched;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
                {
                    // Already exited, or not accessible (e.g. elevated); the session events still cover it.
                    watched?.Dispose();
                    return null;
                }
            }

            private void OnStateChanged(object sender, AudioSessionStateChangedEventArgs e) => owner.RaiseSessionsChanged();

            private void OnSimpleVolumeChanged(object sender, AudioSessionSimpleVolumeChangedEventArgs e)
            {
                if (e.EventContext != EventContext)
                    owner.RaiseSessionsChanged();
            }

            private void OnSessionDisconnected(object sender, AudioSessionDisconnectedEventArgs e)
            {
                if (e.DisconnectReason == AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval ||
                    e.DisconnectReason == AudioSessionDisconnectReason.DisconnectReasonServerShutdown)
                    owner.RaiseReattachRequired();
                else
                    owner.RaiseSessionsChanged();
            }

            private void OnProcessExited(object sender, EventArgs e) => owner.RaiseSessionsChanged();

            public void Dispose()
            {
                try
                {
                    session.StateChanged -= OnStateChanged;
                    session.SimpleVolumeChanged -= OnSimpleVolumeChanged;
                    session.SessionDisconnected -= OnSessionDisconnected;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Unsubscribing session events failed: {ex.Message}");
                }
                DisposeQuietly(session);

                if (process != null)
                {
                    process.Exited -= OnProcessExited;
                    process.Dispose();
                }
            }
        }

        private sealed class CoreAudioSession : IAudioSession
        {
            private readonly SimpleAudioVolume simpleAudioVolume;

            public CoreAudioSession(string name, bool isSystemSound, SimpleAudioVolume simpleAudioVolume)
            {
                Name = name;
                IsSystemSound = isSystemSound;
                this.simpleAudioVolume = simpleAudioVolume;
            }

            public string Name { get; }

            public bool IsSystemSound { get; }

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

            public void Dispose()
            {
                simpleAudioVolume.Dispose();
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

            public static ProcessInfo Get(int processId)
            {
                // 0: sessions that are not tied to a single process (e.g. system sounds).
                if (processId == 0)
                    return new ProcessInfo(null, false);

                try
                {
                    using var process = Process.GetProcessById(processId);
                    return new ProcessInfo(process.ProcessName, false);
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
