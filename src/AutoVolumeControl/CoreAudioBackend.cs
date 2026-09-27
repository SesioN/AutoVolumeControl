using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AutoVolumeControl.Interop;

namespace AutoVolumeControl
{
    /// <summary>
    /// <see cref="IAudioBackend"/> on top of the Windows Core Audio API.
    /// <para>
    /// Every session of a running app is kept in a <see cref="SessionWatch"/> that caches its name and volume
    /// interface and forwards its events. The session list is only re-read after an event that can change it
    /// (session created, state changed, process exited, attach) or when invalidated, so a master volume change
    /// costs one read and the necessary writes.
    /// </para>
    /// <para>
    /// All COM objects are plain runtime-callable wrappers released with <see cref="CoreAudio.Release"/>; unlike
    /// wrapper libraries that unregister callbacks in finalizers, nothing here can throw on the finalizer thread.
    /// </para>
    /// </summary>
    sealed class CoreAudioBackend : IAudioBackend
    {
        /// <summary>Tags our own volume writes so the resulting notifications can be ignored.</summary>
        internal static readonly Guid EventContext = new Guid("5d6c3a52-6f0b-4d1e-9a51-0c8f3f3e7a11");

        private readonly Dictionary<string, SessionWatch> watches = new Dictionary<string, SessionWatch>();
        private readonly DeviceNotifications deviceNotifications;
        private readonly VolumeNotifications volumeNotifications;
        private readonly SessionNotifications sessionNotifications;
        private IMMDeviceEnumerator enumerator;
        private bool deviceNotificationsRegistered;
        private IMMDevice device;
        private IAudioEndpointVolume endpointVolume;
        private bool volumeNotificationsRegistered;
        private IAudioSessionManager2 sessionManager;
        private bool sessionNotificationsRegistered;
        private bool attached;
        private int sessionsDirty = 1;

        public CoreAudioBackend()
        {
            deviceNotifications = new DeviceNotifications(this);
            volumeNotifications = new VolumeNotifications(this);
            sessionNotifications = new SessionNotifications(this);
        }

        public event EventHandler MasterVolumeChanged;
        public event EventHandler SessionsChanged;
        public event EventHandler ReattachRequired;

        public bool IsAttached => attached;

        internal int WatchedSessionCount => watches.Count;

        public void Attach()
        {
            Detach();
            try
            {
                if (enumerator == null)
                {
                    enumerator = CoreAudio.CreateDeviceEnumerator();
                    enumerator.RegisterEndpointNotificationCallback(deviceNotifications);
                    deviceNotificationsRegistered = true;
                }

                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device);

                endpointVolume = CoreAudio.Activate<IAudioEndpointVolume>(device, CoreAudio.AudioEndpointVolumeId);
                endpointVolume.RegisterControlChangeNotify(volumeNotifications);
                volumeNotificationsRegistered = true;

                sessionManager = CoreAudio.Activate<IAudioSessionManager2>(device, CoreAudio.AudioSessionManager2Id);
                sessionManager.RegisterSessionNotification(sessionNotifications);
                sessionNotificationsRegistered = true;

                // Windows discards session notifications until IAudioSessionEnumerator::GetCount was called once.
                sessionManager.GetSessionEnumerator(out var sessions);
                try
                {
                    sessions.GetCount(out _);
                }
                finally
                {
                    CoreAudio.Release(sessions);
                }

                attached = true;
                MarkSessionsDirty();
            }
            catch (COMException ex) when (ex.ErrorCode == CoreAudio.ErrorNotFound)
            {
                // No playback device. Keep the enumerator so its default device notification reports the next one.
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
            endpointVolume.GetMasterVolumeLevelScalar(out float volume);
            endpointVolume.GetMute(out bool muted);
            return (volume, muted);
        }

        public void InvalidateSessions() => MarkSessionsDirty();

        public IReadOnlyList<IAudioSession> GetSessions()
        {
            EnsureAttached();

            if (Interlocked.Exchange(ref sessionsDirty, 0) == 1)
            {
                try
                {
                    Rescan();
                }
                catch
                {
                    MarkSessionsDirty();
                    throw;
                }
            }

            // The watches own the COM objects; disposing the returned sessions is a no-op.
            return watches.Values.Cast<IAudioSession>().ToList();
        }

        private void Rescan()
        {
            var seen = new HashSet<string>();
            sessionManager.GetSessionEnumerator(out var sessionEnumerator);
            try
            {
                sessionEnumerator.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl session = null;
                    bool kept = false;
                    try
                    {
                        sessionEnumerator.GetSession(i, out session);
                        var control = (IAudioSessionControl2)session;
                        control.GetSessionInstanceIdentifier(out var id);
                        if (id == null)
                            continue;

                        control.GetState(out var state);
                        if (watches.TryGetValue(id, out var existing))
                        {
                            if (SessionFilter.BelongsToRunningApp(state, existing.ProcessExited))
                                seen.Add(id);
                            continue;
                        }

                        control.GetProcessId(out int processId);
                        control.GetSessionIdentifier(out var sessionIdentifier);
                        var process = ProcessInfo.Get(processId, sessionIdentifier);
                        if (!SessionFilter.BelongsToRunningApp(state, process.Exited))
                            continue;

                        control.GetDisplayName(out var displayName);
                        var name = SessionNameResolver.Resolve(process.Name, displayName, sessionIdentifier);
                        bool isSystemSound = control.IsSystemSoundsSession() == 0;
                        var executablePath = isSystemSound ? null : FindExecutable(control, processId, sessionIdentifier);

                        var watch = new SessionWatch(this, control, id, name, executablePath, isSystemSound, processId, sessionIdentifier);
                        kept = true;
                        watches.Add(id, watch);
                        seen.Add(id);

                        // A state change between reading the state and subscribing would be lost; re-check it.
                        control.GetState(out var stateAfterSubscribing);
                        if (stateAfterSubscribing == AudioSessionState.Expired)
                            RaiseSessionsChanged(markDirty: true);
                    }
                    catch (Exception ex) when (ex is COMException || ex is InvalidCastException || ex is ArgumentException)
                    {
                        // A session that vanished while being read.
                        Trace.WriteLine($"Skipping audio session: {ex.Message}");
                    }
                    finally
                    {
                        if (!kept)
                            CoreAudio.Release(session);
                    }
                }
            }
            finally
            {
                CoreAudio.Release(sessionEnumerator);
            }

            foreach (var id in watches.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                watches[id].Release();
                watches.Remove(id);
            }
        }

        /// <summary>Only used for the app's icon, so a failure just means the generic icon.</summary>
        private static string FindExecutable(IAudioSessionControl2 control, int processId, string sessionIdentifier)
        {
            try
            {
                return ExecutableLocator.Find(processId, sessionIdentifier, () =>
                {
                    control.GetIconPath(out var iconPath);
                    return iconPath;
                });
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                Trace.WriteLine($"Finding the executable of process {processId} failed: {ex.Message}");
                return null;
            }
        }

        private void EnsureAttached()
        {
            if (!attached)
                throw new InvalidOperationException("No playback device attached.");
        }

        private void MarkSessionsDirty() => Interlocked.Exchange(ref sessionsDirty, 1);

        private void RaiseSessionsChanged(bool markDirty)
        {
            if (markDirty)
                MarkSessionsDirty();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseReattachRequired() => ReattachRequired?.Invoke(this, EventArgs.Empty);

        private void RaiseMasterVolumeChanged() => MasterVolumeChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Checks with a cheap call whether the device binding still works. A single session can fail with a
        /// "binding lost" error while the device is fine; re-attaching would then only loop.
        /// </summary>
        private bool IsBindingAlive()
        {
            if (!attached)
                return false;

            try
            {
                endpointVolume.GetMute(out _);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }

        private void Detach()
        {
            attached = false;

            foreach (var watch in watches.Values)
                watch.Release();
            watches.Clear();

            if (sessionManager != null)
            {
                if (sessionNotificationsRegistered)
                    Try(() => sessionManager.UnregisterSessionNotification(sessionNotifications), "UnregisterSessionNotification");
                CoreAudio.Release(sessionManager);
            }

            if (endpointVolume != null)
            {
                if (volumeNotificationsRegistered)
                    Try(() => endpointVolume.UnregisterControlChangeNotify(volumeNotifications), "UnregisterControlChangeNotify");
                CoreAudio.Release(endpointVolume);
            }

            CoreAudio.Release(device);
            sessionManager = null;
            sessionNotificationsRegistered = false;
            endpointVolume = null;
            volumeNotificationsRegistered = false;
            device = null;
        }

        public void Dispose()
        {
            Detach();

            if (enumerator != null)
            {
                if (deviceNotificationsRegistered)
                    Try(() => enumerator.UnregisterEndpointNotificationCallback(deviceNotifications), "UnregisterEndpointNotificationCallback");
                CoreAudio.Release(enumerator);
            }
            enumerator = null;
            deviceNotificationsRegistered = false;
        }

        /// <summary>Unregistering from a stopped audio service fails; that must not prevent cleaning up the rest.</summary>
        private static void Try(Action action, string what)
        {
            try
            {
                action();
            }
            catch (COMException ex)
            {
                Trace.WriteLine($"{what} failed: {ex.Message}");
            }
        }

        /// <summary>Callback objects must never let an exception travel back into the audio service.</summary>
        private static int Notify(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Handling an audio notification failed: {ex}");
            }
            return 0;
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class DeviceNotifications : IMMNotificationClient
        {
            private readonly CoreAudioBackend owner;

            public DeviceNotifications(CoreAudioBackend owner) => this.owner = owner;

            public int OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId) => Notify(() =>
            {
                if (AudioEventRules.IsWatchedDefaultDevice(dataFlow, role))
                    owner.RaiseReattachRequired();
            });

            public int OnDeviceStateChanged(string deviceId, int newState) => 0;
            public int OnDeviceAdded(string deviceId) => 0;
            public int OnDeviceRemoved(string deviceId) => 0;
            public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class VolumeNotifications : IAudioEndpointVolumeCallback
        {
            private readonly CoreAudioBackend owner;

            public VolumeNotifications(CoreAudioBackend owner) => this.owner = owner;

            // Only signal the change; the sync reads the current volume itself, so a burst of notifications can
            // never leave the apps on an outdated value.
            public int OnNotify(IntPtr notifyData) => Notify(owner.RaiseMasterVolumeChanged);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class SessionNotifications : IAudioSessionNotification
        {
            private readonly CoreAudioBackend owner;

            public SessionNotifications(CoreAudioBackend owner) => this.owner = owner;

            // The new session is re-read on the audio thread; it is not touched here.
            public int OnSessionCreated(IntPtr newSession) => Notify(() => owner.RaiseSessionsChanged(markDirty: true));
        }

        /// <summary>
        /// One session of a running app: caches its name and volume interface and forwards its events —
        /// state changes (e.g. expired when the app closed), volume changes made by the app itself, disconnects
        /// (device removed, audio service shut down) and the exit of its process. For the system sounds session
        /// only disconnects are used; it exists while the audio service runs, so a service restart is noticed even
        /// when no app plays audio.
        /// </summary>
        private sealed class SessionWatch : IAudioSession
        {
            private readonly CoreAudioBackend owner;
            private readonly IAudioSessionControl2 control;
            private readonly ISimpleAudioVolume simpleAudioVolume;
            private readonly SessionEvents events;
            private readonly int processId;
            private readonly string sessionIdentifier;
            private readonly Process process;
            private volatile bool exited;

            /// <summary>Takes over the reference to <paramref name="control"/>.</summary>
            public SessionWatch(CoreAudioBackend owner, IAudioSessionControl2 control, string id, string name, string executablePath, bool isSystemSound, int processId, string sessionIdentifier)
            {
                this.owner = owner;
                this.control = control;
                this.processId = processId;
                this.sessionIdentifier = sessionIdentifier;
                Id = id;
                Name = name;
                ExecutablePath = executablePath;
                IsSystemSound = isSystemSound;

                // Same COM object, other interface: no additional reference to release.
                simpleAudioVolume = (ISimpleAudioVolume)control;
                events = new SessionEvents(this);
                control.RegisterAudioSessionNotification(events);
                if (!isSystemSound)
                    process = TryWatchExit(processId);
            }

            public string Id { get; }

            public string Name { get; }

            public string ExecutablePath { get; }

            public bool IsSystemSound { get; }

            public int ProcessId => processId;

            public bool ProcessExited =>
                process != null ? exited : ProcessInfo.Get(processId, sessionIdentifier).Exited;

            public float Volume
            {
                get => Call(() => { simpleAudioVolume.GetMasterVolume(out float value); return value; });
                set => Call(() => { var context = EventContext; simpleAudioVolume.SetMasterVolume(value, ref context); return true; });
            }

            public bool Muted
            {
                get => Call(() => { simpleAudioVolume.GetMute(out bool value); return value; });
                set => Call(() => { var context = EventContext; simpleAudioVolume.SetMute(value, ref context); return true; });
            }

            /// <summary>The backend owns the watch; callers of GetSessions do not release it.</summary>
            void IDisposable.Dispose()
            {
            }

            public void Release()
            {
                Try(() => control.UnregisterAudioSessionNotification(events), "UnregisterAudioSessionNotification");
                CoreAudio.Release(control);

                if (process != null)
                {
                    process.Exited -= OnProcessExited;
                    process.Dispose();
                }
            }

            /// <summary>
            /// A failure means the cached session is stale: re-read the list, and re-attach if the whole binding
            /// is gone (e.g. the audio service restarted without a disconnect notification).
            /// </summary>
            private T Call<T>(Func<T> call)
            {
                try
                {
                    return call();
                }
                catch (COMException ex)
                {
                    if (AudioEventRules.IsBindingLost(ex.ErrorCode) && !owner.IsBindingAlive())
                        owner.RaiseReattachRequired();
                    else
                        owner.MarkSessionsDirty();
                    throw;
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
                    // Already exited, or protected; ProcessExited then checks on demand during rescans.
                    if (watched != null)
                    {
                        watched.Exited -= OnProcessExited;
                        watched.Dispose();
                    }
                    return null;
                }
            }

            private void OnProcessExited(object sender, EventArgs e)
            {
                exited = true;
                owner.RaiseSessionsChanged(markDirty: true);
            }

            [ComVisible(true)]
            [ClassInterface(ClassInterfaceType.None)]
            private sealed class SessionEvents : IAudioSessionEvents
            {
                private readonly SessionWatch watch;

                public SessionEvents(SessionWatch watch) => this.watch = watch;

                public int OnStateChanged(AudioSessionState newState) => Notify(() =>
                {
                    if (!watch.IsSystemSound)
                        watch.owner.RaiseSessionsChanged(markDirty: true);
                });

                // Our own writes are tagged; only changes made by the app itself need a correction.
                public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext) => Notify(() =>
                {
                    if (!watch.IsSystemSound && CoreAudio.ReadGuid(eventContext) != EventContext)
                        watch.owner.RaiseSessionsChanged(markDirty: false);
                });

                public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => Notify(() =>
                {
                    if (AudioEventRules.RequiresReattach(disconnectReason))
                        watch.owner.RaiseReattachRequired();
                    else
                        watch.owner.RaiseSessionsChanged(markDirty: true);
                });

                public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) => 0;
                public int OnIconPathChanged(string newIconPath, IntPtr eventContext) => 0;
                public int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumes, int changedChannel, IntPtr eventContext) => 0;
                public int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext) => 0;
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
