using CSCore.CoreAudioAPI;
using System;
using System.Runtime.InteropServices;

namespace AutoVolumeControl
{
    class AudioEndpointVolumeCallback : IAudioEndpointVolumeCallback
    {
        private MMDevice playbackDevice;
        private AppSettings appSettings;
        private Apps apps;

        public AudioEndpointVolumeCallback(MMDevice playbackDevice, AppSettings appSettings, Apps apps)
        {
            this.playbackDevice = playbackDevice;
            this.appSettings = appSettings;
            this.apps = apps;
        }

        public int OnNotify(IntPtr pNotifyData)
        {
            try
            {
                // Marshal the pNotifyData to an AudioVolumeNotificationData structure
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotifyData);

                this.apps.Refresh();

                using (var sessionManager = AudioSessionManager2.FromMMDevice(playbackDevice))
                {
                    foreach (var session in sessionManager.GetSessionEnumerator())
                    {
                        using (var audioSessionControl = session.QueryInterface<AudioSessionControl2>())
                        {
                            string appName = !string.IsNullOrEmpty(audioSessionControl.DisplayName)
                                ? audioSessionControl.DisplayName
                                : GetNameFromProcessId(audioSessionControl);

                            if (Convert.ToBoolean(this.appSettings.get(appName)))
                            {
                                using (var simpleAudioVolume = session.QueryInterface<SimpleAudioVolume>())
                                {
                                    simpleAudioVolume.MasterVolume = data.MasterVolume;
                                    simpleAudioVolume.IsMuted = data.Muted;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle or log the exception as needed
                Console.WriteLine($"Error in OnNotify: {ex.Message}");
            }

            // Return 0 (S_OK) to indicate success
            return 0;
        }

        private string GetNameFromProcessId(AudioSessionControl2 audioSession)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById((int)audioSession.ProcessID);
                return process.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }
    }
}