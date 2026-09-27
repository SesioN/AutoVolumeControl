using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    /// <summary><see cref="IAudioBackend"/> on top of the Windows Core Audio API (via CSCore).</summary>
    sealed class CoreAudioBackend : IAudioBackend
    {
        private MMDeviceEnumerator enumerator;
        private MMNotificationClient notificationClient;
        private MMDevice device;
        private AudioEndpointVolume endpointVolume;
        private VolumeCallback volumeCallback;
        private AudioSessionManager2 sessionManager;

        public event EventHandler MasterVolumeChanged;
        public event EventHandler SessionCreated;
        public event EventHandler DefaultDeviceChanged;

        public bool IsAttached => endpointVolume != null;

        public void Attach()
        {
            Detach();

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

            sessionManager = AudioSessionManager2.FromMMDevice(device);
            sessionManager.SessionCreated += OnSessionCreated;
            // Windows discards session notifications until IAudioSessionEnumerator::GetCount was called once.
            using (var sessions = sessionManager.GetSessionEnumerator())
                _ = sessions.Count;
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
            using var sessionEnumerator = sessionManager.GetSessionEnumerator();
            foreach (var session in sessionEnumerator)
            {
                try
                {
                    sessions.Add(new CoreAudioSession(session));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Skipping audio session: {ex.Message}");
                }
                finally
                {
                    session.Dispose();
                }
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
                DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionCreated(object sender, SessionCreatedEventArgs e)
        {
            // CSCore adds a reference for the handler; the session is re-read on the audio thread.
            e.NewSession?.Dispose();
            SessionCreated?.Invoke(this, EventArgs.Empty);
        }

        private void Detach()
        {
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
                try
                {
                    sessionManager.SessionCreated -= OnSessionCreated;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Unregistering session notification failed: {ex.Message}");
                }
            }

            sessionManager?.Dispose();
            endpointVolume?.Dispose();
            device?.Dispose();
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
                notificationClient.Dispose();
                notificationClient = null;
            }

            enumerator?.Dispose();
            enumerator = null;
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

        private sealed class CoreAudioSession : IAudioSession
        {
            private readonly SimpleAudioVolume simpleAudioVolume;

            public CoreAudioSession(AudioSessionControl session)
            {
                using var control = session.QueryInterface<AudioSessionControl2>();
                IsSystemSound = control.IsSystemSoundSession;
                Name = SessionNameResolver.Resolve(GetProcessName(control.ProcessID), control.DisplayName, control.SessionIdentifier);
                simpleAudioVolume = session.QueryInterface<SimpleAudioVolume>();
            }

            public string Name { get; }

            public bool IsSystemSound { get; }

            public void Apply(float volume, bool muted)
            {
                simpleAudioVolume.MasterVolume = volume;
                simpleAudioVolume.IsMuted = muted;
            }

            public void Dispose()
            {
                simpleAudioVolume.Dispose();
            }

            private static string GetProcessName(int processId)
            {
                // 0 is the idle process; such sessions do not belong to an app.
                if (processId == 0)
                    return null;

                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
                {
                    // Process already exited; the session identifier still names the executable.
                    return null;
                }
            }
        }
    }
}
