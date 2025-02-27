using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    class AudioEndpointVolumeCallback : IAudioEndpointVolumeCallback, IDisposable
    {
        private readonly MMDevice playbackDevice;
        private readonly AppSettings appSettings;
        private readonly Apps apps;

        public AudioEndpointVolumeCallback(MMDevice playbackDevice, AppSettings appSettings, Apps apps)
        {
            this.playbackDevice = playbackDevice;
            this.appSettings = appSettings;
            this.apps = apps;
        }

        public int OnNotify(IntPtr pNotifyData)
        {
            var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotifyData);

            Task.Run(() =>
            {
                try
                {
                    InitializeCom();
                    apps.Refresh();
                    SyncSessionVolumes(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in OnNotify Task: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    ComHelper.CoUninitialize();
                }
            });

            return 0;
        }

        private void InitializeCom()
        {
            int hr = ComHelper.CoInitializeEx(IntPtr.Zero, ComHelper.COINIT.COINIT_MULTITHREADED);
            if (hr == (int)HResult.RPC_E_CHANGED_MODE)
            {
                Console.WriteLine("COM already initialized with a different threading model in AudioEndpointVolumeCallback");
            }
            else if (hr < 0)
            {
                throw new COMException("Failed to initialize COM library", hr);
            }
        }

        private void SyncSessionVolumes(AudioVolumeNotificationData data)
        {
            using var sessionManager = AudioSessionManager2.FromMMDevice(playbackDevice);
            foreach (var session in sessionManager.GetSessionEnumerator())
            {
                using var audioSessionControl = session.QueryInterface<AudioSessionControl2>();
                if (audioSessionControl.IsSystemSoundSession)
                    continue;

                string appName = GetSessionDisplayName(audioSessionControl);
                if (Convert.ToBoolean(appSettings.Get(appName)))
                {
                    using var simpleAudioVolume = session.QueryInterface<SimpleAudioVolume>();
                    simpleAudioVolume.MasterVolume = data.MasterVolume;
                    simpleAudioVolume.IsMuted = data.Muted;
                }
            }
        }

        private string GetSessionDisplayName(AudioSessionControl2 audioSession)
        {
            string name = GetProcessName(audioSession.Process) ?? audioSession.DisplayName ?? audioSession.SessionIdentifier;
            return name?.Trim();
        }

        private string GetProcessName(Process process)
        {
            try
            {
                return process?.ProcessName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting process name: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
          
        }

        public enum HResult : int
        {
            S_OK = 0,
            S_FALSE = 1,
            RPC_E_CHANGED_MODE = unchecked((int)0x80010106)
        }
    }
}
